using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RcloneUI.Transfers;

public sealed class TransferOrchestrator(ITransferExecutionAdapter adapter, ITransferRunJournal journal) : ITransferOrchestrator
{
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<AcceptedPreviewId, TransferPreview> previews = new();
    private readonly ConcurrentDictionary<TransferRunId, ActiveRun> active = new();

    public async ValueTask<PreviewOutcome> PreviewAsync(TransferTaskRevision task, CancellationToken cancellationToken = default)
    {
        if (task.Source.WriteOverlapKey == task.Target.WriteOverlapKey)
            return new(null, TransferFailureClass.Configuration, "source-target-overlap");
        if (task.SafetyRetention is { } retention && retention <= TimeSpan.Zero)
            return new(null, TransferFailureClass.Configuration, "safety-retention-invalid");
        var result = await adapter.PreviewAsync(task, cancellationToken).ConfigureAwait(false);
        if (result.Failure != TransferFailureClass.None) return new(null, result.Failure, result.DiagnosticCode);
        var created = DateTimeOffset.UtcNow;
        var preview = new TransferPreview(
            AcceptedPreviewId.New(), task, created, created + PreviewLifetime, result.Counts, result.Paths,
            HashManifest(task, result.Paths),
            task.Operation is TransferOperation.Move or TransferOperation.MirrorSync || task.DeleteExcluded || task.HardCutoff);
        previews[preview.Id] = preview;
        return new(preview, TransferFailureClass.None, null);
    }

