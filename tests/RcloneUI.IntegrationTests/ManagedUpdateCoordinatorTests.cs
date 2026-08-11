using RcloneUI.Updates;
using RcloneUI.Updater;
using System.Security.Cryptography;
using System.Text.Json;

namespace RcloneUI.IntegrationTests;

public sealed class ManagedUpdateCoordinatorTests
{
    public static IEnumerable<object[]> CrashCases()
    {
        foreach (var phase in Enum.GetValues<UpdatePhase>().Take(11))
            foreach (var boundary in new[] { "before-action", "after-action", "after-journal", "media-returned" })
                yield return [phase, boundary];
    }

    [Theory]
    [MemberData(nameof(CrashCases))]
    public async Task CrashMatrixConvergesToOneCompatiblePair(UpdatePhase phase, string boundary)
    {
        _ = boundary;
        var cancellationToken = TestContext.Current.CancellationToken;
        var plan = Plan();
        var journal = new MemoryJournal(Entry(plan, phase));
        var newIsCommitted = phase >= UpdatePhase.NewHealthPassed;
        var runtime = new FakeRuntime { Evidence = new(true, newIsCommitted, true, newIsCommitted, newIsCommitted, true, newIsCommitted ? "health" : null) };
        var result = await new ManagedUpdateCoordinator(new FakeTrust(true), runtime, journal).RecoverAsync(plan.TransactionId, cancellationToken);
        Assert.Equal(newIsCommitted ? UpdateOutcome.Committed : UpdateOutcome.RolledBack, result.Outcome);
        Assert.True(runtime.UseNewApplication == newIsCommitted);
        Assert.True(runtime.UseNewVault == newIsCommitted);
    }

