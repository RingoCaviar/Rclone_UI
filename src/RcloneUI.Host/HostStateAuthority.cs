using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RcloneUI.Contracts.HostProtocol.V1;
using RcloneUI.DataRoot;
using RcloneUI.Rclone;

namespace RcloneUI.Host;

internal sealed record HostCommandResult(string ResultType, JsonElement Body, StateCursor State, bool StateChanged = false);

internal sealed class HostStateAuthority : IDisposable
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);
    private readonly object sync = new();
    private readonly DurableIdempotencyStore idempotency;
    private readonly IRcloneRuntime? rclone;
    private readonly IHostRemoteProjection? remotes;
    private readonly LibArgon2Binding? argon2;
    private readonly SemaphoreSlim dispatchGate = new(1, 1);
    private readonly Dictionary<Guid, CopyRunState> copyRuns = [];
    private readonly StateEpoch epoch = new(Guid.NewGuid().ToString("N"));
    private ulong revision;
    private int activationCount;

    internal HostStateAuthority(string dataRootPath, IRcloneRuntime? rclone = null, IHostRemoteProjection? remotes = null, LibArgon2Binding? argon2 = null)
    {
        this.rclone = rclone;
        this.remotes = remotes;
        this.argon2 = argon2;
        idempotency = new(Path.Combine(dataRootPath, "runtime", "idempotency.json"));
        foreach (var record in idempotency.Records)
        {
            revision = Math.Max(revision, record.Revision);
            if (record.ResultType != "activated") continue;
            using var body = JsonDocument.Parse(record.ResultBody);
            if (body.RootElement.TryGetProperty("activationCount", out var count))
                activationCount = Math.Max(activationCount, count.GetInt32());
        }
    }

    internal StateCursor Cursor
    {
        get
        {
            lock (sync) return new(epoch, revision);
        }
    }

    internal async ValueTask<HostCommandResult> DispatchAsync(ProtocolEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (envelope.Request.IsExpired(DateTimeOffset.UtcNow))
            return CreateResult("deadline-expired", new { }, Cursor);
        var commandType = ReadCommandType(envelope.Body);
        var semanticHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Body.GetRawText())));
        await dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (sync)
            {
                var prior = idempotency.Find(envelope.Request.IdempotencyKey.Value);
                if (prior is not null)
                {
                    if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(prior.SemanticHash), Convert.FromHexString(semanticHash)))
                        return CreateResult("idempotency-conflict", new { }, new(epoch, revision));
                    using var priorBody = JsonDocument.Parse(prior.ResultBody);
                    return new(prior.ResultType, priorBody.RootElement.Clone(), new(epoch, prior.Revision));
                }
            }

            HostCommandResult result;
            if (commandType == "get-snapshot")
            {
                IReadOnlyList<HostRemoteSummary> summaries = [];
                if (remotes is not null && remotes.SessionState == "operational")
                {
                    try { summaries = await remotes.ListAsync(cancellationToken).ConfigureAwait(false); }
                    catch (Exception exception) when (exception is not OperationCanceledException) { return CreateResult("snapshot-unavailable", new { code = "remote-projection-unavailable" }, Cursor); }
                }
                lock (sync) result = CreateResult("snapshot", new { session = remotes?.SessionState ?? "locked", activationCount, remotes = summaries, copyRuns = copyRuns.Values.OrderBy(x => x.CreatedUtc).ToArray(), rclone = new { status = rclone is null ? "unavailable" : "ready", capabilityBinding = rclone?.Capabilities.Binding }, vault = new { kdfStatus = argon2 is null ? "unavailable" : "ready" } }, new(epoch, revision));
            }
            else if (commandType == "activate-ui")
            {
                lock (sync) { activationCount++; revision = checked(revision + 1); result = CreateResult("activated", new { activationCount }, new(epoch, revision), stateChanged: true); }
            }
            else if (commandType == "unlock-vault")
            {
                result = await UnlockVaultAsync(envelope.Body, cancellationToken).ConfigureAwait(false);
            }
            else if (commandType == "start-copy")
            {
                result = await StartCopyAsync(envelope.Body, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                lock (sync) result = CreateResult("unknown-command", new { }, new(epoch, revision));
            }

            if (commandType is not ("get-snapshot" or "unlock-vault"))
                lock (sync) idempotency.Record(new(envelope.Request.IdempotencyKey.Value, semanticHash, result.ResultType, result.Body.GetRawText(), result.State.Revision));
            return result;
        }
        finally { dispatchGate.Release(); }
    }

    private async ValueTask<HostCommandResult> StartCopyAsync(JsonElement body, CancellationToken cancellationToken)
    {
        if (remotes?.SessionState != "operational") return CreateResult("vault-locked", new { }, Cursor);
        if (rclone is null) return CreateResult("rclone-unavailable", new { recoveryAction = "Install or repair the managed rclone component." }, Cursor);
        if (!body.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object) return CreateResult("copy-invalid", new { code = "arguments-missing" }, Cursor);
        var sourceFs = ReadArgument(arguments, "sourceFs"); var sourcePath = ReadArgument(arguments, "sourcePath");
        var destinationFs = ReadArgument(arguments, "destinationFs"); var destinationPath = ReadArgument(arguments, "destinationPath");
        var binding = ReadArgument(arguments, "capabilityBinding");
        if (sourceFs is null || sourcePath is null || destinationFs is null || destinationPath is null || binding is null) return CreateResult("copy-invalid", new { code = "arguments-invalid" }, Cursor);
        var id = Guid.NewGuid();
        RcloneExecutionHandle handle;
        try
        {
            handle = await rclone.StartAsync(new(id, binding, RclonePrimitive.Copy, new(sourceFs, sourcePath), new(destinationFs, destinationPath), $"copy/{id:N}"), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CreateResult("copy-not-started", new { code = exception.GetType().Name.ToLowerInvariant() }, Cursor);
        }
        CopyRunState state;
        lock (sync)
        {
            revision = checked(revision + 1);
            state = new(id, "running", 0, 0, 0, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            copyRuns.Add(id, state);
        }
        _ = ObserveCopyAsync(handle);
        return CreateResult("copy-accepted", new { runId = id }, Cursor, stateChanged: true);
    }

    private async ValueTask<HostCommandResult> UnlockVaultAsync(JsonElement body, CancellationToken cancellationToken)
    {
        if (remotes is not IHostVaultSession vault) return CreateResult("vault-unavailable", new { }, Cursor);
        if (!body.TryGetProperty("arguments", out var arguments)
            || !arguments.TryGetProperty("passwordUtf8", out var encoded)
            || encoded.ValueKind != JsonValueKind.String
            || encoded.GetString() is not { Length: > 0 and <= 2048 } value)
            return CreateResult("vault-password-invalid", new { }, Cursor);
        byte[] password;
        try { password = Convert.FromBase64String(value); }
        catch (FormatException) { return CreateResult("vault-password-invalid", new { }, Cursor); }
        try
        {
            var resultType = await vault.UnlockAsync(password, cancellationToken).ConfigureAwait(false);
            lock (sync)
            {
                if (resultType == "vault-unlocked") revision = checked(revision + 1);
                return CreateResult(resultType, new { }, new(epoch, revision), resultType == "vault-unlocked");
            }
        }
        finally { CryptographicOperations.ZeroMemory(password); }
    }

    private async Task ObserveCopyAsync(RcloneExecutionHandle handle)
    {
        try
        {
            var stats = await rclone!.GetStatsAsync(handle, CancellationToken.None).ConfigureAwait(false);
            lock (sync) copyRuns[handle.ExecutionId] = copyRuns[handle.ExecutionId] with { Bytes = stats.Bytes, TotalBytes = stats.TotalBytes, BytesPerSecond = stats.BytesPerSecond, UpdatedUtc = DateTimeOffset.UtcNow };
            var result = await rclone.WaitAsync(handle, CancellationToken.None).ConfigureAwait(false);
            lock (sync)
            {
                revision = checked(revision + 1);
                copyRuns[handle.ExecutionId] = copyRuns[handle.ExecutionId] with { State = result.Success ? "succeeded" : result.Cancelled ? "cancelled" : "failed", ErrorCode = result.ErrorCode, UpdatedUtc = DateTimeOffset.UtcNow };
            }
        }
        catch (Exception exception)
        {
            lock (sync)
            {
                revision = checked(revision + 1);
                copyRuns[handle.ExecutionId] = copyRuns[handle.ExecutionId] with { State = "failed", ErrorCode = exception.GetType().Name.ToLowerInvariant(), UpdatedUtc = DateTimeOffset.UtcNow };
            }
        }
    }

    private static string? ReadArgument(JsonElement arguments, string name) => arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 2048 } text ? text : null;

    private static string ReadCommandType(JsonElement body)
    {
        if (!body.TryGetProperty("commandType", out var value) || value.ValueKind != JsonValueKind.String)
            return string.Empty;
        var commandType = value.GetString()!;
        return commandType.Length <= 64 ? commandType : string.Empty;
    }

    public void Dispose() => dispatchGate.Dispose();

    private static HostCommandResult CreateResult(string resultType, object body, StateCursor state, bool stateChanged = false)
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(body, WireJson));
        return new(resultType, document.RootElement.Clone(), state, stateChanged);
    }
}

