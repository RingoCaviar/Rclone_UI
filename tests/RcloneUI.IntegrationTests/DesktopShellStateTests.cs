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
        Assert.Equal(expected is DesktopConnectionState.ConnectedOperational or DesktopConnectionState.ConnectedLocked ? "已自动连接" : "已连接（恢复模式）", shell.ConnectionLabel);
        Assert.Equal(expected == DesktopConnectionState.ConnectedOperational ? "已解锁" : expected == DesktopConnectionState.ConnectedLocked ? "已锁定" : "只读恢复", shell.VaultStatusLabel);
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
    public void LanguageSwitchUpdatesNavigationHomeAndCommonActions()
    {
        var shell = new DesktopShellState();
        Assert.Equal("⌂  主页", shell.NavHome);
        Assert.Equal("退出界面", shell.ExitLabel);
        Assert.Equal("高级选项", shell.AdvancedOptionsLabel);
        Assert.Equal("一切尽在掌握", shell.HomeHeading);

        shell.ToggleLanguage();

        Assert.Equal("⌂  Home", shell.NavHome);
        Assert.Equal("☁  Remotes", shell.NavRemotes);
        Assert.Equal("Exit Desktop", shell.ExitLabel);
        Assert.Equal("Advanced options", shell.AdvancedOptionsLabel);
        Assert.Equal("Everything under control", shell.HomeHeading);
    }

    [Fact]
    public void CommandFailuresProduceAVisibleLocalizedNotification()
    {
        var shell = new DesktopShellState();

        shell.ApplyAction("remote-input-invalid");

        Assert.True(shell.HasActionNotification);
        Assert.Equal(DesktopActionNotificationKind.Error, shell.ActionNotificationKind);
        Assert.Contains("服务器", shell.ActionNotificationMessage, StringComparison.Ordinal);

        shell.ToggleLanguage();

        Assert.Contains("server", shell.ActionNotificationMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WinFspInstallImmediatelyReportsProgressAndThenCompletion()
    {
        var shell = new DesktopShellState();
        var controller = new DesktopHostController(new RecordingClient(), shell, new DelayedWinFspInstaller());

        var install = controller.InstallWinFspAsync(TestContext.Current.CancellationToken).AsTask();

        Assert.Equal("winfsp-install-started", shell.LastAction);
        await install;
        Assert.Equal("winfsp-install-complete", shell.LastAction);
    }

    [Fact]
    public void ConnectionSetupShowsOnlyProtocolRelevantRequiredFields()
    {
        var shell = new DesktopShellState();

        Assert.True(shell.IsConnectionRemoteSetup);
        Assert.False(shell.IsTokenRemoteSetup);
        Assert.False(shell.IsSftpConnection);
        Assert.False(shell.IsFtpsConnection);
        Assert.Contains("名称", shell.RemoteSetupRequiredFields, StringComparison.Ordinal);
        Assert.DoesNotContain("主机密钥", shell.RemoteSetupRequiredFields, StringComparison.Ordinal);

        shell.ConnectionProtocol = shell.ConnectionProtocols.Single(protocol => protocol.Key == "sftp");

        Assert.True(shell.IsSftpConnection);
        Assert.True(shell.IsSftpHostKeyVisible);
        Assert.Contains("主机密钥", shell.RemoteSetupRequiredFields, StringComparison.Ordinal);
    }

    [Fact]
    public void AdvancedOptionsAreShownOnlyForImplementedJourneysAndResetOnNavigation()
    {
        var shell = new DesktopShellState();
        Assert.False(shell.IsAdvancedOptionsAvailable);

        shell.Navigate("Remotes");
        Assert.True(shell.IsAdvancedOptionsAvailable);
        Assert.False(shell.IsRemoteAdvancedVisible);
        shell.ToggleAdvancedOptions();
        Assert.True(shell.IsRemoteAdvancedVisible);
        Assert.Equal("收起高级选项", shell.AdvancedOptionsLabel);

        shell.Navigate("Mounts");
        Assert.False(shell.IsAdvancedOptionsAvailable);
        Assert.False(shell.IsAdvancedOptionsExpanded);

        shell.Navigate("Transfers");
        shell.ToggleAdvancedOptions();
        Assert.True(shell.IsTransferAdvancedVisible);
        shell.ToggleLanguage();
        Assert.Equal("Hide advanced options", shell.AdvancedOptionsLabel);
    }

    [Fact]
    public void MountPresentationSelectionShowsOnlyRelevantInputsAndRelocalizes()
    {
        var shell = new DesktopShellState();
        shell.Navigate("Mounts");
        Assert.Equal("network-drive", shell.MountPresentation.Key);
        Assert.True(shell.IsMountDrivePresentation);
        Assert.True(shell.IsPreferredDriveLetter);

        shell.MountDriveSelection = shell.MountDriveSelectionOptions.Single(option => option.Key == "automatic");
        Assert.False(shell.IsPreferredDriveLetter);
        shell.MountPresentation = shell.MountPresentationOptions.Single(option => option.Key == "fixed-directory");
        Assert.True(shell.IsFixedDirectoryMount);
        Assert.False(shell.IsMountDrivePresentation);

        shell.ToggleLanguage();
        Assert.Equal("fixed-directory", shell.MountPresentation.Key);
        Assert.Equal("Fixed directory", shell.MountPresentation.DisplayName);
    }

    [Fact]
    public void MountWritePresetsAreVisibleAndExplainTheCacheBoundary()
    {
        var shell = new DesktopShellState();

        Assert.Contains("可用", shell.MountStandardPresetLabel, StringComparison.Ordinal);
        shell.MountCachePreset = shell.MountCachePresetOptions.Single(option => option.Key == "standard-read-write");
        Assert.Contains("data/cache/rclone", shell.MountReadOnlyNotice, StringComparison.Ordinal);

        shell.ToggleLanguage();

        Assert.Equal("Read-only browsing (available)", shell.MountReadOnlyPresetLabel);
        Assert.Contains("not yet available", shell.MountMaximumPresetLabel, StringComparison.Ordinal);
        Assert.Equal("Standard read/write (available)", shell.MountStandardPresetLabel);
        Assert.Contains("soft cache target", shell.MountWritePresetExplanation, StringComparison.Ordinal);
    }

    [Fact]
    public void OfficialStableWinFspInstallerPinsOfficialStableRelease()
    {
        Assert.Equal("2.1.25156", OfficialStableWinFspInstaller.Version);
        Assert.Equal("073A70E00F77423E34BED98B86E600DEF93393BA5822204FAC57A29324DB9F7A", OfficialStableWinFspInstaller.Sha256);
        Assert.Equal("https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi", OfficialStableWinFspInstaller.DownloadUri.AbsoluteUri);
    }

    [Fact]
    public void AuthenticodeVerifierRejectsAnUnsignedMsiFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rcloneui-{Guid.NewGuid():N}.msi");
        try
        {
            File.WriteAllBytes(path, [0x4D, 0x53, 0x49]);
            Assert.False(WindowsAuthenticodeVerifier.IsTrusted(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task TransferPrimaryActionUsesSnapshotCapabilityAndTypedFields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(cancellationToken);
        shell.Navigate("Transfers"); shell.TransferMode = DesktopTransferMode.RemoteCopy; shell.CopySourcePath = "from"; shell.CopyDestinationPath = "to";
        await controller.ActivatePrimaryAsync(cancellationToken);
        Assert.Equal("start-copy", client.CommandType);
        Assert.Equal(RecordingClient.SourceId, client.Arguments.GetProperty("sourceRemoteId").GetGuid());
        Assert.Equal(RecordingClient.DestinationId, client.Arguments.GetProperty("destinationRemoteId").GetGuid());
        Assert.Equal("caps", client.Arguments.GetProperty("capabilityBinding").GetString());
    }

    [Fact]
    public async Task DownloadPrimaryActionUsesSelectedLocalFolderWithoutDestinationRemote()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(cancellationToken);
        shell.Navigate("Transfers"); shell.CopySourcePath = "photos"; shell.DownloadDestinationPath = "C:\\Downloads";

        await controller.ActivatePrimaryAsync(cancellationToken);

        Assert.Equal("start-copy", client.CommandType);
        Assert.Equal("C:\\Downloads", client.Arguments.GetProperty("destinationLocalPath").GetString());
        Assert.Equal(JsonValueKind.Null, client.Arguments.GetProperty("destinationRemoteId").ValueKind);
    }

    [Fact]
    public async Task MountPrimaryActionSendsSelectedReadOnlyProfileToHost()
    {
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);
        shell.Navigate("Mounts"); shell.MountSubpath = "photos"; shell.MountDriveLetter = "R"; shell.MountVolumeName = "My Cloud";

        await controller.ActivatePrimaryAsync(TestContext.Current.CancellationToken);

        Assert.Equal("start-mount-profile", client.CommandType);
        Assert.Equal(RecordingClient.ProfileId, client.Arguments.GetProperty("profileId").GetGuid());
    }

    [Fact]
    public async Task RemotePrimaryActionSubmitsBoundedTokenSetupAndClearsSecretInput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(cancellationToken);
        shell.Navigate("Remotes"); shell.RemoteSetupKind = shell.RemoteSetupKinds.Single(kind => kind.Key == "token"); shell.RemoteDisplayName = "Personal"; shell.RemoteProviderType = "drive"; shell.RemoteToken = "secret";
        await controller.ActivatePrimaryAsync(cancellationToken);
        Assert.Equal("add-token-remote", client.CommandType);
        Assert.Equal("Personal", client.Arguments.GetProperty("displayName").GetString());
        Assert.Equal("secret", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(client.Arguments.GetProperty("tokenUtf8").GetString()!)));
        Assert.Empty(shell.RemoteToken);
    }

    [Fact]
    public async Task ConnectionRemoteSubmitsFtpsParametersAndClearsPassword()
    {
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);
        shell.Navigate("Remotes"); shell.RemoteDisplayName = "FTPS"; shell.ConnectionHost = "files.example.test"; shell.ConnectionPort = "21"; shell.ConnectionUser = "alice"; shell.ConnectionPassword = "secret"; shell.ConnectionProtocol = shell.ConnectionProtocols.Single(value => value.Key == "ftps-explicit");

        await controller.ActivatePrimaryAsync(TestContext.Current.CancellationToken);

        Assert.Equal("add-connection-remote", client.CommandType);
        Assert.Equal("ftp", client.Arguments.GetProperty("providerType").GetString());
        Assert.Equal("true", client.Arguments.GetProperty("configuration").GetProperty("explicit_tls").GetString());
        Assert.Empty(shell.ConnectionPassword);
    }

    [Fact]
    public async Task ConnectionFailureKeepsPasswordForRetry()
    {
        var client = new RecordingClient(connectionResultType: "remote-test-failed"); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);
        shell.Navigate("Remotes"); shell.RemoteDisplayName = "FTPS"; shell.ConnectionHost = "files.example.test"; shell.ConnectionPort = "21"; shell.ConnectionUser = "alice"; shell.ConnectionPassword = "secret";

        await controller.ActivatePrimaryAsync(TestContext.Current.CancellationToken);

        Assert.Equal("remote-test-failed", shell.LastAction);
        Assert.Equal("secret", shell.ConnectionPassword);
    }

    [Fact]
    public async Task IncompleteConnectionRemoteShowsInputNotificationWithoutSendingOAuthCommand()
    {
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);
        shell.Navigate("Remotes"); shell.RemoteDisplayName = "FTPS"; shell.ConnectionUser = "alice"; shell.ConnectionPassword = "secret";

        await controller.ActivatePrimaryAsync(TestContext.Current.CancellationToken);

        Assert.Null(client.CommandType);
        Assert.Equal("remote-input-invalid", shell.LastAction);
        Assert.Equal(DesktopActionNotificationKind.Error, shell.ActionNotificationKind);
        shell.ToggleLanguage();
        Assert.Contains("server", shell.ActionNotificationMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NewDesktopSessionLocksVaultBeforePresentingSnapshot()
    {
        var client = new RecordingClient();
        var shell = new DesktopShellState();
        var controller = new DesktopHostController(client, shell);

        await controller.InitializeDesktopSessionAsync(TestContext.Current.CancellationToken);

        Assert.Equal("lock-vault", client.CommandType);
    }

    private sealed class RecordingClient : IDesktopHostClient
    {
        private readonly string connectionResultType;
        public RecordingClient(string connectionResultType = "remote-added") => this.connectionResultType = connectionResultType;
        internal static readonly Guid SourceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        internal static readonly Guid DestinationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        internal static readonly Guid ProfileId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        public string? CommandType { get; private set; }
        public JsonElement Arguments { get; private set; }
        public ValueTask<HostSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { session = "operational", remotes = new[] { new { id = SourceId, displayName = "Source" }, new { id = DestinationId, displayName = "Destination" } }, mountProfiles = new[] { new { id = new { value = ProfileId }, revision = 1UL, displayName = "Photos", remoteId = SourceId, subpath = "photos", presentationMode = 0, driveLetterSelection = 0, preferredDriveLetter = 'R', fixedDirectoryPath = (string?)null, volumeName = "Cloud", cachePreset = 0, autoMount = false } }, copyRuns = Array.Empty<object>(), mounts = Array.Empty<object>(), rclone = new { status = "ready", capabilityBinding = "caps", mountAvailable = true }, winFsp = new { status = "ready", version = "2.1-test" } }));
            return ValueTask.FromResult(new HostSnapshot(new(new("epoch"), 1), DateTimeOffset.UtcNow, document.RootElement.Clone()));
        }
        public ValueTask<JsonElement> SendCommandAsync(string commandType, JsonElement arguments, CancellationToken cancellationToken)
        {
            CommandType = commandType; Arguments = arguments.Clone();
            var resultType = commandType == "add-token-remote" ? "remote-added" : commandType == "add-connection-remote" ? connectionResultType : "copy-not-started";
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { resultType, result = new { code = "test" } }));
            return ValueTask.FromResult(document.RootElement.Clone());
        }
    }

    private sealed class DelayedWinFspInstaller : IWinFspInstaller
    {
        public async ValueTask<WinFspInstallResult> InstallAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(10, cancellationToken);
            return new("winfsp-install-complete");
        }
    }
}
