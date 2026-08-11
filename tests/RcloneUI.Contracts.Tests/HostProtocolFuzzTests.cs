using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RcloneUI.Contracts.HostProtocol.V1;

namespace RcloneUI.Contracts.Tests;

public sealed class HostProtocolFuzzTests
{
    private static readonly byte[] Key = SHA256.HashData("host-ipc-fuzz-key"u8);
    private const string ValidJson = "{\"protocolMajor\":1,\"protocolMinor\":0,\"messageType\":\"command\",\"requestId\":\"r\",\"sequence\":1,\"stateEpoch\":\"e\",\"stateRevision\":0,\"deadlineUtc\":\"2030-01-02T03:04:05Z\",\"idempotencyKey\":\"i\",\"cancellationId\":\"c\",\"body\":{\"commandType\":\"get-snapshot\"}}";

    public static IEnumerable<object[]> HostileContracts()
    {
        yield return ["\uFEFF" + ValidJson, ProtocolErrorCode.InvalidUtf8];
        yield return [ValidJson.Replace("\"protocolMajor\":1", "\"protocolMajor\":1,\"protocolMajor\":1", StringComparison.Ordinal), ProtocolErrorCode.DuplicateProperty];
        yield return [ValidJson.Replace("\"command\"", "\"unknown\"", StringComparison.Ordinal), ProtocolErrorCode.UnknownMessageType];
        yield return [ValidJson.Replace("\"sequence\":1", "\"sequence\":18446744073709551616", StringComparison.Ordinal), ProtocolErrorCode.InvalidField];
        yield return [ValidJson.Replace("\"body\":{\"commandType\":\"get-snapshot\"}", "\"body\":[]", StringComparison.Ordinal), ProtocolErrorCode.InvalidField];
        yield return [ValidJson[..^1], ProtocolErrorCode.InvalidJson];
        yield return [ValidJson.Replace("\"requestId\":\"r\"", $"\"requestId\":\"{new string('r', 129)}\"", StringComparison.Ordinal), ProtocolErrorCode.InvalidField];
    }

    [Theory]
    [MemberData(nameof(HostileContracts))]
    public void AuthenticatedHostileContractsFailWithTypedErrors(string json, ProtocolErrorCode expected)
    {
        var exception = Assert.Throws<ProtocolException>(() => HostFrameCodec.Decode(Authenticate(json), Key, 1));
        Assert.Equal(expected, exception.Code);
    }

    [Fact]
    public void DeterministicMutationCorpusNeverEscapesProtocolBoundary()
    {
        var seed = Encoding.UTF8.GetBytes(ValidJson);
        var random = new Random(0x52434C4F);
        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            var payload = seed.ToArray();
            var changes = 1 + random.Next(8);
            for (var change = 0; change < changes; change++) payload[random.Next(payload.Length)] = (byte)random.Next(256);
            var frame = Authenticate(payload);
            try
            {
                var decoded = HostFrameCodec.Decode(frame, Key, 1);
                Assert.True(decoded.Envelope.ProtocolMajor >= 0);
                Assert.Equal(1UL, decoded.Envelope.Sequence);
            }
            catch (ProtocolException exception)
            {
                Assert.True(Enum.IsDefined(exception.Code));
            }
        }
    }

    [Fact]
    public void CorruptedAuthenticationAlwaysWinsOverHostileJson()
    {
        var frame = Authenticate("not-json");
        frame[^1] ^= 0x80;
        var exception = Assert.Throws<ProtocolException>(() => HostFrameCodec.Decode(frame, Key, 1));
        Assert.Equal(ProtocolErrorCode.InvalidAuthenticationTag, exception.Code);
    }

    private static byte[] Authenticate(string json) => Authenticate(Encoding.UTF8.GetBytes(json));
    private static byte[] Authenticate(byte[] payload)
    {
        var frame = new byte[sizeof(uint) + payload.Length + HostFrameCodec.AuthenticationTagBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, checked((uint)payload.Length));
        payload.CopyTo(frame, sizeof(uint));
        HMACSHA256.HashData(Key, frame.AsSpan(0, sizeof(uint) + payload.Length), frame.AsSpan(sizeof(uint) + payload.Length));
        return frame;
    }
}
