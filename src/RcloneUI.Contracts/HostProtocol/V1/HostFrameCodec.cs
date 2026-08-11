using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RcloneUI.Contracts.HostProtocol.V1;

public static class HostFrameCodec
{
    public const int MaximumJsonBytes = 8 * 1024 * 1024;
    public const int AuthenticationTagBytes = 32;
    public const int MaximumDepth = 64;
    public const int MaximumProperties = 4096;
    public const int MaximumArrayItems = 16384;
    public const int MaximumStringCharacters = 1_048_576;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] RequiredProperties =
    [
        "protocolMajor", "protocolMinor", "messageType", "requestId", "sequence", "stateEpoch",
        "stateRevision", "deadlineUtc", "idempotencyKey", "cancellationId", "body",
    ];

    public static byte[] Encode(ProtocolEnvelope envelope, ReadOnlySpan<byte> authenticationKey)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateKey(authenticationKey);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocolMajor", envelope.ProtocolMajor);
            writer.WriteNumber("protocolMinor", envelope.ProtocolMinor);
            writer.WriteString("messageType", ToWireName(envelope.MessageType));
            writer.WriteString("requestId", envelope.RequestId.Value);
            writer.WriteNumber("sequence", envelope.Sequence);
            writer.WriteString("stateEpoch", envelope.State.Epoch.Value);
            writer.WriteNumber("stateRevision", envelope.State.Revision);
            writer.WriteString("deadlineUtc", envelope.Request.DeadlineUtc.UtcDateTime);
            writer.WriteString("idempotencyKey", envelope.Request.IdempotencyKey.Value);
            writer.WriteString("cancellationId", envelope.Request.CancellationId.Value);
            writer.WritePropertyName("body");
            envelope.Body.WriteTo(writer);
            writer.WriteEndObject();
        }

        if (buffer.WrittenCount > MaximumJsonBytes)
        {
            throw new ProtocolException(ProtocolErrorCode.FrameTooLarge);
        }

        var frame = new byte[4 + buffer.WrittenCount + AuthenticationTagBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, checked((uint)buffer.WrittenCount));
        buffer.WrittenSpan.CopyTo(frame.AsSpan(4));
        HMACSHA256.HashData(authenticationKey, frame.AsSpan(0, 4 + buffer.WrittenCount), frame.AsSpan(4 + buffer.WrittenCount));
        return frame;
    }

    public static DecodedHostFrame Decode(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> authenticationKey, ulong expectedSequence)
    {
        ValidateKey(authenticationKey);
        if (frame.Length < sizeof(uint))
        {
            throw new ProtocolException(ProtocolErrorCode.TruncatedPrefix);
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(frame);
        if (length > MaximumJsonBytes)
        {
            throw new ProtocolException(ProtocolErrorCode.FrameTooLarge);
        }

        var completeLength = checked(sizeof(uint) + (int)length + AuthenticationTagBytes);
        if (frame.Length != completeLength)
        {
            throw new ProtocolException(ProtocolErrorCode.FrameLengthMismatch);
        }

        var authenticatedBytes = frame[..(sizeof(uint) + (int)length)];
        Span<byte> expectedTag = stackalloc byte[AuthenticationTagBytes];
        HMACSHA256.HashData(authenticationKey, authenticatedBytes, expectedTag);
        if (!CryptographicOperations.FixedTimeEquals(expectedTag, frame[authenticatedBytes.Length..]))
        {
            throw new ProtocolException(ProtocolErrorCode.InvalidAuthenticationTag);
        }

        var json = frame.Slice(sizeof(uint), (int)length);
        ValidateStrictJson(json);
        using var document = ParseDocument(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ProtocolException(ProtocolErrorCode.InvalidField);
        }

        foreach (var required in RequiredProperties)
        {
            if (!root.TryGetProperty(required, out _))
            {
                throw new ProtocolException(ProtocolErrorCode.MissingRequiredField);
            }
        }

        var sequence = ReadUInt64(root, "sequence");
        if (sequence == 0 || sequence != expectedSequence)
        {
            throw new ProtocolException(ProtocolErrorCode.UnexpectedSequence);
        }

        var body = root.GetProperty("body");
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new ProtocolException(ProtocolErrorCode.InvalidField);
        }

        var envelope = new ProtocolEnvelope(
            ReadInt32(root, "protocolMajor"),
            ReadInt32(root, "protocolMinor"),
            ReadMessageType(root),
            new(ReadBoundedString(root, "requestId", ProtocolIdentifier.MaximumLength)),
            sequence,
            new(new(ReadBoundedString(root, "stateEpoch", ProtocolIdentifier.MaximumLength)), ReadUInt64(root, "stateRevision")),
            new(
                new(ReadBoundedString(root, "idempotencyKey", ProtocolIdentifier.MaximumLength)),
                new(ReadBoundedString(root, "cancellationId", ProtocolIdentifier.MaximumLength)),
                ReadDeadline(root)),
            body.Clone());

        var requiredNames = RequiredProperties.ToHashSet(StringComparer.Ordinal);
        var additive = root.EnumerateObject()
            .Where(property => !requiredNames.Contains(property.Name))
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        return new(envelope, additive);
    }

    private static void ValidateStrictJson(ReadOnlySpan<byte> json)
    {
        if (json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF)
        {
            throw new ProtocolException(ProtocolErrorCode.InvalidUtf8);
        }

        try
        {
            _ = StrictUtf8.GetCharCount(json);
            var reader = new Utf8JsonReader(json, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumDepth,
            });
            var containers = new Stack<ContainerState>();
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        containers.Push(new(true));
                        break;
                    case JsonTokenType.StartArray:
                        containers.Push(new(false));
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        containers.Pop();
                        CountArrayValue(containers);
                        break;
                    case JsonTokenType.PropertyName:
                        var container = containers.Peek();
                        if (++container.Count > MaximumProperties)
                        {
                            throw new ProtocolException(ProtocolErrorCode.ResourceLimitExceeded);
                        }

                        var propertyName = reader.GetString()!;
                        if (propertyName.Length > ProtocolIdentifier.MaximumLength || !container.Properties!.Add(propertyName))
                        {
                            throw new ProtocolException(propertyName.Length > ProtocolIdentifier.MaximumLength
                                ? ProtocolErrorCode.ResourceLimitExceeded
                                : ProtocolErrorCode.DuplicateProperty);
                        }

                        break;
                    case JsonTokenType.String:
                        if (reader.GetString()!.Length > MaximumStringCharacters)
                        {
                            throw new ProtocolException(ProtocolErrorCode.ResourceLimitExceeded);
                        }

                        CountArrayValue(containers);
                        break;
                    default:
                        CountArrayValue(containers);
                        break;
                }
            }
        }
        catch (ProtocolException)
        {
            throw;
        }
        catch (DecoderFallbackException)
        {
            throw new ProtocolException(ProtocolErrorCode.InvalidUtf8);
        }
        catch (JsonException)
        {
            throw new ProtocolException(ProtocolErrorCode.InvalidJson);
        }
    }

    private static void CountArrayValue(Stack<ContainerState> containers)
    {
        if (containers.TryPeek(out var container) && !container.IsObject && ++container.Count > MaximumArrayItems)
        {
            throw new ProtocolException(ProtocolErrorCode.ResourceLimitExceeded);
        }
    }

    private static JsonDocument ParseDocument(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = MaximumDepth });
        }
        catch (JsonException)
        {
            throw new ProtocolException(ProtocolErrorCode.InvalidJson);
        }
    }

    private static MessageType ReadMessageType(JsonElement root)
    {
        var value = ReadBoundedString(root, "messageType", 32);
        return value switch
        {
            "hello" => MessageType.Hello,
            "hello-ack" => MessageType.HelloAck,
            "command" => MessageType.Command,
            "response" => MessageType.Response,
            "event" => MessageType.Event,
            "error" => MessageType.Error,
            "cancel" => MessageType.Cancel,
            "snapshot" => MessageType.Snapshot,
            _ => throw new ProtocolException(ProtocolErrorCode.UnknownMessageType),
        };
    }

    private static string ToWireName(MessageType value) => value switch
    {
        MessageType.Hello => "hello",
        MessageType.HelloAck => "hello-ack",
        MessageType.Command => "command",
        MessageType.Response => "response",
        MessageType.Event => "event",
        MessageType.Error => "error",
        MessageType.Cancel => "cancel",
        MessageType.Snapshot => "snapshot",
        _ => throw new ProtocolException(ProtocolErrorCode.UnknownMessageType),
    };

    private static string ReadBoundedString(JsonElement root, string name, int maximumLength)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new ProtocolException(ProtocolErrorCode.InvalidField);
        }

        var value = property.GetString()!;
        if (value.Length is 0 || value.Length > maximumLength || value.Any(static character => char.IsControl(character)))
        {
            throw new ProtocolException(ProtocolErrorCode.InvalidField);
        }

        return value;
    }

    private static int ReadInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || !property.TryGetInt32(out var value) || value < 0)
        {
            throw new ProtocolException(ProtocolErrorCode.InvalidField);
        }

        return value;
    }

    private static ulong ReadUInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || !property.TryGetUInt64(out var value))
        {
            throw new ProtocolException(ProtocolErrorCode.InvalidField);
        }

        return value;
    }

    private static DateTimeOffset ReadDeadline(JsonElement root)
    {
        var value = ReadBoundedString(root, "deadlineUtc", 40);
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var deadline)
            || !value.EndsWith('Z'))
        {
            throw new ProtocolException(ProtocolErrorCode.InvalidField);
        }

        return deadline;
    }

    private static void ValidateKey(ReadOnlySpan<byte> authenticationKey)
    {
        if (authenticationKey.Length < 32)
        {
            throw new ArgumentException("Authentication keys must contain at least 256 bits.", nameof(authenticationKey));
        }
    }

    private sealed class ContainerState(bool isObject)
    {
        internal bool IsObject { get; } = isObject;
        internal HashSet<string>? Properties { get; } = isObject ? new(StringComparer.Ordinal) : null;
        internal int Count { get; set; }
    }
}
