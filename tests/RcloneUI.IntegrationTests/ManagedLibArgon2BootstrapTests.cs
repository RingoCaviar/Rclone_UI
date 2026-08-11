using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using RcloneUI.Host;

namespace RcloneUI.IntegrationTests;

[SupportedOSPlatform("windows")]
public sealed class ManagedLibArgon2BootstrapTests
{
    [Fact]
    public void DiscoversOnlyTheLibraryPinnedByTheBoundedManifest()
    {
        var root = Directory.CreateTempSubdirectory("RcloneUI-ARGON2-MANIFEST-");
        try
        {
            var host = Directory.CreateDirectory(Path.Combine(root.FullName, "host"));
            var component = Directory.CreateDirectory(Path.Combine(root.FullName, "components", "libargon2"));
            var library = Path.Combine(component.FullName, "argon2.dll");
            File.WriteAllBytes(library, [1, 2, 3]);
            WriteManifest(component.FullName, "argon2.dll", Convert.ToHexString(SHA256.HashData([1, 2, 3])));
            var binding = Assert.IsType<RcloneUI.DataRoot.LibArgon2Binding>(ManagedLibArgon2Bootstrap.TryDiscover(host.FullName));
            Assert.Equal(Path.GetFullPath(library), binding.AbsoluteLibraryPath);
            File.AppendAllText(library, "tampered");
            Assert.Throws<CryptographicException>(() => ManagedLibArgon2Bootstrap.TryDiscover(host.FullName));
        }
        finally { root.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("../argon2.dll")]
    [InlineData("C:/Windows/System32/argon2.dll")]
    public void EscapingManifestPathIsRejected(string library)
    {
        var root = Directory.CreateTempSubdirectory("RcloneUI-ARGON2-MANIFEST-");
        try
        {
            var host = Directory.CreateDirectory(Path.Combine(root.FullName, "host"));
            var component = Directory.CreateDirectory(Path.Combine(root.FullName, "components", "libargon2"));
            WriteManifest(component.FullName, library, new string('A', 64));
            Assert.Throws<InvalidDataException>(() => ManagedLibArgon2Bootstrap.TryDiscover(host.FullName));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void MissingComponentIsAnExplicitUnavailableState()
    {
        var host = Directory.CreateTempSubdirectory("RcloneUI-ARGON2-MISSING-");
        try { Assert.Null(ManagedLibArgon2Bootstrap.TryDiscover(host.FullName)); }
        finally { host.Delete(recursive: true); }
    }

    private static void WriteManifest(string directory, string library, string sha256) =>
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new { format = 1, version = "20190702", sha256, library }));
}
