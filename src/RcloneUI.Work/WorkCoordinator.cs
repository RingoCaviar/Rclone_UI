using System.Collections.Concurrent;
using RcloneUI.Transfers;

namespace RcloneUI.Work;

public sealed class WorkCoordinator : IWorkCoordinator, IAsyncDisposable
{
    private readonly ITransferOrchestrator transfers;
    private readonly int maximumConcurrency;
    private readonly object sync = new();
    private readonly Dictionary<WorkItemId, WorkItem> items = [];
    private readonly Dictionary<WorkScheduleId, ScheduleState> schedules = [];
    private readonly HashSet<WorkItemId> activeWriteItems = [];
    private readonly CancellationTokenSource shutdown = new();
    private int running;

    public WorkCoordinator(ITransferOrchestrator transfers, int maximumConcurrency = 2)
    {
        if (maximumConcurrency is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        this.transfers = transfers;
        this.maximumConcurrency = maximumConcurrency;
    }

    public ValueTask<WorkItemSnapshot> EnqueueAsync(WorkSubmission submission, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = new WorkItem(WorkItemId.New(), submission, WorkItemState.Queued);
        lock (sync)
        {
            items.Add(item.Id, item);
            PumpLocked();
            return ValueTask.FromResult(item.Snapshot());
        }
    }

    public ValueTask PauseQueuedAsync(WorkItemId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var item = Find(id);
            if (item.State == WorkItemState.Queued) item.State = WorkItemState.Paused;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask RunNextAsync(WorkItemId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var item = Find(id);
            if (item.State is WorkItemState.Paused or WorkItemState.Queued)
            {
                item.State = WorkItemState.Queued;
                item.Priority = Math.Max(item.Priority, items.Values.Where(value => value.State == WorkItemState.Queued).Select(value => value.Priority).DefaultIfEmpty(0).Max() + 1);
                PumpLocked();
            }
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask CancelAsync(WorkItemId id, CancellationToken cancellationToken = default)
    {
        WorkItem? runningItem = null;
        lock (sync)
        {
            var item = Find(id);
            if (item.State is WorkItemState.Queued or WorkItemState.Paused) item.State = WorkItemState.Cancelled;
            else if (item.State == WorkItemState.Running) runningItem = item;
        }
        if (runningItem is not null) runningItem.Cancellation.Cancel();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public IReadOnlyList<WorkItemSnapshot> Observe()
    {
        lock (sync) return items.Values.OrderByDescending(item => item.Priority).ThenBy(item => item.CreatedOrder).Select(item => item.Snapshot()).ToArray();
    }

    public void RegisterSchedule(WorkSchedule schedule)
    {
        if (schedule.Interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(schedule));
        var catchUp = schedule.CatchUpWindow ?? TimeSpan.FromHours(24);
        var networkWait = schedule.NetworkWait ?? TimeSpan.FromMinutes(30);
        if (catchUp < TimeSpan.Zero || catchUp > TimeSpan.FromDays(7) || networkWait < TimeSpan.Zero || networkWait > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(schedule));
        lock (sync) schedules[schedule.Id] = new(schedule);
    }

    public async ValueTask EvaluateSchedulesAsync(DateTimeOffset nowUtc, bool networkAvailable, CancellationToken cancellationToken = default)
    {
        List<WorkSubmission> due = [];
        lock (sync)
        {
            foreach (var state in schedules.Values.Where(value => value.Schedule.Enabled && value.Schedule.NextDueUtc <= nowUtc))
            {
                var schedule = state.Schedule;
                var latestDue = schedule.NextDueUtc;
                while (latestDue + schedule.Interval <= nowUtc) latestDue += schedule.Interval;
                state.Schedule = schedule with { NextDueUtc = latestDue + schedule.Interval };
                if ((schedule.MissedPolicy == MissedOccurrencePolicy.Skip && nowUtc > latestDue) || nowUtc - latestDue > (schedule.CatchUpWindow ?? TimeSpan.FromHours(24)))
                {
                    state.LastOutcome = "missed-skipped";
                    state.WaitingForNetworkSinceUtc = null;
                    continue;
                }

                if (!networkAvailable)
                {
                    state.WaitingForNetworkSinceUtc ??= nowUtc;
                    if (nowUtc - state.WaitingForNetworkSinceUtc <= (schedule.NetworkWait ?? TimeSpan.FromMinutes(30)))
                    {
                        state.Schedule = state.Schedule with { NextDueUtc = nowUtc };
                        state.LastOutcome = "waiting-for-network";
                    }
                    else
                    {
                        state.LastOutcome = "network-timeout";
                        state.WaitingForNetworkSinceUtc = null;
                    }
                    continue;
                }

                due.Add(schedule.Submission);
                state.LastEnqueuedUtc = nowUtc;
                state.LastOutcome = "enqueued";
                state.WaitingForNetworkSinceUtc = null;
            }
        }

        foreach (var submission in due) await EnqueueAsync(submission, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<WorkScheduleSnapshot> ObserveSchedules()
    {
        lock (sync) return schedules.Values.Select(value => value.Snapshot()).ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        WorkItem[] active;
        lock (sync) active = items.Values.Where(item => item.State == WorkItemState.Running).ToArray();
        foreach (var item in active)
            item.Cancellation.Cancel();
        shutdown.Dispose();
    }

    private void PumpLocked()
    {
        while (running < maximumConcurrency)
        {
            var next = items.Values
                .Where(item => item.State == WorkItemState.Queued && (item.Submission.AllowUnknownOverlap || activeWriteItems.All(active => !LocationsOverlap(item.Target, items[active].Target))))
                .OrderByDescending(item => item.Priority).ThenBy(item => item.CreatedOrder).FirstOrDefault();
            if (next is null) return;
            next.State = WorkItemState.Running;
            activeWriteItems.Add(next.Id);
            running++;
            _ = RunAsync(next);
        }
    }

    private async Task RunAsync(WorkItem item)
    {
        try
        {
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, item.Cancellation.Token);
            var snapshot = await transfers.ExecuteAsync(item.Submission.ExecuteRequest, combined.Token).ConfigureAwait(false);
            lock (sync)
            {
                item.Transfer = snapshot;
                item.TransferRunId = snapshot.RunId;
                item.State = snapshot.TerminalResult switch
                {
                    TransferTerminalResult.Succeeded or TransferTerminalResult.CompletedWithConflicts or TransferTerminalResult.StoppedByLimit => WorkItemState.Completed,
                    TransferTerminalResult.CancelledWithPartialResults => WorkItemState.Cancelled,
                    _ => WorkItemState.Failed,
                };
            }
        }
        catch (OperationCanceledException)
        {
            lock (sync) { item.State = WorkItemState.Cancelled; item.DiagnosticCode = "coordinator-cancelled"; }
        }
        catch (Exception exception)
        {
            lock (sync) { item.State = WorkItemState.Failed; item.DiagnosticCode = exception.GetType().Name.ToLowerInvariant(); }
        }
        finally
        {
            lock (sync)
            {
                activeWriteItems.Remove(item.Id);
                running--;
                PumpLocked();
            }
        }
    }

    private WorkItem Find(WorkItemId id) => items.GetValueOrDefault(id) ?? throw new KeyNotFoundException("Work item not found.");

    private static bool LocationsOverlap(TransferLocation left, TransferLocation right)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(left.CanonicalEndpoint, right.CanonicalEndpoint)) return false;
        var first = left.Path.Trim('/');
        var second = right.Path.Trim('/');
        return first.Length == 0 || second.Length == 0 || first.Equals(second, StringComparison.OrdinalIgnoreCase)
            || first.StartsWith(second + '/', StringComparison.OrdinalIgnoreCase)
            || second.StartsWith(first + '/', StringComparison.OrdinalIgnoreCase);
    }

    private sealed class WorkItem(WorkItemId id, WorkSubmission submission, WorkItemState state)
    {
        private static long sequence;
        internal WorkItemId Id { get; } = id;
        internal WorkSubmission Submission { get; } = submission;
        internal long CreatedOrder { get; } = Interlocked.Increment(ref sequence);
        internal string WriteKey { get; } = submission.Preview.Task.Target.WriteOverlapKey;
        internal TransferLocation Target { get; } = submission.Preview.Task.Target;
        internal WorkItemState State { get; set; } = state;
        internal int Priority { get; set; } = submission.Priority;
        internal TransferRunSnapshot? Transfer { get; set; }
        internal TransferRunId? TransferRunId { get; set; }
        internal CancellationTokenSource Cancellation { get; } = new();
        internal string? DiagnosticCode { get; set; }
        internal WorkItemSnapshot Snapshot() => new(Id, State, Priority, WriteKey, Transfer, DiagnosticCode);
    }

    private sealed class ScheduleState(WorkSchedule schedule)
    {
        internal WorkSchedule Schedule { get; set; } = schedule;
        internal DateTimeOffset? WaitingForNetworkSinceUtc { get; set; }
        internal DateTimeOffset? LastEnqueuedUtc { get; set; }
        internal string? LastOutcome { get; set; }
        internal WorkScheduleSnapshot Snapshot() => new(Schedule, WaitingForNetworkSinceUtc, LastEnqueuedUtc, LastOutcome);
    }
}
