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
    public async Task DeletionAdmissionContainsOnlyVerifiedEligibleCleanPaths()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var adapter = new MixedEvidenceAdapter();
        var orchestrator = new TransferOrchestrator(adapter, new MemoryJournal());
        var preview = Assert.IsType<TransferPreview>((await orchestrator.PreviewAsync(CreateTask(TransferOperation.Move), cancellationToken)).Preview);

        var result = await orchestrator.ExecuteAsync(new(preview.Id, "target", preview.Task.CapabilityBinding), cancellationToken);

        Assert.Equal(TransferTerminalResult.Succeeded, result.TerminalResult);
        var admission = Assert.IsType<DeletionAdmission>(adapter.LastDeletionAdmission);
        Assert.Equal(["clean"], admission.Paths.Select(path => path.RelativePath));
        Assert.True(admission.RequiresSourceRevalidation);
    }

    [Theory]
    [InlineData(TransferOperation.Move, TransferFailureClass.Quota)]
    [InlineData(TransferOperation.Move, TransferFailureClass.Verification)]
    [InlineData(TransferOperation.Move, TransferFailureClass.Configuration)]
    [InlineData(TransferOperation.MirrorSync, TransferFailureClass.Quota)]
    [InlineData(TransferOperation.MirrorSync, TransferFailureClass.Verification)]
    [InlineData(TransferOperation.MirrorSync, TransferFailureClass.Configuration)]
    public async Task CopyOrVerificationFailureNeverReachesDeletion(TransferOperation operation, TransferFailureClass failure)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var adapter = new FailingAdapter(failure);
        var orchestrator = new TransferOrchestrator(adapter, new MemoryJournal());
        var preview = Assert.IsType<TransferPreview>((await orchestrator.PreviewAsync(CreateTask(operation), cancellationToken)).Preview);

        var result = await orchestrator.ExecuteAsync(new(preview.Id, "target", preview.Task.CapabilityBinding), cancellationToken);

        Assert.Equal(TransferTerminalResult.Failed, result.TerminalResult);
        Assert.Null(adapter.LastDeletionAdmission);
    }

    [Fact]
    public async Task DurableManifestIsNotTruncatedToRcloneTransferredWindow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var adapter = new LargeManifestAdapter(250);
        var orchestrator = new TransferOrchestrator(adapter, new MemoryJournal());
        var preview = Assert.IsType<TransferPreview>((await orchestrator.PreviewAsync(CreateTask(TransferOperation.Move), cancellationToken)).Preview);

        var result = await orchestrator.ExecuteAsync(new(preview.Id, "target", preview.Task.CapabilityBinding), cancellationToken);

        Assert.Equal(250, result.Evidence.Count(item => item.Outcome == TransferPathOutcome.Copied));
        Assert.Equal(250, result.Evidence.Count(item => item.Outcome == TransferPathOutcome.Verified));
        Assert.Equal(250, Assert.IsType<DeletionAdmission>(adapter.LastDeletionAdmission).Paths.Length);
    }

    [Fact]
    public async Task RestartMarksEveryIncompleteRunInterruptedWithoutDiscardingManifest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var journal = new MemoryJournal();
        var runId = TransferRunId.New();
        var evidence = Enumerable.Range(0, 250).Select(index => new TransferExecutionEvidence($"path-{index:D3}", TransferPathOutcome.Copied, null)).ToImmutableArray();
        await journal.SaveAsync(new(runId, AcceptedPreviewId.New(), TransferPhase.Copying, null, evidence, 0, DateTimeOffset.UtcNow), cancellationToken);
        var orchestrator = new TransferOrchestrator(new ScriptedAdapter(), journal);

        var recovered = Assert.Single(await orchestrator.RecoverInterruptedAsync(cancellationToken));

        Assert.Equal(TransferTerminalResult.InterruptedBySystemOrCrash, recovered.TerminalResult);
        Assert.Equal(250, recovered.Evidence.Count(item => item.Outcome == TransferPathOutcome.Copied));
        Assert.Contains(recovered.Evidence, item => item.Detail == "interrupted-by-system-or-crash");
        Assert.Empty(await orchestrator.RecoverInterruptedAsync(cancellationToken));
    }

    [Theory]
    [InlineData(TransferOperation.MirrorSync, "safety")]
    [InlineData(TransferOperation.Move, "copy")]
    [InlineData(TransferOperation.Move, "verify")]
    [InlineData(TransferOperation.Move, "delete-source")]
    [InlineData(TransferOperation.MirrorSync, "delete-target")]
    public async Task CancellationAtEveryPhaseIsTruthfulAndRequestsCooperativeStop(TransferOperation operation, string phase)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var adapter = new PhaseBlockingAdapter(phase);
        var orchestrator = new TransferOrchestrator(adapter, new MemoryJournal());
        var preview = Assert.IsType<TransferPreview>((await orchestrator.PreviewAsync(CreateTask(operation), cancellationToken)).Preview);
        using var stop = new CancellationTokenSource();
        var execution = orchestrator.ExecuteAsync(new(preview.Id, "target", preview.Task.CapabilityBinding), stop.Token).AsTask();
        await adapter.Started.Task.WaitAsync(cancellationToken);

        stop.Cancel();
        var result = await execution;

        Assert.Equal(TransferTerminalResult.CancelledWithPartialResults, result.TerminalResult);
        Assert.Contains(result.Evidence, item => item.Outcome == TransferPathOutcome.PossiblyAffected && item.Detail == "cancelled");
        Assert.True(adapter.CancelRequests > 0);
    }

    [Fact]
    public async Task EventualConsistencyMayRetryVerificationButDeletesOnlyAfterSuccess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var adapter = new ConsistencyDelayAdapter(succeedOnAttempt: 3);
        var orchestrator = new TransferOrchestrator(adapter, new MemoryJournal());
        var preview = Assert.IsType<TransferPreview>((await orchestrator.PreviewAsync(CreateTask(TransferOperation.Move), cancellationToken)).Preview);

        var result = await orchestrator.ExecuteAsync(new(preview.Id, "target", preview.Task.CapabilityBinding), cancellationToken);

        Assert.Equal(TransferTerminalResult.Succeeded, result.TerminalResult);
        Assert.Equal(3, adapter.VerifyAttempts);
        Assert.Equal(2, result.RetryRound);
        Assert.NotNull(adapter.LastDeletionAdmission);
    }

    [Fact]
    public async Task ExhaustedConsistencyDelayNeverDeletes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var adapter = new ConsistencyDelayAdapter(succeedOnAttempt: int.MaxValue);
        var orchestrator = new TransferOrchestrator(adapter, new MemoryJournal());
        var preview = Assert.IsType<TransferPreview>((await orchestrator.PreviewAsync(CreateTask(TransferOperation.Move), cancellationToken)).Preview);

        var result = await orchestrator.ExecuteAsync(new(preview.Id, "target", preview.Task.CapabilityBinding), cancellationToken);

        Assert.Equal(TransferTerminalResult.Failed, result.TerminalResult);
        Assert.Equal(3, adapter.VerifyAttempts);
        Assert.Null(adapter.LastDeletionAdmission);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NonAtomicSafetyCopyFallbackMustCompleteBeforeMirrorDeletion(bool fallbackSucceeds)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var adapter = new SafetyFallbackAdapter(fallbackSucceeds);
        var orchestrator = new TransferOrchestrator(adapter, new MemoryJournal());
        var preview = Assert.IsType<TransferPreview>((await orchestrator.PreviewAsync(CreateTask(TransferOperation.MirrorSync), cancellationToken)).Preview);

        var result = await orchestrator.ExecuteAsync(new(preview.Id, "target", preview.Task.CapabilityBinding), cancellationToken);

        Assert.Equal(fallbackSucceeds ? TransferTerminalResult.Succeeded : TransferTerminalResult.Failed, result.TerminalResult);
        Assert.Equal(fallbackSucceeds, adapter.LastDeletionAdmission is not null);
        Assert.Contains(result.Evidence, item => item.Detail == "non-atomic-copy-fallback");
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
        internal DeletionAdmission? LastDeletionAdmission { get; private set; }
        public virtual ValueTask<AdapterPreviewResult> PreviewAsync(TransferTaskRevision task, CancellationToken cancellationToken)
        {
            Calls.Add("preview");
            ImmutableArray<PreviewPath> paths = task.Operation == TransferOperation.MirrorSync
                ? [new("a", TransferPathOutcome.Copied, 1), new("obsolete", TransferPathOutcome.TargetDeleted, 1)]
                : [new("a", TransferPathOutcome.Copied, 1)];
            return ValueTask.FromResult(new AdapterPreviewResult(new(1, 0, task.Operation == TransferOperation.MirrorSync ? 1 : 0, 0, 0, 0, 1), paths, TransferFailureClass.None, null, RclonePreviewEvidencePolicy.Evaluate(task.Operation, new(true, true, true))));
        }
        public virtual ValueTask<AdapterPhaseResult> PrepareSafetyCopiesAsync(TransferPreview preview, CancellationToken cancellationToken) => Result("safety", TransferPathOutcome.Skipped);
        public virtual ValueTask<AdapterPhaseResult> CopyAsync(TransferPreview preview, CancellationToken cancellationToken) => Result("copy", TransferPathOutcome.Copied);
        public virtual ValueTask<AdapterPhaseResult> VerifyAsync(TransferPreview preview, CancellationToken cancellationToken) { Calls.Add("verify"); return ValueTask.FromResult(new AdapterPhaseResult(true, TransferFailureClass.None, preview.Paths.Select(path => new TransferExecutionEvidence(path.RelativePath, TransferPathOutcome.Verified, null)).ToImmutableArray())); }
        public virtual ValueTask<AdapterPhaseResult> DeleteVerifiedSourcesAsync(DeletionAdmission admission, CancellationToken cancellationToken) { LastDeletionAdmission = admission; return Result("delete-source", TransferPathOutcome.SourceDeleted); }
        public virtual ValueTask<AdapterPhaseResult> DeleteApprovedTargetsAsync(DeletionAdmission admission, CancellationToken cancellationToken) { LastDeletionAdmission = admission; return Result("delete-target", TransferPathOutcome.TargetDeleted); }
        public virtual ValueTask CancelAsync(TransferRunId runId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        protected ValueTask<AdapterPhaseResult> Result(string call, TransferPathOutcome outcome) { Calls.Add(call); return ValueTask.FromResult(new AdapterPhaseResult(true, TransferFailureClass.None, [new("a", outcome, null)])); }
    }

    private sealed class MixedEvidenceAdapter : ScriptedAdapter
    {
        public override ValueTask<AdapterPreviewResult> PreviewAsync(TransferTaskRevision task, CancellationToken cancellationToken)
        {
            Calls.Add("preview");
            ImmutableArray<PreviewPath> paths = [new("clean", TransferPathOutcome.Copied, 1), new("changed", TransferPathOutcome.Copied, 1), new("skipped", TransferPathOutcome.Copied, 1), new("unverified", TransferPathOutcome.Copied, 1)];
            return ValueTask.FromResult(new AdapterPreviewResult(new(4, 0, 0, 0, 0, 0, 4), paths, TransferFailureClass.None, null, RclonePreviewEvidencePolicy.Evaluate(task.Operation, new(true, true, true))));
        }

        public override ValueTask<AdapterPhaseResult> VerifyAsync(TransferPreview preview, CancellationToken cancellationToken)
        {
            Calls.Add("verify");
            ImmutableArray<TransferExecutionEvidence> evidence =
            [
                new("clean", TransferPathOutcome.Verified, null),
                new("changed", TransferPathOutcome.Verified, null),
                new("changed", TransferPathOutcome.PossiblyAffected, "source-changed"),
                new("skipped", TransferPathOutcome.Verified, null),
                new("skipped", TransferPathOutcome.Skipped, "filtered")
            ];
            return ValueTask.FromResult(new AdapterPhaseResult(true, TransferFailureClass.None, evidence));
        }
    }

    private sealed class FailingAdapter(TransferFailureClass failure) : ScriptedAdapter
    {
        public override ValueTask<AdapterPhaseResult> CopyAsync(TransferPreview preview, CancellationToken cancellationToken) =>
            failure != TransferFailureClass.Verification
                ? ValueTask.FromResult(new AdapterPhaseResult(false, failure, [new("a", TransferPathOutcome.Failed, failure.ToString().ToLowerInvariant())]))
                : base.CopyAsync(preview, cancellationToken);

        public override ValueTask<AdapterPhaseResult> VerifyAsync(TransferPreview preview, CancellationToken cancellationToken) =>
            failure == TransferFailureClass.Verification
                ? ValueTask.FromResult(new AdapterPhaseResult(false, failure, [new("a", TransferPathOutcome.Failed, "verification")]))
                : base.VerifyAsync(preview, cancellationToken);
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
        public ValueTask<ImmutableArray<TransferRunSnapshot>> ReadIncompleteAsync(CancellationToken cancellationToken) => ValueTask.FromResult(snapshots.Values.Where(snapshot => snapshot.TerminalResult is null).ToImmutableArray());
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
        public ValueTask<ImmutableArray<TransferRunSnapshot>> RecoverInterruptedAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(ImmutableArray<TransferRunSnapshot>.Empty);
    }

    private sealed class LargeManifestAdapter(int count) : ScriptedAdapter
    {
        public override ValueTask<AdapterPreviewResult> PreviewAsync(TransferTaskRevision task, CancellationToken cancellationToken)
        {
            Calls.Add("preview");
            var paths = Enumerable.Range(0, count).Select(index => new PreviewPath($"path-{index:D3}", TransferPathOutcome.Copied, 1)).ToImmutableArray();
            return ValueTask.FromResult(new AdapterPreviewResult(new(count, 0, 0, 0, 0, 0, count), paths, TransferFailureClass.None, null, RclonePreviewEvidencePolicy.Evaluate(task.Operation, new(true, true, true))));
        }

        public override ValueTask<AdapterPhaseResult> CopyAsync(TransferPreview preview, CancellationToken cancellationToken)
        {
            Calls.Add("copy");
            return ValueTask.FromResult(new AdapterPhaseResult(true, TransferFailureClass.None, preview.Paths.Select(path => new TransferExecutionEvidence(path.RelativePath, TransferPathOutcome.Copied, null)).ToImmutableArray()));
        }
    }

    private sealed class PhaseBlockingAdapter(string blockedPhase) : ScriptedAdapter
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CancelRequests { get; private set; }

        public override ValueTask<AdapterPhaseResult> PrepareSafetyCopiesAsync(TransferPreview preview, CancellationToken cancellationToken) =>
            blockedPhase == "safety" ? BlockAsync("safety", cancellationToken) : base.PrepareSafetyCopiesAsync(preview, cancellationToken);
        public override ValueTask<AdapterPhaseResult> CopyAsync(TransferPreview preview, CancellationToken cancellationToken) =>
            blockedPhase == "copy" ? BlockAsync("copy", cancellationToken) : base.CopyAsync(preview, cancellationToken);
        public override ValueTask<AdapterPhaseResult> VerifyAsync(TransferPreview preview, CancellationToken cancellationToken) =>
            blockedPhase == "verify" ? BlockAsync("verify", cancellationToken) : base.VerifyAsync(preview, cancellationToken);
        public override ValueTask<AdapterPhaseResult> DeleteVerifiedSourcesAsync(DeletionAdmission admission, CancellationToken cancellationToken) =>
            blockedPhase == "delete-source" ? BlockAsync("delete-source", cancellationToken) : base.DeleteVerifiedSourcesAsync(admission, cancellationToken);
        public override ValueTask<AdapterPhaseResult> DeleteApprovedTargetsAsync(DeletionAdmission admission, CancellationToken cancellationToken) =>
            blockedPhase == "delete-target" ? BlockAsync("delete-target", cancellationToken) : base.DeleteApprovedTargetsAsync(admission, cancellationToken);
        public override ValueTask CancelAsync(TransferRunId runId, CancellationToken cancellationToken) { CancelRequests++; return ValueTask.CompletedTask; }

        private async ValueTask<AdapterPhaseResult> BlockAsync(string call, CancellationToken cancellationToken)
        {
            Calls.Add(call);
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation did not interrupt the adapter.");
        }
    }

    private sealed class ConsistencyDelayAdapter(int succeedOnAttempt) : ScriptedAdapter
    {
        internal int VerifyAttempts { get; private set; }

        public override ValueTask<AdapterPhaseResult> VerifyAsync(TransferPreview preview, CancellationToken cancellationToken)
        {
            VerifyAttempts++;
            if (VerifyAttempts < succeedOnAttempt)
                return ValueTask.FromResult(new AdapterPhaseResult(false, TransferFailureClass.Transient, []));
            return base.VerifyAsync(preview, cancellationToken);
        }
    }

    private sealed class SafetyFallbackAdapter(bool succeeds) : ScriptedAdapter
    {
        public override ValueTask<AdapterPhaseResult> PrepareSafetyCopiesAsync(TransferPreview preview, CancellationToken cancellationToken)
        {
            Calls.Add("safety-fallback");
            return ValueTask.FromResult(new AdapterPhaseResult(succeeds, succeeds ? TransferFailureClass.None : TransferFailureClass.Permission, [new("obsolete", succeeds ? TransferPathOutcome.Copied : TransferPathOutcome.Failed, "non-atomic-copy-fallback")]));
        }
    }
}
