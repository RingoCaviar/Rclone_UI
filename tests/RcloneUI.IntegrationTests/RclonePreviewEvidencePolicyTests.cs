using RcloneUI.Transfers;

namespace RcloneUI.IntegrationTests;

public sealed class RclonePreviewEvidencePolicyTests
{
    [Theory]
    [InlineData(TransferOperation.Copy)]
    [InlineData(TransferOperation.Move)]
    [InlineData(TransferOperation.MirrorSync)]
    public void CompleteTriangulatedEvidenceCanCreateAcceptedPreview(TransferOperation operation)
    {
        var decision = RclonePreviewEvidencePolicy.Evaluate(operation, Complete());
        Assert.True(decision.CanCreateAcceptedPreview);
        Assert.Equal([PreviewEvidenceKind.DryRunLogger, PreviewEvidenceKind.IndependentListings, PreviewEvidenceKind.RcloneCheck], decision.Evidence);
    }

    [Theory]
    [MemberData(nameof(IncompleteCases))]
    public void KnownLoggerLimitationsFailClosed(RclonePreviewConditions conditions, string code)
    {
        var decision = RclonePreviewEvidencePolicy.Evaluate(TransferOperation.MirrorSync, conditions);
        Assert.False(decision.CanCreateAcceptedPreview);
        Assert.Empty(decision.Evidence);
        Assert.Equal(code, decision.DiagnosticCode);
    }

    public static TheoryData<RclonePreviewConditions, string> IncompleteCases => new()
    {
        { Complete() with { HardCutoff = true }, "preview-hard-cutoff-incomplete" },
        { Complete() with { HighLevelRetries = 2 }, "preview-retries-duplicate-logger-records" },
        { Complete() with { CompareDest = true }, "preview-compare-dest-incomplete" },
        { Complete() with { CopyDest = true }, "preview-compare-dest-incomplete" },
        { Complete() with { ServerSideDirectoryMove = true }, "preview-server-side-directory-move-incomplete" },
        { Complete() with { LoggerReportedErrors = true }, "preview-logger-error" },
        { Complete() with { CombinedLoggerComplete = false }, "preview-combined-logger-missing" },
        { Complete() with { IndependentListingsComplete = false, NoTraverse = true }, "preview-destination-listing-required" },
        { Complete() with { CheckComplete = false }, "preview-check-required" },
    };

    private static RclonePreviewConditions Complete() => new(true, true, true);
}
