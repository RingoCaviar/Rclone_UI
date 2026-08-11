using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RcloneUI.Desktop.Presentation;

public sealed class DesktopHostController(IDesktopHostClient client, DesktopShellState shell)
{
    private int reconnecting;

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

    public async ValueTask ActivatePrimaryAsync(CancellationToken cancellationToken = default)
    {
        if (shell.CurrentRoute == "Remotes") { await AddRemoteAsync(cancellationToken); return; }
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

    private async ValueTask AddRemoteAsync(CancellationToken cancellationToken)
    {
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
