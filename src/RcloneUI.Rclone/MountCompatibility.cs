using System.Collections.Immutable;
using System.Text.Json;

namespace RcloneUI.Rclone;

public enum RcloneVfsCacheMode { Off, Minimal, Writes, Full }

public sealed record MountFeatureAvailability(
    bool CanMount,
    bool CanObserveStats,
    bool CanObserveQueue,
    bool HasMountOptionSchema,
    bool HasVfsOptionSchema);

public sealed record MountVfsObservation(
    long? BytesUsed,
    int? ErroredFiles,
    int? UploadsInProgress,
    int? UploadsQueued,
    bool? OutOfSpace,
    int? InUse,
    RcloneVfsCacheMode? CacheMode);

public sealed record MountCompatibilityFixture(
    string RcloneVersion,
    ImmutableSortedSet<string> Endpoints,
    ImmutableSortedSet<string> MountTypes,
    MountFeatureAvailability Features,
    MountVfsObservation Stats,
    bool QueueShapeKnown)
{
    public static MountCompatibilityFixture Parse(JsonElement root)
    {
        var endpoints = root.GetProperty("rcList").GetProperty("commands").EnumerateArray()
            .Select(command => command.GetProperty("Path").GetString())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToImmutableSortedSet(StringComparer.Ordinal);
        var mountTypes = root.GetProperty("mountTypes").GetProperty("mountTypes").EnumerateArray()
            .Select(value => value.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToImmutableSortedSet(StringComparer.Ordinal);
        var hasOptions = root.TryGetProperty("optionsInfo", out var options) && options.ValueKind == JsonValueKind.Object;
        var hasStats = root.TryGetProperty("vfsStats", out var stats) && stats.ValueKind == JsonValueKind.Object;
        var hasQueue = root.TryGetProperty("vfsQueue", out var queue) && queue.ValueKind == JsonValueKind.Object && queue.TryGetProperty("queue", out var queueItems) && queueItems.ValueKind == JsonValueKind.Array;
        var disk = hasStats && stats.TryGetProperty("diskCache", out var diskCache) && diskCache.ValueKind == JsonValueKind.Object ? diskCache : default;
        var opt = hasStats && stats.TryGetProperty("opt", out var optionValues) && optionValues.ValueKind == JsonValueKind.Object ? optionValues : default;
        return new(
            root.GetProperty("rcloneVersion").GetString() ?? throw new InvalidDataException("Fixture version is missing."),
            endpoints,
            mountTypes,
            new(
                endpoints.Contains("mount/mount") && mountTypes.Contains("cmount"),
                endpoints.Contains("vfs/stats") && hasStats,
                endpoints.Contains("vfs/queue") && hasQueue,
                hasOptions && options.TryGetProperty("mount", out var mountOptions) && mountOptions.ValueKind == JsonValueKind.Array,
                hasOptions && options.TryGetProperty("vfs", out var vfsOptions) && vfsOptions.ValueKind == JsonValueKind.Array),
            new(
                ReadInt64(disk, "bytesUsed"),
                ReadInt32(disk, "erroredFiles"),
                ReadInt32(disk, "uploadsInProgress"),
                ReadInt32(disk, "uploadsQueued"),
                ReadBoolean(disk, "outOfSpace"),
                ReadInt32(stats, "inUse"),
                ParseCacheMode(ReadInt32(opt, "CacheMode"))),
            hasQueue);
    }

    private static int? ReadInt32(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;
    private static long? ReadInt64(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : null;
    private static bool? ReadBoolean(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static RcloneVfsCacheMode? ParseCacheMode(int? value) => value is >= 0 and <= 3 ? (RcloneVfsCacheMode)value.Value : null;
}
