using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using RcloneUI.Desktop.Presentation;
using RcloneUI.Host;
using RcloneUI.Rclone;
using RcloneUI.Remotes;

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
                Assert.Equal("locked", snapshot.Body.GetProperty("session").GetString());
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

    [Fact]
    public async Task CopyCommandReportsActualRcloneRuntimeTerminalState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "RcloneUI.Desktop.Copy.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var binary = new RcloneBinaryIdentity("test", new string('A', 64), 1);
        var capabilities = new RcloneCapabilitySnapshot(binary, new string('B', 64), new string('C', 64), ImmutableSortedSet.Create("sync/copy"), ImmutableSortedSet<string>.Empty, DateTimeOffset.UtcNow);
        var runtime = new ScriptedRcloneRuntime(capabilities, [new(RclonePrimitive.Copy, new(64, 64, 1, 0, 64, TimeSpan.FromSeconds(1), true), ScriptedRcloneRuntime.Success())]);
        try
        {
            await using (var host = BackgroundHostShell.TryCreate(root, Guid.NewGuid(), runtime))
            {
                Assert.NotNull(host); host.Start(); var client = new NamedPipeDesktopHostClient(root);
                using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { sourceFs = "source:", sourcePath = "from", destinationFs = "target:", destinationPath = "to", capabilityBinding = capabilities.Binding }));
                var accepted = await client.SendCommandAsync("start-copy", arguments.RootElement, cancellationToken);
                Assert.Equal("copy-accepted", accepted.GetProperty("resultType").GetString());
                JsonElement run;
                do
                {
                    await Task.Delay(10, cancellationToken);
                    run = (await client.GetSnapshotAsync(cancellationToken)).Body.GetProperty("copyRuns").EnumerateArray().Single().Clone();
                } while (run.GetProperty("state").GetString() == "running");
                Assert.Equal("succeeded", run.GetProperty("state").GetString());
                Assert.Equal(64, run.GetProperty("bytes").GetInt64());
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SnapshotProjectsRemoteSummariesWithoutConfigurationSecrets()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "RcloneUI.Desktop.Remote.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var projection = new VaultHostRemoteProjection(new TestRemoteStore());
        try
        {
            await using (var host = BackgroundHostShell.TryCreate(root, Guid.NewGuid(), remotes: projection))
            {
                Assert.NotNull(host); host.Start();
                var snapshot = await new NamedPipeDesktopHostClient(root).GetSnapshotAsync(cancellationToken);
                var json = snapshot.Body.GetRawText();
                var remote = Assert.Single(snapshot.Body.GetProperty("remotes").EnumerateArray());
                Assert.Equal("Personal Drive", remote.GetProperty("displayName").GetString());
                Assert.DoesNotContain("super-secret", json, StringComparison.Ordinal);
                Assert.DoesNotContain("configuration", json, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class TestRemoteStore : IRemoteStore
    {
        private readonly StoredRemote remote = new(new RcloneUI.Remotes.RemoteId(Guid.NewGuid()), "Personal Drive", "drive", 1, new Dictionary<string, string> { ["token"] = "super-secret" }, new(RemoteHealthKind.Healthy, DateTimeOffset.UtcNow, "caps", null));
        public ValueTask<IReadOnlyList<RemoteSummary>> ListAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<RemoteSummary>>([new(remote.Id, remote.DisplayName, remote.ProviderType, remote.Revision, remote.Health)]);
        public ValueTask<StoredRemote?> ReadAsync(RcloneUI.Remotes.RemoteId id, CancellationToken cancellationToken) => ValueTask.FromResult<StoredRemote?>(remote);
        public ValueTask<StoredRemote> UpsertAsync(StoredRemote value, ulong expectedRevision, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<bool> DeleteAsync(RcloneUI.Remotes.RemoteId id, ulong expectedRevision, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
