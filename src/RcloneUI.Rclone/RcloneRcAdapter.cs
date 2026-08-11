using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace RcloneUI.Rclone;

public sealed class RcloneRcAdapter : IRcloneRuntime, IDisposable
{
    private readonly HttpClient client;

    private RcloneRcAdapter(HttpClient client, RcloneCapabilitySnapshot capabilities)
    {
        this.client = client;
        Capabilities = capabilities;
    }

    public RcloneCapabilitySnapshot Capabilities { get; }

    public void Dispose() => client.Dispose();

    public async Task<RcloneBackendCapabilitySnapshot> DiscoverBackendAsync(string fileSystem, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("operations/fsinfo", new { fs = fileSystem }, cancellationToken).ConfigureAwait(false);
        using var document = await ReadSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return RcloneCapabilityDiscovery.CreateBackend(fileSystem, document.RootElement, DateTimeOffset.UtcNow);
    }

    public static async Task<RcloneRcAdapter> DiscoverAsync(
        RcloneDaemonConfiguration configuration,
        VerifiedRcloneBinary binary,
        CancellationToken cancellationToken)
    {
        var client = configuration.CreateClient();
        try
        {
            using var version = await PostAsync(client, "core/version", cancellationToken).ConfigureAwait(false);
            var reported = version.RootElement.GetProperty("version").GetString() ?? string.Empty;
            var health = BundledRcloneDiscovery.AssessReportedVersion(binary, reported);
            if (health.Health != RcloneComponentHealth.Healthy) throw new InvalidDataException(health.Detail);
            using var rcList = await PostAsync(client, "rc/list", cancellationToken).ConfigureAwait(false);
            using var options = await PostAsync(client, "options/info", cancellationToken).ConfigureAwait(false);
            using var mounts = await PostAsync(client, "mount/types", cancellationToken).ConfigureAwait(false);
            var snapshot = RcloneCapabilityDiscovery.Create(binary.Identity, rcList.RootElement, options.RootElement, mounts.RootElement, DateTimeOffset.UtcNow);
            return new(client, snapshot);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async ValueTask<RcloneExecutionHandle> StartAsync(RcloneExecutionRequest request, CancellationToken cancellationToken)
    {
        EnsureBinding(request.ExpectedCapabilityBinding);
        var (endpoint, body) = RcloneRequestMapper.Map(request);
        if (!Capabilities.Endpoints.Contains(endpoint))
            throw new NotSupportedException($"The managed rclone does not expose '{endpoint}'.");
        using var response = await client.PostAsJsonAsync(endpoint, body, cancellationToken).ConfigureAwait(false);
        using var result = await ReadSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        if (!result.RootElement.TryGetProperty("jobid", out var jobId) || !jobId.TryGetInt64(out var value))
            throw new InvalidDataException("rclone did not return an asynchronous job identifier.");
        return new(request.ExecutionId, value, request.Group);
    }

    public async ValueTask<RcloneTransferStats> GetStatsAsync(RcloneExecutionHandle handle, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("core/stats", new { group = handle.Group }, cancellationToken).ConfigureAwait(false);
        using var document = await ReadSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        return new(
            ReadInt64(root, "bytes"),
            ReadInt64(root, "totalBytes"),
            ReadInt64(root, "transfers"),
            ReadInt64(root, "errors"),
            ReadDouble(root, "speed"),
            TimeSpan.FromSeconds(ReadDouble(root, "elapsedTime")),
            false);
    }

    public async ValueTask<RcloneExecutionResult> WaitAsync(RcloneExecutionHandle handle, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var response = await client.PostAsJsonAsync("job/status", new { jobid = handle.JobId }, cancellationToken).ConfigureAwait(false);
            using var document = await ReadSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetProperty("finished", out var finished) || !finished.GetBoolean())
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var success = root.TryGetProperty("success", out var succeeded) && succeeded.GetBoolean();
            var error = root.TryGetProperty("error", out var errorValue) && errorValue.ValueKind == JsonValueKind.String ? errorValue.GetString() : null;
            return new(success, false, error, root.Clone());
        }
    }

