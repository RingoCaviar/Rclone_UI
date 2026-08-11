using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RcloneUI.Contracts.HostProtocol.V1;

namespace RcloneUI.Host;

internal sealed record HostCommandResult(string ResultType, JsonElement Body, StateCursor State, bool StateChanged = false);

internal sealed class HostStateAuthority
{
    private readonly object sync = new();
    private readonly DurableIdempotencyStore idempotency;
    private readonly StateEpoch epoch = new(Guid.NewGuid().ToString("N"));
    private ulong revision;
    private int activationCount;

    internal HostStateAuthority(string dataRootPath)
    {
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

    internal HostCommandResult Dispatch(ProtocolEnvelope envelope)
    {
        if (envelope.Request.IsExpired(DateTimeOffset.UtcNow))
            return CreateResult("deadline-expired", new { }, Cursor);
        var commandType = ReadCommandType(envelope.Body);
        var semanticHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Body.GetRawText())));
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

            HostCommandResult result;
            if (commandType == "get-snapshot")
            {
                result = CreateResult("snapshot", new { session = "operational", activationCount, remotes = Array.Empty<object>(), copyRuns = Array.Empty<object>() }, new(epoch, revision));
            }
            else if (commandType == "activate-ui")
            {
                activationCount++;
                revision = checked(revision + 1);
                result = CreateResult("activated", new { activationCount }, new(epoch, revision), stateChanged: true);
            }
            else
            {
                result = CreateResult("unknown-command", new { }, new(epoch, revision));
            }

            if (commandType != "get-snapshot")
                idempotency.Record(new(envelope.Request.IdempotencyKey.Value, semanticHash, result.ResultType, result.Body.GetRawText(), result.State.Revision));
            return result;
        }
    }

    private static string ReadCommandType(JsonElement body)
    {
        if (!body.TryGetProperty("commandType", out var value) || value.ValueKind != JsonValueKind.String)
            return string.Empty;
        var commandType = value.GetString()!;
        return commandType.Length <= 64 ? commandType : string.Empty;
    }

    private static HostCommandResult CreateResult(string resultType, object body, StateCursor state, bool stateChanged = false)
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(body));
        return new(resultType, document.RootElement.Clone(), state, stateChanged);
    }
}

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