internal sealed record CopyRunState(Guid RunId, string State, long Bytes, long TotalBytes, double BytesPerSecond, string? ErrorCode, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);

internal sealed record IdempotencyRecord(string Key, string SemanticHash, string ResultType, string ResultBody, ulong Revision);

internal sealed class DurableIdempotencyStore
{
    private const int MaximumRecords = 4096;
    private readonly string path;
    private readonly Dictionary<string, IdempotencyRecord> records = new(StringComparer.Ordinal);

    internal DurableIdempotencyStore(string path)
    {
        this.path = path;
        if (!File.Exists(path)) return;
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Idempotency store exceeds its resource limit.");
        var loaded = JsonSerializer.Deserialize<List<IdempotencyRecord>>(bytes) ?? throw new InvalidDataException("Idempotency store is invalid.");
        if (loaded.Count > MaximumRecords) throw new InvalidDataException("Idempotency store has too many records.");
        foreach (var record in loaded) records.Add(record.Key, record);
    }

    internal IdempotencyRecord? Find(string key) => records.GetValueOrDefault(key);

    internal IEnumerable<IdempotencyRecord> Records => records.Values;

    internal void Record(IdempotencyRecord record)
    {
        if (records.Count >= MaximumRecords) throw new InvalidOperationException("Idempotency retention is full.");
        records.Add(record.Key, record);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".new";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(records.Values.OrderBy(value => value.Key, StringComparer.Ordinal));
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }
}
