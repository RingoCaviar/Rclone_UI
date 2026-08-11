using RcloneUI.DataRoot;
using RcloneUI.Mounts;

namespace RcloneUI.IntegrationTests;

public sealed class VaultMountProfileStoreTests
{
    [Fact]
    public async Task ProfilesPersistWithRevisionConflictAndRecoverableDeleteSemantics()
    {
        var session = new MemoryDataRootSession();
        using var first = new VaultMountProfileStore(session);
        var profile = Profile();

        var created = await first.UpsertAsync(profile, 0, TestContext.Current.CancellationToken);
        Assert.Equal(1UL, created.Revision);

        using var reopened = new VaultMountProfileStore(session);
        var persisted = Assert.Single(await reopened.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(profile.Id, persisted.Id);
        Assert.Equal(profile.RemoteId, persisted.RemoteId);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await reopened.UpsertAsync(profile with { DisplayName = "Stale" }, 0, TestContext.Current.CancellationToken));
        var updated = await reopened.UpsertAsync(persisted with { DisplayName = "Updated" }, 1, TestContext.Current.CancellationToken);
        Assert.Equal(2UL, updated.Revision);
        Assert.False(await reopened.DeleteAsync(updated.Id, 1, TestContext.Current.CancellationToken));
        Assert.True(await reopened.DeleteAsync(updated.Id, 2, TestContext.Current.CancellationToken));
        Assert.Empty(await reopened.ListAsync(TestContext.Current.CancellationToken));
    }

    private static SavedMountProfile Profile() => new(MountProfileId.New(), 0, "Photos", Guid.NewGuid(), "photos", MountPresentationMode.NetworkDrive, DriveLetterSelection.Preferred, 'R', null, "Cloud", MountCachePreset.ReadOnlyBrowsing, false);

    private sealed class MemoryDataRootSession : IDataRootSession
    {
        private readonly Dictionary<Guid, VaultRecord> records = [];
        private ulong revision;
        public DataRootSnapshot Observe() => new(new(Guid.NewGuid()), Guid.NewGuid(), 1, revision, DataRootSessionState.Unlocked, "C:\\data");
        public ValueTask<DataRootCommandResult> ExecuteAsync(DataRootCommand command, ulong expectedRevision, CancellationToken cancellationToken = default)
        {
            if (expectedRevision != revision) return ValueTask.FromResult(new DataRootCommandResult(DataRootCommandStatus.RevisionConflict, revision));
            switch (command)
            {
                case UpsertVaultRecord upsert:
                    revision++;
                    records[upsert.RecordId] = new(upsert.RecordId, upsert.RecordType, upsert.SchemaVersion, revision, upsert.Plaintext.ToArray());
                    return ValueTask.FromResult(new DataRootCommandResult(DataRootCommandStatus.Applied, revision));
                case DeleteVaultRecord delete when records.Remove(delete.RecordId):
                    revision++;
                    return ValueTask.FromResult(new DataRootCommandResult(DataRootCommandStatus.Applied, revision));
                default: return ValueTask.FromResult(new DataRootCommandResult(DataRootCommandStatus.NotFound, revision));
            }
        }
        public ValueTask<VaultRecord?> ReadAsync(Guid recordId, CancellationToken cancellationToken = default) => ValueTask.FromResult(records.GetValueOrDefault(recordId));
        public void Lock() { }
        public ValueTask<bool> UnlockAsync(ReadOnlyMemory<byte> masterPasswordUtf8, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
        public ValueTask CloseAsync(string reason, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
