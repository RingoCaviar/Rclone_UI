using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using RcloneUI.Contracts.HostProtocol.V1;

namespace RcloneUI.Host;

internal sealed class RentedHostFrame : IDisposable
{
    private byte[]? buffer;

    internal RentedHostFrame(byte[] buffer, int length)
    {
        this.buffer = buffer;
        Memory = buffer.AsMemory(0, length);
    }

    internal ReadOnlyMemory<byte> Memory { get; }

    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref buffer, null);
        if (owned is null) return;
        CryptographicOperations.ZeroMemory(owned);
        ArrayPool<byte>.Shared.Return(owned);
    }
}

internal static class HostFrameStream
{
    internal static async ValueTask<RentedHostFrame> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[sizeof(uint)];
        try
        {
            await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new ProtocolException(ProtocolErrorCode.TruncatedPrefix);
        }
        var jsonLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (jsonLength > HostFrameCodec.MaximumJsonBytes)
            throw new ProtocolException(ProtocolErrorCode.FrameTooLarge);
        var frameLength = checked(sizeof(uint) + (int)jsonLength + HostFrameCodec.AuthenticationTagBytes);
        var buffer = ArrayPool<byte>.Shared.Rent(frameLength);
        prefix.CopyTo(buffer, 0);
        try
        {
            try
            {
                await stream.ReadExactlyAsync(buffer.AsMemory(sizeof(uint), frameLength - sizeof(uint)), cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                throw new ProtocolException(ProtocolErrorCode.FrameLengthMismatch);
            }
            return new(buffer, frameLength);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    internal static async ValueTask WriteAsync(Stream stream, ProtocolEnvelope envelope, ReadOnlyMemory<byte> key, CancellationToken cancellationToken)
    {
        var frame = HostFrameCodec.Encode(envelope, key.Span);
        try
        {
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(frame);
        }
    }
}
