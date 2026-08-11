using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
using var bodyDoc = JsonDocument.Parse("{\"command\":\"ActivateUi\",\"expectedStateRevision\":41}");
var envelope = new Envelope(1, 2, "Command", "req-0001", 1, "epoch-a", 41,
    DateTimeOffset.Parse("2000-01-01T00:00:00Z"), "idem-0001", bodyDoc.RootElement.Clone());
var golden = FrameCodec.Encode(envelope, key);
var cases = new List<Case>();

Check("golden-roundtrip", () => FrameCodec.Decode(golden, key, 1).Envelope.RequestId == "req-0001");
CheckReject("truncated-prefix", golden[..3]);
CheckReject("truncated-json", golden[..^33]);
CheckReject("truncated-mac", golden[..^1]);
var oversized = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(oversized, FrameCodec.MaxJsonBytes + 1u); CheckReject("oversized-before-allocation", oversized);
var badMac = (byte[])golden.Clone(); badMac[^1] ^= 1; CheckReject("bad-mac", badMac);

var invalidUtf8 = BuildRaw([0x7B, 0x22, 0x78, 0x22, 0x3A, 0x22, 0xC3, 0x28, 0x22, 0x7D]); CheckReject("invalid-utf8", invalidUtf8);
var duplicate = BuildRaw(Encoding.UTF8.GetBytes("{\"protocolMajor\":1,\"protocolMajor\":1,\"protocolMinor\":0,\"messageType\":\"Command\",\"requestId\":\"r\",\"sequence\":1,\"stateEpoch\":\"e\",\"stateRevision\":0,\"deadlineUtc\":\"2026-08-11T06:00:00Z\",\"idempotencyKey\":\"i\",\"body\":{}}")); CheckReject("duplicate-key", duplicate);
var missing = BuildRaw(Encoding.UTF8.GetBytes("{\"protocolMajor\":1}")); CheckReject("missing-required", missing);
var unknownType = envelope with { MessageType = "FutureRequired" }; CheckReject("unknown-message-type", FrameCodec.Encode(unknownType, key));

var sequence = new SequenceGate();
Check("sequence-first", () => sequence.Accept(1));
Check("sequence-next", () => sequence.Accept(2));
Check("sequence-replay-rejected", () => !sequence.Accept(2));
Check("sequence-gap-rejected", () => !sequence.Accept(4));

Check("major-match-minor-overlap", () => Negotiation.Negotiate(new(1, 0, 3), new(1, 1, 2)) == 2);
Check("major-mismatch", () => Negotiation.Negotiate(new(1, 0, 2), new(2, 0, 2)) is null);
Check("minor-no-overlap", () => Negotiation.Negotiate(new(1, 0, 1), new(1, 2, 3)) is null);
var additive = BuildRaw(Encoding.UTF8.GetBytes("{\"protocolMajor\":1,\"protocolMinor\":3,\"messageType\":\"Command\",\"requestId\":\"r2\",\"sequence\":1,\"stateEpoch\":\"e\",\"stateRevision\":1,\"deadlineUtc\":\"2026-08-11T06:00:00Z\",\"idempotencyKey\":\"i2\",\"body\":{},\"futureOptional\":true}"));
Check("unknown-additive-field", () => FrameCodec.Decode(additive, key, 1).Envelope.RequestId == "r2");

var idempotency = new IdempotencyGate();
Check("idempotency-first-executes", () => idempotency.Admit("k", "hash-a") == "Execute");
Check("idempotency-same-replays", () => idempotency.Admit("k", "hash-a") == "ReplayResult");
Check("idempotency-conflict-rejected", () => idempotency.Admit("k", "hash-b") == "RejectConflict");

var revisions = new RevisionGate("epoch-a", 10);
Check("state-next-event", () => revisions.Observe("epoch-a", 11) == "Applied");
Check("state-gap-resnapshot", () => revisions.Observe("epoch-a", 13) == "RequiresSnapshot");
Check("state-epoch-change-resnapshot", () => revisions.Observe("epoch-b", 1) == "RequiresSnapshot");
Check("expired-deadline", () => FrameCodec.Decode(golden, key, 1).Envelope.DeadlineUtc < DateTimeOffset.UtcNow);

var report = new
{
    prototype = true,
    contract = "host-ipc/v1",
    frame = "uint32-le jsonLength | strict UTF-8 JSON | HMAC-SHA-256(length || JSON)",
    maxJsonBytes = FrameCodec.MaxJsonBytes,
    goldenVectorHex = Convert.ToHexString(golden),
    cases = cases.Count,
    passed = cases.Count(item => item.Pass),
    failed = cases.Count(item => !item.Pass),
    results = cases
};
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return cases.All(item => item.Pass) ? 0 : 2;

void Check(string name, Func<bool> assertion)
{
    try { cases.Add(new(name, assertion(), null)); }
    catch (Exception ex) { cases.Add(new(name, false, ex.GetType().Name)); }
}

void CheckReject(string name, byte[] frame)
{
    try { FrameCodec.Decode(frame, key, 1); cases.Add(new(name, false, "accepted")); }
    catch (ProtocolException ex) { cases.Add(new(name, true, ex.Code)); }
}

byte[] BuildRaw(byte[] json)
{
    var frame = new byte[4 + json.Length + 32];
    BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)json.Length);
    json.CopyTo(frame, 4);
    HMACSHA256.HashData(key, frame.AsSpan(0, 4 + json.Length), frame.AsSpan(4 + json.Length));
    return frame;
}

