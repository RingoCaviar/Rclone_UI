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

internal sealed class VaultHostRemoteProjection(IRemoteStore store) : IHostRemoteProjection
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
}
