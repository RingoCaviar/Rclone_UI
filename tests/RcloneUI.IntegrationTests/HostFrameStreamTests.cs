using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using RcloneUI.Contracts.HostProtocol.V1;
using RcloneUI.Host;

namespace RcloneUI.IntegrationTests;

public sealed class HostFrameStreamTests
{
    [Fact]
    public async Task IncrementalReaderHandlesOneByteChunks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var key = RandomNumberGenerator.GetBytes(32);
        var envelope = ProtocolEnvelope.CreateRequest(MessageType.Command, new("request"), 1, new(new("epoch"), 0), new(new("key"), new("cancel"), DateTimeOffset.UtcNow.AddMinutes(1)), JsonSerializer.SerializeToUtf8Bytes(new { commandType = "get-snapshot" }));
        var bytes = HostFrameCodec.Encode(envelope, key);
        await using var stream = new OneByteStream(bytes);
        using var frame = await HostFrameStream.ReadAsync(stream, cancellationToken);
        Assert.Equal("get-snapshot", HostFrameCodec.Decode(frame.Memory.Span, key, 1).Envelope.Body.GetProperty("commandType").GetString());
    }

    [Theory]
    [InlineData(0, ProtocolErrorCode.TruncatedPrefix)]
    [InlineData(3, ProtocolErrorCode.TruncatedPrefix)]
    [InlineData(4, ProtocolErrorCode.FrameLengthMismatch)]
    public async Task TruncationHasTypedBoundedFailure(int availableBytes, ProtocolErrorCode expected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var bytes = new byte[Math.Max(availableBytes, 4)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 64);
        await using var stream = new MemoryStream(bytes, 0, availableBytes, writable: false);
        var exception = await Assert.ThrowsAsync<ProtocolException>(async () => await HostFrameStream.ReadAsync(stream, cancellationToken));
        Assert.Equal(expected, exception.Code);
    }

    [Fact]
    public async Task OversizedPrefixIsRejectedBeforePayloadRead()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var prefix = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(prefix, HostFrameCodec.MaximumJsonBytes + 1u);
        await using var stream = new MemoryStream(prefix, writable: false);
        var exception = await Assert.ThrowsAsync<ProtocolException>(async () => await HostFrameStream.ReadAsync(stream, cancellationToken));
        Assert.Equal(ProtocolErrorCode.FrameTooLarge, exception.Code);
    }

    private sealed class OneByteStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
