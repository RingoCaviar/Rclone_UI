using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RcloneUI.Rclone;

public static class RcloneCapabilityDiscovery
{
    public static RcloneCapabilitySnapshot Create(
        RcloneBinaryIdentity binary,
        JsonElement rcList,
        JsonElement optionsInfo,
        JsonElement mountTypes,
        DateTimeOffset capturedUtc)
    {
        var endpoints = ReadStrings(rcList, "commands");
        var mounts = ReadStrings(mountTypes, "mountTypes");
        return new(binary, HashStrings(endpoints), HashJson(optionsInfo), endpoints, mounts, capturedUtc);
    }

    public static RcloneBackendCapabilitySnapshot CreateBackend(string fileSystem, JsonElement fsInfo, DateTimeOffset capturedUtc)
    {
        if (string.IsNullOrWhiteSpace(fileSystem)) throw new ArgumentException("A filesystem is required.", nameof(fileSystem));
        var features = fsInfo.TryGetProperty("Features", out var upper)
            ? upper
            : fsInfo.TryGetProperty("features", out var lower)
                ? lower
                : throw new InvalidDataException("rclone fsinfo omitted backend features.");
        return new(fileSystem, HashJson(features), features.Clone(), capturedUtc);
    }

    private static ImmutableSortedSet<string> ReadStrings(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array)
            return ImmutableSortedSet<string>.Empty;
        return values.EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.ValueKind == JsonValueKind.Object && value.TryGetProperty("Path", out var path) && path.ValueKind == JsonValueKind.String
                    ? path.GetString()
                    : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToImmutableSortedSet(StringComparer.Ordinal);
    }

    private static string HashStrings(IEnumerable<string> values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values))));

    private static string HashJson(JsonElement value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) WriteCanonical(writer, value);
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}
