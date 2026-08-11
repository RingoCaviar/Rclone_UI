using System.Collections.Immutable;

namespace RcloneUI.Transfers;

public enum PreviewEvidenceKind { DryRunLogger, IndependentListings, RcloneCheck }
public enum PreviewEvidenceDisposition { Complete, Blocked }

public sealed record RclonePreviewConditions(
    bool CombinedLoggerComplete,
    bool IndependentListingsComplete,
    bool CheckComplete,
    bool NoTraverse = false,
    bool HardCutoff = false,
    int HighLevelRetries = 1,
    bool CompareDest = false,
    bool CopyDest = false,
    bool ServerSideDirectoryMove = false,
    bool LoggerReportedErrors = false);

public sealed record PreviewEvidenceDecision(
    PreviewEvidenceDisposition Disposition,
    ImmutableArray<PreviewEvidenceKind> Evidence,
    string? DiagnosticCode)
{
    public bool CanCreateAcceptedPreview => Disposition == PreviewEvidenceDisposition.Complete;
}

public static class RclonePreviewEvidencePolicy
{
    public static PreviewEvidenceDecision Evaluate(TransferOperation operation, RclonePreviewConditions conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        if (conditions.HardCutoff) return Block("preview-hard-cutoff-incomplete");
        if (conditions.HighLevelRetries != 1) return Block("preview-retries-duplicate-logger-records");
        if (conditions.CompareDest || conditions.CopyDest) return Block("preview-compare-dest-incomplete");
        if (conditions.ServerSideDirectoryMove) return Block("preview-server-side-directory-move-incomplete");
        if (conditions.LoggerReportedErrors) return Block("preview-logger-error");
        if (!conditions.CombinedLoggerComplete) return Block("preview-combined-logger-missing");
        if (!conditions.IndependentListingsComplete) return Block(operation == TransferOperation.MirrorSync || conditions.NoTraverse ? "preview-destination-listing-required" : "preview-independent-listing-required");
        if (!conditions.CheckComplete) return Block("preview-check-required");
        return new(PreviewEvidenceDisposition.Complete, [PreviewEvidenceKind.DryRunLogger, PreviewEvidenceKind.IndependentListings, PreviewEvidenceKind.RcloneCheck], null);
    }

    private static PreviewEvidenceDecision Block(string code) => new(PreviewEvidenceDisposition.Blocked, [], code);
}
