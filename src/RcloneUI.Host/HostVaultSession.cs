using RcloneUI.DataRoot;
using RcloneUI.Mounts;
using RcloneUI.Rclone;
using RcloneUI.Remotes;

namespace RcloneUI.Host;

internal delegate ValueTask<DataRootOpenResult> DataRootOpener(DataRootOpenRequest request, CancellationToken cancellationToken);

internal sealed class HostVaultSession : IHostVaultSession, IHostRemoteResolver, IHostRemoteManager, IHostMountProfileManager, IAsyncDisposable
{
    private readonly string dataRootPath;
    private readonly LibArgon2Binding? argon2;
    private readonly DataRootOpener opener;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IDataRootSession? session;
    private VaultRemoteStore? store;
    private VaultMountProfileStore? mountProfiles;
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
            mountProfiles = new(session);
            sessionState = "operational";
            return "vault-unlocked";
        }
        finally { gate.Release(); }
    }

    public async ValueTask<string> LockAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sessionState != "operational" || session is null) return "vault-already-locked";
            session.Lock();
            sessionState = "locked";
            await configWriter.ClearAsync(CancellationToken.None).ConfigureAwait(false);
            return "vault-locked";
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

    public async ValueTask<string> AddTokenRemoteAsync(string displayName, string providerType, string token, IRcloneRuntime rclone, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 80 || displayName.IndexOfAny(['\r', '\n', '\0']) >= 0) return "remote-display-name-invalid";
        if (providerType is not ("drive" or "onedrive" or "dropbox")) return "remote-provider-unsupported";
        if (string.IsNullOrWhiteSpace(token) || token.Length > 16 * 1024 || token.IndexOfAny(['\r', '\n', '\0']) >= 0) return "remote-token-invalid";
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sessionState != "operational" || store is null) return "vault-locked";
            if ((await store.ListAsync(cancellationToken).ConfigureAwait(false)).Any(remote => StringComparer.OrdinalIgnoreCase.Equals(remote.DisplayName, displayName))) return "remote-name-conflict";
            var candidate = new StoredRemote(RemoteId.New(), displayName.Trim(), providerType, 0, new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = token }, new(RemoteHealthKind.Unknown, DateTimeOffset.UtcNow, null, null));
            var fileSystem = await configWriter.BindAsync(candidate, cancellationToken).ConfigureAwait(false);
            var committed = false;
            try
            {
                var handle = await rclone.StartAsync(new(Guid.NewGuid(), rclone.Capabilities.Binding, RclonePrimitive.List, new(fileSystem, string.Empty), null, $"remote-test/{candidate.Id.Value:N}"), cancellationToken).ConfigureAwait(false);
                var tested = await rclone.WaitAsync(handle, cancellationToken).ConfigureAwait(false);
                if (!tested.Success) return "remote-test-failed";
                var healthy = candidate with { Health = new(RemoteHealthKind.Healthy, DateTimeOffset.UtcNow, rclone.Capabilities.Binding, null) };
                await store.UpsertAsync(healthy, 0, cancellationToken).ConfigureAwait(false);
                committed = true;
                return "remote-added";
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { return "remote-test-failed"; }
            finally { if (!committed) await configWriter.UnbindAsync(candidate.Id.Value, CancellationToken.None).ConfigureAwait(false); }
        }
        finally { gate.Release(); }
    }

    public async ValueTask<string> AddConnectionRemoteAsync(string displayName, string providerType, IReadOnlyDictionary<string, string> configuration, IRcloneRuntime rclone, CancellationToken cancellationToken)
    {
        if (providerType is not ("ftp" or "sftp") || string.IsNullOrWhiteSpace(displayName) || configuration.Count == 0) return "remote-input-invalid";
        if (!configuration.TryGetValue("host", out var host) || string.IsNullOrWhiteSpace(host) || host.Length > 255 || !configuration.TryGetValue("user", out var user) || string.IsNullOrWhiteSpace(user) || !configuration.TryGetValue("pass", out var password) || string.IsNullOrWhiteSpace(password) || !configuration.TryGetValue("port", out var portValue) || !int.TryParse(portValue, System.Globalization.CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535) return "remote-input-invalid";
        if (providerType == "ftp" && configuration.ContainsKey("tls") && configuration.ContainsKey("explicit_tls")) return "remote-input-invalid";
        if (providerType == "sftp" && (!configuration.TryGetValue("host_key_fingerprint", out var fingerprint) || string.IsNullOrWhiteSpace(fingerprint))) return "remote-host-key-required";
        if (configuration.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value.Length > 16 * 1024 || item.Value.IndexOfAny(['\r', '\n', '\0']) >= 0)) return "remote-input-invalid";
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sessionState != "operational" || store is null) return "vault-locked";
            var secured = new Dictionary<string, string>(configuration, StringComparer.Ordinal);
            if (secured.TryGetValue("pass", out var clearPassword)) secured["pass"] = await rclone.ObscureAsync(clearPassword, cancellationToken).ConfigureAwait(false);
            var candidate = new StoredRemote(RemoteId.New(), displayName.Trim(), providerType, 0, secured, new(RemoteHealthKind.Unknown, DateTimeOffset.UtcNow, null, null));
            var fileSystem = await configWriter.BindAsync(candidate, cancellationToken).ConfigureAwait(false); var committed = false;
            try { var handle = await rclone.StartAsync(new(Guid.NewGuid(), rclone.Capabilities.Binding, RclonePrimitive.List, new(fileSystem, string.Empty), null, $"remote-test/{candidate.Id.Value:N}"), cancellationToken).ConfigureAwait(false); if (!(await rclone.WaitAsync(handle, cancellationToken).ConfigureAwait(false)).Success) return "remote-test-failed"; await store.UpsertAsync(candidate with { Health = new(RemoteHealthKind.Healthy, DateTimeOffset.UtcNow, rclone.Capabilities.Binding, null) }, 0, cancellationToken).ConfigureAwait(false); committed = true; return "remote-added"; }
            finally { if (!committed) await configWriter.UnbindAsync(candidate.Id.Value, CancellationToken.None).ConfigureAwait(false); }
        }
        finally { gate.Release(); }
    }

    public async ValueTask<IReadOnlyList<SavedMountProfile>> ListMountProfilesAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return sessionState == "operational" && mountProfiles is not null ? await mountProfiles.ListAsync(cancellationToken).ConfigureAwait(false) : []; }
        finally { gate.Release(); }
    }

    public async ValueTask<SavedMountProfile?> ReadMountProfileAsync(MountProfileId id, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return sessionState == "operational" && mountProfiles is not null ? await mountProfiles.ReadAsync(id, cancellationToken).ConfigureAwait(false) : null; }
        finally { gate.Release(); }
    }

    public async ValueTask<(string ResultType, SavedMountProfile? Profile)> UpsertMountProfileAsync(SavedMountProfile profile, ulong expectedRevision, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sessionState != "operational" || mountProfiles is null) return ("vault-locked", null);
            try { return ("mount-profile-saved", await mountProfiles.UpsertAsync(profile, expectedRevision, cancellationToken).ConfigureAwait(false)); }
            catch (InvalidOperationException exception) when (exception.Message == "mount-profile-revision-conflict") { return ("mount-profile-conflict", null); }
            catch (ArgumentException) { return ("mount-profile-invalid", null); }
        }
        finally { gate.Release(); }
    }

    public async ValueTask<string> DeleteMountProfileAsync(MountProfileId id, ulong expectedRevision, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sessionState != "operational" || mountProfiles is null) return "vault-locked";
            return await mountProfiles.DeleteAsync(id, expectedRevision, cancellationToken).ConfigureAwait(false) ? "mount-profile-deleted" : "mount-profile-conflict";
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            store?.Dispose();
            mountProfiles?.Dispose();
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            configWriter.Dispose();
            sessionState = "closed";
        }
        finally { gate.Release(); gate.Dispose(); }
    }
}
