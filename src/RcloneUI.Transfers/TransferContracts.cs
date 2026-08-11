using System.Collections.Immutable;

namespace RcloneUI.Transfers;

public readonly record struct TransferTaskId(Guid Value) { public static TransferTaskId New() => new(Guid.NewGuid()); }
public readonly record struct AcceptedPreviewId(Guid Value) { public static AcceptedPreviewId New() => new(Guid.NewGuid()); }
public readonly record struct TransferRunId(Guid Value) { public static TransferRunId New() => new(Guid.NewGuid()); }

public enum TransferOperation { Copy, Move, MirrorSync }
public enum VerificationStrength { Basic, Hash, HighAssurance }
public enum TransferPhase { Pending, SafetyCopying, Copying, Verifying, DeletingSources, DeletingTargets, Cancelling, Completed }
public enum TransferTerminalResult { Succeeded, CompletedWithConflicts, Failed, CancelledWithPartialResults, InterruptedBySystemOrCrash, NotExecuted, StoppedByLimit }
public enum TransferConflictPolicy { PreserveNewerTarget, SourceAlwaysWins, SkipExisting, StopOnConflict }
public enum TransferPathOutcome { Copied, Verified, SourceDeleted, TargetDeleted, Skipped, Conflict, Failed, PossiblyAffected }
public enum TransferFailureClass { None, Transient, Authentication, Permission, Quota, InvalidName, Configuration, Verification, LimitReached, Unknown }

public sealed record TransferLocation(string CanonicalEndpoint, string Path)
{
    public string WriteOverlapKey => $"{CanonicalEndpoint.TrimEnd('/')}|{Path.Trim('/')}";
}

public sealed record TransferTaskRevision(
    TransferTaskId Id,
    ulong Revision,
    TransferOperation Operation,
    TransferLocation Source,
    TransferLocation Target,
    TransferConflictPolicy ConflictPolicy,
    VerificationStrength Verification,
    bool DeleteExcluded,
    bool HardCutoff,
    string CapabilityBinding,
    string FilterBinding,
    long? MaximumTransferBytes = null,
    TimeSpan? MaximumDuration = null,
    long? BandwidthBytesPerSecond = null,
    TimeSpan? SafetyRetention = null);

public sealed record PreviewCounts(long Copy, long Replace, long Delete, long Skip, long Conflict, long FilterCausedDelete, long Bytes);
public sealed record PreviewPath(string RelativePath, TransferPathOutcome PlannedOutcome, long? Size);
public sealed record TransferPreview(
    AcceptedPreviewId Id,
    TransferTaskRevision Task,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc,
    PreviewCounts Counts,
    ImmutableArray<PreviewPath> Paths,
    string ManifestHash,
    bool RequiresTypedTargetConfirmation);

public sealed record PreviewOutcome(TransferPreview? Preview, TransferFailureClass Failure, string? DiagnosticCode)
{
    public bool IsAccepted => Preview is not null;
}

public sealed record TransferExecutionEvidence(string RelativePath, TransferPathOutcome Outcome, string? Detail);
public sealed record DeletionCandidate(string RelativePath, long? AcceptedSize);
public sealed record DeletionAdmission(TransferPreview Preview, ImmutableArray<DeletionCandidate> Paths, bool RequiresSourceRevalidation);
public sealed record TransferRunSnapshot(
    TransferRunId RunId,
    AcceptedPreviewId PreviewId,
    TransferPhase Phase,
    TransferTerminalResult? TerminalResult,
    ImmutableArray<TransferExecutionEvidence> Evidence,
    int RetryRound,
    DateTimeOffset UpdatedUtc);

public sealed record ExecuteAcceptedPreviewRequest(AcceptedPreviewId PreviewId, string TypedTargetRemoteName, string ExpectedCapabilityBinding);

public interface ITransferOrchestrator
{
    ValueTask<PreviewOutcome> PreviewAsync(TransferTaskRevision task, CancellationToken cancellationToken = default);
    ValueTask<TransferRunSnapshot> ExecuteAsync(ExecuteAcceptedPreviewRequest request, CancellationToken cancellationToken = default);
    ValueTask<TransferRunSnapshot?> ObserveAsync(TransferRunId runId, CancellationToken cancellationToken = default);
    ValueTask CancelAsync(TransferRunId runId, CancellationToken cancellationToken = default);
    ValueTask<ImmutableArray<TransferRunSnapshot>> RecoverInterruptedAsync(CancellationToken cancellationToken = default);
}
