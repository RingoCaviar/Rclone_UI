using System.Collections.Immutable;

namespace RcloneUI.Transfers;

public sealed record AdapterPreviewResult(PreviewCounts Counts, ImmutableArray<PreviewPath> Paths, TransferFailureClass Failure, string? DiagnosticCode, PreviewEvidenceDecision EvidenceDecision);
public sealed record AdapterPhaseResult(bool Success, TransferFailureClass Failure, ImmutableArray<TransferExecutionEvidence> Evidence);

public interface ITransferExecutionAdapter
{
    ValueTask<AdapterPreviewResult> PreviewAsync(TransferTaskRevision task, CancellationToken cancellationToken);
    ValueTask<AdapterPhaseResult> PrepareSafetyCopiesAsync(TransferPreview preview, CancellationToken cancellationToken);
    ValueTask<AdapterPhaseResult> CopyAsync(TransferPreview preview, CancellationToken cancellationToken);
    ValueTask<AdapterPhaseResult> VerifyAsync(TransferPreview preview, CancellationToken cancellationToken);
    ValueTask<AdapterPhaseResult> DeleteVerifiedSourcesAsync(TransferPreview preview, CancellationToken cancellationToken);
    ValueTask<AdapterPhaseResult> DeleteApprovedTargetsAsync(TransferPreview preview, CancellationToken cancellationToken);
    ValueTask CancelAsync(TransferRunId runId, CancellationToken cancellationToken);
}

public interface ITransferRunJournal
{
    ValueTask SaveAsync(TransferRunSnapshot snapshot, CancellationToken cancellationToken);
    ValueTask<TransferRunSnapshot?> ReadAsync(TransferRunId runId, CancellationToken cancellationToken);
}
