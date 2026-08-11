using RcloneUI.Contracts.HostProtocol.V1;

namespace RcloneUI.Contracts.Tests;

public sealed class HandshakeContractTests
{
    [Fact]
    public void ConnectionKeyMatchesGoldenVector()
    {
        var challengeKey = Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        var clientNonce = Convert.FromHexString("202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F");
        var hostNonce = Convert.FromHexString("404142434445464748494A4B4C4D4E4F505152535455565758595A5B5C5D5E5F");

        var key = ConnectionKeyDerivation.Derive(challengeKey, clientNonce, hostNonce);

        Assert.Equal("5BBFFD1406DB5E0638C67F0814661D451251DCF90293609095B04FF0BA50F83C", Convert.ToHexString(key));
    }

    [Fact]
    public void EachDirectionHasAnIndependentContiguousSequence()
    {
        var sequences = new DuplexSequenceTracker();

        Assert.True(sequences.AcceptInbound(1));
        Assert.True(sequences.AcceptOutbound(1));
        Assert.False(sequences.AcceptInbound(3));
        Assert.False(sequences.AcceptOutbound(1));
    }

    [Fact]
    public void ExpiredRequestIsRejectedBeforeAdmission()
    {
        var request = new RequestMetadata(
            new IdempotencyKey("idempotency-1"),
            new CancellationId("cancellation-1"),
            new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));

        Assert.True(request.IsExpired(new DateTimeOffset(2030, 1, 2, 3, 4, 6, TimeSpan.Zero)));
    }

    [Fact]
    public void StableIdentifierRejectsControlCharacters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RequestId("unsafe\ridentifier"));
    }
}
