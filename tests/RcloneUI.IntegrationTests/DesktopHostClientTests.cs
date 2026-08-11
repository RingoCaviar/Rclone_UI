using System.Runtime.Versioning;
using System.Text.Json;
using RcloneUI.Desktop.Presentation;
using RcloneUI.Host;

namespace RcloneUI.IntegrationTests;

[SupportedOSPlatform("windows")]
public sealed class DesktopHostClientTests
{
    [Fact]
    public async Task DesktopClientAuthenticatesAndRefreshesRealHostSnapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "RcloneUI.Desktop.Host.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using (var host = BackgroundHostShell.TryCreate(root, Guid.NewGuid()))
            {
                Assert.NotNull(host); host.Start();
                var client = new NamedPipeDesktopHostClient(root);
                var snapshot = await client.GetSnapshotAsync(cancellationToken);
                Assert.Equal("operational", snapshot.Body.GetProperty("session").GetString());
                Assert.Empty(snapshot.Body.GetProperty("remotes").EnumerateArray());
                using var arguments = JsonDocument.Parse("{}");
                var command = await client.SendCommandAsync("activate-ui", arguments.RootElement, cancellationToken);
                Assert.Equal("activated", command.GetProperty("resultType").GetString());
                Assert.Equal(1, (await client.GetSnapshotAsync(cancellationToken)).Body.GetProperty("activationCount").GetInt32());
            }
        }
        finally
        {
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(root, recursive: true); break; }
                catch (IOException) when (attempt < 10) { await Task.Delay(25 * (attempt + 1), cancellationToken); }
            }
        }
    }

    [Fact]
    public async Task ControllerFailsClosedWhenEndpointIsUnavailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var shell = new DesktopShellState();
        var controller = new DesktopHostController(new NamedPipeDesktopHostClient(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))), shell);
        await controller.ReconnectAsync(cancellationToken);
        Assert.Equal("连接已中断", shell.ConnectionLabel);
        Assert.True(shell.NeedsAttention);
    }
}
