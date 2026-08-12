using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using RcloneUI.DataRoot;
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
        var store = new TestRemoteStore();
        try
        {
            await using (var host = BackgroundHostShell.TryCreate(root, Guid.NewGuid(), runtime, new VaultHostRemoteProjection(store, new(root))))
            {
                Assert.NotNull(host); host.Start(); var client = new NamedPipeDesktopHostClient(root);
                using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { sourceRemoteId = store.Id, sourcePath = "from", destinationRemoteId = store.Id, destinationPath = "to", capabilityBinding = capabilities.Binding }));
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
                var request = Assert.Single(runtime.Requests);
                Assert.Equal($"rcloneui_{store.Id:N}:", request.Source.FileSystem);
                Assert.DoesNotContain("super-secret", JsonSerializer.Serialize(request), StringComparison.Ordinal);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DownloadCommandMapsSelectedAbsoluteFolderToLocalRcloneFileSystem()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "RcloneUI.Desktop.Download.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "downloads"); Directory.CreateDirectory(destination);
        var binary = new RcloneBinaryIdentity("test", new string('A', 64), 1);
        var capabilities = new RcloneCapabilitySnapshot(binary, new string('B', 64), new string('C', 64), ImmutableSortedSet.Create("sync/copy"), ImmutableSortedSet<string>.Empty, DateTimeOffset.UtcNow);
        var runtime = new ScriptedRcloneRuntime(capabilities, [new(RclonePrimitive.Copy, new(1, 1, 1, 0, 1, TimeSpan.Zero, true), ScriptedRcloneRuntime.Success())]);
        var store = new TestRemoteStore();
        try
        {
            await using (var host = BackgroundHostShell.TryCreate(root, Guid.NewGuid(), runtime, new VaultHostRemoteProjection(store, new(root))))
            {
                Assert.NotNull(host); host.Start(); var client = new NamedPipeDesktopHostClient(root);
                using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { sourceRemoteId = store.Id, sourcePath = "photos", destinationLocalPath = destination, capabilityBinding = capabilities.Binding }));

                var accepted = await client.SendCommandAsync("start-copy", arguments.RootElement, cancellationToken);

                Assert.Equal("copy-accepted", accepted.GetProperty("resultType").GetString());
                var request = Assert.Single(runtime.Requests);
                Assert.Equal(destination.Replace('\\', '/').TrimEnd('/') + "/", request.Destination?.FileSystem);
                Assert.Equal(string.Empty, request.Destination?.Path);
                JsonElement run;
                do
                {
                    await Task.Delay(10, cancellationToken);
                    run = (await client.GetSnapshotAsync(cancellationToken)).Body.GetProperty("copyRuns").EnumerateArray().Single().Clone();
                } while (run.GetProperty("state").GetString() == "running");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UploadCommandMapsSelectedAbsoluteFolderToLocalRcloneFileSystem()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "RcloneUI.Desktop.Upload.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var source = Path.Combine(root, "uploads"); Directory.CreateDirectory(source);
        var binary = new RcloneBinaryIdentity("test", new string('A', 64), 1);
        var capabilities = new RcloneCapabilitySnapshot(binary, new string('B', 64), new string('C', 64), ImmutableSortedSet.Create("sync/copy"), ImmutableSortedSet<string>.Empty, DateTimeOffset.UtcNow);
        var runtime = new ScriptedRcloneRuntime(capabilities, [new(RclonePrimitive.Copy, new(1, 1, 1, 0, 1, TimeSpan.Zero, true), ScriptedRcloneRuntime.Success())]);
        var store = new TestRemoteStore();
        try
        {
            await using (var host = BackgroundHostShell.TryCreate(root, Guid.NewGuid(), runtime, new VaultHostRemoteProjection(store, new(root))))
            {
                Assert.NotNull(host); host.Start(); var client = new NamedPipeDesktopHostClient(root);
                using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { sourceLocalPath = source, destinationRemoteId = store.Id, destinationPath = "backup", maximumTransferBytes = 512L * 1024 * 1024, maximumDurationMinutes = 30, capabilityBinding = capabilities.Binding }));

                var accepted = await client.SendCommandAsync("start-copy", arguments.RootElement, cancellationToken);

                Assert.Equal("copy-accepted", accepted.GetProperty("resultType").GetString());
                var request = Assert.Single(runtime.Requests);
                Assert.Equal(source.Replace('\\', '/').TrimEnd('/') + "/", request.Source.FileSystem);
                Assert.Equal(string.Empty, request.Source.Path);
                Assert.Equal("backup", request.Destination?.Path);
                Assert.Equal(512L * 1024 * 1024, request.MaximumTransferBytes);
                Assert.Equal(TimeSpan.FromMinutes(30), request.MaximumDuration);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UnlockCommandOpensVaultWithoutPersistingPasswordMaterial()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "RcloneUI.Desktop.Unlock.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var vault = new HostVaultSession(root, new("C:\\not-loaded-in-test.dll", new string('A', 64)), (request, _) => ValueTask.FromResult(new DataRootOpenResult(DataRootOpenStatus.Opened, new EmptyDataRootSession(root), null)));
        try
        {
            await using (var host = BackgroundHostShell.TryCreate(root, Guid.NewGuid(), remotes: vault))
            {
                Assert.NotNull(host); host.Start(); var client = new NamedPipeDesktopHostClient(root);
                using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { passwordUtf8 = Convert.ToBase64String("correct horse"u8) }));
                var result = await client.SendCommandAsync("unlock-vault", arguments.RootElement, cancellationToken);
                Assert.Equal("vault-unlocked", result.GetProperty("resultType").GetString());
                Assert.Equal("operational", (await client.GetSnapshotAsync(cancellationToken)).Body.GetProperty("session").GetString());
                using var empty = JsonDocument.Parse("{}");
                var locked = await client.SendCommandAsync("lock-vault", empty.RootElement, cancellationToken);
                Assert.Equal("vault-locked", locked.GetProperty("resultType").GetString());
                Assert.Equal("locked", (await client.GetSnapshotAsync(cancellationToken)).Body.GetProperty("session").GetString());
                Assert.False(File.Exists(Path.Combine(root, "runtime", "idempotency.json")));
            }
        }
        finally { await vault.DisposeAsync(); Directory.Delete(root, recursive: true); }
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

    [Fact]
    public async Task AddRemoteCommandTestsBeforePublishingSummaryAndDoesNotJournalToken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "RcloneUI.Desktop.AddRemote.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var binary = new RcloneBinaryIdentity("test", new string('A', 64), 1);
        var capabilities = new RcloneCapabilitySnapshot(binary, new string('B', 64), new string('C', 64), ImmutableSortedSet.Create("operations/list"), ImmutableSortedSet<string>.Empty, DateTimeOffset.UtcNow);
        var runtime = new ScriptedRcloneRuntime(capabilities, [new(RclonePrimitive.List, new(0, 0, 0, 0, 0, TimeSpan.Zero, true), ScriptedRcloneRuntime.Success())]);
        var manager = new TestRemoteManager();
        try
        {
            await using (var host = BackgroundHostShell.TryCreate(root, Guid.NewGuid(), runtime, manager))
            {
                Assert.NotNull(host); host.Start(); var client = new NamedPipeDesktopHostClient(root);
                using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { displayName = "Personal Drive", providerType = "drive", tokenUtf8 = Convert.ToBase64String("top-secret"u8) }));
                var result = await client.SendCommandAsync("add-token-remote", arguments.RootElement, cancellationToken);
                Assert.Equal("remote-added", result.GetProperty("resultType").GetString());
                var snapshot = await client.GetSnapshotAsync(cancellationToken);
                Assert.Equal("Personal Drive", Assert.Single(snapshot.Body.GetProperty("remotes").EnumerateArray()).GetProperty("displayName").GetString());
                Assert.DoesNotContain("top-secret", snapshot.Body.GetRawText(), StringComparison.Ordinal);
                Assert.False(File.Exists(Path.Combine(root, "runtime", "idempotency.json")));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class TestRemoteStore : IRemoteStore
    {
        private readonly StoredRemote remote = new(new RcloneUI.Remotes.RemoteId(Guid.NewGuid()), "Personal Drive", "drive", 1, new Dictionary<string, string> { ["token"] = "super-secret" }, new(RemoteHealthKind.Healthy, DateTimeOffset.UtcNow, "caps", null));
        internal Guid Id => remote.Id.Value;
        public ValueTask<IReadOnlyList<RemoteSummary>> ListAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<RemoteSummary>>([new(remote.Id, remote.DisplayName, remote.ProviderType, remote.Revision, remote.Health)]);
        public ValueTask<StoredRemote?> ReadAsync(RcloneUI.Remotes.RemoteId id, CancellationToken cancellationToken) => ValueTask.FromResult<StoredRemote?>(remote);
        public ValueTask<StoredRemote> UpsertAsync(StoredRemote value, ulong expectedRevision, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<bool> DeleteAsync(RcloneUI.Remotes.RemoteId id, ulong expectedRevision, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class EmptyDataRootSession(string root) : IDataRootSession
    {
        public DataRootSnapshot Observe() => new(new(Guid.NewGuid()), Guid.NewGuid(), 1, 0, DataRootSessionState.Unlocked, root);
        public ValueTask<DataRootCommandResult> ExecuteAsync(DataRootCommand command, ulong expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<VaultRecord?> ReadAsync(Guid recordId, CancellationToken cancellationToken = default) => ValueTask.FromResult<VaultRecord?>(null);
        public void Lock() { }
        public ValueTask<bool> UnlockAsync(ReadOnlyMemory<byte> masterPasswordUtf8, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
        public ValueTask CloseAsync(string reason, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestRemoteManager : IHostRemoteManager
    {
        private HostRemoteSummary? remote;
        public string SessionState => "operational";
        public ValueTask<IReadOnlyList<HostRemoteSummary>> ListAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<HostRemoteSummary>>(remote is null ? [] : [remote]);
        public async ValueTask<string> AddTokenRemoteAsync(string displayName, string providerType, string token, IRcloneRuntime rclone, CancellationToken cancellationToken)
        {
            var handle = await rclone.StartAsync(new(Guid.NewGuid(), rclone.Capabilities.Binding, RclonePrimitive.List, new("test:", string.Empty), null, "remote-test"), cancellationToken);
            if (!(await rclone.WaitAsync(handle, cancellationToken)).Success) return "remote-test-failed";
            remote = new(Guid.NewGuid(), displayName, providerType, 1, "Healthy", null);
            return "remote-added";
        }
        public ValueTask<string> AddConnectionRemoteAsync(string displayName, string providerType, IReadOnlyDictionary<string, string> configuration, IRcloneRuntime rclone, CancellationToken cancellationToken) => AddTokenRemoteAsync(displayName, providerType, string.Empty, rclone, cancellationToken);
        public ValueTask<string> DeleteRemoteAsync(Guid remoteId, ulong expectedRevision, CancellationToken cancellationToken)
        {
            if (remote?.Id != remoteId || remote.Revision != expectedRevision) return ValueTask.FromResult("remote-delete-conflict");
            remote = null;
            return ValueTask.FromResult("remote-deleted");
        }
    }
}
