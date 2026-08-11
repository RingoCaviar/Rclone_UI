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
        if (shell.CurrentRoute != "Transfers") { await ReconnectAsync(cancellationToken); return; }
        if (shell.CapabilityBinding is null) { shell.ApplyAction("rclone-unavailable"); return; }
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { sourceFs = shell.CopySourceFs, sourcePath = shell.CopySourcePath, destinationFs = shell.CopyDestinationFs, destinationPath = shell.CopyDestinationPath, capabilityBinding = shell.CapabilityBinding }));
        var result = await client.SendCommandAsync("start-copy", arguments.RootElement, cancellationToken);
        var resultType = result.GetProperty("resultType").GetString() ?? "unknown-result";
        shell.ApplyAction(resultType);
        await ReconnectAsync(cancellationToken);
        if (resultType == "copy-accepted") _ = ObserveCopyAsync(cancellationToken);
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
