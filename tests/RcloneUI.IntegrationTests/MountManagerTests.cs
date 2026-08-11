using RcloneUI.Mounts;

namespace RcloneUI.IntegrationTests;

public sealed class MountManagerTests
{
    [Fact]
    public async Task RcStartIsNotReadyWithoutWindowsAndRootEvidence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = new Fixture(new(true, true, false, false, true, 0, 0, 0));
        var result = await fixture.Manager.StartAsync(Profile(), cancellationToken);
        Assert.Equal(MountState.NeedsRemount, result.State);
        Assert.Equal("mount-namespace-not-presented", result.DiagnosticCode);
        Assert.True(result.StartupCleanup?.ProvesCleanup);
    }

    [Fact]
    public async Task FailedStartBecomesRecoveryRequiredWhenNamespaceCleanupCannotBeProved()
    {
        var fixture = new Fixture(ReadyEvidence() with { ExpectedTokenVisible = false });
        fixture.Adapter.CleanupEvidence = new(true, false, false, DiagnosticCode: "namespace-still-visible");

        var result = await fixture.Manager.StartAsync(Profile(), TestContext.Current.CancellationToken);

        Assert.Equal(MountState.RecoveryRequired, result.State);
        Assert.Equal("failed-start-cleanup-not-proved", result.DiagnosticCode);
        Assert.Equal("namespace-still-visible", result.StartupCleanup?.DiagnosticCode);
    }

    [Fact]
    public async Task FailedNetworkStartRecordsWhenHostExitWasNeededForBoundedCleanup()
    {
        var fixture = new Fixture(ReadyEvidence() with { RootProbeSucceeded = false });
        fixture.Adapter.CleanupEvidence = new(true, true, true, HostExitRequired: true);

        var result = await fixture.Manager.StartAsync(Profile(), TestContext.Current.CancellationToken);

        Assert.Equal(MountState.NeedsRemount, result.State);
        Assert.True(result.StartupCleanup?.HostExitRequired);
    }

    [Fact]
    public async Task SafeUnmountRefusesUnknownCleanliness()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = new Fixture(new(true, true, true, true, null, null, null, null));
        var started = await fixture.Manager.StartAsync(Profile(), cancellationToken);
        var result = await fixture.Manager.StopAsync(started.InstanceId, MountStopMode.Safe, cancellationToken: cancellationToken);
        Assert.Equal(MountState.SafeUnmount, result.State);
        Assert.Equal(MountRisk.CannotProveClean, result.Risk);
        Assert.Equal(0, fixture.Adapter.StopCalls);
    }

    [Fact]
    public async Task ForcedUnmountPreservesPendingCacheForRecovery()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = new Fixture(new(true, true, true, true, true, 2, 42, 0));
        var started = await fixture.Manager.StartAsync(Profile(), cancellationToken);
        var result = await fixture.Manager.StopAsync(started.InstanceId, MountStopMode.Force, true, cancellationToken);
        Assert.Equal(MountState.RecoveryRequired, result.State);
        Assert.Equal(MountRisk.PendingWrites, result.Risk);
        Assert.Equal("recovery/cache", result.RecoveryCachePath);
        Assert.Equal(1, fixture.Adapter.StopCalls);
    }

    [Fact]
    public async Task SafeUnmountOnlyReportsStoppedWithCompleteCleanEvidence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = new Fixture(new(true, true, true, true, true, 0, 0, 0));
        var started = await fixture.Manager.StartAsync(Profile(), cancellationToken);
        var result = await fixture.Manager.StopAsync(started.InstanceId, MountStopMode.Safe, cancellationToken: cancellationToken);
        Assert.Equal(MountState.Stopped, result.State);
        Assert.Equal(MountRisk.None, result.Risk);
        Assert.True(result.StopEvidence?.ProvesStopped);
    }

    [Theory]
    [MemberData(nameof(UncleanDrainEvidence))]
    public async Task SafeUnmountRefusesIncompleteOrUnstableDrainEvidence(MountEvidence evidence)
    {
        var fixture = new Fixture(evidence);
        var started = await fixture.Manager.StartAsync(Profile(), TestContext.Current.CancellationToken);

        var result = await fixture.Manager.StopAsync(started.InstanceId, MountStopMode.Safe, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(MountState.SafeUnmount, result.State);
        Assert.Equal("cannot-prove-clean", result.DiagnosticCode);
        Assert.Equal(0, fixture.Adapter.StopCalls);
    }

    [Fact]
    public async Task RcUnmountWithoutNamespaceRemovalRequiresRecovery()
    {
        var fixture = new Fixture(ReadyEvidence());
        fixture.Adapter.StopEvidence = new(true, false, false, DiagnosticCode: "namespace-still-visible");
        var started = await fixture.Manager.StartAsync(Profile(), TestContext.Current.CancellationToken);

        var result = await fixture.Manager.StopAsync(started.InstanceId, MountStopMode.Safe, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(MountState.RecoveryRequired, result.State);
        Assert.Equal("unmount-cleanup-not-proved", result.DiagnosticCode);
        Assert.Equal("recovery/cache", result.RecoveryCachePath);
    }

    [Fact]
    public async Task ValidationFailsClosedForStaleCapabilityOrForeignDrive()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var adapter = new FakeAdapter(new(true, true, true, true, true, 0, 0, 0)) { Environment = Environment() with { DriveLetterAvailable = false, DriveLetterOwnedByProfile = false } };
        var manager = new MountManager(adapter, new MemoryJournal(), new FakeRecovery());
        Assert.Equal("drive-letter-conflict", (await manager.ValidateAsync(Profile(), cancellationToken)).DiagnosticCode);
        adapter.Environment = Environment() with { CapabilityBinding = "changed" };
        Assert.Equal("capability-binding-changed", (await manager.ValidateAsync(Profile(), cancellationToken)).DiagnosticCode);
    }

    [Fact]
    public async Task InterruptedMountIsNeverAutomaticallyRemounted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = new Fixture(new(false, false, false, false, null, null, null, null));
        var profile = Profile(); var id = MountInstanceId.New();
        await fixture.Journal.SaveAsync(new(id, profile, MountState.Starting, MountRisk.None, new(false, false, false, false, null, null, null, null), DateTimeOffset.UtcNow), cancellationToken);
        var result = Assert.Single(await fixture.Manager.ReconcileInterruptedAsync(cancellationToken));
        Assert.Equal(MountState.RecoveryRequired, result.State);
        Assert.Equal(MountRisk.Interrupted, result.Risk);
        Assert.Equal(0, fixture.Adapter.StartCalls);
    }

    [Theory]
    [MemberData(nameof(ReadinessFailures))]
    public async Task ReadyRequiresEveryIndependentWindowsProbe(MountEvidence evidence, string expectedCode)
    {
        var result = await new Fixture(evidence).Manager.StartAsync(Profile(), TestContext.Current.CancellationToken);
        Assert.Equal(MountState.NeedsRemount, result.State);
        Assert.Equal(expectedCode, result.DiagnosticCode);
    }

    public static TheoryData<MountEvidence, string> ReadinessFailures => new()
    {
        { ReadyEvidence() with { RcRequestAccepted = false }, "mount-rc-not-accepted" },
        { ReadyEvidence() with { ProcessAlive = false }, "mount-process-exited" },
        { ReadyEvidence() with { EndpointRegistered = false }, "mount-endpoint-not-registered" },
        { ReadyEvidence() with { NamespacePresented = false }, "mount-namespace-not-presented" },
        { ReadyEvidence() with { NamespaceOwnedByInstance = false }, "mount-namespace-owner-mismatch" },
        { ReadyEvidence() with { ExpectedTokenVisible = false }, "mount-token-not-visible" },
        { ReadyEvidence() with { RootProbeWithinDeadline = false }, "mount-root-probe-timeout" },
        { ReadyEvidence() with { RootProbeSucceeded = false }, "mount-root-probe-failed" },
        { ReadyEvidence() with { CacheObservable = false }, "mount-cache-observation-failed" }
    };

    public static TheoryData<MountEvidence> UncleanDrainEvidence => new()
    {
        ReadyEvidence() with { PendingFiles = 1, PendingBytes = 42 },
        ReadyEvidence() with { OpenFiles = 1 },
        ReadyEvidence() with { QueueObservable = false },
        ReadyEvidence() with { UploadingFiles = 1 },
        ReadyEvidence() with { FailedUploads = 1 },
        ReadyEvidence() with { OutOfSpace = true },
        ReadyEvidence() with { RemoteHealthy = false },
        ReadyEvidence() with { QuietIntervalObserved = false }
    };

    [Theory]
    [InlineData(MountPresentationMode.NetworkDrive)]
    [InlineData(MountPresentationMode.FixedDrive)]
    [InlineData(MountPresentationMode.FixedDirectory)]
    public async Task AllPresentationModesHaveExplicitValidation(MountPresentationMode mode)
    {
        var profile = Profile() with
        {
            PresentationMode = mode,
            FixedDirectoryPath = mode == MountPresentationMode.FixedDirectory ? "C:/mounts/cloud" : null,
            ShareName = mode == MountPresentationMode.NetworkDrive ? "rclone-ui-cloud-1234" : null,
            DriveLetterSelection = mode == MountPresentationMode.FixedDrive ? DriveLetterSelection.Automatic : DriveLetterSelection.Preferred
        };
        var result = await new Fixture(ReadyEvidence()).Manager.ValidateAsync(profile, TestContext.Current.CancellationToken);
        Assert.True(result.IsValid);
    }

    private static MountProfile Profile() => new(MountProfileId.New(), 1, "Cloud", "cloud:", null, 'R', "Rclone Cloud", WindowsDriveType.Network, MountCachePreset.StandardReadWrite, "cache/mounts/id", 10L * 1024 * 1024 * 1024, false, "caps-v1", ShareName: "rclone-ui-cloud-1234");
    private static MountEnvironmentEvidence Environment() => new(true, true, true, true, true, false, true, "caps-v1");
    private static MountEvidence ReadyEvidence() => new(true, true, true, true, true, 0, 0, 0);

    private sealed class Fixture
    {
        public FakeAdapter Adapter { get; }
        public MemoryJournal Journal { get; } = new();
        public MountManager Manager { get; }
        public Fixture(MountEvidence evidence) { Adapter = new(evidence) { Environment = Environment() }; Manager = new(Adapter, Journal, new FakeRecovery()); }
    }
    private sealed class FakeAdapter(MountEvidence evidence) : IMountExecutionAdapter
    {
        public MountEnvironmentEvidence Environment { get; set; } = Environment();
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public MountCleanupEvidence CleanupEvidence { get; set; } = new(true, true, true);
        public MountStopEvidence StopEvidence { get; set; } = new(true, true, true);
        public ValueTask<MountEnvironmentEvidence> InspectAsync(MountProfile profile, CancellationToken cancellationToken) => ValueTask.FromResult(Environment);
        public ValueTask StartAsync(MountInstanceId instanceId, MountProfile profile, CancellationToken cancellationToken) { StartCalls++; return ValueTask.CompletedTask; }
        public ValueTask<MountEvidence> ObserveAsync(MountInstanceId instanceId, MountProfile profile, CancellationToken cancellationToken) => ValueTask.FromResult(evidence);
        public ValueTask<MountCleanupEvidence> CleanupFailedStartAsync(MountInstanceId instanceId, MountProfile profile, CancellationToken cancellationToken) => ValueTask.FromResult(CleanupEvidence);
        public ValueTask<MountStopEvidence> StopAsync(MountInstanceId instanceId, bool force, CancellationToken cancellationToken) { StopCalls++; return ValueTask.FromResult(StopEvidence); }
    }
    private sealed class MemoryJournal : IMountJournal
    {
        private readonly Dictionary<MountInstanceId, MountSnapshot> values = [];
        public ValueTask SaveAsync(MountSnapshot snapshot, CancellationToken cancellationToken) { values[snapshot.InstanceId] = snapshot; return ValueTask.CompletedTask; }
        public ValueTask<MountSnapshot?> ReadAsync(MountInstanceId id, CancellationToken cancellationToken) => ValueTask.FromResult(values.GetValueOrDefault(id));
        public ValueTask<IReadOnlyList<MountSnapshot>> ReadActiveAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<MountSnapshot>>([.. values.Values.Where(x => x.State is not MountState.Stopped)]);
    }
    private sealed class FakeRecovery : IRecoveryCacheRegistry { public ValueTask<string> PreserveAsync(MountSnapshot snapshot, MountRisk risk, CancellationToken cancellationToken) => ValueTask.FromResult("recovery/cache"); }
}
