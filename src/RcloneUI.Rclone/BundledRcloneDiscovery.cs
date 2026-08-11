using System.Security.Cryptography;

namespace RcloneUI.Rclone;

public static class BundledRcloneDiscovery
{
    public static VerifiedRcloneBinary RequireVerified(string executablePath, string expectedSha256, string expectedVersion)
    {
        var status = Verify(executablePath, expectedSha256, expectedVersion);
        if (status.Health != RcloneComponentHealth.Healthy || status.Binary is null)
            throw new InvalidDataException(status.Detail ?? "Bundled rclone verification failed.");
        return new(Path.GetFullPath(executablePath), status.Binary.Value);
    }

    public static RcloneComponentStatus Verify(string executablePath, string expectedSha256, string expectedVersion)
    {
        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath)) return new(RcloneComponentHealth.Missing, "Bundled rclone.exe is missing.", null);
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var digest = Convert.ToHexString(SHA256.HashData(stream));
        var identity = new RcloneBinaryIdentity(expectedVersion, digest, stream.Length);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedSha256), Convert.FromHexString(digest)))
            return new(RcloneComponentHealth.HashMismatch, "Bundled rclone.exe did not match the release manifest.", identity);
        return new(RcloneComponentHealth.Healthy, null, identity);
    }

    public static RcloneComponentStatus AssessReportedVersion(VerifiedRcloneBinary binary, string reportedVersion)
    {
        if (!StringComparer.Ordinal.Equals(binary.Identity.Version, reportedVersion))
            return new(RcloneComponentHealth.VersionMismatch, $"Expected {binary.Identity.Version}, reported {reportedVersion}.", binary.Identity);
        return new(RcloneComponentHealth.Healthy, null, binary.Identity);
    }
}

public sealed record VerifiedRcloneBinary
{
    internal VerifiedRcloneBinary(string path, RcloneBinaryIdentity identity) => (Path, Identity) = (path, identity);
    public string Path { get; }
    public RcloneBinaryIdentity Identity { get; }
}
