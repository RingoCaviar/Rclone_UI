using System.IO.Compression;
using RcloneUI.Diagnostics;

namespace RcloneUI.IntegrationTests;

public sealed class DiagnosticExporterTests
{
    [Fact]
    public async Task PreviewAndArchiveExcludeSecretsAndMaskPaths()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new TempDirectory();
        var exporter = new DiagnosticExporter();
        DiagnosticItem[] items = [new("host.txt", "token=abc123\npath=C:\\Users\\Alice\\vault\nAuthorization: Bearer secret"), new("vault.txt", "plaintext", true)];
        var preview = exporter.Preview(items);
        Assert.True(preview.TotalRedactions >= 3);
        Assert.False(preview.Items.Single(x => x.LogicalName == "vault.txt").Included);
        var result = await exporter.ExportAsync(items, Path.Combine(workspace.Path, "diagnostics.zip"), cancellationToken);
        using var archive = ZipFile.OpenRead(result.ArchivePath); using var reader = new StreamReader(Assert.Single(archive.Entries).Open()); var text = await reader.ReadToEndAsync(cancellationToken);
        Assert.DoesNotContain("abc123", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Alice", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer secret", text, StringComparison.Ordinal);
        Assert.Contains("<redacted>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsafeNamesAndOversizedLogsAreExcluded()
    {
        var preview = new DiagnosticExporter().Preview([new("../escape.txt", "x"), new("huge.txt", new string('x', DiagnosticExporter.MaximumItemBytes + 1))]);
        Assert.All(preview.Items, item => Assert.False(item.Included));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("RcloneUI-DIAG-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
