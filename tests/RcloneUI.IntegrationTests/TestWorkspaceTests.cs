namespace RcloneUI.IntegrationTests;

public sealed class TestWorkspaceTests
{
    [Fact]
    public void ScratchStateIsScopedToTheSystemTempDirectory()
    {
        var scratch = Directory.CreateTempSubdirectory("RcloneUI-TEST-");
        try
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), scratch.FullName, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }
}
