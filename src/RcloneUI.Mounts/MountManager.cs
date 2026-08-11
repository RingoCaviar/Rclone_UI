using System.Collections.Concurrent;

namespace RcloneUI.Mounts;

public sealed class MountManager(IMountExecutionAdapter adapter, IMountJournal journal, IRecoveryCacheRegistry recoveryCaches) : IMountManager
{
    private readonly ConcurrentDictionary<MountInstanceId, SemaphoreSlim> gates = new();

    public async ValueTask<MountValidation> ValidateAsync(MountProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.Id.Value == Guid.Empty || string.IsNullOrWhiteSpace(profile.Remote) || string.IsNullOrWhiteSpace(profile.VolumeName)) return new(false, "profile-invalid");
        if (profile.PresentationMode != MountPresentationMode.FixedDirectory && profile.DriveLetterSelection == DriveLetterSelection.Preferred && profile.PreferredDriveLetter is < 'A' or > 'Z') return new(false, "drive-letter-invalid");
        if (profile.PresentationMode == MountPresentationMode.FixedDirectory && string.IsNullOrWhiteSpace(profile.FixedDirectoryPath)) return new(false, "fixed-directory-required");
        if (profile.PresentationMode == MountPresentationMode.NetworkDrive && string.IsNullOrWhiteSpace(profile.ShareName)) return new(false, "share-name-required");
        if (profile.CachePreset != MountCachePreset.ReadOnlyBrowsing && profile.CacheCapacityBytes <= 0) return new(false, "cache-capacity-invalid");
        var environment = await adapter.InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!environment.OperationalSession) return new(false, "session-not-operational");
        if (!environment.RemoteHealthy) return new(false, "remote-unhealthy");
        if (!environment.SubpathExists) return new(false, "subpath-missing");
        if (!environment.WinFspCompatible) return new(false, "winfsp-incompatible");
        if (profile.PresentationMode != MountPresentationMode.FixedDirectory && !environment.DriveLetterAvailable && !environment.DriveLetterOwnedByProfile) return new(false, "drive-letter-conflict");
        if (profile.PresentationMode == MountPresentationMode.NetworkDrive && !environment.ShareNameAvailable) return new(false, "share-name-conflict");
        if (profile.PresentationMode == MountPresentationMode.FixedDirectory && !environment.DirectoryTargetAvailable) return new(false, "fixed-directory-conflict");
        if (profile.CachePreset != MountCachePreset.ReadOnlyBrowsing && !environment.CacheWritable) return new(false, "cache-not-writable");
        if (!StringComparer.Ordinal.Equals(profile.CapabilityBinding, environment.CapabilityBinding)) return new(false, "capability-binding-changed");
        return new(true, null);
    }

    public async ValueTask<MountSnapshot> StartAsync(MountProfile profile, CancellationToken cancellationToken = default)
    {
        var validation = await ValidateAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid) return Snapshot(MountInstanceId.New(), profile, MountState.Stopped, MountRisk.None, EmptyEvidence(), validation.DiagnosticCode);
        var id = MountInstanceId.New();
        var gate = gates.GetOrAdd(id, static _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var starting = Snapshot(id, profile, MountState.Starting, MountRisk.None, EmptyEvidence());
            await journal.SaveAsync(starting, cancellationToken).ConfigureAwait(false);
            await adapter.StartAsync(id, profile, cancellationToken).ConfigureAwait(false);
            var evidence = await adapter.ObserveAsync(id, profile, cancellationToken).ConfigureAwait(false);
            var provesReady = evidence.ProvesReadyFor(profile);
            MountCleanupEvidence? cleanup = null;
            if (!provesReady) cleanup = await adapter.CleanupFailedStartAsync(id, profile, cancellationToken).ConfigureAwait(false);
            var cleanupProved = cleanup?.ProvesCleanup == true;
            var ready = Snapshot(id, profile, provesReady ? MountState.Ready : cleanupProved ? MountState.NeedsRemount : MountState.RecoveryRequired,
                provesReady ? MountRisk.None : MountRisk.CannotProveClean, evidence,
                provesReady ? null : cleanupProved ? ReadinessFailure(evidence) : "failed-start-cleanup-not-proved") with
            { StartupCleanup = cleanup };
            await journal.SaveAsync(ready, cancellationToken).ConfigureAwait(false);
            return ready;
        }
        finally { gate.Release(); }
    }

    public async ValueTask<MountSnapshot> StopAsync(MountInstanceId instanceId, MountStopMode mode, bool forceConfirmed = false, CancellationToken cancellationToken = default)
    {
        var current = await journal.ReadAsync(instanceId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("Mount instance was not found.");
        if (mode == MountStopMode.Force && !forceConfirmed) throw new InvalidOperationException("Force unmount requires explicit confirmation.");
        var gate = gates.GetOrAdd(instanceId, static _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var draining = current with { State = MountState.SafeUnmount, UpdatedUtc = DateTimeOffset.UtcNow };
            await journal.SaveAsync(draining, cancellationToken).ConfigureAwait(false);
            var evidence = await adapter.ObserveAsync(instanceId, current.Profile, cancellationToken).ConfigureAwait(false);
            if (mode == MountStopMode.Safe && !evidence.ProvesCleanDrain)
            {
                var refused = draining with { Risk = RiskFor(evidence), Evidence = evidence, DiagnosticCode = "cannot-prove-clean", UpdatedUtc = DateTimeOffset.UtcNow };
                await journal.SaveAsync(refused, cancellationToken).ConfigureAwait(false);
                return refused;
            }
            var stopEvidence = await adapter.StopAsync(instanceId, mode == MountStopMode.Force, cancellationToken).ConfigureAwait(false);
            if (evidence.ProvesCleanDrain && stopEvidence.ProvesStopped)
            {
                var stopped = draining with { State = MountState.Stopped, Risk = MountRisk.None, Evidence = evidence, StopEvidence = stopEvidence, DiagnosticCode = null, UpdatedUtc = DateTimeOffset.UtcNow };
                await journal.SaveAsync(stopped, cancellationToken).ConfigureAwait(false);
                return stopped;
            }
            var diagnosticCode = !stopEvidence.ProvesStopped ? "unmount-cleanup-not-proved" : "forced-unmount-recovery-required";
            var risky = draining with { State = MountState.RecoveryRequired, Risk = RiskFor(evidence), Evidence = evidence, StopEvidence = stopEvidence, DiagnosticCode = diagnosticCode, UpdatedUtc = DateTimeOffset.UtcNow };
            var path = await recoveryCaches.PreserveAsync(risky, risky.Risk, cancellationToken).ConfigureAwait(false);
            risky = risky with { RecoveryCachePath = path };
            await journal.SaveAsync(risky, cancellationToken).ConfigureAwait(false);
            return risky;
        }
        finally { gate.Release(); }
    }

    public ValueTask<MountSnapshot?> ObserveAsync(MountInstanceId instanceId, CancellationToken cancellationToken = default) => journal.ReadAsync(instanceId, cancellationToken);

    public async ValueTask<IReadOnlyList<MountSnapshot>> ReconcileInterruptedAsync(CancellationToken cancellationToken = default)
    {
        var active = await journal.ReadActiveAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<MountSnapshot>(active.Count);
        foreach (var snapshot in active)
        {
            var evidence = await adapter.ObserveAsync(snapshot.InstanceId, snapshot.Profile, cancellationToken).ConfigureAwait(false);
            if (evidence.ProvesReadyFor(snapshot.Profile))
            {
                var recoveryStillRequiresReview = snapshot.State == MountState.RecoveryRequired || !string.IsNullOrWhiteSpace(snapshot.RecoveryCachePath);
                var live = snapshot with
                {
                    State = recoveryStillRequiresReview ? MountState.RecoveryRequired : MountState.Ready,
                    Risk = recoveryStillRequiresReview ? snapshot.Risk : MountRisk.None,
                    Evidence = evidence,
                    DiagnosticCode = recoveryStillRequiresReview ? "recovery-review-required" : null,
                    UpdatedUtc = DateTimeOffset.UtcNow
                };
                await journal.SaveAsync(live, cancellationToken);
                results.Add(live);
                continue;
            }
            var risk = ReconciliationRisk(evidence);
            var interrupted = snapshot with { State = MountState.RecoveryRequired, Risk = risk, Evidence = evidence, DiagnosticCode = ReconciliationDiagnostic(evidence), UpdatedUtc = DateTimeOffset.UtcNow };
            if (string.IsNullOrWhiteSpace(interrupted.RecoveryCachePath))
            {
                var path = await recoveryCaches.PreserveAsync(interrupted, interrupted.Risk, cancellationToken).ConfigureAwait(false);
                interrupted = interrupted with { RecoveryCachePath = path };
            }
            await journal.SaveAsync(interrupted, cancellationToken).ConfigureAwait(false);
            results.Add(interrupted);
        }
        return results;
    }

    private static MountRisk RiskFor(MountEvidence evidence) => evidence.PendingFiles > 0 || evidence.PendingBytes > 0 || evidence.UploadingFiles > 0 || evidence.FailedUploads > 0 ? MountRisk.PendingWrites : MountRisk.CannotProveClean;
    private static MountRisk ReconciliationRisk(MountEvidence evidence) => evidence.CacheObservable == false && StringComparer.Ordinal.Equals(evidence.DiagnosticCode, "cache-missing") ? MountRisk.CorruptCache : MountRisk.Interrupted;
    private static string ReconciliationDiagnostic(MountEvidence evidence)
    {
        if (!evidence.NamespaceOwnedByInstance) return "stale-namespace-owner-mismatch";
        if (evidence.CacheObservable == false && StringComparer.Ordinal.Equals(evidence.DiagnosticCode, "cache-missing")) return "recovery-cache-missing";
        if (!evidence.ProcessAlive) return "mount-process-terminated";
        if (!evidence.NamespacePresented) return "mount-namespace-delayed-or-missing";
        return "interrupted-mount";
    }
    private static string ReadinessFailure(MountEvidence evidence)
    {
        if (!evidence.RcRequestAccepted) return "mount-rc-not-accepted";
        if (!evidence.ProcessAlive) return "mount-process-exited";
        if (!evidence.EndpointRegistered) return "mount-endpoint-not-registered";
        if (!evidence.NamespacePresented) return "mount-namespace-not-presented";
        if (!evidence.NamespaceOwnedByInstance) return "mount-namespace-owner-mismatch";
        if (!evidence.ExpectedTokenVisible) return "mount-token-not-visible";
        if (!evidence.RootProbeWithinDeadline) return "mount-root-probe-timeout";
        if (!evidence.RootProbeSucceeded) return "mount-root-probe-failed";
        if (evidence.CacheObservable is false) return "mount-cache-observation-failed";
        return evidence.DiagnosticCode ?? "readiness-not-proved";
    }
    private static MountEvidence EmptyEvidence() => new(false, false, false, false, null, null, null, null);
    private static MountSnapshot Snapshot(MountInstanceId id, MountProfile profile, MountState state, MountRisk risk, MountEvidence evidence, string? code = null) => new(id, profile, state, risk, evidence, DateTimeOffset.UtcNow, null, code);
}
