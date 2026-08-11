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
}

internal interface IHostRemoteResolver : IHostRemoteProjection
{
    ValueTask<string?> ResolveFileSystemAsync(Guid remoteId, CancellationToken cancellationToken);
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
