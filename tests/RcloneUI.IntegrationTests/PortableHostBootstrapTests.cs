using RcloneUI.Desktop.Presentation;

namespace RcloneUI.IntegrationTests;

public sealed class PortableHostBootstrapTests
{
    [Fact]
    public void ResolveCreatesStablePortableDataRootIdentity()
    {
        var root = Directory.CreateTempSubdirectory("RcloneUI-BOOTSTRAP-");
        try
        {
            var first = PortableHostBootstrap.Resolve(root.FullName);
            var second = PortableHostBootstrap.Resolve(root.FullName);
            Assert.Equal(first.DataRootId, second.DataRootId);
            Assert.StartsWith(root.FullName, first.DataRootPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(first.DataRootPath, "data-root.id")));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void CorruptIdentityFailsClosed()
    {
        var root = Directory.CreateTempSubdirectory("RcloneUI-BOOTSTRAP-");
        try
        {
            var data = Directory.CreateDirectory(Path.Combine(root.FullName, "data"));
            File.WriteAllText(Path.Combine(data.FullName, "data-root.id"), "not-an-id");
            Assert.Throws<InvalidDataException>(() => PortableHostBootstrap.Resolve(root.FullName));
        }
        finally { root.Delete(recursive: true); }
    }
}
