using System.Security.Cryptography;
using System.Text.Json;
using RcloneUI.Contracts.HostProtocol.V1;

namespace RcloneUI.Host;

internal sealed class HostProtocolSession
{
    private static readonly string[] Capabilities = ["host.snapshot", "host.events", "ui.activate"];
    private readonly HostEndpoint endpoint;
    private readonly HostStateAuthority state;

    internal HostProtocolSession(HostEndpoint endpoint, HostStateAuthority state)
    {
        this.endpoint = endpoint;
        this.state = state;
    }

    internal async Task RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        var challengeKey = Convert.FromBase64String(endpoint.ChallengeKey);
        byte[]? connectionKey = null;
        try
        {
            var handshake = await AuthenticateAsync(stream, challengeKey, cancellationToken).ConfigureAwait(false);
            connectionKey = handshake.ConnectionKey;
            ulong inboundSequence = 1;
            ulong outboundSequence = 1;
            while (!cancellationToken.IsCancellationRequested)
            {
                using var frame = await HostFrameStream.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                var request = HostFrameCodec.Decode(frame.Memory.Span, connectionKey, inboundSequence++).Envelope;
                if (request.ProtocolMajor != HostProtocolVersion.Major || request.ProtocolMinor != handshake.NegotiatedMinor)
                    throw new ProtocolException(ProtocolErrorCode.InvalidField);
                if (request.MessageType is not (MessageType.Command or MessageType.Cancel))
                    throw new ProtocolException(ProtocolErrorCode.UnknownMessageType);
                var result = await state.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
                var body = BuildResponseBody(result);
                var response = ProtocolEnvelope.CreateRequest(
                    result.ResultType == "snapshot" ? MessageType.Snapshot : MessageType.Response,
                    request.RequestId,
                    outboundSequence++,
                    result.State,
                    new(request.Request.IdempotencyKey, request.Request.CancellationId, request.Request.DeadlineUtc),
                    body);
                await HostFrameStream.WriteAsync(stream, response, connectionKey, cancellationToken).ConfigureAwait(false);
                if (result.StateChanged)
                {
                    var eventEnvelope = ProtocolEnvelope.CreateRequest(
                        MessageType.Event,
                        new($"event-{result.State.Revision}"),
                        outboundSequence++,
                        result.State,
                        new(new($"event-{result.State.Revision}"), request.Request.CancellationId, request.Request.DeadlineUtc),
                        BuildStateEventBody(result));
                    await HostFrameStream.WriteAsync(stream, eventEnvelope, connectionKey, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (EndOfStreamException)
        {
            // Client disconnect is a session event; Host-owned work and state continue.
        }
        finally
        {
            CryptographicOperations.ZeroMemory(challengeKey);
            if (connectionKey is not null) CryptographicOperations.ZeroMemory(connectionKey);
        }
    }

    private async ValueTask<HandshakeResult> AuthenticateAsync(Stream stream, byte[] challengeKey, CancellationToken cancellationToken)
    {
        using var frame = await HostFrameStream.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        var helloEnvelope = HostFrameCodec.Decode(frame.Memory.Span, challengeKey, 1).Envelope;
        if (helloEnvelope.MessageType != MessageType.Hello) throw new ProtocolException(ProtocolErrorCode.UnknownMessageType);
        var hello = ParseHello(helloEnvelope.Body);
        if (hello.DataRootId != endpoint.DataRootId || hello.Incarnation != endpoint.Incarnation)
            throw new CryptographicException("Handshake endpoint identity mismatch.");
        var negotiation = ProtocolNegotiator.Negotiate(hello.Offer, new(new(HostProtocolVersion.Major, 0, HostProtocolVersion.CurrentMinor), Capabilities));
        if (negotiation.Status != NegotiationStatus.Compatible) throw new InvalidOperationException("Protocol versions are incompatible.");
        var hostNonce = RandomNumberGenerator.GetBytes(ConnectionKeyDerivation.NonceBytes);
        var ackBody = BuildAckBody(hostNonce, negotiation);
        var cursor = state.Cursor;
        var ack = ProtocolEnvelope.CreateRequest(
            MessageType.HelloAck,
            helloEnvelope.RequestId,
            1,
            cursor,
            helloEnvelope.Request,
            ackBody);
        await HostFrameStream.WriteAsync(stream, ack, challengeKey, cancellationToken).ConfigureAwait(false);
        var connectionKey = ConnectionKeyDerivation.Derive(challengeKey, hello.ClientNonce, hostNonce);
        CryptographicOperations.ZeroMemory(hostNonce);
        return new(connectionKey, negotiation.Minor!.Value);
    }

    private static ParsedHello ParseHello(JsonElement body)
    {
        try
        {
            var dataRootId = body.GetProperty("dataRootId").GetGuid();
            var incarnation = body.GetProperty("incarnation").GetGuid();
            var nonce = Convert.FromBase64String(body.GetProperty("clientNonce").GetString()!);
            if (nonce.Length != ConnectionKeyDerivation.NonceBytes) throw new ProtocolException(ProtocolErrorCode.InvalidField);
            var major = body.GetProperty("protocolMajor").GetInt32();
            var minimum = body.GetProperty("minimumMinor").GetInt32();
            var maximum = body.GetProperty("maximumMinor").GetInt32();
            var capabilities = body.GetProperty("capabilities").EnumerateArray().Take(65).Select(item => item.GetString()!).ToArray();
            if (capabilities.Length > 64 || capabilities.Any(value => value.Length is 0 or > 128))
                throw new ProtocolException(ProtocolErrorCode.ResourceLimitExceeded);
            return new(dataRootId, incarnation, nonce, new(new(major, minimum, maximum), capabilities));
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new ProtocolException(ProtocolErrorCode.InvalidField);
        }
    }

    private static byte[] BuildAckBody(byte[] hostNonce, NegotiationResult negotiation)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            hostNonce = Convert.ToBase64String(hostNonce),
            negotiatedMinor = negotiation.Minor,
            capabilities = negotiation.Capabilities,
        });
    }

    private static byte[] BuildResponseBody(HostCommandResult result)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("resultType", result.ResultType);
            writer.WritePropertyName("result");
            result.Body.WriteTo(writer);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static byte[] BuildStateEventBody(HostCommandResult result) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        eventType = "ui-activation-changed",
        revision = result.State.Revision,
        result = result.Body,
    });

    private sealed record ParsedHello(Guid DataRootId, Guid Incarnation, byte[] ClientNonce, ProtocolOffer Offer);
    private sealed record HandshakeResult(byte[] ConnectionKey, int NegotiatedMinor);
}
