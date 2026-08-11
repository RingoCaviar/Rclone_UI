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
    string? DiagnosticCode = null);

public interface IMountExecutionAdapter
{
    ValueTask<MountEnvironmentEvidence> InspectAsync(MountProfile profile, CancellationToken cancellationToken);
    ValueTask StartAsync(MountInstanceId instanceId, MountProfile profile, CancellationToken cancellationToken);
    ValueTask<MountEvidence> ObserveAsync(MountInstanceId instanceId, MountProfile profile, CancellationToken cancellationToken);
    ValueTask StopAsync(MountInstanceId instanceId, bool force, CancellationToken cancellationToken);
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
