using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using RcloneUI.Rclone;

namespace RcloneUI.Host;

[SupportedOSPlatform("windows")]
internal sealed class ManagedRcloneSession(RcloneJob job, ContainedRcloneProcess process, RcloneRcAdapter runtime) : IAsyncDisposable
{
    internal IRcloneRuntime Runtime => runtime;
    public ValueTask DisposeAsync()
    {
        runtime.Dispose();
        process.Dispose();
        job.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed record ManagedRcloneManifest(int Format, string Version, string Sha256, string Executable);

[SupportedOSPlatform("windows")]
internal static class ManagedRcloneBootstrap
{
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web);

    internal static async ValueTask<ManagedRcloneSession?> TryStartAsync(string dataRootPath, string hostDirectory, CancellationToken cancellationToken)
    {
        var componentRoot = Path.GetFullPath(Path.Combine(hostDirectory, "..", "components", "rclone"));
        var manifestPath = Path.Combine(componentRoot, "manifest.json");
        if (!File.Exists(manifestPath)) return null;
        var manifest = ReadManifest(manifestPath);
        var executable = RequireChild(componentRoot, manifest.Executable);
        var binary = BundledRcloneDiscovery.RequireVerified(executable, manifest.Sha256, manifest.Version);
        var configuration = RcloneDaemonConfiguration.Create(ReserveLoopbackPort());
        var configPath = Path.Combine(Path.GetFullPath(dataRootPath), "runtime", "rclone.conf");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        if (!File.Exists(configPath)) File.WriteAllText(configPath, string.Empty);
        var job = new RcloneJob();
        ContainedRcloneProcess? process = null;
        try
        {
            process = job.Launch(binary, configuration.BuildArguments(configPath));
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            Exception? last = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.WaitForExit(TimeSpan.Zero)) throw new InvalidOperationException("Managed rclone exited during startup.");
                try
                {
                    var runtime = await RcloneRcAdapter.DiscoverAsync(configuration, binary, cancellationToken).ConfigureAwait(false);
                    return new(job, process, runtime);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    last = exception;
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }
            throw new TimeoutException("Managed rclone RC did not become healthy.", last);
        }
        catch
        {
            process?.Dispose();
            job.Dispose();
            throw;
        }
    }

    internal static ManagedRcloneManifest ReadManifest(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is 0 or > 16 * 1024) throw new InvalidDataException("Managed rclone manifest size is invalid.");
        var value = JsonSerializer.Deserialize<ManagedRcloneManifest>(bytes, ManifestJson) ?? throw new InvalidDataException("Managed rclone manifest is invalid.");
        if (value.Format != 1 || string.IsNullOrWhiteSpace(value.Version) || value.Version.Length > 64 || value.Sha256.Length != 64 || value.Sha256.Any(x => !Uri.IsHexDigit(x)) || string.IsNullOrWhiteSpace(value.Executable)) throw new InvalidDataException("Managed rclone manifest fields are invalid.");
        return value;
    }

    private static string RequireChild(string root, string relative)
    {
        if (Path.IsPathFullyQualified(relative)) throw new InvalidDataException("Managed rclone path must be relative.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Managed rclone path escaped its component root.");
        return path;
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
