using System.Collections.Immutable;
using System.Text.Json;
using RcloneUI.Contracts.HostProtocol.V1;
using RcloneUI.Host;
using RcloneUI.Rclone;

namespace RcloneUI.IntegrationTests;

public sealed class HostBrowseProtocolTests
{
    [Fact]
    public async Task BrowseProjectsNestedRcloneOutputIntoBoundedDesktopItems()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rcloneui-browse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var capabilities = new RcloneCapabilitySnapshot(new("test", new string('A', 64), 1), new string('B', 64), new string('C', 64), ImmutableSortedSet.Create("operations/list"), ImmutableSortedSet<string>.Empty, DateTimeOffset.UtcNow);
            var runtime = new ScriptedRcloneRuntime(capabilities, [new(RclonePrimitive.List, new(0, 0, 0, 0, 0, TimeSpan.Zero, true), Success("""{"output":{"list":[{"Path":"notes","IsDir":true,"Size":999,"Hashes":{"sha1":"secret"}},{"Path":"readme.txt","Size":42,"MimeType":"text/plain"}]}}"""))]);
            var remoteId = Guid.NewGuid();
            using var authority = new HostStateAuthority(root, runtime, new BrowseProjection(remoteId));

            var result = await authority.DispatchAsync(Command(new { remoteId, path = "docs", capabilityBinding = capabilities.Binding }), TestContext.Current.CancellationToken);

            Assert.Equal("browse-completed", result.ResultType);
            var items = result.Body.GetProperty("items");
            Assert.Equal(2, items.GetArrayLength());
            Assert.Equal("notes", items[0].GetProperty("path").GetString());
            Assert.True(items[0].GetProperty("isDirectory").GetBoolean());
            Assert.Equal(999, items[0].GetProperty("size").GetInt64());
            Assert.Equal("readme.txt", items[1].GetProperty("path").GetString());
            Assert.DoesNotContain("secret", result.Body.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("MimeType", result.Body.GetRawText(), StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CreateFolderMapsOnlyAValidatedChildPathToRcloneMkdir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rcloneui-mkdir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var capabilities = new RcloneCapabilitySnapshot(new("test", new string('A', 64), 1), new string('B', 64), new string('C', 64), ImmutableSortedSet.Create("operations/mkdir"), ImmutableSortedSet<string>.Empty, DateTimeOffset.UtcNow);
            var runtime = new ScriptedRcloneRuntime(capabilities, [new(RclonePrimitive.MakeDirectory, new(0, 0, 0, 0, 0, TimeSpan.Zero, true), Success("{}"))]);
            var remoteId = Guid.NewGuid();
            using var authority = new HostStateAuthority(root, runtime, new BrowseProjection(remoteId));

            var result = await authority.DispatchAsync(FolderCommand(new { remoteId, path = "docs/2026", name = "reports", capabilityBinding = capabilities.Binding }), TestContext.Current.CancellationToken);

            Assert.Equal("folder-created", result.ResultType);
            var request = Assert.Single(runtime.Requests);
            Assert.Equal(RclonePrimitive.MakeDirectory, request.Primitive);
            Assert.Equal("remote-abc", request.Source.FileSystem);
            Assert.Equal("docs/2026/reports", request.Source.Path);
            var invalid = await authority.DispatchAsync(FolderCommand(new { remoteId, path = "docs", name = "../escape", capabilityBinding = capabilities.Binding }), TestContext.Current.CancellationToken);
            Assert.Equal("folder-create-invalid", invalid.ResultType);
            Assert.Single(runtime.Requests);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DeleteFileMapsOnlyAValidatedChildFileToRcloneDeleteFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rcloneui-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var capabilities = new RcloneCapabilitySnapshot(new("test", new string('A', 64), 1), new string('B', 64), new string('C', 64), ImmutableSortedSet.Create("operations/deletefile"), ImmutableSortedSet<string>.Empty, DateTimeOffset.UtcNow);
            var runtime = new ScriptedRcloneRuntime(capabilities, [new(RclonePrimitive.DeleteFile, new(0, 0, 0, 0, 0, TimeSpan.Zero, true), Success("{}"))]);
            var remoteId = Guid.NewGuid();
            using var authority = new HostStateAuthority(root, runtime, new BrowseProjection(remoteId));

            var result = await authority.DispatchAsync(DeleteFileCommand(new { remoteId, path = "docs", name = "report.pdf", capabilityBinding = capabilities.Binding }), TestContext.Current.CancellationToken);

            Assert.Equal("file-deleted", result.ResultType);
            var request = Assert.Single(runtime.Requests);
            Assert.Equal(RclonePrimitive.DeleteFile, request.Primitive);
            Assert.Equal("docs/report.pdf", request.Source.Path);
            var invalid = await authority.DispatchAsync(DeleteFileCommand(new { remoteId, path = "docs", name = "nested/file.txt", capabilityBinding = capabilities.Binding }), TestContext.Current.CancellationToken);
            Assert.Equal("file-delete-invalid", invalid.ResultType);
            Assert.Single(runtime.Requests);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ProtocolEnvelope Command(object arguments) => ProtocolEnvelope.CreateRequest(MessageType.Command, new("browse-request"), 1, new(new("client"), 0), new(new("browse-key"), new("browse-cancel"), DateTimeOffset.UtcNow.AddMinutes(1)), JsonSerializer.SerializeToUtf8Bytes(new { commandType = "browse-remote", arguments }));

    private static ProtocolEnvelope FolderCommand(object arguments) => ProtocolEnvelope.CreateRequest(MessageType.Command, new("folder-request"), 1, new(new("client"), 0), new(new($"folder-key-{Guid.NewGuid():N}"), new("folder-cancel"), DateTimeOffset.UtcNow.AddMinutes(1)), JsonSerializer.SerializeToUtf8Bytes(new { commandType = "create-remote-folder", arguments }));
    private static ProtocolEnvelope DeleteFileCommand(object arguments) => ProtocolEnvelope.CreateRequest(MessageType.Command, new("delete-file-request"), 1, new(new("client"), 0), new(new($"delete-file-key-{Guid.NewGuid():N}"), new("delete-file-cancel"), DateTimeOffset.UtcNow.AddMinutes(1)), JsonSerializer.SerializeToUtf8Bytes(new { commandType = "delete-remote-file", arguments }));

    private static RcloneExecutionResult Success(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new(true, false, null, document.RootElement.Clone());
    }

    private sealed class BrowseProjection(Guid id) : IHostRemoteResolver
    {
        public string SessionState => "operational";
        public ValueTask<IReadOnlyList<HostRemoteSummary>> ListAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<HostRemoteSummary>>([]);
        public ValueTask<string?> ResolveFileSystemAsync(Guid remoteId, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(remoteId == id ? "remote-abc" : null);
    }
}
