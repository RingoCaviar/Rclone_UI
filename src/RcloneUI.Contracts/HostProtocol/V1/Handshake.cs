using System.Security.Cryptography;
using System.Text;

namespace RcloneUI.Contracts.HostProtocol.V1;

public sealed record Hello(
    string DataRootId,
    string Incarnation,
    string ClientBuild,
    ProtocolOffer Offer,
    byte[] ClientNonce,
    byte[] ChallengeProof);

public sealed record HelloAcknowledgement(
    string HostBuild,
    int NegotiatedMinor,
    IReadOnlyList<string> Capabilities,
    StateEpoch StateEpoch,
    byte[] HostNonce,
    byte[] ChallengeProof);

public static class ConnectionKeyDerivation
{
    public const int NonceBytes = 32;
    public const int ConnectionKeyBytes = 32;

    private static readonly byte[] Context = Encoding.ASCII.GetBytes("host-ipc/v1 connection key");

    public static byte[] Derive(
        ReadOnlySpan<byte> challengeKey,
        ReadOnlySpan<byte> clientNonce,
        ReadOnlySpan<byte> hostNonce)
    {
        if (challengeKey.Length < 32)
        {
            throw new ArgumentException("The challenge key must contain at least 256 bits.", nameof(challengeKey));
        }

        if (clientNonce.Length != NonceBytes || hostNonce.Length != NonceBytes)
        {
            throw new ArgumentException($"Handshake nonces must contain exactly {NonceBytes} bytes.");
        }

        Span<byte> salt = stackalloc byte[NonceBytes * 2];
        clientNonce.CopyTo(salt);
        hostNonce.CopyTo(salt[NonceBytes..]);
        Span<byte> pseudorandomKey = stackalloc byte[32];
        HMACSHA256.HashData(salt, challengeKey, pseudorandomKey);

        var expandInput = new byte[Context.Length + 1];
        Context.CopyTo(expandInput, 0);
        expandInput[^1] = 1;
        var connectionKey = new byte[ConnectionKeyBytes];
        HMACSHA256.HashData(pseudorandomKey, expandInput, connectionKey);
        CryptographicOperations.ZeroMemory(pseudorandomKey);
        CryptographicOperations.ZeroMemory(salt);
        return connectionKey;
    }
}

public sealed class DuplexSequenceTracker
{
    private ulong nextInbound = 1;
    private ulong nextOutbound = 1;

    public bool AcceptInbound(ulong sequence) => Accept(sequence, ref nextInbound);

    public bool AcceptOutbound(ulong sequence) => Accept(sequence, ref nextOutbound);

    private static bool Accept(ulong sequence, ref ulong next)
    {
        if (next == 0 || sequence != next)
        {
            return false;
        }

        next = next == ulong.MaxValue ? 0 : next + 1;
        return true;
    }
}