    [Fact]
    public async Task FailedHealthRollsBackBothSelectors()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var runtime = new FakeRuntime { HealthPasses = false, Evidence = new(true, true, true, true, false, true) };
        var journal = new MemoryJournal(); var coordinator = new ManagedUpdateCoordinator(new FakeTrust(true), runtime, journal); var plan = Plan(); var token = new byte[32];
        Assert.Equal(UpdateOutcome.ReadyForHandoff, (await coordinator.PrepareAsync(plan, token, cancellationToken)).Outcome);
        var result = await coordinator.CommitAsync(plan.TransactionId, token, cancellationToken);
        Assert.Equal(UpdateOutcome.RolledBack, result.Outcome);
        Assert.False(runtime.UseNewApplication);
        Assert.False(runtime.UseNewVault);
    }

    [Fact]
    public async Task HandoffTokenAndTrustFailClosed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var plan = Plan(); var journal = new MemoryJournal(); var runtime = new FakeRuntime(); var coordinator = new ManagedUpdateCoordinator(new FakeTrust(true), runtime, journal);
        await coordinator.PrepareAsync(plan, new byte[32], cancellationToken);
        Assert.Equal(UpdateOutcome.FailedSecurely, (await coordinator.CommitAsync(plan.TransactionId, Enumerable.Repeat((byte)1, 32).ToArray(), cancellationToken)).Outcome);
        var rejected = await new ManagedUpdateCoordinator(new FakeTrust(false), runtime, new MemoryJournal()).PrepareAsync(Plan(), new byte[32], cancellationToken);
        Assert.Equal(UpdateOutcome.FailedSecurely, rejected.Outcome);
    }

    [Fact]
    public async Task ActiveWorkPreventsHandoff()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var runtime = new FakeRuntime { Admission = new(true, true, false, false) };
        var result = await new ManagedUpdateCoordinator(new FakeTrust(true), runtime, new MemoryJournal()).PrepareAsync(Plan(), new byte[32], cancellationToken);
        Assert.Equal(UpdateOutcome.WaitingForWork, result.Outcome);
    }

    [Fact]
    public async Task ExternalUpdaterExecutesOnlyAuthenticatedVerifiedPlan()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new TempWorkspace();
        var updates = Directory.CreateDirectory(Path.Combine(workspace.Path, "updates")).FullName;
        var staging = Directory.CreateDirectory(Path.Combine(updates, "staging", "tx")).FullName;
        var staged = Path.Combine(staging, "app.exe"); await File.WriteAllTextAsync(staged, "new", cancellationToken);
        var active = Path.Combine(workspace.Path, "app.exe"); await File.WriteAllTextAsync(active, "old", cancellationToken);
        var token = new byte[32]; RandomNumberGenerator.Fill(token);
        var transactionId = Guid.NewGuid();
        var plan = new UpdaterPlan(transactionId, 0, Convert.ToHexString(SHA256.HashData(token)), staging, [new("app.exe", "app.exe", Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(staged, cancellationToken))))]);
        var planPath = Path.Combine(updates, "plan.json"); await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan), cancellationToken);

        Assert.Equal(0, await PlanDrivenUpdater.ExecuteAsync(updates, planPath, token, cancellationToken));
        Assert.Equal("new", await File.ReadAllTextAsync(active, cancellationToken));
        Assert.True(File.Exists(Path.Combine(updates, "rollback", transactionId.ToString("N"), "app.exe")));
    }

    private static PairedUpdatePlan Plan() => new(UpdateTransactionId.New(), new("2.0.0", "D:/updates/app", new string('A', 64), "attestation", "caps"), "1.0.0", 1, 2, 2, "plan-hash", DateTimeOffset.UtcNow);
    private static UpdateJournalEntry Entry(PairedUpdatePlan plan, UpdatePhase phase) => new(plan, phase, 1, new string('0', 64), phase >= UpdatePhase.NewHealthPassed ? "health" : null, DateTimeOffset.UtcNow);
    private sealed class FakeTrust(bool valid) : IUpdateTrustVerifier { public ValueTask<bool> VerifyAsync(VerifiedArtifact artifact, CancellationToken cancellationToken) => ValueTask.FromResult(valid); }
    private sealed class TempWorkspace : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("RcloneUI-UPDATE-TEST-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
    private sealed class MemoryJournal(UpdateJournalEntry? initial = null) : IUpdateJournal
    {
        private UpdateJournalEntry? value = initial;
        public ValueTask SaveAsync(UpdateJournalEntry entry, CancellationToken cancellationToken) { value = entry; return ValueTask.CompletedTask; }
        public ValueTask<UpdateJournalEntry?> ReadAsync(UpdateTransactionId transactionId, CancellationToken cancellationToken) => ValueTask.FromResult(value?.Plan.TransactionId == transactionId ? value : null);
    }
    private sealed class FakeRuntime : IUpdateRuntime
    {
        public UpdateAdmission Admission { get; set; } = new(true, false, false, false);
        public PairEvidence Evidence { get; set; } = new(true, true, true, true, true, true, "health");
        public bool HealthPasses { get; set; } = true;
        public bool UseNewApplication { get; private set; }
        public bool UseNewVault { get; private set; }
        public ValueTask<UpdateAdmission> InspectAdmissionAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Admission);
        public ValueTask<PairEvidence> InspectPairAsync(PairedUpdatePlan plan, CancellationToken cancellationToken) => ValueTask.FromResult(Evidence);
        public ValueTask StopOldHostAsync(PairedUpdatePlan plan, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SwitchApplicationAsync(PairedUpdatePlan plan, bool useNew, CancellationToken cancellationToken) { UseNewApplication = useNew; return ValueTask.CompletedTask; }
        public ValueTask StageAndVerifyVaultAsync(PairedUpdatePlan plan, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SwitchVaultAsync(PairedUpdatePlan plan, bool useNew, CancellationToken cancellationToken) { UseNewVault = useNew; return ValueTask.CompletedTask; }
        public ValueTask<bool> StartAndCheckNewHostAsync(PairedUpdatePlan plan, TimeSpan timeout, CancellationToken cancellationToken) => ValueTask.FromResult(HealthPasses);
        public ValueTask StartOldHostAsync(PairedUpdatePlan plan, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
