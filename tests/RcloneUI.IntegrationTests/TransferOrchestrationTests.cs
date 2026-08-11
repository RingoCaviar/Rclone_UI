using System.Collections.Immutable;
using RcloneUI.Transfers;
using RcloneUI.Work;

namespace RcloneUI.IntegrationTests;

public sealed class TransferOrchestrationTests
{
    [Fact]
    public async Task MoveUsesCopyVerifyThenDeleteSource()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var adapter = new ScriptedAdapter();
        var journal = new MemoryJournal();
        var orchestrator = new TransferOrchestrator(adapter, journal);
        var preview = Assert.IsType<TransferPreview>((await orchestrator.PreviewAsync(CreateTask(TransferOperation.Move), cancellationToken)).Preview);

        var result = await orchestrator.ExecuteAsync(new(preview.Id, "target", preview.Task.CapabilityBinding), cancellationToken);

        Assert.Equal(TransferTerminalResult.Succeeded, result.TerminalResult);
        Assert.Equal(["preview", "copy", "verify", "delete-source"], adapter.Calls);
        Assert.NotNull(await orchestrator.ObserveAsync(result.RunId, cancellationToken));
    }

    [Fact]
    public async Task CancellationPreventsMoveSourceDeletion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var adapter = new BlockingAdapter();
        var orchestrator = new TransferOrchestrator(adapter, new MemoryJournal());
        var preview = Assert.IsType<TransferPreview>((await orchestrator.PreviewAsync(CreateTask(TransferOperation.Move), cancellationToken)).Preview);
        using var stop = new CancellationTokenSource();
        var execution = orchestrator.ExecuteAsync(new(preview.Id, "target", preview.Task.CapabilityBinding), stop.Token).AsTask();
        await adapter.CopyStarted.Task.WaitAsync(cancellationToken);

        stop.Cancel();
        var result = await execution;

        Assert.Equal(TransferTerminalResult.CancelledWithPartialResults, result.TerminalResult);
        Assert.DoesNotContain("delete-source", adapter.Calls);
    }

    [Fact]
    public async Task MirrorPreparesSafetyCopiesBeforeTargetDeletion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var adapter = new ScriptedAdapter();
        var orchestrator = new TransferOrchestrator(adapter, new MemoryJournal());
        var preview = Assert.IsType<TransferPreview>((await orchestrator.PreviewAsync(CreateTask(TransferOperation.MirrorSync), cancellationToken)).Preview);

        var result = await orchestrator.ExecuteAsync(new(preview.Id, "target", preview.Task.CapabilityBinding), cancellationToken);

        Assert.Equal(TransferTerminalResult.Succeeded, result.TerminalResult);
        Assert.Equal(["preview", "safety", "copy", "verify", "delete-target"], adapter.Calls);
    }

    [Fact]
    public async Task CapabilityChangeInvalidatesAcceptedPreview()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var adapter = new ScriptedAdapter();
        var orchestrator = new TransferOrchestrator(adapter, new MemoryJournal());
        var preview = Assert.IsType<TransferPreview>((await orchestrator.PreviewAsync(CreateTask(TransferOperation.Move), cancellationToken)).Preview);

        var result = await orchestrator.ExecuteAsync(new(preview.Id, "target", "cap-v2"), cancellationToken);

        Assert.Equal(TransferTerminalResult.NotExecuted, result.TerminalResult);
        Assert.Contains(result.Evidence, evidence => evidence.Detail == "capability-binding-changed");
        Assert.Equal(["preview"], adapter.Calls);
    }

    [Fact]
    public async Task WorkCoordinatorSerializesOverlappingWriteTargets()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var preview = CreatePreview();
        var transfers = new DelayedOrchestrator();
        await using var coordinator = new WorkCoordinator(transfers, maximumConcurrency: 2);
        var first = await coordinator.EnqueueAsync(new(preview, new(preview.Id, "target", preview.Task.CapabilityBinding)), cancellationToken);
        var second = await coordinator.EnqueueAsync(new(preview with { Id = AcceptedPreviewId.New() }, new(AcceptedPreviewId.New(), "target", preview.Task.CapabilityBinding)), cancellationToken);

        await transfers.FirstStarted.Task.WaitAsync(cancellationToken);
        Assert.Equal(1, transfers.Executions);
        transfers.AllowFirst.SetResult();
        await WaitForAsync(() => coordinator.Observe().All(item => item.State == WorkItemState.Completed), cancellationToken);

        Assert.Equal(2, transfers.Executions);
        Assert.Equal(WorkItemState.Completed, coordinator.Observe().Single(item => item.Id == first.Id).State);
        Assert.Equal(WorkItemState.Completed, coordinator.Observe().Single(item => item.Id == second.Id).State);
    }

    [Fact]
    public async Task ScheduleCoalescesMissedOccurrencesIntoOneCatchUpRun()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var preview = CreatePreview();
        var transfers = new DelayedOrchestrator();
        await using var coordinator = new WorkCoordinator(transfers);
        var now = DateTimeOffset.UtcNow;
        coordinator.RegisterSchedule(new(WorkScheduleId.New(), new(preview, new(preview.Id, "target", preview.Task.CapabilityBinding)), TimeSpan.FromHours(1), now.AddHours(-3), true));

        await coordinator.EvaluateSchedulesAsync(now, networkAvailable: true, cancellationToken);
        await transfers.FirstStarted.Task.WaitAsync(cancellationToken);

        Assert.Equal(1, transfers.Executions);
        Assert.Equal("enqueued", Assert.Single(coordinator.ObserveSchedules()).LastOutcome);
        transfers.AllowFirst.SetResult();
    }

    private static TransferTaskRevision CreateTask(TransferOperation operation) => new(TransferTaskId.New(), 1, operation, new("source:", "folder"), new("target:", "folder"), TransferConflictPolicy.PreserveNewerTarget, VerificationStrength.Hash, false, false, "cap-v1", "filters-v1");
    private static TransferPreview CreatePreview()
    {
        var task = CreateTask(TransferOperation.Copy);
        return new(AcceptedPreviewId.New(), task, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), new(1, 0, 0, 0, 0, 0, 1), [], "manifest", false);
    }

    private static async Task WaitForAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate()) await Task.Delay(10, cancellationToken);
    }

    private class ScriptedAdapter : ITransferExecutionAdapter
    {
        internal List<string> Calls { get; } = [];
        public ValueTask<AdapterPreviewResult> PreviewAsync(TransferTaskRevision task, CancellationToken cancellationToken) { Calls.Add("preview"); return ValueTask.FromResult(new AdapterPreviewResult(new(1, 0, task.Operation == TransferOperation.MirrorSync ? 1 : 0, 0, 0, 0, 1), [new("a", TransferPathOutcome.Copied, 1)], TransferFailureClass.None, null, RclonePreviewEvidencePolicy.Evaluate(task.Operation, new(true, true, true)))); }
        public ValueTask<AdapterPhaseResult> PrepareSafetyCopiesAsync(TransferPreview preview, CancellationToken cancellationToken) => Result("safety", TransferPathOutcome.Skipped);
        public virtual ValueTask<AdapterPhaseResult> CopyAsync(TransferPreview preview, CancellationToken cancellationToken) => Result("copy", TransferPathOutcome.Copied);
        public ValueTask<AdapterPhaseResult> VerifyAsync(TransferPreview preview, CancellationToken cancellationToken) => Result("verify", TransferPathOutcome.Verified);
        public ValueTask<AdapterPhaseResult> DeleteVerifiedSourcesAsync(TransferPreview preview, CancellationToken cancellationToken) => Result("delete-source", TransferPathOutcome.SourceDeleted);
        public ValueTask<AdapterPhaseResult> DeleteApprovedTargetsAsync(TransferPreview preview, CancellationToken cancellationToken) => Result("delete-target", TransferPathOutcome.TargetDeleted);
        public ValueTask CancelAsync(TransferRunId runId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        protected ValueTask<AdapterPhaseResult> Result(string call, TransferPathOutcome outcome) { Calls.Add(call); return ValueTask.FromResult(new AdapterPhaseResult(true, TransferFailureClass.None, [new("a", outcome, null)])); }
    }

    private sealed class BlockingAdapter : ScriptedAdapter
    {
        internal TaskCompletionSource CopyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<AdapterPhaseResult> CopyAsync(TransferPreview preview, CancellationToken cancellationToken)
        {
            Calls.Add("copy");
            CopyStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation did not interrupt the adapter.");
        }
    }

    private sealed class MemoryJournal : ITransferRunJournal
    {
        private readonly Dictionary<TransferRunId, TransferRunSnapshot> snapshots = [];
        public ValueTask SaveAsync(TransferRunSnapshot snapshot, CancellationToken cancellationToken) { snapshots[snapshot.RunId] = snapshot; return ValueTask.CompletedTask; }
        public ValueTask<TransferRunSnapshot?> ReadAsync(TransferRunId runId, CancellationToken cancellationToken) => ValueTask.FromResult(snapshots.GetValueOrDefault(runId));
    }

    private sealed class DelayedOrchestrator : ITransferOrchestrator
    {
        internal int Executions { get; private set; }
        internal TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<PreviewOutcome> PreviewAsync(TransferTaskRevision task, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async ValueTask<TransferRunSnapshot> ExecuteAsync(ExecuteAcceptedPreviewRequest request, CancellationToken cancellationToken = default)
        {
            Executions++;
            if (Executions == 1) { FirstStarted.SetResult(); await AllowFirst.Task.WaitAsync(cancellationToken); }
            return new(TransferRunId.New(), request.PreviewId, TransferPhase.Completed, TransferTerminalResult.Succeeded, [], 0, DateTimeOffset.UtcNow);
        }
        public ValueTask<TransferRunSnapshot?> ObserveAsync(TransferRunId runId, CancellationToken cancellationToken = default) => ValueTask.FromResult<TransferRunSnapshot?>(null);
        public ValueTask CancelAsync(TransferRunId runId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
