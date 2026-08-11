using RcloneUI.DataRoot;
using RcloneUI.Remotes;

namespace RcloneUI.Host;

internal delegate ValueTask<DataRootOpenResult> DataRootOpener(DataRootOpenRequest request, CancellationToken cancellationToken);

internal sealed class HostVaultSession : IHostVaultSession, IHostRemoteResolver, IAsyncDisposable
{
    private readonly string dataRootPath;
    private readonly LibArgon2Binding? argon2;
    private readonly DataRootOpener opener;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IDataRootSession? session;
    private VaultRemoteStore? store;
    private string sessionState = "locked";
    private readonly HostRcloneConfigWriter configWriter;

    internal HostVaultSession(string dataRootPath, LibArgon2Binding? argon2, DataRootOpener? opener = null)
    {
        this.dataRootPath = dataRootPath;
        this.argon2 = argon2;
        this.opener = opener ?? DataRootSession.OpenAsync;
        configWriter = new(dataRootPath);
    }

    public string SessionState => sessionState;

    public async ValueTask<string> UnlockAsync(byte[] masterPasswordUtf8, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(masterPasswordUtf8);
        if (masterPasswordUtf8.Length is 0 or > 1024) return "vault-password-invalid";
        if (argon2 is null) return "vault-kdf-unavailable";
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session is not null)
            {
                if (session.Observe().State == DataRootSessionState.Unlocked) return "vault-already-unlocked";
                if (!await session.UnlockAsync(masterPasswordUtf8, cancellationToken).ConfigureAwait(false)) return "vault-authentication-failed";
                sessionState = "operational";
                return "vault-unlocked";
            }

            var opened = await opener(new(dataRootPath, DataRootOpenMode.CreateIfMissing, masterPasswordUtf8, argon2), cancellationToken).ConfigureAwait(false);
            if (opened.Status != DataRootOpenStatus.Opened || opened.Session is null)
            {
                sessionState = opened.Status == DataRootOpenStatus.NeedsRecovery ? "read-only-recovery" : "locked";
                return opened.Status switch
                {
                    DataRootOpenStatus.AuthenticationFailed => "vault-authentication-failed",
                    DataRootOpenStatus.NeedsRecovery => "vault-needs-recovery",
                    DataRootOpenStatus.AlreadyOwned => "vault-already-owned",
                    _ => "vault-unavailable",
                };
            }
            session = opened.Session;
            store = new(session);
            sessionState = "operational";
            return "vault-unlocked";
        }
        finally { gate.Release(); }
    }

    public async ValueTask<IReadOnlyList<HostRemoteSummary>> ListAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sessionState != "operational" || store is null) return [];
            var remotes = await store.ListAsync(cancellationToken).ConfigureAwait(false);
            return remotes.Select(remote => new HostRemoteSummary(remote.Id.Value, remote.DisplayName, remote.ProviderType, remote.Revision, remote.Health.Kind.ToString(), remote.Health.Failure?.DiagnosticCode)).ToArray();
        }
        finally { gate.Release(); }
    }

    public async ValueTask<string?> ResolveFileSystemAsync(Guid remoteId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sessionState != "operational" || store is null) return null;
            var remote = await store.ReadAsync(new(remoteId), cancellationToken).ConfigureAwait(false);
            return remote is null ? null : await configWriter.BindAsync(remote, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            store?.Dispose();
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            configWriter.Dispose();
            sessionState = "closed";
        }
        finally { gate.Release(); gate.Dispose(); }
    }
}
