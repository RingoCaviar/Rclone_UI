using System.Collections.Concurrent;

namespace RcloneUI.Mounts;

public sealed class MountManager(IMountExecutionAdapter adapter, IMountJournal journal, IRecoveryCacheRegistry recoveryCaches) : IMountManager
{
    private readonly ConcurrentDictionary<MountInstanceId, SemaphoreSlim> gates = new();

    public async ValueTask<MountValidation> ValidateAsync(MountProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.Id.Value == Guid.Empty || string.IsNullOrWhiteSpace(profile.Remote) || string.IsNullOrWhiteSpace(profile.VolumeName)) return new(false, "profile-invalid");
        if (profile.PreferredDriveLetter is < 'A' or > 'Z') return new(false, "drive-letter-invalid");
        if (profile.CachePreset != MountCachePreset.ReadOnlyBrowsing && profile.CacheCapacityBytes <= 0) return new(false, "cache-capacity-invalid");
        var environment = await adapter.InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!environment.OperationalSession) return new(false, "session-not-operational");
        if (!environment.RemoteHealthy) return new(false, "remote-unhealthy");
        if (!environment.SubpathExists) return new(false, "subpath-missing");
        if (!environment.WinFspCompatible) return new(false, "winfsp-incompatible");
        if (!environment.DriveLetterAvailable && !environment.DriveLetterOwnedByProfile) return new(false, "drive-letter-conflict");
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
            var ready = Snapshot(id, profile, evidence.ProvesReady ? MountState.Ready : MountState.NeedsRemount,
                evidence.ProvesReady ? MountRisk.None : MountRisk.CannotProveClean, evidence,
                evidence.ProvesReady ? null : evidence.DiagnosticCode ?? "readiness-not-proved");
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
            if (mode == MountStopMode.Safe && !evidence.ProvesClean)
            {
                var refused = draining with { Risk = RiskFor(evidence), Evidence = evidence, DiagnosticCode = "cannot-prove-clean", UpdatedUtc = DateTimeOffset.UtcNow };
                await journal.SaveAsync(refused, cancellationToken).ConfigureAwait(false);
                return refused;
            }
            await adapter.StopAsync(instanceId, mode == MountStopMode.Force, cancellationToken).ConfigureAwait(false);
            if (evidence.ProvesClean)
            {
                var stopped = draining with { State = MountState.Stopped, Risk = MountRisk.None, Evidence = evidence, DiagnosticCode = null, UpdatedUtc = DateTimeOffset.UtcNow };
                await journal.SaveAsync(stopped, cancellationToken).ConfigureAwait(false);
                return stopped;
            }
            var risky = draining with { State = MountState.RecoveryRequired, Risk = RiskFor(evidence), Evidence = evidence, DiagnosticCode = "forced-unmount-recovery-required", UpdatedUtc = DateTimeOffset.UtcNow };
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
            if (evidence.ProvesReady) { var live = snapshot with { State = MountState.Ready, Evidence = evidence, UpdatedUtc = DateTimeOffset.UtcNow }; await journal.SaveAsync(live, cancellationToken); results.Add(live); continue; }
            var interrupted = snapshot with { State = MountState.RecoveryRequired, Risk = MountRisk.Interrupted, Evidence = evidence, DiagnosticCode = "interrupted-mount", UpdatedUtc = DateTimeOffset.UtcNow };
            var path = await recoveryCaches.PreserveAsync(interrupted, interrupted.Risk, cancellationToken).ConfigureAwait(false);
            interrupted = interrupted with { RecoveryCachePath = path };
            await journal.SaveAsync(interrupted, cancellationToken).ConfigureAwait(false);
            results.Add(interrupted);
        }
        return results;
    }

    private static MountRisk RiskFor(MountEvidence evidence) => evidence.PendingFiles > 0 || evidence.PendingBytes > 0 ? MountRisk.PendingWrites : MountRisk.CannotProveClean;
    private static MountEvidence EmptyEvidence() => new(false, false, false, false, null, null, null, null);
    private static MountSnapshot Snapshot(MountInstanceId id, MountProfile profile, MountState state, MountRisk risk, MountEvidence evidence, string? code = null) => new(id, profile, state, risk, evidence, DateTimeOffset.UtcNow, null, code);
}
