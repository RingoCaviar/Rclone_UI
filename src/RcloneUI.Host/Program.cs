namespace RcloneUI.Host;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 2 || !Guid.TryParse(args[1], out var dataRootId)) return 2;
        await using var host = BackgroundHostShell.TryCreate(args[0], dataRootId);
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
