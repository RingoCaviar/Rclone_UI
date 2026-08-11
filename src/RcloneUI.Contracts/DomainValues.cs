namespace RcloneUI.Contracts;

public readonly record struct DataRootId(Guid Value);

public readonly record struct RemoteId(Guid Value);

public readonly record struct TransferTaskId(Guid Value);

public readonly record struct TransferRunId(Guid Value);

public readonly record struct MountProfileId(Guid Value);

public readonly record struct MountId(Guid Value);

public readonly record struct WorkRunId(Guid Value);

public readonly record struct UpdatePlanId(Guid Value);

public readonly record struct ByteCount(ulong Value);

public readonly record struct MillisecondCount(ulong Value);

public enum WorkActor
{
    Manual,
    Schedule,
    Recovery,
    Update,
}

public enum CancellationState
{
    Requested,
    Acknowledged,
    Completed,
}
