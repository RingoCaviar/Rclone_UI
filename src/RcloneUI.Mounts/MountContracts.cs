namespace RcloneUI.Mounts;

public readonly record struct MountProfileId(Guid Value) { public static MountProfileId New() => new(Guid.NewGuid()); }
public readonly record struct MountInstanceId(Guid Value) { public static MountInstanceId New() => new(Guid.NewGuid()); }

public enum MountCachePreset { ReadOnlyBrowsing, StandardReadWrite, MaximumCompatibility, Custom }
public enum WindowsDriveType { Network, Fixed }
public enum MountPresentationMode { NetworkDrive, FixedDrive, FixedDirectory }
public enum DriveLetterSelection { Preferred, Automatic }
public enum MountState { Stopped, Starting, Ready, DegradedConnection, SafeUnmount, NeedsRemount, RecoveryRequired }
public enum MountStopMode { Safe, Force }
public enum MountRisk { None, PendingWrites, CannotProveClean, Interrupted, CorruptCache }
public enum MountCutoffMode { Soft, Hard, Cautious }

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
    string CapabilityBinding,
    MountPresentationMode PresentationMode = MountPresentationMode.NetworkDrive,
    DriveLetterSelection DriveLetterSelection = DriveLetterSelection.Preferred,
    string? FixedDirectoryPath = null,
    string? ShareName = null,
    string? ResolvedRecoveryContractBinding = null,
    long? MaximumTransferBytes = null,
    MountCutoffMode? CutoffMode = null);

public sealed record MountEvidence(
    bool ProcessAlive,
    bool EndpointRegistered,
    bool NamespacePresented,
    bool RootProbeSucceeded,
    bool? CacheObservable,
    int? PendingFiles,
    long? PendingBytes,
    int? OpenFiles,
    string? DiagnosticCode = null,
    bool RcRequestAccepted = true,
    bool NamespaceOwnedByInstance = true,
    bool ExpectedTokenVisible = true,
    bool RootProbeWithinDeadline = true,
    bool QueueObservable = true,
    int? UploadingFiles = 0,
    int? FailedUploads = 0,
    bool? OutOfSpace = false,
    bool RemoteHealthy = true,
    bool QuietIntervalObserved = true,
    string? ResolvedRecoveryContractBinding = null)
{
    public bool ProvesReadyFor(MountProfile profile) => RcRequestAccepted && ProcessAlive && EndpointRegistered && NamespacePresented && NamespaceOwnedByInstance && ExpectedTokenVisible && RootProbeSucceeded && RootProbeWithinDeadline && CacheObservable is not false && (string.IsNullOrWhiteSpace(profile.ResolvedRecoveryContractBinding) || StringComparer.Ordinal.Equals(profile.ResolvedRecoveryContractBinding, ResolvedRecoveryContractBinding));
    public bool ProvesCleanDrain => CacheObservable == true && QueueObservable && PendingFiles == 0 && PendingBytes == 0 && UploadingFiles == 0 && FailedUploads == 0 && OpenFiles == 0 && OutOfSpace == false && RemoteHealthy && QuietIntervalObserved;
}

public sealed record MountSnapshot(
    MountInstanceId InstanceId,
    MountProfile Profile,
    MountState State,
    MountRisk Risk,
    MountEvidence Evidence,
    DateTimeOffset UpdatedUtc,
    string? RecoveryCachePath = null,
    string? DiagnosticCode = null,
    MountCleanupEvidence? StartupCleanup = null,
    MountStopEvidence? StopEvidence = null);

public sealed record MountValidation(bool IsValid, string? DiagnosticCode);

public sealed record SavedMountProfile(
    MountProfileId Id,
    ulong Revision,
    string DisplayName,
    Guid RemoteId,
    string Subpath,
    MountPresentationMode PresentationMode,
    DriveLetterSelection DriveLetterSelection,
    char PreferredDriveLetter,
    string? FixedDirectoryPath,
    string VolumeName,
    MountCachePreset CachePreset,
    bool AutoMount);

public interface IMountProfileStore
{
    ValueTask<IReadOnlyList<SavedMountProfile>> ListAsync(CancellationToken cancellationToken = default);
    ValueTask<SavedMountProfile?> ReadAsync(MountProfileId id, CancellationToken cancellationToken = default);
    ValueTask<SavedMountProfile> UpsertAsync(SavedMountProfile profile, ulong expectedRevision, CancellationToken cancellationToken = default);
    ValueTask<bool> DeleteAsync(MountProfileId id, ulong expectedRevision, CancellationToken cancellationToken = default);
}

public interface IMountManager
{
    ValueTask<MountValidation> ValidateAsync(MountProfile profile, CancellationToken cancellationToken = default);
    ValueTask<MountSnapshot> StartAsync(MountProfile profile, CancellationToken cancellationToken = default);
    ValueTask<MountSnapshot> StopAsync(MountInstanceId instanceId, MountStopMode mode, bool forceConfirmed = false, CancellationToken cancellationToken = default);
    ValueTask<MountSnapshot?> ObserveAsync(MountInstanceId instanceId, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<MountSnapshot>> ReconcileInterruptedAsync(CancellationToken cancellationToken = default);
}
