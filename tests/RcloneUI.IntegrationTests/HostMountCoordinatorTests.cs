using System.Collections.Immutable;
using System.Text.Json;
using RcloneUI.Host;
using RcloneUI.Mounts;
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
        var coordinator = new HostMountCoordinator(runtime, new FakeResolver(), namespaceProbe, new FakeWinFsp());

        var (startType, mounted) = await coordinator.StartReadOnlyAsync(Guid.NewGuid(), "photos", MountPresentationMode.NetworkDrive, DriveLetterSelection.Preferred, 'R', null, "Cloud", capabilities.Binding, TestContext.Current.CancellationToken);

        Assert.Equal("mount-ready", startType);
        Assert.NotNull(mounted);
        var mountRequest = Assert.Single(runtime.Requests);
        Assert.Equal(RclonePrimitive.Mount, mountRequest.Primitive);
        Assert.True(mountRequest.MountOptions?.ReadOnly);
        Assert.True(mountRequest.MountOptions?.NetworkMode);
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
        var coordinator = new HostMountCoordinator(runtime, new FakeResolver(), new FakeNamespace { Presented = true }, new FakeWinFsp());

        var (resultType, snapshot) = await coordinator.StartReadOnlyAsync(Guid.NewGuid(), string.Empty, MountPresentationMode.NetworkDrive, DriveLetterSelection.Preferred, 'R', null, "Cloud", capabilities.Binding, TestContext.Current.CancellationToken);

        Assert.Equal("mount-conflict", resultType);
        Assert.Equal("drive-letter-conflict", snapshot?.DiagnosticCode);
        Assert.Empty(runtime.Requests);
    }

    [Theory]
    [InlineData(MountPresentationMode.NetworkDrive, true)]
    [InlineData(MountPresentationMode.FixedDrive, false)]
    public async Task DrivePresentationMapsNetworkMode(MountPresentationMode presentationMode, bool networkMode)
    {
        var capabilities = Capabilities();
        var runtime = new ScriptedRcloneRuntime(capabilities, [new(RclonePrimitive.Mount, Stats(), ScriptedRcloneRuntime.Success())]);
        var coordinator = new HostMountCoordinator(runtime, new FakeResolver(), new FakeNamespace(), new FakeWinFsp());

        var (resultType, _) = await coordinator.StartReadOnlyAsync(Guid.NewGuid(), string.Empty, presentationMode, DriveLetterSelection.Preferred, 'S', null, "Cloud", capabilities.Binding, TestContext.Current.CancellationToken);

        Assert.Equal("mount-ready", resultType);
        Assert.Equal(networkMode, Assert.Single(runtime.Requests).MountOptions?.NetworkMode);
    }

    [Fact]
    public async Task AutomaticDriveRequiresAndUsesResolvedMountPoint()
    {
        var capabilities = Capabilities();
        using var body = JsonDocument.Parse("""{"output":{"mountPoint":"T:"}}""");
        var result = new RcloneExecutionResult(true, false, null, body.RootElement.Clone());
        var runtime = new ScriptedRcloneRuntime(capabilities, [new(RclonePrimitive.Mount, Stats(), result)]);
        var coordinator = new HostMountCoordinator(runtime, new FakeResolver(), new FakeNamespace(), new FakeWinFsp());

        var (resultType, mounted) = await coordinator.StartReadOnlyAsync(Guid.NewGuid(), string.Empty, MountPresentationMode.NetworkDrive, DriveLetterSelection.Automatic, 'R', null, "Cloud", capabilities.Binding, TestContext.Current.CancellationToken);

        Assert.Equal("mount-ready", resultType);
        Assert.Equal("*", Assert.Single(runtime.Requests).Destination?.Path);
        Assert.Equal("T:", mounted?.MountPoint);
    }

    [Fact]
    public async Task FixedDirectoryRequiresExistingEmptyTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "RcloneUI.Mount.Directory.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var capabilities = Capabilities();
            var runtime = new ScriptedRcloneRuntime(capabilities, [new(RclonePrimitive.Mount, Stats(), ScriptedRcloneRuntime.Success())]);
            var coordinator = new HostMountCoordinator(runtime, new FakeResolver(), new FakeNamespace(), new FakeWinFsp());

            var (resultType, mounted) = await coordinator.StartReadOnlyAsync(Guid.NewGuid(), string.Empty, MountPresentationMode.FixedDirectory, DriveLetterSelection.Preferred, 'R', root, "Cloud", capabilities.Binding, TestContext.Current.CancellationToken);

            Assert.Equal("mount-ready", resultType);
            Assert.Equal(root, mounted?.MountPoint);
            Assert.False(Assert.Single(runtime.Requests).MountOptions?.NetworkMode);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RestartMarksMissingNamespaceAsNeedsRemountWithoutCallingRclone()
    {
        var id = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var journal = new FakeJournal([new(id, profileId, "R:", MountPresentationMode.NetworkDrive, "ready", now, now, null)]);
        var runtime = new ScriptedRcloneRuntime(Capabilities(), []);

        var coordinator = new HostMountCoordinator(runtime, new FakeResolver(), new FakeNamespace(), new FakeWinFsp(), journal);

        var snapshot = Assert.Single(coordinator.Snapshots);
        Assert.Equal("needs-remount", snapshot.State);
        Assert.Equal("mount-process-interrupted", snapshot.DiagnosticCode);
        Assert.Empty(runtime.Requests);
        Assert.DoesNotContain("test:", JsonSerializer.Serialize(journal.Records));
    }

    [Fact]
    public async Task RestartDoesNotUnmountPresentedNamespaceWithUnknownOwnership()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var journal = new FakeJournal([new(id, Guid.NewGuid(), "S:", MountPresentationMode.FixedDrive, "ready", now, now, null)]);
        var runtime = new ScriptedRcloneRuntime(Capabilities(), []);
        var coordinator = new HostMountCoordinator(runtime, new FakeResolver(), new FakeNamespace { Presented = true }, new FakeWinFsp(), journal);

        var snapshot = Assert.Single(coordinator.Snapshots);
        Assert.Equal("recovery-required", snapshot.State);
        var (resultType, _) = await coordinator.StopAsync(id, Capabilities().Binding, TestContext.Current.CancellationToken);

        Assert.Equal("mount-recovery-required", resultType);
        Assert.Empty(runtime.Requests);
    }

    [Fact]
    public async Task CorruptJournalFailsClosed()
    {
        var runtime = new ScriptedRcloneRuntime(Capabilities(), []);
        var coordinator = new HostMountCoordinator(runtime, new FakeResolver(), new FakeNamespace(), new FakeWinFsp(), new ThrowingJournal());

        var (resultType, snapshot) = await coordinator.StartReadOnlyAsync(Guid.NewGuid(), string.Empty, MountPresentationMode.NetworkDrive, DriveLetterSelection.Preferred, 'R', null, "Cloud", Capabilities().Binding, TestContext.Current.CancellationToken, Guid.NewGuid());

        Assert.Equal("mount-recovery-required", resultType);
        Assert.Equal("mount-lifecycle-journal-corrupt", snapshot?.DiagnosticCode);
        Assert.Empty(runtime.Requests);
    }

    [Fact]
    public async Task FileJournalSurvivesRestartAndContainsNoRemotePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "RcloneUI.Mount.Journal.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var profileId = Guid.NewGuid();
            var runtime = new ScriptedRcloneRuntime(Capabilities(), [new(RclonePrimitive.Mount, Stats(), ScriptedRcloneRuntime.Success())]);
            var first = new HostMountCoordinator(runtime, new FakeResolver(), new FakeNamespace(), new FakeWinFsp(), new HostMountLifecycleJournal(root));
            var (resultType, mounted) = await first.StartReadOnlyAsync(Guid.NewGuid(), "private/photos", MountPresentationMode.NetworkDrive, DriveLetterSelection.Preferred, 'R', null, "Cloud", Capabilities().Binding, TestContext.Current.CancellationToken, profileId);

            Assert.Equal("mount-ready", resultType);
            var raw = File.ReadAllText(Path.Combine(root, "runtime", "mount-lifecycle.json"));
            Assert.DoesNotContain("private/photos", raw);
            Assert.DoesNotContain("test:", raw);

            var restarted = new HostMountCoordinator(new ScriptedRcloneRuntime(Capabilities(), []), new FakeResolver(), new FakeNamespace(), new FakeWinFsp(), new HostMountLifecycleJournal(root));
            var recovered = Assert.Single(restarted.Snapshots);
            Assert.Equal(mounted?.InstanceId, recovered.InstanceId);
            Assert.Equal(profileId, recovered.ProfileId);
            Assert.Equal("needs-remount", recovered.State);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
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

    private sealed class FakeWinFsp : IWinFspDetector
    {
        public WinFspSnapshot Inspect() => new("ready", "2.1-test", "winfsp-ready");
    }

    private sealed class FakeJournal(IReadOnlyList<HostMountLifecycleRecord> records) : IHostMountLifecycleJournal
    {
        public IReadOnlyList<HostMountLifecycleRecord> Records { get; private set; } = records;
        public IReadOnlyList<HostMountLifecycleRecord> Read() => Records;
        public void Write(IReadOnlyList<HostMountLifecycleRecord> values) => Records = values;
    }

    private sealed class ThrowingJournal : IHostMountLifecycleJournal
    {
        public IReadOnlyList<HostMountLifecycleRecord> Read() => throw new InvalidDataException();
        public void Write(IReadOnlyList<HostMountLifecycleRecord> records) => throw new InvalidOperationException();
    }
}
