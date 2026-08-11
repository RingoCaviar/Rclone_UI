using System.Text.Json;
using RcloneUI.DataRoot;

namespace RcloneUI.Remotes;

public sealed class VaultRemoteStore(IDataRootSession dataRoot) : IRemoteStore, IDisposable
{
    private static readonly Guid CatalogRecordId = Guid.Parse("a8f9849b-07db-4f02-b705-4dc7f31f0114");
    private readonly SemaphoreSlim gate = new(1, 1);

    public void Dispose() => gate.Dispose();

    public async ValueTask<IReadOnlyList<RemoteSummary>> ListAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
            return catalog.Active.Values.Select(ToSummary).OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally { gate.Release(); }
    }

    public async ValueTask<StoredRemote?> ReadAsync(RemoteId id, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
            return catalog.Active.GetValueOrDefault(id.Value);
        }
        finally { gate.Release(); }
    }

    public async ValueTask<StoredRemote> UpsertAsync(StoredRemote remote, ulong expectedRevision, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
            var prior = catalog.Active.GetValueOrDefault(remote.Id.Value);
            if ((prior?.Revision ?? 0) != expectedRevision) throw new InvalidOperationException("remote-revision-conflict");
            var saved = remote with { Revision = checked(expectedRevision + 1) };
            catalog.Active[remote.Id.Value] = saved;
            await WriteCatalogAsync(catalog, cancellationToken).ConfigureAwait(false);
            return saved;
        }
        finally { gate.Release(); }
    }

    public async ValueTask<bool> DeleteAsync(RemoteId id, ulong expectedRevision, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
            if (!catalog.Active.TryGetValue(id.Value, out var remote) || remote.Revision != expectedRevision) return false;
            catalog.Active.Remove(id.Value);
            catalog.Deleted.Add(new(remote, DateTimeOffset.UtcNow));
            if (catalog.Deleted.Count > 20) catalog.Deleted.RemoveRange(0, catalog.Deleted.Count - 20);
            await WriteCatalogAsync(catalog, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally { gate.Release(); }
    }

    private async ValueTask<RemoteStoreDocument> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        var record = await dataRoot.ReadAsync(CatalogRecordId, cancellationToken).ConfigureAwait(false);
        if (record is null) return new();
        if (record.RecordType != VaultRecordType.Remote || record.SchemaVersion != 1) throw new InvalidDataException("Remote catalog schema is unsupported.");
        return JsonSerializer.Deserialize<RemoteStoreDocument>(record.Plaintext.Span) ?? throw new InvalidDataException("Remote catalog is invalid.");
    }

    private async ValueTask WriteCatalogAsync(RemoteStoreDocument catalog, CancellationToken cancellationToken)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(catalog);
        try
        {
            var snapshot = dataRoot.Observe();
            var result = await dataRoot.ExecuteAsync(new UpsertVaultRecord(CatalogRecordId, VaultRecordType.Remote, 1, plaintext), snapshot.Revision, cancellationToken).ConfigureAwait(false);
            if (result.Status != DataRootCommandStatus.Applied) throw new InvalidOperationException($"vault-{result.Status.ToString().ToLowerInvariant()}");
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static RemoteSummary ToSummary(StoredRemote value) => new(value.Id, value.DisplayName, value.ProviderType, value.Revision, value.Health);

    private sealed class RemoteStoreDocument
    {
        public Dictionary<Guid, StoredRemote> Active { get; init; } = [];
        public List<DeletedRemoteSnapshot> Deleted { get; init; } = [];
    }

    private sealed record DeletedRemoteSnapshot(StoredRemote Remote, DateTimeOffset DeletedUtc);
}
