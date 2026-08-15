using System.Collections.Immutable;
using System.Text.Json;
using RcloneUI.Contracts.HostProtocol.V1;
using RcloneUI.Host;
using RcloneUI.Rclone;

namespace RcloneUI.IntegrationTests;

public sealed class HostCopyCancellationProtocolTests
{
    [Fact]
    public async Task HostRejectsCopyingAnIdenticalRemotePathOntoItself()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rcloneui-copy-self-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var capabilities = new RcloneCapabilitySnapshot(new("test", new string('A', 64), 1), new string('B', 64), new string('C', 64), ImmutableSortedSet.Create("sync/copy"), ImmutableSortedSet<string>.Empty, DateTimeOffset.UtcNow);
            var remoteId = Guid.NewGuid();
            using var authority = new HostStateAuthority(root, new ScriptedRcloneRuntime(capabilities, []), new Resolver(remoteId));

            var rejected = await authority.DispatchAsync(Command("start-copy", new { sourceRemoteId = remoteId, sourcePath = "/photos/", destinationRemoteId = remoteId, destinationPath = "photos", capabilityBinding = capabilities.Binding }), TestContext.Current.CancellationToken);

            Assert.Equal("copy-source-equals-destination", rejected.ResultType);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task HostCancelsOnlyItsTrackedRunningCopy()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rcloneui-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var capabilities = new RcloneCapabilitySnapshot(new("test", new string('A', 64), 1), new string('B', 64), new string('C', 64), ImmutableSortedSet.Create("sync/copy"), ImmutableSortedSet<string>.Empty, DateTimeOffset.UtcNow);
            var runtime = new ScriptedRcloneRuntime(capabilities, [new(RclonePrimitive.Copy, new(1, 10, 1, 0, 1, TimeSpan.Zero, false), ScriptedRcloneRuntime.Success(), TimeSpan.FromMilliseconds(100))]);
            var remoteId = Guid.NewGuid();
            using var authority = new HostStateAuthority(root, runtime, new Resolver(remoteId));

            var started = await authority.DispatchAsync(Command("start-copy", new { sourceRemoteId = remoteId, sourcePath = "source", destinationRemoteId = remoteId, destinationPath = "destination", capabilityBinding = capabilities.Binding }), TestContext.Current.CancellationToken);
            var runId = started.Body.GetProperty("runId").GetGuid();
            var cancelled = await authority.DispatchAsync(Command("cancel-copy", new { runId }), TestContext.Current.CancellationToken);

            Assert.Equal("copy-cancel-requested", cancelled.ResultType);
            await Task.Delay(150, TestContext.Current.CancellationToken);
            var snapshot = await authority.DispatchAsync(Command("get-snapshot", new { }), TestContext.Current.CancellationToken);
            Assert.Equal("cancelled", Assert.Single(snapshot.Body.GetProperty("copyRuns").EnumerateArray()).GetProperty("state").GetString());
            var unknown = await authority.DispatchAsync(Command("cancel-copy", new { runId = Guid.NewGuid() }), TestContext.Current.CancellationToken);
            Assert.Equal("copy-cancel-not-running", unknown.ResultType);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ProtocolEnvelope Command(string commandType, object arguments) => ProtocolEnvelope.CreateRequest(MessageType.Command, new("copy-cancel-request"), 1, new(new("client"), 0), new(new($"copy-cancel-{Guid.NewGuid():N}"), new("copy-cancel"), DateTimeOffset.UtcNow.AddMinutes(1)), JsonSerializer.SerializeToUtf8Bytes(new { commandType, arguments }));

    private sealed class Resolver(Guid remoteId) : IHostRemoteResolver
    {
        public string SessionState => "operational";
        public ValueTask<IReadOnlyList<HostRemoteSummary>> ListAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<HostRemoteSummary>>([]);
        public ValueTask<string?> ResolveFileSystemAsync(Guid id, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(id == remoteId ? "remote:" : null);
    }
}
