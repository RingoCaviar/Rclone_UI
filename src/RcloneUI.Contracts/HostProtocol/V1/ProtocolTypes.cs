using System.Text.Json;

namespace RcloneUI.Contracts.HostProtocol.V1;

public static class HostProtocolVersion
{
    public const string Family = "host-ipc/v1";
    public const int Major = 1;
    public const int CurrentMinor = 0;
}

public enum MessageType
{
    Hello,
    HelloAck,
    Command,
    Response,
    Event,
    Error,
    Cancel,
    Snapshot,
}

public enum ProtocolErrorCode
{
    TruncatedPrefix,
    FrameTooLarge,
    FrameLengthMismatch,
    InvalidAuthenticationTag,
    InvalidUtf8,
    InvalidJson,
    DuplicateProperty,
    ResourceLimitExceeded,
    MissingRequiredField,
    InvalidField,
    UnknownMessageType,
    UnexpectedSequence,
}

public sealed class ProtocolException(ProtocolErrorCode code) : Exception(code.ToString())
{
    public ProtocolErrorCode Code { get; } = code;
}

public readonly record struct ProtocolRange(int Major, int MinimumMinor, int MaximumMinor)
{
    public bool IsValid => Major >= 0 && MinimumMinor >= 0 && MaximumMinor >= MinimumMinor;
}

public sealed record ProtocolOffer(ProtocolRange Range, IReadOnlyCollection<string> Capabilities);

public enum NegotiationStatus
{
    Compatible,
    MajorMismatch,
    MinorRangeMismatch,
}

public sealed record NegotiationResult(NegotiationStatus Status, int? Minor, IReadOnlyList<string> Capabilities);

public static class ProtocolNegotiator
{
    public static NegotiationResult Negotiate(ProtocolOffer client, ProtocolOffer host)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(host);
        if (!client.Range.IsValid || !host.Range.IsValid)
        {
            throw new ArgumentException("Protocol ranges must be non-negative inclusive ranges.");
        }

        if (client.Range.Major != host.Range.Major)
        {
            return new(NegotiationStatus.MajorMismatch, null, []);
        }

        var minimum = Math.Max(client.Range.MinimumMinor, host.Range.MinimumMinor);
        var maximum = Math.Min(client.Range.MaximumMinor, host.Range.MaximumMinor);
        if (minimum > maximum)
        {
            return new(NegotiationStatus.MinorRangeMismatch, null, []);
        }

        var hostCapabilities = host.Capabilities.ToHashSet(StringComparer.Ordinal);
        var shared = client.Capabilities
            .Where(hostCapabilities.Contains)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new(NegotiationStatus.Compatible, maximum, shared);
    }
}

public readonly record struct StateCursor(StateEpoch Epoch, ulong Revision);

public sealed record RequestMetadata(
    IdempotencyKey IdempotencyKey,
    CancellationId CancellationId,
    DateTimeOffset DeadlineUtc)
{
    public bool IsExpired(DateTimeOffset now) => DeadlineUtc <= now;
}

public sealed record ProtocolEnvelope(
    int ProtocolMajor,
    int ProtocolMinor,
    MessageType MessageType,
    RequestId RequestId,
    ulong Sequence,
    StateCursor State,
    RequestMetadata Request,
    JsonElement Body)
{
    public static ProtocolEnvelope CreateRequest(
        MessageType messageType,
        RequestId requestId,
        ulong sequence,
        StateCursor state,
        RequestMetadata request,
        ReadOnlySpan<byte> body)
    {
        using var document = JsonDocument.Parse(body.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A protocol body must be a JSON object.", nameof(body));
        }

        return new(
            HostProtocolVersion.Major,
            HostProtocolVersion.CurrentMinor,
            messageType,
            requestId,
            sequence,
            state,
            request,
            document.RootElement.Clone());
    }
}

public sealed record DecodedHostFrame(
    ProtocolEnvelope Envelope,
    IReadOnlyDictionary<string, JsonElement> AdditiveFields);

public enum StateEventDisposition
{
    Applied,
    RequiresSnapshot,
}

public sealed class StateStreamCursor(StateCursor initial)
{
    private StateCursor current = initial;
    private bool requiresSnapshot;

    public StateEventDisposition Observe(StateCursor next)
    {
        if (requiresSnapshot || next.Epoch != current.Epoch || current.Revision == ulong.MaxValue || next.Revision != current.Revision + 1)
        {
            requiresSnapshot = true;
            return StateEventDisposition.RequiresSnapshot;
        }

        current = next;
        return StateEventDisposition.Applied;
    }
}

public sealed record HostSnapshot(StateCursor State, DateTimeOffset CapturedUtc, JsonElement Body);

public sealed record HostEvent(EventId EventId, StateCursor State, JsonElement Body);
