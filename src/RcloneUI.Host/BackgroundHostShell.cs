using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using RcloneUI.Contracts.HostProtocol.V1;
using RcloneUI.Rclone;

namespace RcloneUI.Host;

[SupportedOSPlatform("windows")]
internal sealed class BackgroundHostShell : IAsyncDisposable
{
    private readonly string dataRootPath;
    private readonly HostOwnership ownership;
    private readonly HostEndpoint endpoint;
    private readonly HostWindowsIdentity identity;
    private readonly HostStateAuthority state;
    private readonly HostWorkReconciler workReconciler;
    private readonly HostLifecycleJournal lifecycleJournal;
    private readonly CancellationTokenSource shutdown = new();
    private readonly ConcurrentDictionary<int, Task> sessions = new();
    private Task? listener;
    private HostLifecycleWindow? lifecycleWindow;
    private int sessionNumber;

    private BackgroundHostShell(
        string dataRootPath,
        HostOwnership ownership,
        HostEndpoint endpoint,
        HostWindowsIdentity identity,
        IRcloneRuntime? rclone,
        IHostRemoteProjection? remotes)
    {
        this.dataRootPath = dataRootPath;
        this.ownership = ownership;
        this.endpoint = endpoint;
        this.identity = identity;
        state = new(dataRootPath, rclone, remotes);
        workReconciler = new(dataRootPath);
        lifecycleJournal = new(dataRootPath);
    }

    internal HostEndpoint Endpoint => endpoint;

    internal static BackgroundHostShell? TryCreate(string dataRootPath, Guid dataRootId, IRcloneRuntime? rclone = null, IHostRemoteProjection? remotes = null)
    {
        var identity = HostWindowsIdentity.Current();
        var names = HostEndpointNaming.Derive(dataRootId, identity.LogonSid.Value);
        var ownership = HostOwnership.TryAcquire(dataRootPath, names.MutexName);
        if (ownership is null) return null;
        try
        {
            var endpoint = HostEndpointPublisher.Create(dataRootId, names.PipeName);
            return new(dataRootPath, ownership, endpoint, identity, rclone, remotes);
        }
        catch
        {
            ownership.Dispose();
            throw;
        }
    }

    internal void Start()
    {
        if (listener is not null) throw new InvalidOperationException("The Host listener has already started.");
        var first = SecureNamedPipeFactory.Create(endpoint.PipeName, identity, firstInstance: true);
        try
        {
            lifecycleWindow = HostLifecycleWindow.Start(lifecycleJournal);
            HostEndpointPublisher.Publish(dataRootPath, endpoint);
            listener = ListenAsync(first, shutdown.Token);
        }
        catch
        {
            lifecycleWindow?.Dispose();
            lifecycleWindow = null;
            first.Dispose();
            throw;
        }
    }

    internal async Task StopAsync(TimeSpan deadline)
    {
        shutdown.Cancel();
        var active = sessions.Values.Append(listener ?? Task.CompletedTask).ToArray();
        try
        {
            await Task.WhenAll(active).WaitAsync(deadline).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Disposing the shell closes remaining pipe handles; domain work reconciliation owns truthful outcomes.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        lifecycleWindow?.Dispose();
        state.Dispose();
        shutdown.Dispose();
        ownership.Dispose();
    }

    private async Task ListenAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        using (server)
        {
            var current = server;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await current.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    if (!SecureNamedPipeFactory.VerifyConnectedClient(current, identity))
                    {
                        current.Disconnect();
                        continue;
                    }

                    var accepted = current;
                    current = SecureNamedPipeFactory.Create(endpoint.PipeName, identity, firstInstance: false);
                    var number = Interlocked.Increment(ref sessionNumber);
                    var task = RunSessionAsync(number, accepted, cancellationToken);
                    sessions.TryAdd(number, task);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    current.Dispose();
                    return;
                }
            }

            current.Dispose();
        }
    }

    private async Task RunSessionAsync(int number, NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                await new HostProtocolSession(endpoint, state).RunAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ProtocolException or CryptographicException or InvalidDataException or IOException)
            {
                // Authentication/protocol failure closes only this client and never reaches domain dispatch again.
            }
            finally
            {
                sessions.TryRemove(number, out _);
            }
        }
    }
}
