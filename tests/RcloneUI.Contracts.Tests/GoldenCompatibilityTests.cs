using RcloneUI.Contracts.HostProtocol.V1;

namespace RcloneUI.Contracts.Tests;

public sealed class GoldenCompatibilityTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();

    [Theory]
    [InlineData("host-ipc-v1-current.hex", "golden-current", MessageType.Command)]
    [InlineData("host-ipc-v1-previous.hex", "golden-previous", MessageType.Snapshot)]
    public void CurrentDecoderAcceptsSupportedGoldenFrames(string fixture, string requestId, MessageType messageType)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        var frame = Convert.FromHexString(File.ReadAllText(fixturePath).Trim());

        var decoded = HostFrameCodec.Decode(frame, Key, 1);

        Assert.Equal(requestId, decoded.Envelope.RequestId.Value);
        Assert.Equal(messageType, decoded.Envelope.MessageType);
    }
}
