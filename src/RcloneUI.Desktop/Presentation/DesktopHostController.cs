using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RcloneUI.Desktop.Presentation;

public sealed class DesktopHostController(IDesktopHostClient client, DesktopShellState shell, IWinFspInstaller? winFspInstaller = null)
{
    private int reconnecting;

    public async ValueTask InitializeDesktopSessionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var arguments = JsonDocument.Parse("{}");
            await client.SendCommandAsync("lock-vault", arguments.RootElement, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Reconnect supplies the truthful disconnected state and retry action.
        }
        await ReconnectAsync(cancellationToken);
    }

    public async ValueTask ReconnectAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref reconnecting, 1) != 0) return;
        try
        {
            shell.ApplyConnection(DesktopConnectionState.Connecting);
            var snapshot = await client.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            shell.ApplySnapshot(snapshot);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            shell.ApplyConnection(DesktopConnectionState.Disconnected);
            shell.ApplyAction("host-unavailable");
        }
        finally { Volatile.Write(ref reconnecting, 0); }
    }

    public async ValueTask InstallWinFspAsync(CancellationToken cancellationToken = default)
    {
        if (winFspInstaller is null) { shell.ApplyAction("winfsp-installer-unavailable"); return; }
        shell.ApplyAction("winfsp-install-started");
        try
        {
            var result = await winFspInstaller.InstallAsync(cancellationToken).ConfigureAwait(false);
            shell.ApplyAction(result.Detail is null ? result.ResultType : $"{result.ResultType}:{result.Detail}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            shell.ApplyAction($"winfsp-install-failed:{exception.GetType().Name}");
        }
        await ReconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> ShutdownHostAsync(CancellationToken cancellationToken = default)
    {
        using var arguments = JsonDocument.Parse("{}");
        var result = await client.SendCommandAsync("shutdown-host", arguments.RootElement, cancellationToken).ConfigureAwait(false);
        var accepted = result.GetProperty("resultType").GetString() == "shutdown-accepted";
        shell.ApplyAction(accepted ? "shutdown-accepted" : "shutdown-blocked-active-mount");
        return accepted;
    }

    public async ValueTask ActivatePrimaryAsync(CancellationToken cancellationToken = default)
    {
        if (shell.CurrentRoute == "Remotes") { await AddRemoteAsync(cancellationToken); return; }
        if (shell.CurrentRoute == "Browser") { await BrowseAsync(cancellationToken); return; }
        if (shell.CurrentRoute == "Mounts") { await ToggleMountAsync(cancellationToken); return; }
        if (shell.CurrentRoute != "Transfers") { await ReconnectAsync(cancellationToken); return; }
        if (shell.CapabilityBinding is null) { shell.ApplyAction("rclone-unavailable"); return; }
        if (shell.CopySourceRemote is null) { shell.ApplyAction("source-remote-required"); return; }
        if (shell.TransferMode == DesktopTransferMode.Download && string.IsNullOrWhiteSpace(shell.DownloadDestinationPath)) { shell.ApplyAction("download-folder-required"); return; }
        if (shell.TransferMode == DesktopTransferMode.RemoteCopy && shell.CopyDestinationRemote is null) { shell.ApplyAction("destination-remote-required"); return; }
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            sourceRemoteId = shell.CopySourceRemote.Id,
            sourcePath = shell.CopySourcePath,
            destinationRemoteId = shell.TransferMode == DesktopTransferMode.RemoteCopy ? shell.CopyDestinationRemote?.Id : null,
            destinationPath = shell.TransferMode == DesktopTransferMode.RemoteCopy ? shell.CopyDestinationPath : null,
            destinationLocalPath = shell.TransferMode == DesktopTransferMode.Download ? shell.DownloadDestinationPath : null,
            capabilityBinding = shell.CapabilityBinding
        }));
        var result = await client.SendCommandAsync("start-copy", arguments.RootElement, cancellationToken);
        var resultType = result.GetProperty("resultType").GetString() ?? "unknown-result";
        shell.ApplyAction(resultType);
        await ReconnectAsync(cancellationToken);
        if (resultType == "copy-accepted") _ = ObserveCopyAsync(cancellationToken);
    }

    private async ValueTask BrowseAsync(CancellationToken cancellationToken)
    {
        if (shell.CapabilityBinding is null) { shell.ApplyAction("rclone-unavailable"); return; }
        if (shell.BrowserRemote is null) { shell.ApplyAction("source-remote-required"); return; }
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { remoteId = shell.BrowserRemote.Id, path = shell.BrowserPath, capabilityBinding = shell.CapabilityBinding }));
        var result = await client.SendCommandAsync("browse-remote", arguments.RootElement, cancellationToken).ConfigureAwait(false);
        var resultType = result.GetProperty("resultType").GetString() ?? "unknown-result";
        shell.ApplyAction(resultType);
        if (resultType == "browse-completed" && result.TryGetProperty("output", out var output))
        {
            var root = output.TryGetProperty("list", out var list) ? list : output;
            shell.ApplyBrowserItems(root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().Select(item => item.TryGetProperty("Path", out var path) ? path.GetString() : item.TryGetProperty("Name", out var name) ? name.GetString() : null).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!) : []);
        }
    }

    private async ValueTask ToggleMountAsync(CancellationToken cancellationToken)
    {
        if (shell.CapabilityBinding is null) { shell.ApplyAction("rclone-unavailable"); return; }
        if (!shell.HasActiveMount && !shell.MountPrerequisitesReady) { shell.ApplyAction("mount-prerequisites-unavailable"); return; }
        object payload;
        string command;
        if (shell.ActiveMountId is { } instanceId)
        {
            command = "stop-mount";
            payload = new { instanceId, capabilityBinding = shell.CapabilityBinding };
        }
        else
        {
            if (shell.SelectedMountProfile is null) { shell.ApplyAction("mount-profile-required"); return; }
            command = "start-mount-profile";
            payload = new { profileId = shell.SelectedMountProfile.Id, capabilityBinding = shell.CapabilityBinding };
        }
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var result = await client.SendCommandAsync(command, arguments.RootElement, cancellationToken);
        shell.ApplyAction(result.GetProperty("resultType").GetString() ?? "unknown-result");
        await ReconnectAsync(cancellationToken);
    }

    public async ValueTask SaveMountProfileAsync(CancellationToken cancellationToken = default)
    {
        if (shell.MountRemote is null) { shell.ApplyAction("mount-remote-required"); return; }
        var existing = shell.SelectedMountProfile;
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { profileId = existing?.Id ?? Guid.NewGuid(), expectedRevision = existing?.Revision ?? 0, displayName = shell.MountProfileName, remoteId = shell.MountRemote.Id, subpath = shell.MountSubpath, presentationMode = shell.MountPresentation.Key, driveSelection = shell.IsFixedDirectoryMount ? "preferred" : shell.MountDriveSelection.Key, cachePreset = shell.MountCachePreset.Key, driveLetter = shell.MountDriveLetter, fixedDirectoryPath = shell.IsFixedDirectoryMount ? shell.MountFixedDirectoryPath : null, volumeName = shell.MountVolumeName }));
        var result = await client.SendCommandAsync("save-mount-profile", arguments.RootElement, cancellationToken);
        shell.ApplyAction(result.GetProperty("resultType").GetString() ?? "unknown-result");
        await ReconnectAsync(cancellationToken);
    }

    public async ValueTask DeleteMountProfileAsync(CancellationToken cancellationToken = default)
    {
        if (shell.SelectedMountProfile is not { } profile) return;
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { profileId = profile.Id, expectedRevision = profile.Revision }));
        var result = await client.SendCommandAsync("delete-mount-profile", arguments.RootElement, cancellationToken);
        shell.ApplyAction(result.GetProperty("resultType").GetString() ?? "unknown-result");
        await ReconnectAsync(cancellationToken);
    }

    private async ValueTask AddRemoteAsync(CancellationToken cancellationToken)
    {
        if (shell.IsConnectionRemoteSetup) { await AddConnectionRemoteAsync(cancellationToken); return; }
        var token = Encoding.UTF8.GetBytes(shell.RemoteToken);
        try
        {
            using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { displayName = shell.RemoteDisplayName, providerType = shell.RemoteProviderType, tokenUtf8 = Convert.ToBase64String(token) }));
            var result = await client.SendCommandAsync("add-token-remote", arguments.RootElement, cancellationToken);
            var resultType = result.GetProperty("resultType").GetString() ?? "unknown-result";
            shell.ApplyAction(resultType);
            if (resultType == "remote-added") shell.RemoteDisplayName = string.Empty;
        }
        finally { CryptographicOperations.ZeroMemory(token); shell.RemoteToken = string.Empty; }
        await ReconnectAsync(cancellationToken);
    }

    private async ValueTask AddConnectionRemoteAsync(CancellationToken cancellationToken)
    {
        if (!int.TryParse(shell.ConnectionPort, out var port) || port is < 1 or > 65535 || string.IsNullOrWhiteSpace(shell.RemoteDisplayName) || string.IsNullOrWhiteSpace(shell.ConnectionHost) || string.IsNullOrWhiteSpace(shell.ConnectionUser) || string.IsNullOrWhiteSpace(shell.ConnectionPassword)) { shell.ApplyAction("remote-input-invalid"); return; }
        var password = Encoding.UTF8.GetBytes(shell.ConnectionPassword);
        var added = false;
        try
        {
            var protocol = shell.ConnectionProtocol.Key;
            var configuration = new Dictionary<string, string> { ["host"] = shell.ConnectionHost.Trim(), ["port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture), ["user"] = shell.ConnectionUser.Trim(), ["pass"] = Encoding.UTF8.GetString(password) };
            var provider = protocol == "sftp" ? "sftp" : "ftp";
            if (protocol == "ftps-explicit") configuration["explicit_tls"] = "true";
            if (protocol == "ftps-implicit") { configuration["tls"] = "true"; if (port == 21) configuration["port"] = "990"; }
            if (provider == "ftp" && shell.ConnectionSkipCertificateVerification) configuration["no_check_certificate"] = "true";
            if (provider == "sftp") configuration["host_key_fingerprint"] = shell.ConnectionHostKeyFingerprint.Trim();
            using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { displayName = shell.RemoteDisplayName, providerType = provider, configuration }));
            var result = await client.SendCommandAsync("add-connection-remote", arguments.RootElement, cancellationToken).ConfigureAwait(false);
            var type = result.GetProperty("resultType").GetString() ?? "unknown-result"; shell.ApplyAction(type);
            if (type == "remote-added") { added = true; shell.RemoteDisplayName = string.Empty; shell.ConnectionHost = string.Empty; shell.ConnectionUser = string.Empty; shell.ConnectionHostKeyFingerprint = string.Empty; shell.ConnectionPort = "21"; }
        }
        finally { CryptographicOperations.ZeroMemory(password); if (added) shell.ConnectionPassword = string.Empty; }
        await ReconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UnlockAsync(CancellationToken cancellationToken = default)
    {
        var password = Encoding.UTF8.GetBytes(shell.MasterPassword);
        try
        {
            using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { passwordUtf8 = Convert.ToBase64String(password) }));
            var result = await client.SendCommandAsync("unlock-vault", arguments.RootElement, cancellationToken);
            shell.ApplyAction(result.GetProperty("resultType").GetString() ?? "unknown-result");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            shell.MasterPassword = string.Empty;
        }
        await ReconnectAsync(cancellationToken);
    }

    public async ValueTask LockAsync(CancellationToken cancellationToken = default)
    {
        using var arguments = JsonDocument.Parse("{}");
        var result = await client.SendCommandAsync("lock-vault", arguments.RootElement, cancellationToken);
        var resultType = result.GetProperty("resultType").GetString() ?? "unknown-result";
        shell.ApplyAction(resultType == "vault-locked" ? "vault-lock-completed" : resultType);
        await ReconnectAsync(cancellationToken);
    }

    private async Task ObserveCopyAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 600 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            await Task.Delay(500, cancellationToken);
            await ReconnectAsync(cancellationToken);
            if (!shell.CopyStatus.StartsWith("running", StringComparison.Ordinal)) return;
        }
    }
}
