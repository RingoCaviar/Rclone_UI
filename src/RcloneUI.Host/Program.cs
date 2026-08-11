namespace RcloneUI.Host;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 2 || !Guid.TryParse(args[1], out var dataRootId)) return 2;
        ManagedRcloneSession? rclone = null;
        try { rclone = await ManagedRcloneBootstrap.TryStartAsync(args[0], AppContext.BaseDirectory, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { await Console.Error.WriteLineAsync($"Managed rclone unavailable: {exception.GetType().Name}").ConfigureAwait(false); }
        RcloneUI.DataRoot.LibArgon2Binding? argon2 = null;
        try { argon2 = ManagedLibArgon2Bootstrap.TryDiscover(AppContext.BaseDirectory); }
        catch (Exception exception) { await Console.Error.WriteLineAsync($"Managed libargon2 unavailable: {exception.GetType().Name}").ConfigureAwait(false); }
        await using var managedRclone = rclone;
        await using var host = BackgroundHostShell.TryCreate(args[0], dataRootId, rclone?.Runtime, argon2: argon2);
        if (host is null) return 3;
        host.Start();
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await host.StopAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        return 0;
    }
}
