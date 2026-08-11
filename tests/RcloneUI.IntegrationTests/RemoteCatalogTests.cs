using System.Collections.Immutable;
using RcloneUI.DataRoot;
using RcloneUI.Remotes;

namespace RcloneUI.IntegrationTests;

public sealed class RemoteCatalogTests
{
    [Fact]
    public async Task SetupKeepsSecretTransientUntilSuccessfulTestAndSave()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var engine = new ScriptedRemoteEngine();
        var store = new MemoryRemoteStore();
        var catalog = new RemoteCatalog(engine, store, new NoDependencies());

        var setup = await catalog.BeginSetupAsync("drive", "Personal Drive", cancellationToken);
        Assert.Equal(RemoteSetupStage.Configure, setup.Stage);
        Assert.True(setup.Question!.Option.Sensitive);

        var ready = await catalog.AdvanceSetupAsync(setup.SetupId, new("token", "top-secret"), cancellationToken);
        Assert.Equal(RemoteSetupStage.TestAndSave, ready.Stage);
        Assert.Equal(ready, catalog.ObserveSetup(setup.SetupId));
        Assert.DoesNotContain("top-secret", ready.ToString(), StringComparison.Ordinal);
        Assert.Empty(store.Items);

        Assert.Equal(RemoteHealthKind.Healthy, (await catalog.TestSetupAsync(setup.SetupId, cancellationToken)).Kind);
        var saved = await catalog.SaveSetupAsync(setup.SetupId, cancellationToken);

