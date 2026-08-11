using System.Collections.Immutable;
using RcloneUI.Transfers;

namespace RcloneUI.Work;

public readonly record struct WorkItemId(Guid Value) { public static WorkItemId New() => new(Guid.NewGuid()); }
public readonly record struct WorkScheduleId(Guid Value) { public static WorkScheduleId New() => new(Guid.NewGuid()); }
public enum WorkItemState { Queued, Running, Paused, Completed, Failed, Cancelled }
public enum MissedOccurrencePolicy { CatchUpLatest, Skip }

public sealed record WorkSubmission(
    TransferPreview Preview,
    ExecuteAcceptedPreviewRequest ExecuteRequest,
    int Priority = 0,
    bool AllowUnknownOverlap = false);

public sealed record WorkItemSnapshot(
    WorkItemId Id,
    WorkItemState State,
    int Priority,
    string WriteOverlapKey,
    TransferRunSnapshot? Transfer,
    string? DiagnosticCode);

public sealed record WorkSchedule(
    WorkScheduleId Id,
    WorkSubmission Submission,
    TimeSpan Interval,
    DateTimeOffset NextDueUtc,
    bool Enabled,
    MissedOccurrencePolicy MissedPolicy = MissedOccurrencePolicy.CatchUpLatest,
    TimeSpan? CatchUpWindow = null,
    TimeSpan? NetworkWait = null);

public sealed record WorkScheduleSnapshot(WorkSchedule Schedule, DateTimeOffset? WaitingForNetworkSinceUtc, DateTimeOffset? LastEnqueuedUtc, string? LastOutcome);

public interface IWorkCoordinator
{
    ValueTask<WorkItemSnapshot> EnqueueAsync(WorkSubmission submission, CancellationToken cancellationToken = default);
    ValueTask PauseQueuedAsync(WorkItemId id, CancellationToken cancellationToken = default);
    ValueTask RunNextAsync(WorkItemId id, CancellationToken cancellationToken = default);
    ValueTask CancelAsync(WorkItemId id, CancellationToken cancellationToken = default);
    void RegisterSchedule(WorkSchedule schedule);
    ValueTask EvaluateSchedulesAsync(DateTimeOffset nowUtc, bool networkAvailable, CancellationToken cancellationToken = default);
    IReadOnlyList<WorkItemSnapshot> Observe();
    IReadOnlyList<WorkScheduleSnapshot> ObserveSchedules();
}
