using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using RcloneUI.Host;
using RcloneUI.Rclone;

namespace RcloneUI.IntegrationTests;

[SupportedOSPlatform("windows")]
public sealed class ManagedRcloneBootstrapTests
{
    [Fact]
    public void ManifestIsBoundToRelativeExecutableAndSha256()
    {
        var root = Directory.CreateTempSubdirectory("RcloneUI-RCLONE-MANIFEST-");
        try
        {
            var executable = Path.Combine(root.FullName, "rclone.exe"); File.WriteAllBytes(executable, [1, 2, 3]);
            var hash = Convert.ToHexString(SHA256.HashData([1, 2, 3]));
            var manifestPath = Path.Combine(root.FullName, "manifest.json"); File.WriteAllText(manifestPath, JsonSerializer.Serialize(new { format = 1, version = "v1", sha256 = hash, executable = "rclone.exe" }));
            var manifest = ManagedRcloneBootstrap.ReadManifest(manifestPath);
            Assert.Equal("v1", manifest.Version);
            Assert.Equal(RcloneComponentHealth.Healthy, BundledRcloneDiscovery.Verify(executable, manifest.Sha256, manifest.Version).Health);
            File.AppendAllText(executable, "tampered");
            Assert.Equal(RcloneComponentHealth.HashMismatch, BundledRcloneDiscovery.Verify(executable, manifest.Sha256, manifest.Version).Health);
        }
        finally { root.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("../rclone.exe")]
    [InlineData("C:/Windows/rclone.exe")]
    public async Task EscapingManifestPathIsRejectedAtStartup(string executable)
    {
        var root = Directory.CreateTempSubdirectory("RcloneUI-RCLONE-MANIFEST-");
        try
        {
            var component = Directory.CreateDirectory(Path.Combine(root.FullName, "host", "..", "components", "rclone"));
            File.WriteAllText(Path.Combine(component.FullName, "manifest.json"), JsonSerializer.Serialize(new { format = 1, version = "v1", sha256 = new string('A', 64), executable }));
            await Assert.ThrowsAnyAsync<Exception>(() => ManagedRcloneBootstrap.TryStartAsync(root.FullName, Path.Combine(root.FullName, "host"), TestContext.Current.CancellationToken).AsTask());
        }
        finally { root.Delete(recursive: true); }
    }
}