        Assert.Equal("Personal Drive", saved.DisplayName);
        Assert.Equal("top-secret", Assert.Single(store.Items).Configuration["token"]);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => catalog.AdvanceSetupAsync(setup.SetupId, new("token", "again"), cancellationToken).AsTask());
    }

    [Fact]
    public async Task CuratedPresentationStillUsesLiveSensitiveSchema()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var catalog = new RemoteCatalog(new ScriptedRemoteEngine(), new MemoryRemoteStore(), new NoDependencies());
        var providers = await catalog.DescribeProvidersAsync(cancellationToken);
        var drive = Assert.Single(providers.Providers);
        Assert.True(drive.Curated);
        Assert.Equal("Google Drive", drive.DisplayName);
        Assert.True(Assert.Single(drive.Options).Sensitive);
    }

    [Fact]
    public async Task DeleteNeverSilentlyCascadesAndBlocksActiveDependency()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var engine = new ScriptedRemoteEngine();
        var store = new MemoryRemoteStore();
        var setupCatalog = new RemoteCatalog(engine, store, new NoDependencies());
        var setup = await setupCatalog.BeginSetupAsync("drive", "Work", cancellationToken);
        await setupCatalog.AdvanceSetupAsync(setup.SetupId, new("token", "secret"), cancellationToken);
        await setupCatalog.TestSetupAsync(setup.SetupId, cancellationToken);
        var remote = await setupCatalog.SaveSetupAsync(setup.SetupId, cancellationToken);
        var dependency = new RemoteDependency("Mount", Guid.NewGuid(), "W:", true);
        var catalog = new RemoteCatalog(engine, store, new FixedDependencies(dependency));

        var result = await catalog.DeleteAsync(new(remote.Id, DeleteDependencyChoice.DisableDependents, ImmutableHashSet<Guid>.Empty, remote.Revision), cancellationToken);

        Assert.False(result.Applied);
        Assert.Equal("active-dependency", result.FailureCode);
        Assert.Single(store.Items);
    }

    [Fact]
    public async Task EngineExceptionReturnsRedactedRecoveryError()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var catalog = new RemoteCatalog(new ScriptedRemoteEngine(throwOnTest: true), new MemoryRemoteStore(), new NoDependencies());
        var setup = await catalog.BeginSetupAsync("drive", "Broken", cancellationToken);
        await catalog.AdvanceSetupAsync(setup.SetupId, new("token", "secret-token"), cancellationToken);

        var health = await catalog.TestSetupAsync(setup.SetupId, cancellationToken);

        Assert.Equal(RemoteHealthKind.NetworkUnavailable, health.Kind);
        Assert.DoesNotContain("secret-token", health.ToString(), StringComparison.Ordinal);
        Assert.Equal("invalidoperationexception", health.Failure!.DiagnosticCode);
    }

    [Fact]
    public async Task RepairPreservesRemoteIdentityAndReplacesOnlyAfterTest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new MemoryRemoteStore();
        var catalog = new RemoteCatalog(new ScriptedRemoteEngine(), store, new NoDependencies());
        var setup = await catalog.BeginSetupAsync("drive", "Stable", cancellationToken);
        await catalog.AdvanceSetupAsync(setup.SetupId, new("token", "old"), cancellationToken);
        await catalog.TestSetupAsync(setup.SetupId, cancellationToken);
        var original = await catalog.SaveSetupAsync(setup.SetupId, cancellationToken);

        var repair = await catalog.BeginRepairAsync(original.Id, cancellationToken);
        await catalog.AdvanceSetupAsync(repair.SetupId, new("token", "new"), cancellationToken);
        Assert.Equal("old", Assert.Single(store.Items).Configuration["token"]);
        await catalog.TestSetupAsync(repair.SetupId, cancellationToken);
        var repaired = await catalog.SaveSetupAsync(repair.SetupId, cancellationToken);

        Assert.Equal(original.Id, repaired.Id);
        Assert.Equal("new", Assert.Single(store.Items).Configuration["token"]);
    }

    [Fact]
    public async Task VaultStoreRoundTripsRemoteThroughEncryptedRecordBoundary()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var dataRoot = new MemoryDataRootSession();
        using var store = new VaultRemoteStore(dataRoot);
        var remote = new StoredRemote(RemoteId.New(), "Vault", "drive", 0, new Dictionary<string, string> { ["token"] = "secret" }, new(RemoteHealthKind.Healthy, DateTimeOffset.UtcNow, "cap", null));

        var saved = await store.UpsertAsync(remote, 0, cancellationToken);
        var restored = await store.ReadAsync(remote.Id, cancellationToken);

        Assert.Equal(1UL, saved.Revision);
        Assert.Equal("secret", restored!.Configuration["token"]);
        Assert.Equal(VaultRecordType.Remote, dataRoot.Record!.RecordType);
    }

    private sealed class ScriptedRemoteEngine(bool throwOnTest = false) : IRemoteEngine
    {
        private static readonly ProviderOption Token = new("token", "Authorization", RemoteQuestionKind.Password, true, false, true, false, null, []);

        public ValueTask<ProviderCatalog> DescribeProvidersAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new ProviderCatalog(
            [new("drive", "Runtime Drive", "Live", false, [Token])], "schema-v1"));

        public ValueTask<RemoteEngineProgress> BeginConfigurationAsync(string providerType, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RemoteEngineProgress(false, "token-state", new("token-state", Token, null), new Dictionary<string, string>()));

        public ValueTask<RemoteEngineProgress> BeginRepairAsync(string providerType, IReadOnlyDictionary<string, string> current, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RemoteEngineProgress(false, "token-state", new("token-state", Token, null), new Dictionary<string, string>(current)));

        public ValueTask<RemoteEngineProgress> ContinueConfigurationAsync(string providerType, string state, RemoteAnswer answer, IReadOnlyDictionary<string, string> current, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RemoteEngineProgress(true, string.Empty, null, new Dictionary<string, string> { [answer.Name] = answer.Value }));

        public ValueTask<RemoteTestResult> TestAsync(string providerType, IReadOnlyDictionary<string, string> configuration, CancellationToken cancellationToken)
        {
            if (throwOnTest) throw new InvalidOperationException("secret-token https://private.example");
            return ValueTask.FromResult(new RemoteTestResult(true, RemoteHealthKind.Healthy, null, "cap-v1"));
        }

        public ValueTask<BrowsePage> BrowseAsync(string providerType, IReadOnlyDictionary<string, string> configuration, string path, PageRequest page, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new BrowsePage([new("Folder", "Folder", true, null, null, null)], null));

        public ValueTask<RemoteImportPreview> PreviewImportAsync(ReadOnlyMemory<byte> rcloneConfiguration, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RemoteImportPreview(Guid.Empty, []));

        public ValueTask<ImportedRemoteConfiguration> MaterializeImportAsync(ReadOnlyMemory<byte> rcloneConfiguration, string sourceName, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ImportedRemoteConfiguration(sourceName, "drive", new Dictionary<string, string>()));
    }

    private sealed class MemoryRemoteStore : IRemoteStore
    {
        internal List<StoredRemote> Items { get; } = [];
        public ValueTask<IReadOnlyList<RemoteSummary>> ListAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<RemoteSummary>>(Items.Select(ToSummary).ToArray());
        public ValueTask<StoredRemote?> ReadAsync(RemoteId id, CancellationToken cancellationToken) => ValueTask.FromResult(Items.SingleOrDefault(item => item.Id == id));
        public ValueTask<StoredRemote> UpsertAsync(StoredRemote remote, ulong expectedRevision, CancellationToken cancellationToken) { var saved = remote with { Revision = expectedRevision + 1 }; Items.RemoveAll(item => item.Id == remote.Id); Items.Add(saved); return ValueTask.FromResult(saved); }
        public ValueTask<bool> DeleteAsync(RemoteId id, ulong expectedRevision, CancellationToken cancellationToken) => ValueTask.FromResult(Items.RemoveAll(item => item.Id == id && item.Revision == expectedRevision) == 1);
        private static RemoteSummary ToSummary(StoredRemote value) => new(value.Id, value.DisplayName, value.ProviderType, value.Revision, value.Health);

    }

    private sealed class NoDependencies : IRemoteDependencyPolicy
    {
        public ValueTask<ImmutableArray<RemoteDependency>> FindAsync(RemoteId remoteId, CancellationToken cancellationToken) => ValueTask.FromResult(ImmutableArray<RemoteDependency>.Empty);
        public ValueTask ApplyDeleteChoiceAsync(RemoteDeleteRequest request, ImmutableArray<RemoteDependency> dependencies, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class FixedDependencies(RemoteDependency dependency) : IRemoteDependencyPolicy
    {
        public ValueTask<ImmutableArray<RemoteDependency>> FindAsync(RemoteId remoteId, CancellationToken cancellationToken) => ValueTask.FromResult(ImmutableArray.Create(dependency));
        public ValueTask ApplyDeleteChoiceAsync(RemoteDeleteRequest request, ImmutableArray<RemoteDependency> dependencies, CancellationToken cancellationToken) => throw new InvalidOperationException("Must not cascade while active.");
    }

    private sealed class MemoryDataRootSession : IDataRootSession
    {
        private ulong revision;
        internal VaultRecord? Record { get; private set; }
        public DataRootSnapshot Observe() => new(new RcloneUI.Contracts.DataRootId(Guid.NewGuid()), Guid.NewGuid(), 1, revision, DataRootSessionState.Unlocked, "X:\\");
        public ValueTask<DataRootCommandResult> ExecuteAsync(DataRootCommand command, ulong expectedRevision, CancellationToken cancellationToken = default)
        {
            if (expectedRevision != revision) return ValueTask.FromResult(new DataRootCommandResult(DataRootCommandStatus.RevisionConflict, revision));
            var upsert = Assert.IsType<UpsertVaultRecord>(command);
            revision++;
            Record = new(upsert.RecordId, upsert.RecordType, upsert.SchemaVersion, revision, upsert.Plaintext.ToArray());
            return ValueTask.FromResult(new DataRootCommandResult(DataRootCommandStatus.Applied, revision));
        }
        public ValueTask<VaultRecord?> ReadAsync(Guid recordId, CancellationToken cancellationToken = default) => ValueTask.FromResult(Record?.RecordId == recordId ? Record : null);
        public void Lock() { }
        public ValueTask<bool> UnlockAsync(ReadOnlyMemory<byte> masterPasswordUtf8, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
        public ValueTask CloseAsync(string reason, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