    public async ValueTask CancelAsync(RcloneExecutionHandle handle, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("job/stop", new { jobid = handle.JobId }, cancellationToken).ConfigureAwait(false);
        using var _ = await ReadSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureBinding(string expected)
    {
        if (!StringComparer.Ordinal.Equals(expected, Capabilities.Binding))
            throw new RcloneCapabilityChangedException(expected, Capabilities.Binding);
    }

    private static async Task<JsonDocument> ReadSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> PostAsync(HttpClient client, string endpoint, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(endpoint, new { }, cancellationToken).ConfigureAwait(false);
        return await ReadSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static long ReadInt64(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.TryGetInt64(out var parsed) ? parsed : 0;

    private static double ReadDouble(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var item)) return 0;
        if (item.TryGetDouble(out var parsed)) return parsed;
        return item.ValueKind == JsonValueKind.String && double.TryParse(item.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
    }
}

internal static class RcloneRequestMapper
{
    internal static (string Endpoint, object Body) Map(RcloneExecutionRequest request)
    {
        var body = new Dictionary<string, object?>
        {
            ["_async"] = true,
            ["_group"] = request.Group,
            ["_config"] = BuildConfig(request),
        };
        var endpoint = request.Primitive switch
        {
            RclonePrimitive.List => MapSingle(body, request, "operations/list"),
            RclonePrimitive.Copy => MapPair(body, request, "sync/copy"),
            RclonePrimitive.Check => MapPair(body, request, "operations/check"),
            RclonePrimitive.DeleteFile => MapSingle(body, request, "operations/deletefile"),
            RclonePrimitive.Stat => MapSingle(body, request, "operations/stat"),
            RclonePrimitive.Mount => MapMount(body, request),
            RclonePrimitive.Unmount => MapUnmount(body, request),
            RclonePrimitive.MountStatus => "mount/listmounts",
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        return (endpoint, body);
    }

    private static string MapSingle(Dictionary<string, object?> body, RcloneExecutionRequest request, string endpoint)
    {
        body["fs"] = request.Source.FileSystem;
        body["remote"] = request.Source.Path;
        return endpoint;
    }

    private static string MapPair(Dictionary<string, object?> body, RcloneExecutionRequest request, string endpoint)
    {
        var destination = request.Destination ?? throw new ArgumentException("This primitive requires a destination.", nameof(request));
        body["srcFs"] = request.Source.FileSystem;
        body["srcRemote"] = request.Source.Path;
        body["dstFs"] = destination.FileSystem;
        body["dstRemote"] = destination.Path;
        return endpoint;
    }

    private static string MapMount(Dictionary<string, object?> body, RcloneExecutionRequest request)
    {
        var destination = request.Destination ?? throw new ArgumentException("Mount requires a mount point.", nameof(request));
        var options = request.MountOptions ?? throw new ArgumentException("Mount requires explicit options.", nameof(request));
        body["fs"] = request.Source.FileSystem + request.Source.Path;
        body["mountPoint"] = destination.Path;
        body["mountType"] = options.MountType;
        body["vfsOpt"] = new { ReadOnly = options.ReadOnly, CacheMode = 0 };
        body["mountOpt"] = new { VolumeName = options.VolumeName, NetworkMode = true };
        return "mount/mount";
    }

    private static string MapUnmount(Dictionary<string, object?> body, RcloneExecutionRequest request)
    {
        body["mountPoint"] = request.Source.Path;
        return "mount/unmount";
    }

    private static Dictionary<string, object> BuildConfig(RcloneExecutionRequest request)
    {
        var config = new Dictionary<string, object> { ["Retries"] = request.HighLevelRetries };
        if (request.MaximumTransferBytes is not null) config["MaxTransfer"] = request.MaximumTransferBytes.Value;
        if (request.MaximumDuration is not null) config["MaxDuration"] = request.MaximumDuration.Value.ToString("c", CultureInfo.InvariantCulture);
        return config;
    }
}
