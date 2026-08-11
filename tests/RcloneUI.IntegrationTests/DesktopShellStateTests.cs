using System.Text.Json;
using RcloneUI.Contracts.HostProtocol.V1;
using RcloneUI.Desktop.Presentation;

namespace RcloneUI.IntegrationTests;

public sealed class DesktopShellStateTests
{
    [Theory]
    [InlineData("operational", DesktopConnectionState.ConnectedOperational, false)]
    [InlineData("locked", DesktopConnectionState.ConnectedLocked, true)]
    [InlineData("read-only-recovery", DesktopConnectionState.ReadOnlyRecovery, true)]
    [InlineData("unexpected", DesktopConnectionState.ReadOnlyRecovery, true)]
    public void HostSnapshotProducesTruthfulSessionPresentation(string session, DesktopConnectionState expected, bool needsAttention)
    {
        using var document = JsonDocument.Parse($$"""{"session":"{{session}}"}""");
        var shell = new DesktopShellState();
        shell.ApplySnapshot(new(new(new(Guid.NewGuid().ToString("N")), 1), DateTimeOffset.UtcNow, document.RootElement.Clone()));
        Assert.Equal(needsAttention, shell.NeedsAttention);
        Assert.Equal(expected == DesktopConnectionState.ConnectedOperational ? "运行正常" : expected == DesktopConnectionState.ConnectedLocked ? "已锁定" : "只读恢复", shell.ConnectionLabel);
    }

    [Fact]
    public void NavigationAndLanguageKeepJourneysActionable()
    {
        var shell = new DesktopShellState();
        shell.Navigate("Transfers");
        Assert.True(shell.IsJourney);
        Assert.Contains("预览", shell.JourneyDescription, StringComparison.Ordinal);
        shell.ToggleLanguage();
        Assert.Equal("Transfer Tasks", shell.PageTitle);
        Assert.Contains("preview", shell.JourneyDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TransferPrimaryActionUsesSnapshotCapabilityAndTypedFields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(cancellationToken);
        shell.Navigate("Transfers"); shell.CopySourcePath = "from"; shell.CopyDestinationPath = "to";
        await controller.ActivatePrimaryAsync(cancellationToken);
        Assert.Equal("start-copy", client.CommandType);
        Assert.Equal(RecordingClient.SourceId, client.Arguments.GetProperty("sourceRemoteId").GetGuid());
        Assert.Equal(RecordingClient.DestinationId, client.Arguments.GetProperty("destinationRemoteId").GetGuid());
        Assert.Equal("caps", client.Arguments.GetProperty("capabilityBinding").GetString());
    }

    private sealed class RecordingClient : IDesktopHostClient
    {
        internal static readonly Guid SourceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        internal static readonly Guid DestinationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        public string? CommandType { get; private set; }
        public JsonElement Arguments { get; private set; }
        public ValueTask<HostSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { session = "operational", remotes = new[] { new { id = SourceId, displayName = "Source" }, new { id = DestinationId, displayName = "Destination" } }, copyRuns = Array.Empty<object>(), rclone = new { status = "ready", capabilityBinding = "caps" } }));
            return ValueTask.FromResult(new HostSnapshot(new(new("epoch"), 1), DateTimeOffset.UtcNow, document.RootElement.Clone()));
        }
        public ValueTask<JsonElement> SendCommandAsync(string commandType, JsonElement arguments, CancellationToken cancellationToken)
        {
            CommandType = commandType; Arguments = arguments.Clone();
            using var document = JsonDocument.Parse("""{"resultType":"copy-not-started","result":{"code":"test"}}""");
            return ValueTask.FromResult(document.RootElement.Clone());
        }
    }
}
