using System.Diagnostics;
using System.Security.Cryptography;

namespace RcloneUI.Desktop.Presentation;

public sealed record PortableHostLocation(string DataRootPath, Guid DataRootId, string HostExecutablePath);

public static class PortableHostBootstrap
{
    public static PortableHostLocation Resolve(string applicationDirectory, string? selectedDataRoot = null)
    {
        var application = Path.GetFullPath(applicationDirectory);
        var dataRoot = Path.GetFullPath(selectedDataRoot ?? Path.Combine(application, "data"));
        Directory.CreateDirectory(dataRoot);
        var identityPath = Path.Combine(dataRoot, "data-root.id");
        Guid identity;
        if (File.Exists(identityPath))
        {
            var text = File.ReadAllText(identityPath).Trim();
            if (!Guid.TryParseExact(text, "D", out identity) || identity == Guid.Empty) throw new InvalidDataException("Portable Data Root identity is invalid.");
        }
        else
        {
            identity = Guid.NewGuid();
            var temporary = identityPath + ".new";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(identity.ToString("D")); writer.Flush(); stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, identityPath);
        }
        var sibling = Path.GetFullPath(Path.Combine(application, "..", "host", "RcloneUI.Host.exe"));
        var local = Path.Combine(application, "RcloneUI.Host.exe");
        return new(dataRoot, identity, File.Exists(sibling) ? sibling : local);
    }

    public static async ValueTask EnsureStartedAsync(PortableHostLocation location, CancellationToken cancellationToken)
    {
        var endpoint = Path.Combine(location.DataRootPath, "runtime", "endpoint.json");
        if (EndpointMatchesLiveProcess(endpoint)) return;
        if (!File.Exists(location.HostExecutablePath)) throw new FileNotFoundException("The portable Background Host is missing.", location.HostExecutablePath);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = location.HostExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ArgumentList = { location.DataRootPath, location.DataRootId.ToString("D") },
        }) ?? throw new InvalidOperationException("The Background Host could not be started.");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (EndpointMatchesLiveProcess(endpoint)) return;
            if (process.HasExited) throw new InvalidOperationException($"The Background Host exited with code {process.ExitCode}.");
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("The Background Host did not publish its endpoint in time.");
    }

    private static bool EndpointMatchesLiveProcess(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var text = File.ReadAllText(path);
            using var document = System.Text.Json.JsonDocument.Parse(text);
            var pid = document.RootElement.GetProperty("hostProcessId").GetInt32();
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or System.Text.Json.JsonException)
        {
            return false;
        }
    }
}

public sealed class BootstrappingDesktopHostClient(PortableHostLocation location) : IDesktopHostClient
{
    private readonly NamedPipeDesktopHostClient inner = new(location.DataRootPath);
    public string DataRootPath => location.DataRootPath;
    public async ValueTask<RcloneUI.Contracts.HostProtocol.V1.HostSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await PortableHostBootstrap.EnsureStartedAsync(location, cancellationToken).ConfigureAwait(false);
        return await inner.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask<System.Text.Json.JsonElement> SendCommandAsync(string commandType, System.Text.Json.JsonElement arguments, CancellationToken cancellationToken)
    {
        await PortableHostBootstrap.EnsureStartedAsync(location, cancellationToken).ConfigureAwait(false);
        return await inner.SendCommandAsync(commandType, arguments, cancellationToken).ConfigureAwait(false);
    }
}
