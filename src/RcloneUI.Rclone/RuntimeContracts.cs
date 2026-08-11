using System.Collections.Immutable;
using System.Text.Json;

namespace RcloneUI.Rclone;

public readonly record struct RcloneBinaryIdentity(string Version, string Sha256, long Length)
{
    public string ExecuteId => $"{Version}:{Sha256}:{Length}";
}

public sealed record RcloneCapabilitySnapshot(
    RcloneBinaryIdentity Binary,
    string EndpointSetHash,
    string OptionSchemaHash,
    ImmutableSortedSet<string> Endpoints,
    ImmutableSortedSet<string> MountTypes,
    DateTimeOffset CapturedUtc)
{
    public string Binding => $"rclone-adapter/v1:{Binary.ExecuteId}:{EndpointSetHash}:{OptionSchemaHash}";
}

public sealed record RcloneBackendCapabilitySnapshot(
    string FileSystem,
    string FeaturesHash,
    JsonElement Features,
    DateTimeOffset CapturedUtc);

public enum RclonePrimitive
{
    List,
    Copy,
    Check,
    DeleteFile,
    Stat,
    Mount,
    Unmount,
    MountStatus,
}

public sealed record RcloneEndpoint(string FileSystem, string Path);

public sealed record RcloneMountOptions(string MountType, bool ReadOnly, string VolumeName, bool NetworkMode = true);

public sealed record RcloneExecutionRequest(
    Guid ExecutionId,
    string ExpectedCapabilityBinding,
    RclonePrimitive Primitive,
    RcloneEndpoint Source,
    RcloneEndpoint? Destination,
    string Group,
    int HighLevelRetries = 1,
    long? MaximumTransferBytes = null,
    TimeSpan? MaximumDuration = null,
    RcloneMountOptions? MountOptions = null);

public sealed record RcloneExecutionHandle(Guid ExecutionId, long JobId, string Group);

public sealed record RcloneTransferStats(
    long Bytes,
    long TotalBytes,
    long Transfers,
    long Errors,
    double BytesPerSecond,
    TimeSpan Elapsed,
    bool Finished);

public sealed record RcloneExecutionResult(bool Success, bool Cancelled, string? ErrorCode, JsonElement Body);

public interface IRcloneRuntime
{
    RcloneCapabilitySnapshot Capabilities { get; }
    ValueTask<RcloneExecutionHandle> StartAsync(RcloneExecutionRequest request, CancellationToken cancellationToken);
    ValueTask<RcloneTransferStats> GetStatsAsync(RcloneExecutionHandle handle, CancellationToken cancellationToken);
    ValueTask<RcloneExecutionResult> WaitAsync(RcloneExecutionHandle handle, CancellationToken cancellationToken);
    ValueTask CancelAsync(RcloneExecutionHandle handle, CancellationToken cancellationToken);
}

public sealed class RcloneCapabilityChangedException(string expected, string actual)
    : InvalidOperationException($"The rclone capability binding changed. Expected '{expected}', actual '{actual}'.");

public enum RcloneComponentHealth
{
    Healthy,
    Missing,
    HashMismatch,
    VersionMismatch,
    Unavailable,
}

public sealed record RcloneComponentStatus(RcloneComponentHealth Health, string? Detail, RcloneBinaryIdentity? Binary);
