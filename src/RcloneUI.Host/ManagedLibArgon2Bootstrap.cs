using System.Security.Cryptography;
using System.Text.Json;
using RcloneUI.DataRoot;

namespace RcloneUI.Host;

internal sealed record ManagedLibArgon2Manifest(int Format, string Version, string Sha256, string Library);

internal static class ManagedLibArgon2Bootstrap
{
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web);

    internal static LibArgon2Binding? TryDiscover(string hostDirectory)
    {
        var componentRoot = Path.GetFullPath(Path.Combine(hostDirectory, "..", "components", "libargon2"));
        var manifestPath = Path.Combine(componentRoot, "manifest.json");
        if (!File.Exists(manifestPath)) return null;
        var manifest = ReadManifest(manifestPath);
        var library = RequireChild(componentRoot, manifest.Library);
        if (!File.Exists(library)) throw new FileNotFoundException("Managed libargon2 is missing.", library);
        var actual = SHA256.HashData(File.ReadAllBytes(library));
        var expected = Convert.FromHexString(manifest.Sha256);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new CryptographicException("Managed libargon2 did not match its pinned manifest.");
        return new(library, manifest.Sha256);
    }

    internal static ManagedLibArgon2Manifest ReadManifest(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is 0 or > 16 * 1024) throw new InvalidDataException("Managed libargon2 manifest size is invalid.");
        var value = JsonSerializer.Deserialize<ManagedLibArgon2Manifest>(bytes, ManifestJson) ?? throw new InvalidDataException("Managed libargon2 manifest is invalid.");
        if (value.Format != 1
            || string.IsNullOrWhiteSpace(value.Version) || value.Version.Length > 64
            || value.Sha256.Length != 64 || value.Sha256.Any(character => !Uri.IsHexDigit(character))
            || string.IsNullOrWhiteSpace(value.Library))
            throw new InvalidDataException("Managed libargon2 manifest fields are invalid.");
        return value;
    }

    private static string RequireChild(string root, string relative)
    {
        if (Path.IsPathFullyQualified(relative)) throw new InvalidDataException("Managed libargon2 path must be relative.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Managed libargon2 path escaped its component root.");
        return path;
    }
}
