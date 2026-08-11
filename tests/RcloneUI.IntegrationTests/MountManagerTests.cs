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
        Assert.Equal("readiness-not-proved", result.DiagnosticCode);
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

    private static MountProfile Profile() => new(MountProfileId.New(), 1, "Cloud", "cloud:", null, 'R', "Rclone Cloud", WindowsDriveType.Network, MountCachePreset.StandardReadWrite, "cache/mounts/id", 10L * 1024 * 1024 * 1024, false, "caps-v1");
    private static MountEnvironmentEvidence Environment() => new(true, true, true, true, true, false, true, "caps-v1");

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
        public ValueTask<MountEnvironmentEvidence> InspectAsync(MountProfile profile, CancellationToken cancellationToken) => ValueTask.FromResult(Environment);
        public ValueTask StartAsync(MountInstanceId instanceId, MountProfile profile, CancellationToken cancellationToken) { StartCalls++; return ValueTask.CompletedTask; }
        public ValueTask<MountEvidence> ObserveAsync(MountInstanceId instanceId, MountProfile profile, CancellationToken cancellationToken) => ValueTask.FromResult(evidence);
        public ValueTask StopAsync(MountInstanceId instanceId, bool force, CancellationToken cancellationToken) { StopCalls++; return ValueTask.CompletedTask; }
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
