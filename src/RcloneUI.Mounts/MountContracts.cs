namespace RcloneUI.Mounts;

public readonly record struct MountProfileId(Guid Value) { public static MountProfileId New() => new(Guid.NewGuid()); }
public readonly record struct MountInstanceId(Guid Value) { public static MountInstanceId New() => new(Guid.NewGuid()); }

public enum MountCachePreset { ReadOnlyBrowsing, StandardReadWrite, MaximumCompatibility, Custom }
public enum WindowsDriveType { Network, Fixed }
public enum MountState { Stopped, Starting, Ready, DegradedConnection, SafeUnmount, NeedsRemount, RecoveryRequired }
public enum MountStopMode { Safe, Force }
public enum MountRisk { None, PendingWrites, CannotProveClean, Interrupted, CorruptCache }

public sealed record MountProfile(
    MountProfileId Id,
    ulong Revision,
    string DisplayName,
    string Remote,
    string? Subpath,
    char PreferredDriveLetter,
    string VolumeName,
    WindowsDriveType DriveType,
    MountCachePreset CachePreset,
    string CachePath,
    long CacheCapacityBytes,
    bool AutoMount,
    string CapabilityBinding);

public sealed record MountEvidence(
    bool ProcessAlive,
    bool EndpointRegistered,
    bool NamespacePresented,
    bool RootProbeSucceeded,
    bool? CacheObservable,
    int? PendingFiles,
    long? PendingBytes,
    int? OpenFiles,
    string? DiagnosticCode = null)
{
    public bool ProvesReady => ProcessAlive && EndpointRegistered && NamespacePresented && RootProbeSucceeded && CacheObservable is not false;
    public bool ProvesClean => CacheObservable == true && PendingFiles == 0 && PendingBytes == 0 && OpenFiles == 0;
}

public sealed record MountSnapshot(
    MountInstanceId InstanceId,
    MountProfile Profile,
    MountState State,
    MountRisk Risk,
    MountEvidence Evidence,
    DateTimeOffset UpdatedUtc,
    string? RecoveryCachePath = null,
    string? DiagnosticCode = null);

public sealed record MountValidation(bool IsValid, string? DiagnosticCode);

public interface IMountManager
{
    ValueTask<MountValidation> ValidateAsync(MountProfile profile, CancellationToken cancellationToken = default);
    ValueTask<MountSnapshot> StartAsync(MountProfile profile, CancellationToken cancellationToken = default);
    ValueTask<MountSnapshot> StopAsync(MountInstanceId instanceId, MountStopMode mode, bool forceConfirmed = false, CancellationToken cancellationToken = default);
    ValueTask<MountSnapshot?> ObserveAsync(MountInstanceId instanceId, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<MountSnapshot>> ReconcileInterruptedAsync(CancellationToken cancellationToken = default);
}
