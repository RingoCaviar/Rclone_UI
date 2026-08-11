using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RcloneUI.Contracts.HostProtocol.V1;

namespace RcloneUI.Contracts.Tests;

public sealed class HostProtocolContractTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();

    [Fact]
    public void AuthenticatedFrameRoundTripsExactEnvelope()
    {
        var envelope = ProtocolEnvelope.CreateRequest(
            MessageType.Command,
            new RequestId("request-1"),
            1,
            new StateCursor(new StateEpoch("epoch-1"), 7),
            new RequestMetadata(
                new IdempotencyKey("idempotency-1"),
                new CancellationId("cancellation-1"),
                DateTimeOffset.Parse("2030-01-02T03:04:05Z", CultureInfo.InvariantCulture)),
            "{\"commandType\":\"activate-ui\"}"u8);

        var encoded = HostFrameCodec.Encode(envelope, Key);
        var decoded = HostFrameCodec.Decode(encoded, Key, 1);

        Assert.Equal("request-1", decoded.Envelope.RequestId.Value);
        Assert.Equal("activate-ui", decoded.Envelope.Body.GetProperty("commandType").GetString());
        Assert.Empty(decoded.AdditiveFields);
    }

    [Theory]
    [InlineData("duplicate", "{\"protocolMajor\":1,\"protocolMajor\":1}", ProtocolErrorCode.DuplicateProperty)]
    [InlineData("unknown-message", "{\"protocolMajor\":1,\"protocolMinor\":0,\"messageType\":\"future\",\"requestId\":\"r\",\"sequence\":1,\"stateEpoch\":\"e\",\"stateRevision\":0,\"deadlineUtc\":\"2030-01-02T03:04:05Z\",\"idempotencyKey\":\"i\",\"cancellationId\":\"c\",\"body\":{}}", ProtocolErrorCode.UnknownMessageType)]
    public void StrictDecoderRejectsInvalidContracts(string _, string json, ProtocolErrorCode expected)
    {
        var frame = AuthenticateRaw(json);

        var exception = Assert.Throws<ProtocolException>(() => HostFrameCodec.Decode(frame, Key, 1));

        Assert.Equal(expected, exception.Code);
    }

    [Fact]
    public void NegotiationSelectsHighestOverlapAndSharedCapabilities()
    {
        var client = new ProtocolOffer(new ProtocolRange(1, 0, 3), ["snapshots", "transfers"]);
        var host = new ProtocolOffer(new ProtocolRange(1, 1, 2), ["mounts", "snapshots"]);

        var result = ProtocolNegotiator.Negotiate(client, host);

        Assert.Equal(NegotiationStatus.Compatible, result.Status);
        Assert.Equal(2, result.Minor);
        Assert.Equal(["snapshots"], result.Capabilities);
    }

    [Fact]
    public void EventGapRequiresFreshSnapshot()
    {
        var observer = new StateStreamCursor(new StateCursor(new StateEpoch("epoch-1"), 10));

        Assert.Equal(StateEventDisposition.Applied, observer.Observe(new StateCursor(new StateEpoch("epoch-1"), 11)));
        Assert.Equal(StateEventDisposition.RequiresSnapshot, observer.Observe(new StateCursor(new StateEpoch("epoch-1"), 13)));
        Assert.Equal(StateEventDisposition.RequiresSnapshot, observer.Observe(new StateCursor(new StateEpoch("epoch-2"), 12)));
    }

    [Fact]
    public void OversizedPrefixIsRejectedWithoutPayload()
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, HostFrameCodec.MaximumJsonBytes + 1u);

        var exception = Assert.Throws<ProtocolException>(() => HostFrameCodec.Decode(prefix, Key, 1));

        Assert.Equal(ProtocolErrorCode.FrameTooLarge, exception.Code);
    }

    [Fact]
    public void InvalidAuthenticationTagIsRejectedBeforeJsonParsing()
    {
        var frame = AuthenticateRaw("not-json");
        frame[^1] ^= 1;

        var exception = Assert.Throws<ProtocolException>(() => HostFrameCodec.Decode(frame, Key, 1));

        Assert.Equal(ProtocolErrorCode.InvalidAuthenticationTag, exception.Code);
    }

    [Fact]
    public void AuthenticatedAdditiveFieldIsPreserved()
    {
        var frame = AuthenticateRaw("{\"protocolMajor\":1,\"protocolMinor\":0,\"messageType\":\"event\",\"requestId\":\"r\",\"sequence\":1,\"stateEpoch\":\"e\",\"stateRevision\":1,\"deadlineUtc\":\"2030-01-02T03:04:05Z\",\"idempotencyKey\":\"i\",\"cancellationId\":\"c\",\"body\":{},\"futureOptional\":true}");

        var decoded = HostFrameCodec.Decode(frame, Key, 1);

        Assert.True(decoded.AdditiveFields["futureOptional"].GetBoolean());
    }

    private static byte[] AuthenticateRaw(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + payload.Length + HostFrameCodec.AuthenticationTagBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, checked((uint)payload.Length));
        payload.CopyTo(frame, 4);
        HMACSHA256.HashData(Key, frame.AsSpan(0, 4 + payload.Length), frame.AsSpan(4 + payload.Length));
        return frame;
    }
}