internal sealed record Case(string Name, bool Pass, string? Detail);

internal sealed record Envelope(
    [property: JsonPropertyOrder(0)] int ProtocolMajor,
    [property: JsonPropertyOrder(1)] int ProtocolMinor,
    [property: JsonPropertyOrder(2)] string MessageType,
    [property: JsonPropertyOrder(3)] string RequestId,
    [property: JsonPropertyOrder(4)] ulong Sequence,
    [property: JsonPropertyOrder(5)] string StateEpoch,
    [property: JsonPropertyOrder(6)] ulong StateRevision,
    [property: JsonPropertyOrder(7)] DateTimeOffset DeadlineUtc,
    [property: JsonPropertyOrder(8)] string IdempotencyKey,
    [property: JsonPropertyOrder(9)] JsonElement Body);

internal sealed record DecodedFrame(Envelope Envelope, Dictionary<string, JsonElement> OptionalFields);

internal static class FrameCodec
{
    internal const uint MaxJsonBytes = 8 * 1024 * 1024;
    private static readonly HashSet<string> MessageTypes = ["Hello", "HelloAck", "Command", "Response", "Event", "Error", "Cancel"];
    private static readonly HashSet<string> Required = ["protocolMajor", "protocolMinor", "messageType", "requestId", "sequence", "stateEpoch", "stateRevision", "deadlineUtc", "idempotencyKey", "body"];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static byte[] Encode(Envelope envelope, byte[] key)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (json.Length > MaxJsonBytes) throw new ProtocolException("FrameTooLarge");
        var frame = new byte[4 + json.Length + 32];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)json.Length);
        json.CopyTo(frame, 4);
        HMACSHA256.HashData(key, frame.AsSpan(0, 4 + json.Length), frame.AsSpan(4 + json.Length));
        return frame;
    }

    internal static DecodedFrame Decode(ReadOnlySpan<byte> frame, byte[] key, ulong expectedSequence)
    {
        if (frame.Length < 4) throw new ProtocolException("TruncatedPrefix");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(frame);
        if (length > MaxJsonBytes) throw new ProtocolException("FrameTooLarge");
        var expectedLength = checked(4 + (int)length + 32);
        if (frame.Length != expectedLength) throw new ProtocolException("FrameLengthMismatch");
        var signed = frame[..(4 + (int)length)];
        Span<byte> expectedMac = stackalloc byte[32];
        HMACSHA256.HashData(key, signed, expectedMac);
        if (!CryptographicOperations.FixedTimeEquals(expectedMac, frame[(4 + (int)length)..])) throw new ProtocolException("InvalidMac");
        var json = frame.Slice(4, (int)length);
        ValidateStrictJson(json);
        JsonDocument document;
        try { document = JsonDocument.Parse(json.ToArray()); } catch (JsonException) { throw new ProtocolException("InvalidJson"); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ProtocolException("InvalidEnvelope");
            var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            if (!Required.IsSubsetOf(names)) throw new ProtocolException("MissingRequiredField");
            Envelope envelope;
            try { envelope = document.RootElement.Deserialize<Envelope>(JsonOptions)!; }
            catch (Exception) { throw new ProtocolException("InvalidFieldType"); }
            if (!MessageTypes.Contains(envelope.MessageType)) throw new ProtocolException("UnknownMessageType");
            if (envelope.Sequence != expectedSequence) throw new ProtocolException("UnexpectedSequence");
            var optional = document.RootElement.EnumerateObject().Where(p => !Required.Contains(p.Name)).ToDictionary(p => p.Name, p => p.Value.Clone());
            return new(envelope with { Body = envelope.Body.Clone() }, optional);
        }
    }

    private static void ValidateStrictJson(ReadOnlySpan<byte> json)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(json);
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 64 });
            var stack = new Stack<HashSet<string>?>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) stack.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.StartArray) stack.Push(null);
                else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) stack.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName && (stack.Peek()?.Add(reader.GetString()!) == false)) throw new ProtocolException("DuplicateKey");
            }
        }
        catch (ProtocolException) { throw; }
        catch (DecoderFallbackException) { throw new ProtocolException("InvalidUtf8"); }
        catch (JsonException) { throw new ProtocolException("InvalidJson"); }
    }
}

internal sealed class ProtocolException(string code) : Exception(code) { internal string Code { get; } = code; }
internal sealed class SequenceGate { private ulong next = 1; internal bool Accept(ulong value) { if (value != next) return false; next++; return true; } }
internal sealed record ProtocolRange(int Major, int MinMinor, int MaxMinor);
internal static class Negotiation { internal static int? Negotiate(ProtocolRange a, ProtocolRange b) { if (a.Major != b.Major) return null; var min = Math.Max(a.MinMinor, b.MinMinor); var max = Math.Min(a.MaxMinor, b.MaxMinor); return min <= max ? max : null; } }
internal sealed class IdempotencyGate { private readonly Dictionary<string, string> requests = []; internal string Admit(string key, string hash) { if (!requests.TryGetValue(key, out var prior)) { requests[key] = hash; return "Execute"; } return prior == hash ? "ReplayResult" : "RejectConflict"; } }
internal sealed class RevisionGate(string epoch, ulong revision) { private string epoch = epoch; private ulong revision = revision; internal string Observe(string nextEpoch, ulong nextRevision) { if (nextEpoch != epoch || nextRevision != revision + 1) return "RequiresSnapshot"; revision = nextRevision; return "Applied"; } }
