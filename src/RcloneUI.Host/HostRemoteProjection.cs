using RcloneUI.Mounts;
using RcloneUI.Rclone;
using RcloneUI.Remotes;

namespace RcloneUI.Host;

internal sealed record HostRemoteSummary(Guid Id, string DisplayName, string ProviderType, ulong Revision, string Health, string? DiagnosticCode);

internal interface IHostRemoteProjection
{
    string SessionState { get; }
    ValueTask<IReadOnlyList<HostRemoteSummary>> ListAsync(CancellationToken cancellationToken);
}

internal interface IHostVaultSession : IHostRemoteProjection
{
    ValueTask<string> UnlockAsync(byte[] masterPasswordUtf8, CancellationToken cancellationToken);
    ValueTask<string> LockAsync(CancellationToken cancellationToken);
}

internal interface IHostRemoteResolver : IHostRemoteProjection
{
    ValueTask<string?> ResolveFileSystemAsync(Guid remoteId, CancellationToken cancellationToken);
}

internal interface IHostRemoteManager : IHostRemoteProjection
{
    ValueTask<string> AddTokenRemoteAsync(string displayName, string providerType, string token, IRcloneRuntime rclone, CancellationToken cancellationToken);
    ValueTask<string> AddConnectionRemoteAsync(string displayName, string providerType, IReadOnlyDictionary<string, string> configuration, IRcloneRuntime rclone, CancellationToken cancellationToken);
    ValueTask<string> DeleteRemoteAsync(Guid remoteId, ulong expectedRevision, CancellationToken cancellationToken);
}

internal interface IHostMountProfileManager
{
    ValueTask<IReadOnlyList<SavedMountProfile>> ListMountProfilesAsync(CancellationToken cancellationToken);
    ValueTask<SavedMountProfile?> ReadMountProfileAsync(MountProfileId id, CancellationToken cancellationToken);
    ValueTask<(string ResultType, SavedMountProfile? Profile)> UpsertMountProfileAsync(SavedMountProfile profile, ulong expectedRevision, CancellationToken cancellationToken);
    ValueTask<string> DeleteMountProfileAsync(MountProfileId id, ulong expectedRevision, CancellationToken cancellationToken);
}

internal sealed class VaultHostRemoteProjection(IRemoteStore store, HostRcloneConfigWriter? writer = null) : IHostRemoteProjection, IHostRemoteResolver
{
    public string SessionState => "operational";

    public async ValueTask<IReadOnlyList<HostRemoteSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var remotes = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        return remotes.Select(remote => new HostRemoteSummary(
            remote.Id.Value,
            remote.DisplayName,
            remote.ProviderType,
            remote.Revision,
            remote.Health.Kind.ToString(),
            remote.Health.Failure?.DiagnosticCode)).ToArray();
    }

    public async ValueTask<string?> ResolveFileSystemAsync(Guid remoteId, CancellationToken cancellationToken)
    {
        var remote = await store.ReadAsync(new(remoteId), cancellationToken).ConfigureAwait(false);
        return remote is null || writer is null ? null : await writer.BindAsync(remote, cancellationToken).ConfigureAwait(false);
    }
}
