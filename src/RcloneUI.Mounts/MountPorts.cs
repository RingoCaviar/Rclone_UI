namespace RcloneUI.Mounts;

public sealed record MountEnvironmentEvidence(
    bool OperationalSession,
    bool RemoteHealthy,
    bool SubpathExists,
    bool WinFspCompatible,
    bool DriveLetterAvailable,
    bool DriveLetterOwnedByProfile,
    bool CacheWritable,
    string CapabilityBinding,
    string? DiagnosticCode = null,
    char? ResolvedDriveLetter = null,
    bool ShareNameAvailable = true,
    bool DirectoryTargetAvailable = true);

public sealed record MountCleanupEvidence(
    bool UnmountRequested,
    bool NamespaceRemoved,
    bool CompletedWithinDeadline,
    bool HostExitRequired = false,
    string? DiagnosticCode = null)
{
    public bool ProvesCleanup => UnmountRequested && NamespaceRemoved && CompletedWithinDeadline;
}

public sealed record MountStopEvidence(
    bool RcUnmountAccepted,
    bool NamespaceRemoved,
    bool CompletedWithinDeadline,
    bool OwnedProcessTerminated = false,
    string? DiagnosticCode = null)
{
    public bool ProvesStopped => RcUnmountAccepted && NamespaceRemoved && CompletedWithinDeadline;
}

public interface IMountExecutionAdapter
{
    ValueTask<MountEnvironmentEvidence> InspectAsync(MountProfile profile, CancellationToken cancellationToken);
    ValueTask StartAsync(MountInstanceId instanceId, MountProfile profile, CancellationToken cancellationToken);
    ValueTask<MountEvidence> ObserveAsync(MountInstanceId instanceId, MountProfile profile, CancellationToken cancellationToken);
    ValueTask<MountCleanupEvidence> CleanupFailedStartAsync(MountInstanceId instanceId, MountProfile profile, CancellationToken cancellationToken);
    ValueTask<MountStopEvidence> StopAsync(MountInstanceId instanceId, bool force, CancellationToken cancellationToken);
}

public interface IMountJournal
{
    ValueTask SaveAsync(MountSnapshot snapshot, CancellationToken cancellationToken);
    ValueTask<MountSnapshot?> ReadAsync(MountInstanceId instanceId, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<MountSnapshot>> ReadActiveAsync(CancellationToken cancellationToken);
}

public interface IRecoveryCacheRegistry
{
    ValueTask<string> PreserveAsync(MountSnapshot snapshot, MountRisk risk, CancellationToken cancellationToken);
}
