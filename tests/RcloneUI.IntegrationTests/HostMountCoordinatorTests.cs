using System.Collections.Immutable;
using RcloneUI.Host;
using RcloneUI.Rclone;

namespace RcloneUI.IntegrationTests;

public sealed class HostMountCoordinatorTests
{
    [Fact]
    public async Task ReadOnlyMountRequiresNamespaceEvidenceAndCanUnmount()
    {
        var capabilities = Capabilities();
        var runtime = new ScriptedRcloneRuntime(capabilities,
        [
            new(RclonePrimitive.Mount, Stats(), ScriptedRcloneRuntime.Success()),
            new(RclonePrimitive.Unmount, Stats(), ScriptedRcloneRuntime.Success()),
        ]);
        var namespaceProbe = new FakeNamespace();
        var coordinator = new HostMountCoordinator(runtime, new FakeResolver(), namespaceProbe);

        var (startType, mounted) = await coordinator.StartReadOnlyAsync(Guid.NewGuid(), "photos", 'R', "Cloud", capabilities.Binding, TestContext.Current.CancellationToken);

        Assert.Equal("mount-ready", startType);
        Assert.NotNull(mounted);
        var mountRequest = Assert.Single(runtime.Requests);
        Assert.Equal(RclonePrimitive.Mount, mountRequest.Primitive);
        Assert.True(mountRequest.MountOptions?.ReadOnly);
        Assert.Equal("R:", mountRequest.Destination?.Path);

        var (stopType, stopped) = await coordinator.StopAsync(mounted.InstanceId, capabilities.Binding, TestContext.Current.CancellationToken);

        Assert.Equal("mount-stopped", stopType);
        Assert.Equal("stopped", stopped?.State);
        Assert.Equal(RclonePrimitive.Unmount, runtime.Requests.Last().Primitive);
        Assert.Empty(coordinator.Snapshots);
    }

    [Fact]
    public async Task OccupiedDriveLetterFailsBeforeRcloneExecution()
    {
        var capabilities = Capabilities();
        var runtime = new ScriptedRcloneRuntime(capabilities, []);
        var coordinator = new HostMountCoordinator(runtime, new FakeResolver(), new FakeNamespace { Presented = true });

        var (resultType, snapshot) = await coordinator.StartReadOnlyAsync(Guid.NewGuid(), string.Empty, 'R', "Cloud", capabilities.Binding, TestContext.Current.CancellationToken);

        Assert.Equal("mount-conflict", resultType);
        Assert.Equal("drive-letter-conflict", snapshot?.DiagnosticCode);
        Assert.Empty(runtime.Requests);
    }

    private static RcloneCapabilitySnapshot Capabilities() => new(new("test", new string('A', 64), 1), new string('B', 64), new string('C', 64), ImmutableSortedSet.Create("mount/mount", "mount/unmount"), ImmutableSortedSet.Create("mount"), DateTimeOffset.UtcNow);
    private static RcloneTransferStats Stats() => new(0, 0, 0, 0, 0, TimeSpan.Zero, true);

    private sealed class FakeResolver : IHostRemoteResolver
    {
        public string SessionState => "operational";
        public ValueTask<IReadOnlyList<HostRemoteSummary>> ListAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<HostRemoteSummary>>([]);
        public ValueTask<string?> ResolveFileSystemAsync(Guid remoteId, CancellationToken cancellationToken) => ValueTask.FromResult<string?>("test:");
    }

    private sealed class FakeNamespace : IWindowsMountNamespace
    {
        public bool Presented { get; set; }
        public bool IsPresented(string mountPoint) => Presented;
        public ValueTask<bool> WaitForAsync(string mountPoint, bool presented, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Presented = presented;
            return ValueTask.FromResult(true);
        }
    }
}