    public async ValueTask<TransferRunSnapshot> ExecuteAsync(ExecuteAcceptedPreviewRequest request, CancellationToken cancellationToken = default)
    {
        if (!previews.TryGetValue(request.PreviewId, out var preview)) return NotExecuted(request.PreviewId, "preview-not-found");
        if (preview.ExpiresUtc < DateTimeOffset.UtcNow) return NotExecuted(request.PreviewId, "preview-expired");
        if (!StringComparer.Ordinal.Equals(preview.Task.CapabilityBinding, request.ExpectedCapabilityBinding)) return NotExecuted(request.PreviewId, "capability-binding-changed");
        if (preview.RequiresTypedTargetConfirmation && !StringComparer.Ordinal.Equals(TargetName(preview.Task.Target.CanonicalEndpoint), request.TypedTargetRemoteName))
            return NotExecuted(request.PreviewId, "target-confirmation-mismatch");
        if (!previews.TryRemove(request.PreviewId, out _)) return NotExecuted(request.PreviewId, "preview-already-used");

        var id = TransferRunId.New();
        var run = new ActiveRun(id, preview, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        if (!active.TryAdd(id, run)) throw new InvalidOperationException("Could not create the Transfer Run.");
        try
        {
            return await ExecuteRunAsync(run).ConfigureAwait(false);
        }
        finally
        {
            active.TryRemove(id, out _);
            run.Cancellation.Dispose();
        }
    }

    public ValueTask<TransferRunSnapshot?> ObserveAsync(TransferRunId runId, CancellationToken cancellationToken = default) => journal.ReadAsync(runId, cancellationToken);

    public async ValueTask CancelAsync(TransferRunId runId, CancellationToken cancellationToken = default)
    {
        if (!active.TryGetValue(runId, out var run)) return;
        run.Cancellation.Cancel(); // The deletion admission barrier closes before rclone cancellation is requested.
        await adapter.CancelAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TransferRunSnapshot> ExecuteRunAsync(ActiveRun run)
    {
        var evidence = ImmutableArray.CreateBuilder<TransferExecutionEvidence>();
        var retryRound = 0;
        using var cancellationRegistration = run.Cancellation.Token.Register(() => _ = adapter.CancelAsync(run.Id, CancellationToken.None).AsTask());
        try
        {
            if (run.Preview.Counts.Replace > 0 || run.Preview.Counts.Delete > 0)
            {
                var (safety, safetyRetryRound) = await RetryAsync(run, TransferPhase.SafetyCopying, token => adapter.PrepareSafetyCopiesAsync(run.Preview, token), evidence, retryRound).ConfigureAwait(false);
                retryRound = safetyRetryRound;
                if (!safety.Success) return await FinishAsync(run, TerminalFor(safety), TransferPhase.Completed, evidence, retryRound).ConfigureAwait(false);
            }
            var (copy, copyRetryRound) = await RetryAsync(run, TransferPhase.Copying, token => adapter.CopyAsync(run.Preview, token), evidence, retryRound).ConfigureAwait(false);
            retryRound = copyRetryRound;
            if (!copy.Success) return await FinishAsync(run, TerminalFor(copy), TransferPhase.Completed, evidence, retryRound).ConfigureAwait(false);
            var (verify, verifyRetryRound) = await RetryAsync(run, TransferPhase.Verifying, token => adapter.VerifyAsync(run.Preview, token), evidence, retryRound).ConfigureAwait(false);
            retryRound = verifyRetryRound;
            if (!verify.Success) return await FinishAsync(run, TerminalFor(verify), TransferPhase.Completed, evidence, retryRound).ConfigureAwait(false);

            run.Cancellation.Token.ThrowIfCancellationRequested();
            if (run.Preview.Task.Operation == TransferOperation.Move)
            {
                var (deleteSources, deleteSourcesRetryRound) = await RetryAsync(run, TransferPhase.DeletingSources, token => adapter.DeleteVerifiedSourcesAsync(run.Preview, token), evidence, retryRound).ConfigureAwait(false);
                retryRound = deleteSourcesRetryRound;
                if (!deleteSources.Success) return await FinishAsync(run, TerminalFor(deleteSources), TransferPhase.Completed, evidence, retryRound).ConfigureAwait(false);
            }
            else if (run.Preview.Task.Operation == TransferOperation.MirrorSync)
            {
                var (deleteTargets, deleteTargetsRetryRound) = await RetryAsync(run, TransferPhase.DeletingTargets, token => adapter.DeleteApprovedTargetsAsync(run.Preview, token), evidence, retryRound).ConfigureAwait(false);
                retryRound = deleteTargetsRetryRound;
                if (!deleteTargets.Success) return await FinishAsync(run, TerminalFor(deleteTargets), TransferPhase.Completed, evidence, retryRound).ConfigureAwait(false);
            }

            var terminal = evidence.Any(item => item.Outcome == TransferPathOutcome.Conflict) ? TransferTerminalResult.CompletedWithConflicts : TransferTerminalResult.Succeeded;
            return await FinishAsync(run, terminal, TransferPhase.Completed, evidence, retryRound).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            evidence.Add(new(string.Empty, TransferPathOutcome.PossiblyAffected, "cancelled"));
            return await FinishAsync(run, TransferTerminalResult.CancelledWithPartialResults, TransferPhase.Completed, evidence, retryRound).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            evidence.Add(new(string.Empty, TransferPathOutcome.Failed, Redact(exception)));
            return await FinishAsync(run, TransferTerminalResult.Failed, TransferPhase.Completed, evidence, retryRound).ConfigureAwait(false);
        }
    }

    private async Task<(AdapterPhaseResult Result, int RetryRound)> RetryAsync(ActiveRun run, TransferPhase phase, Func<CancellationToken, ValueTask<AdapterPhaseResult>> action, ImmutableArray<TransferExecutionEvidence>.Builder evidence, int retryRound)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await CheckpointAsync(run, phase, null, evidence, retryRound).ConfigureAwait(false);
            var result = await action(run.Cancellation.Token).ConfigureAwait(false);
            evidence.AddRange(result.Evidence);
            if (result.Success || result.Failure != TransferFailureClass.Transient || attempt == 3) return (result, retryRound);
            retryRound++;
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), run.Cancellation.Token).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Retry loop terminated unexpectedly.");
    }

    private async ValueTask<TransferRunSnapshot> FinishAsync(ActiveRun run, TransferTerminalResult result, TransferPhase phase, ImmutableArray<TransferExecutionEvidence>.Builder evidence, int retryRound)
    {
        var snapshot = new TransferRunSnapshot(run.Id, run.Preview.Id, phase, result, evidence.ToImmutable(), retryRound, DateTimeOffset.UtcNow);
        await journal.SaveAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        return snapshot;
    }

    private async ValueTask CheckpointAsync(ActiveRun run, TransferPhase phase, TransferTerminalResult? terminal, ImmutableArray<TransferExecutionEvidence>.Builder evidence, int retryRound)
    {
        var snapshot = new TransferRunSnapshot(run.Id, run.Preview.Id, phase, terminal, evidence.ToImmutable(), retryRound, DateTimeOffset.UtcNow);
        await journal.SaveAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
    }

    private static TransferRunSnapshot NotExecuted(AcceptedPreviewId previewId, string code) =>
        new(TransferRunId.New(), previewId, TransferPhase.Completed, TransferTerminalResult.NotExecuted, [new(string.Empty, TransferPathOutcome.Failed, code)], 0, DateTimeOffset.UtcNow);

    private static string TargetName(string endpoint) => endpoint.Split(':', 2)[0].Trim();
    private static TransferTerminalResult TerminalFor(AdapterPhaseResult result) => result.Failure == TransferFailureClass.LimitReached ? TransferTerminalResult.StoppedByLimit : TransferTerminalResult.Failed;
    private static string Redact(Exception exception) => exception.GetType().Name.ToLowerInvariant();

    private static string HashManifest(TransferTaskRevision task, ImmutableArray<PreviewPath> paths)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { task.Id, task.Revision, task.CapabilityBinding, task.FilterBinding, Paths = paths.OrderBy(path => path.RelativePath, StringComparer.Ordinal) });
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private sealed record ActiveRun(TransferRunId Id, TransferPreview Preview, CancellationTokenSource Cancellation);
}
