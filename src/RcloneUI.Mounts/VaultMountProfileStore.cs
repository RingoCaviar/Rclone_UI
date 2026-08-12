using System.Security.Cryptography;
using System.Text.Json;
using RcloneUI.DataRoot;

namespace RcloneUI.Mounts;

public sealed class VaultMountProfileStore(IDataRootSession dataRoot) : IMountProfileStore, IDisposable
{
    private static readonly Guid CatalogRecordId = Guid.Parse("d815fd8e-a853-48c8-8f7e-19834506c57f");
    private readonly SemaphoreSlim gate = new(1, 1);

    public void Dispose() => gate.Dispose();

    public async ValueTask<IReadOnlyList<SavedMountProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return (await ReadCatalogAsync(cancellationToken).ConfigureAwait(false)).Active.Values.OrderBy(profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray(); }
        finally { gate.Release(); }
    }

    public async ValueTask<SavedMountProfile?> ReadAsync(MountProfileId id, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return (await ReadCatalogAsync(cancellationToken).ConfigureAwait(false)).Active.GetValueOrDefault(id.Value); }
        finally { gate.Release(); }
    }

    public async ValueTask<SavedMountProfile> UpsertAsync(SavedMountProfile profile, ulong expectedRevision, CancellationToken cancellationToken = default)
    {
        Validate(profile);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
            var prior = catalog.Active.GetValueOrDefault(profile.Id.Value);
            if ((prior?.Revision ?? 0) != expectedRevision) throw new InvalidOperationException("mount-profile-revision-conflict");
            var saved = profile with { Revision = checked(expectedRevision + 1) };
            catalog.Active[profile.Id.Value] = saved;
            await WriteCatalogAsync(catalog, cancellationToken).ConfigureAwait(false);
            return saved;
        }
        finally { gate.Release(); }
    }

    public async ValueTask<bool> DeleteAsync(MountProfileId id, ulong expectedRevision, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
            if (!catalog.Active.TryGetValue(id.Value, out var profile) || profile.Revision != expectedRevision) return false;
            catalog.Active.Remove(id.Value);
            catalog.Deleted.Add(new(profile, DateTimeOffset.UtcNow));
            if (catalog.Deleted.Count > 20) catalog.Deleted.RemoveRange(0, catalog.Deleted.Count - 20);
            await WriteCatalogAsync(catalog, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally { gate.Release(); }
    }

    private async ValueTask<MountProfileStoreDocument> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        var record = await dataRoot.ReadAsync(CatalogRecordId, cancellationToken).ConfigureAwait(false);
        if (record is null) return new();
        if (record.RecordType != VaultRecordType.MountProfile || record.SchemaVersion != 1) throw new InvalidDataException("Mount Profile catalog schema is unsupported.");
        return JsonSerializer.Deserialize<MountProfileStoreDocument>(record.Plaintext.Span) ?? throw new InvalidDataException("Mount Profile catalog is invalid.");
    }

    private async ValueTask WriteCatalogAsync(MountProfileStoreDocument catalog, CancellationToken cancellationToken)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(catalog);
        try
        {
            var result = await dataRoot.ExecuteAsync(new UpsertVaultRecord(CatalogRecordId, VaultRecordType.MountProfile, 1, plaintext), dataRoot.Observe().Revision, cancellationToken).ConfigureAwait(false);
            if (result.Status != DataRootCommandStatus.Applied) throw new InvalidOperationException($"vault-{result.Status.ToString().ToLowerInvariant()}");
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static void Validate(SavedMountProfile profile)
    {
        if (profile.Id.Value == Guid.Empty || profile.RemoteId == Guid.Empty || string.IsNullOrWhiteSpace(profile.DisplayName) || profile.DisplayName.Length > 80 || profile.DisplayName.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new ArgumentException("mount-profile-invalid", nameof(profile));
        if (profile.Subpath.Length > 2048 || profile.Subpath.IndexOfAny(['\r', '\n', '\0']) >= 0 || string.IsNullOrWhiteSpace(profile.VolumeName) || profile.VolumeName.Length > 64) throw new ArgumentException("mount-profile-invalid", nameof(profile));
        if (!Enum.IsDefined(profile.PresentationMode) || !Enum.IsDefined(profile.DriveLetterSelection) || !Enum.IsDefined(profile.CachePreset)) throw new ArgumentException("mount-profile-invalid", nameof(profile));
        if (profile.CachePreset is MountCachePreset.MaximumCompatibility or MountCachePreset.Custom) throw new ArgumentException("mount-profile-cache-preset-not-yet-supported", nameof(profile));
        if (profile.PresentationMode == MountPresentationMode.FixedDirectory && (string.IsNullOrWhiteSpace(profile.FixedDirectoryPath) || !Path.IsPathFullyQualified(profile.FixedDirectoryPath))) throw new ArgumentException("mount-profile-invalid", nameof(profile));
        if (profile.PresentationMode != MountPresentationMode.FixedDirectory && profile.DriveLetterSelection == DriveLetterSelection.Preferred && profile.PreferredDriveLetter is < 'D' or > 'Z') throw new ArgumentException("mount-profile-invalid", nameof(profile));
    }

    private sealed class MountProfileStoreDocument
    {
        public Dictionary<Guid, SavedMountProfile> Active { get; init; } = [];
        public List<DeletedMountProfileSnapshot> Deleted { get; init; } = [];
    }

    private sealed record DeletedMountProfileSnapshot(SavedMountProfile Profile, DateTimeOffset DeletedUtc);
}
