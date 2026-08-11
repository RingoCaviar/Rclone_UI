using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RcloneUI.Updates;

public sealed class ManagedUpdateCoordinator(IUpdateTrustVerifier trust, IUpdateRuntime runtime, IUpdateJournal journal) : IManagedUpdateCoordinator
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<UpdateTransactionId, byte> active = new();

    public async ValueTask<UpdateResult> PrepareAsync(PairedUpdatePlan plan, ReadOnlyMemory<byte> oneTimeToken, CancellationToken cancellationToken = default)
    {
        if (plan.TransactionId.Value == Guid.Empty || oneTimeToken.Length < 32 || string.IsNullOrWhiteSpace(plan.PlanHash)) return new(UpdateOutcome.FailedSecurely, null, "plan-invalid");
        if (!active.TryAdd(plan.TransactionId, 0)) return new(UpdateOutcome.FailedSecurely, null, "update-already-active");
        var admission = await runtime.InspectAdmissionAsync(cancellationToken).ConfigureAwait(false);
        if (!admission.DataRootAvailable) { active.TryRemove(plan.TransactionId, out _); return new(UpdateOutcome.DataRootUnavailable, null, "data-root-unavailable"); }
        if (admission.ActiveTransfers || admission.ActiveMounts || admission.RestartPending) { active.TryRemove(plan.TransactionId, out _); return new(UpdateOutcome.WaitingForWork, null, admission.DiagnosticCode ?? "active-work-gate"); }
        if (!await trust.VerifyAsync(plan.Application, cancellationToken).ConfigureAwait(false)) { active.TryRemove(plan.TransactionId, out _); return new(UpdateOutcome.FailedSecurely, null, "artifact-trust-failed"); }
        var tokenDigest = Digest(oneTimeToken.Span);
        var entry = new UpdateJournalEntry(plan, UpdatePhase.VersionVerified, 1, tokenDigest, null, DateTimeOffset.UtcNow);
        await journal.SaveAsync(entry, cancellationToken).ConfigureAwait(false);
        entry = await AdvanceAsync(entry, UpdatePhase.HandoffIssued, cancellationToken).ConfigureAwait(false);
        return new(UpdateOutcome.ReadyForHandoff, entry);
    }

    public async ValueTask<UpdateResult> CommitAsync(UpdateTransactionId transactionId, ReadOnlyMemory<byte> oneTimeToken, CancellationToken cancellationToken = default)
    {
        var entry = await journal.ReadAsync(transactionId, cancellationToken).ConfigureAwait(false);
        if (entry is null || entry.Phase != UpdatePhase.HandoffIssued || !FixedEquals(entry.HandoffTokenDigest, Digest(oneTimeToken.Span))) return new(UpdateOutcome.FailedSecurely, entry, "handoff-authentication-failed");
        try
        {
            await runtime.StopOldHostAsync(entry.Plan, cancellationToken).ConfigureAwait(false); entry = await AdvanceAsync(entry, UpdatePhase.OldHostStopped, cancellationToken).ConfigureAwait(false);
            await runtime.SwitchApplicationAsync(entry.Plan, true, cancellationToken).ConfigureAwait(false); entry = await AdvanceAsync(entry, UpdatePhase.VersionPointerSwitched, cancellationToken).ConfigureAwait(false);
            entry = await AdvanceAsync(entry, UpdatePhase.NewHostBaseHealthy, cancellationToken).ConfigureAwait(false);
            await runtime.StageAndVerifyVaultAsync(entry.Plan, cancellationToken).ConfigureAwait(false); entry = await AdvanceAsync(entry, UpdatePhase.VaultVerified, cancellationToken).ConfigureAwait(false);
            await runtime.SwitchVaultAsync(entry.Plan, true, cancellationToken).ConfigureAwait(false); entry = await AdvanceAsync(entry, UpdatePhase.VaultPointerSwitched, cancellationToken).ConfigureAwait(false);
            if (!await runtime.StartAndCheckNewHostAsync(entry.Plan, HealthTimeout, cancellationToken).ConfigureAwait(false)) return await RollBackAsync(entry, "new-health-failed", cancellationToken).ConfigureAwait(false);
            var evidence = await runtime.InspectPairAsync(entry.Plan, cancellationToken).ConfigureAwait(false);
            if (!ProvesNewPair(evidence)) return await RollBackAsync(entry, "new-pair-not-proved", cancellationToken).ConfigureAwait(false);
            entry = await AdvanceAsync(entry with { HealthEvidenceDigest = evidence.HealthEvidenceDigest }, UpdatePhase.NewHealthPassed, cancellationToken).ConfigureAwait(false);
            entry = await AdvanceAsync(entry, UpdatePhase.Committed, cancellationToken).ConfigureAwait(false);
            active.TryRemove(transactionId, out _);
            return new(UpdateOutcome.Committed, entry);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await RollBackAsync(entry, $"commit-{exception.GetType().Name.ToLowerInvariant()}", CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask<UpdateResult> RecoverAsync(UpdateTransactionId transactionId, CancellationToken cancellationToken = default)
    {
        var entry = await journal.ReadAsync(transactionId, cancellationToken).ConfigureAwait(false);
        if (entry is null) return new(UpdateOutcome.RecoveryRequired, null, "journal-missing");
        var admission = await runtime.InspectAdmissionAsync(cancellationToken).ConfigureAwait(false);
        if (!admission.DataRootAvailable) return new(UpdateOutcome.DataRootUnavailable, entry, "data-root-unavailable");
        var evidence = await runtime.InspectPairAsync(entry.Plan, cancellationToken).ConfigureAwait(false);
        if (entry.Phase >= UpdatePhase.NewHealthPassed && ProvesNewPair(evidence))
        {
            await runtime.SwitchApplicationAsync(entry.Plan, true, cancellationToken).ConfigureAwait(false);
            await runtime.SwitchVaultAsync(entry.Plan, true, cancellationToken).ConfigureAwait(false);
            var committed = await AdvanceAsync(entry, UpdatePhase.Committed, cancellationToken).ConfigureAwait(false);
            return new(UpdateOutcome.Committed, committed);
        }
        if (evidence.OldApplicationValid && evidence.OldVaultValid) return await RollBackAsync(entry, "recovered-old-pair", cancellationToken).ConfigureAwait(false);
        return new(UpdateOutcome.RecoveryRequired, entry, "no-compatible-pair-proved");
    }

    private async ValueTask<UpdateResult> RollBackAsync(UpdateJournalEntry entry, string code, CancellationToken cancellationToken)
    {
        var evidence = await runtime.InspectPairAsync(entry.Plan, cancellationToken).ConfigureAwait(false);
        if (!evidence.OldApplicationValid || !evidence.OldVaultValid) return new(UpdateOutcome.RecoveryRequired, entry, "old-pair-invalid");
        await runtime.SwitchVaultAsync(entry.Plan, false, cancellationToken).ConfigureAwait(false);
        await runtime.SwitchApplicationAsync(entry.Plan, false, cancellationToken).ConfigureAwait(false);
        await runtime.StartOldHostAsync(entry.Plan, cancellationToken).ConfigureAwait(false);
        evidence = await runtime.InspectPairAsync(entry.Plan, cancellationToken).ConfigureAwait(false);
        if (!evidence.ExactlyOneHost) return new(UpdateOutcome.RecoveryRequired, entry, "rollback-host-ambiguous");
        var rolledBack = await AdvanceAsync(entry with { DiagnosticCode = code }, UpdatePhase.RolledBack, cancellationToken).ConfigureAwait(false);
        active.TryRemove(entry.Plan.TransactionId, out _);
        return new(UpdateOutcome.RolledBack, rolledBack, code);
    }

    private async ValueTask<UpdateJournalEntry> AdvanceAsync(UpdateJournalEntry entry, UpdatePhase phase, CancellationToken cancellationToken)
    {
        var next = entry with { Phase = phase, Revision = entry.Revision + 1, UpdatedUtc = DateTimeOffset.UtcNow };
        await journal.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        return next;
    }

    private static bool ProvesNewPair(PairEvidence value) => value.NewApplicationValid && value.NewVaultValid && value.NewHealthValid && value.ExactlyOneHost && !string.IsNullOrWhiteSpace(value.HealthEvidenceDigest);
    private static string Digest(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value));
    private static bool FixedEquals(string left, string right) { try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right)); } catch (FormatException) { return false; } }
}
