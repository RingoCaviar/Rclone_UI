namespace RcloneUI.Updates;

public readonly record struct UpdateTransactionId(Guid Value) { public static UpdateTransactionId New() => new(Guid.NewGuid()); }

public enum ManagedComponent { Application, Rclone, WinFsp }
public enum UpdatePhase { PlanWritten, VersionVerified, HandoffIssued, OldHostStopped, VersionPointerSwitched, NewHostBaseHealthy, VaultStaged, VaultVerified, VaultPointerSwitched, NewHealthPassed, Committed, RolledBack, RecoveryRequired }
public enum UpdateOutcome { ReadyForHandoff, Committed, RolledBack, WaitingForWork, DataRootUnavailable, FailedSecurely, RecoveryRequired }

public sealed record VerifiedArtifact(string Version, string AbsolutePath, string Sha256, string TrustEvidence, string CapabilityBinding);
public sealed record PairedUpdatePlan(
    UpdateTransactionId TransactionId,
    VerifiedArtifact Application,
    string OldApplicationVersion,
    ulong OldVaultGeneration,
    ulong NewVaultGeneration,
    int NewVaultSchema,
    string PlanHash,
    DateTimeOffset CreatedUtc);

public sealed record UpdateJournalEntry(
    PairedUpdatePlan Plan,
    UpdatePhase Phase,
    ulong Revision,
    string HandoffTokenDigest,
    string? HealthEvidenceDigest,
    DateTimeOffset UpdatedUtc,
    string? DiagnosticCode = null);

public sealed record UpdateResult(UpdateOutcome Outcome, UpdateJournalEntry? Journal, string? DiagnosticCode = null);

public interface IManagedUpdateCoordinator
{
    ValueTask<UpdateResult> PrepareAsync(PairedUpdatePlan plan, ReadOnlyMemory<byte> oneTimeToken, CancellationToken cancellationToken = default);
    ValueTask<UpdateResult> CommitAsync(UpdateTransactionId transactionId, ReadOnlyMemory<byte> oneTimeToken, CancellationToken cancellationToken = default);
    ValueTask<UpdateResult> RecoverAsync(UpdateTransactionId transactionId, CancellationToken cancellationToken = default);
}
