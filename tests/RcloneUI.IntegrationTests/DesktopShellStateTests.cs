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
}
