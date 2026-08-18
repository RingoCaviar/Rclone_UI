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
        Assert.Contains("单向复制", shell.JourneyDescription, StringComparison.Ordinal);
        shell.ToggleLanguage();
        Assert.Equal("Transfer Tasks", shell.PageTitle);
        Assert.Contains("one-way copy", shell.JourneyDescription, StringComparison.OrdinalIgnoreCase);
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
    public void WritableMountVfsStatusDistinguishesBlockedUnknownAndCleanObservations()
    {
        var shell = new DesktopShellState();
        using var blocked = JsonDocument.Parse("""
            {"session":"operational","mounts":[{"instanceId":"0a0a0a0a-0000-0000-0000-000000000000","state":"ready","profileId":null,"mountPoint":"R:","startedUtc":"2026-08-12T00:00:00Z","requiresVfsDrain":true}],"mountVfs":[{"instanceId":"0a0a0a0a-0000-0000-0000-000000000000","available":true,"bytesUsed":1024,"erroredFiles":0,"uploadsInProgress":1,"uploadsQueued":2,"outOfSpace":false,"queueItems":2}]}
            """);
        shell.ApplySnapshot(new(new(new("test"), 1), DateTimeOffset.UtcNow, blocked.RootElement.Clone()));
        shell.ToggleLanguage();
        Assert.Contains("active or queued", shell.MountVfsStatus, StringComparison.Ordinal);

        using var clean = JsonDocument.Parse("""
            {"session":"operational","mounts":[{"instanceId":"0a0a0a0a-0000-0000-0000-000000000000","state":"ready","profileId":null,"mountPoint":"R:","startedUtc":"2026-08-12T00:00:00Z","requiresVfsDrain":true}],"mountVfs":[{"instanceId":"0a0a0a0a-0000-0000-0000-000000000000","available":true,"bytesUsed":1073741824,"erroredFiles":0,"uploadsInProgress":0,"uploadsQueued":0,"outOfSpace":false,"queueItems":0}]}
            """);
        shell.ApplySnapshot(new(new(new("test"), 2), DateTimeOffset.UtcNow, clean.RootElement.Clone()));
        Assert.Contains("verify again", shell.MountVfsStatus, StringComparison.Ordinal);

        using var unknown = JsonDocument.Parse("""
            {"session":"operational","mounts":[{"instanceId":"0a0a0a0a-0000-0000-0000-000000000000","state":"ready","profileId":null,"mountPoint":"R:","startedUtc":"2026-08-12T00:00:00Z","requiresVfsDrain":true}],"mountVfs":[{"instanceId":"0a0a0a0a-0000-0000-0000-000000000000","available":false}]}
            """);
        shell.ApplySnapshot(new(new(new("test"), 3), DateTimeOffset.UtcNow, unknown.RootElement.Clone()));
        Assert.Contains("unknown", shell.MountVfsStatus, StringComparison.Ordinal);
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
    public async Task UploadPrimaryActionUsesSelectedLocalFolderAndDestinationRemote()
    {
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);
        shell.Navigate("Transfers"); shell.TransferMode = DesktopTransferMode.Upload; shell.UploadSourcePath = "C:\\Uploads"; shell.CopyDestinationPath = "backup";

        await controller.ActivatePrimaryAsync(TestContext.Current.CancellationToken);

        Assert.Equal("start-copy", client.CommandType);
        Assert.Equal("C:\\Uploads", client.Arguments.GetProperty("sourceLocalPath").GetString());
        Assert.Equal(RecordingClient.DestinationId, client.Arguments.GetProperty("destinationRemoteId").GetGuid());
        Assert.Equal("backup", client.Arguments.GetProperty("destinationPath").GetString());
        Assert.Equal(JsonValueKind.Null, client.Arguments.GetProperty("sourceRemoteId").ValueKind);
    }

    [Fact]
    public async Task TransferLimitsAreBoundedBeforeSendingTheHostCommand()
    {
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);
        shell.Navigate("Transfers"); shell.CopySourcePath = "photos"; shell.DownloadDestinationPath = "C:\\Downloads"; shell.MaximumTransferMiB = "512"; shell.MaximumDurationMinutes = "30";

        await controller.ActivatePrimaryAsync(TestContext.Current.CancellationToken);

        Assert.Equal(512L * 1024 * 1024, client.Arguments.GetProperty("maximumTransferBytes").GetInt64());
        Assert.Equal(30, client.Arguments.GetProperty("maximumDurationMinutes").GetDouble());
        shell.MaximumDurationMinutes = "zero";
        await controller.ActivatePrimaryAsync(TestContext.Current.CancellationToken);
        Assert.Equal("transfer-limits-invalid", shell.LastAction);
    }

    [Fact]
    public async Task TransferPrimaryActionStaysDisabledUntilTheSelectedModeIsComplete()
    {
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);
        shell.Navigate("Transfers");

        shell.CopySourceRemote = null;
        Assert.False(shell.IsJourneyPrimaryEnabled);
        Assert.True(shell.HasTransferReadinessMessage);
        Assert.NotEmpty(shell.TransferReadinessMessage);

        shell.CopySourceRemote = shell.RemoteOptions[0];
        shell.DownloadDestinationPath = "C:\\Downloads";
        Assert.True(shell.IsJourneyPrimaryEnabled);

        shell.TransferMode = DesktopTransferMode.Upload;
        Assert.False(shell.IsJourneyPrimaryEnabled);
        Assert.NotEmpty(shell.TransferReadinessMessage);

        shell.UploadSourcePath = "C:\\Uploads";
        shell.CopyDestinationRemote = null;
        Assert.False(shell.IsJourneyPrimaryEnabled);
        shell.CopyDestinationRemote = shell.RemoteOptions[0];
        Assert.True(shell.IsJourneyPrimaryEnabled);
    }

    [Fact]
    public async Task TransferRouteSummaryShowsTheExactReadyDirection()
    {
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);
        shell.ToggleLanguage();
        shell.Navigate("Transfers");
        shell.CopySourceRemote = shell.RemoteOptions[0];
        shell.CopySourcePath = "photos/2026";
        shell.DownloadDestinationPath = "C:\\Downloads";

        Assert.True(shell.HasTransferRouteSummary);
        Assert.Equal($"Download from {shell.RemoteOptions[0].DisplayName}:/photos/2026 to C:\\Downloads", shell.TransferRouteSummary);

        shell.TransferMode = DesktopTransferMode.RemoteCopy;
        shell.CopyDestinationRemote = shell.RemoteOptions[1];
        shell.CopyDestinationPath = "archive";
        Assert.Equal($"Copy from {shell.RemoteOptions[0].DisplayName}:/photos/2026 to {shell.RemoteOptions[1].DisplayName}:/archive", shell.TransferRouteSummary);
    }

    [Fact]
    public async Task RemoteCopyCannotTargetTheIdenticalRemotePath()
    {
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);
        shell.Navigate("Transfers");
        shell.TransferMode = DesktopTransferMode.RemoteCopy;
        shell.CopySourceRemote = shell.RemoteOptions[0];
        shell.CopyDestinationRemote = shell.RemoteOptions[0];
        shell.CopySourcePath = "/photos/";
        shell.CopyDestinationPath = "photos";

        Assert.False(shell.IsTransferReady);
        Assert.False(shell.IsJourneyPrimaryEnabled);
        Assert.NotEmpty(shell.TransferReadinessMessage);

        shell.CopyDestinationPath = "archive";
        Assert.True(shell.IsTransferReady);
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
    public async Task MountProfileSaveRejectsIncompleteDesktopInputBeforeCallingTheHost()
    {
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);
        shell.Navigate("Mounts"); shell.BeginNewMountProfile();

        await controller.SaveMountProfileAsync(TestContext.Current.CancellationToken);

        Assert.Null(client.CommandType);
        Assert.Equal("mount-profile-input-invalid", shell.LastAction);
        Assert.Equal(DesktopActionNotificationKind.Error, shell.ActionNotificationKind);
        shell.ToggleLanguage();
        Assert.Contains("profile name", shell.ActionNotificationMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MountProfileValidationUpdatesAsRequiredValuesAreEntered()
    {
        var shell = new DesktopShellState();
        shell.BeginNewMountProfile();

        Assert.False(shell.CanSaveMountProfile);
        Assert.Contains("配置名称", shell.MountProfileValidationMessage, StringComparison.Ordinal);
        shell.MountProfileName = "Work";
        shell.MountRemote = new DesktopRemoteOption(Guid.NewGuid(), "FTPS");

        Assert.False(shell.CanSaveMountProfile);
        shell.ApplyConnection(DesktopConnectionState.ConnectedOperational);
        Assert.True(shell.CanSaveMountProfile);
        Assert.Empty(shell.MountProfileValidationMessage);
    }

    [Fact]
    public void SelectedRemoteDetailsPresentProviderAndHealthTruthfully()
    {
        var shell = new DesktopShellState
        {
            SelectedSavedRemote = new DesktopSavedRemoteOption(Guid.NewGuid(), 1, "Office FTPS", "ftps", "Healthy")
        };

        Assert.True(shell.HasSelectedRemoteDetails);
        shell.ToggleLanguage();
        Assert.Equal("Provider: FTPS · Status: Healthy", shell.SelectedRemoteDetails);

        shell.SelectedSavedRemote = new DesktopSavedRemoteOption(Guid.NewGuid(), 1, "Office FTPS", "ftps", "network-unavailable");
        Assert.Contains("Network unavailable", shell.SelectedRemoteDetails, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptedTransferNotificationDoesNotClaimCompletion()
    {
        var shell = new DesktopShellState();
        shell.ApplyAction("copy-accepted");

        Assert.Equal(DesktopActionNotificationKind.Success, shell.ActionNotificationKind);
        shell.ToggleLanguage();
        Assert.Contains("accepted", shell.ActionNotificationMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Activity", shell.ActionNotificationMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("completed", shell.ActionNotificationMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelectedRemoteCanOpenFileBrowserAtItsRootWithoutListing()
    {
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);
        shell.Navigate("Remotes");
        shell.SelectedSavedRemote = shell.SavedRemotes[1];
        shell.BrowserPath = "old/folder";
        shell.BrowserFilter = "old";

        shell.BrowseSelectedRemote();

        Assert.Equal("Browser", shell.CurrentRoute);
        Assert.Equal(shell.SavedRemotes[1].Id, shell.BrowserRemote!.Id);
        Assert.Empty(shell.BrowserPath);
        Assert.Empty(shell.BrowserFilter);
        Assert.Null(client.CommandType);
    }

    [Fact]
    public async Task SelectedRemoteCanPrepareAnUnsavedRootMountProfile()
    {
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);
        shell.Navigate("Remotes");
        shell.SelectedSavedRemote = shell.SavedRemotes[1];

        shell.PrepareSelectedRemoteForMount();

        Assert.Equal("Mounts", shell.CurrentRoute);
        Assert.Null(shell.SelectedMountProfile);
        Assert.Equal(shell.SavedRemotes[1].Id, shell.MountRemote!.Id);
        Assert.Empty(shell.MountSubpath);
        Assert.Equal("Destination root", shell.MountProfileName);
        Assert.Equal("standard-read-write", shell.MountCachePreset.Key);
        Assert.Null(client.CommandType);
    }

    [Fact]
    public async Task SelectedRemoteCanPrepareAnUnstartedRootDownload()
    {
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);
        shell.Navigate("Remotes");
        shell.SelectedSavedRemote = shell.SavedRemotes[1];
        shell.CopySourcePath = "old/path";
        shell.DownloadDestinationPath = "C:\\OldDownloads";

        shell.PrepareSelectedRemoteForDownload();

        Assert.Equal("Transfers", shell.CurrentRoute);
        Assert.Equal(DesktopTransferMode.Download, shell.TransferMode);
        Assert.Equal(shell.SavedRemotes[1].Id, shell.CopySourceRemote!.Id);
        Assert.Empty(shell.CopySourcePath);
        Assert.Empty(shell.DownloadDestinationPath);
        Assert.Null(client.CommandType);
    }

    [Fact]
    public async Task SelectedRemoteCanPrepareARemoteCopyWithoutPreselectingTarget()
    {
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);
        shell.Navigate("Remotes");
        shell.SelectedSavedRemote = shell.SavedRemotes[1];
        shell.CopyDestinationRemote = shell.RemoteOptions[0];
        shell.CopyDestinationPath = "old/path";

        shell.PrepareSelectedRemoteForRemoteCopy();

        Assert.Equal("Transfers", shell.CurrentRoute);
        Assert.Equal(DesktopTransferMode.RemoteCopy, shell.TransferMode);
        Assert.Equal(shell.SavedRemotes[1].Id, shell.CopySourceRemote!.Id);
        Assert.Empty(shell.CopySourcePath);
        Assert.Null(shell.CopyDestinationRemote);
        Assert.Empty(shell.CopyDestinationPath);
        Assert.False(shell.IsTransferReady);
        Assert.Null(client.CommandType);
    }

    [Fact]
    public async Task MountConfigurationSummaryShowsTheReadyTargetAndAccessMode()
    {
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);
        shell.ToggleLanguage();
        shell.BeginNewMountProfile();
        shell.MountProfileName = "FTP files";
        shell.MountRemote = shell.RemoteOptions[0];
        shell.MountSubpath = "exports/2026";
        shell.MountDriveLetter = "S";

        Assert.True(shell.HasMountConfigurationSummary);
        Assert.Equal("Mount Source:/exports/2026 at S: (read/write)", shell.MountConfigurationSummary);

        shell.MountCachePreset = shell.MountCachePresetOptions.Single(option => option.Key == "read-only");
        Assert.Equal("Mount Source:/exports/2026 at S: (read-only)", shell.MountConfigurationSummary);
    }

    [Fact]
    public async Task ValidNewMountProfileCanSaveAndStartInOneAction()
    {
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);
        shell.Navigate("Mounts"); shell.BeginNewMountProfile(); shell.MountProfileName = "Work files";

        await controller.SaveAndStartMountProfileAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["save-mount-profile", "start-mount-profile"], client.CommandTypes.TakeLast(2));
        Assert.Equal("mount-started", shell.LastAction);
    }

    [Fact]
    public async Task SaveAndMountRefusesAnAlreadySavedProfileWithoutSendingCommands()
    {
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);

        await controller.SaveAndStartMountProfileAsync(TestContext.Current.CancellationToken);

        Assert.Null(client.CommandType);
        Assert.Equal("mount-profile-already-saved", shell.LastAction);
        shell.ToggleLanguage();
        Assert.Contains("already saved", shell.ActionNotificationMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MountProfileSelectionCanReturnToTheExactSavedProfileAfterRefresh()
    {
        var targetId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            session = "operational",
            remotes = new[] { new { id = RecordingClient.SourceId, revision = 1UL, displayName = "Source" } },
            mountProfiles = new[]
            {
                new { id = new { value = RecordingClient.ProfileId }, revision = 1UL, displayName = "Older", remoteId = RecordingClient.SourceId, subpath = "old", presentationMode = 0, driveLetterSelection = 0, preferredDriveLetter = "R", fixedDirectoryPath = (string?)null, volumeName = "Cloud", cachePreset = 0 },
                new { id = new { value = targetId }, revision = 1UL, displayName = "New", remoteId = RecordingClient.SourceId, subpath = "new", presentationMode = 0, driveLetterSelection = 0, preferredDriveLetter = "S", fixedDirectoryPath = (string?)null, volumeName = "New Cloud", cachePreset = 1 }
            },
            copyRuns = Array.Empty<object>(),
            mounts = Array.Empty<object>(),
            rclone = new { status = "ready", capabilityBinding = "caps", mountAvailable = true },
            winFsp = new { status = "ready" }
        }));
        var shell = new DesktopShellState();
        shell.ApplySnapshot(new(new(new("mounts"), 1), DateTimeOffset.UtcNow, document.RootElement.Clone()));

        shell.SelectMountProfile(targetId);

        Assert.Equal(targetId, shell.SelectedMountProfile!.Id);
        Assert.Equal("New", shell.MountProfileName);
        Assert.Equal("new", shell.MountSubpath);
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

    [Fact]
    public async Task BrowserListsSelectedRemoteThroughHost()
    {
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);
        shell.Navigate("Browser"); shell.BrowserPath = "docs";

        await controller.ActivatePrimaryAsync(TestContext.Current.CancellationToken);

        Assert.Equal("browse-remote", client.CommandType);
        Assert.Equal(RecordingClient.SourceId, client.Arguments.GetProperty("remoteId").GetGuid());
        Assert.Equal("docs", client.Arguments.GetProperty("path").GetString());
        Assert.Contains(shell.BrowserItems, item => item.Path == "readme.txt");
    }

    [Fact]
    public async Task BrowserRefreshesExplicitlyAndPathChangesClearStaleResults()
    {
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);
        shell.Navigate("Browser");

        await controller.RefreshBrowserAsync(TestContext.Current.CancellationToken);
        Assert.Contains(shell.BrowserItems, item => item.Path == "readme.txt");
        Assert.True(shell.CanUploadIntoBrowserFolder);

        shell.BrowserPath = "archive";

        Assert.Empty(shell.BrowserItems);
        Assert.True(shell.CanBrowseParent);
        Assert.True(shell.CanUploadIntoBrowserFolder);
    }

    [Fact]
    public void ActivityRouteShowsRedactedCopyAndMountStates()
    {
        using var document = JsonDocument.Parse("""{"session":"operational","copyRuns":[{"state":"running","bytes":5,"totalBytes":10,"bytesPerSecond":1}],"mounts":[{"instanceId":"0a0a0a0a-0000-0000-0000-000000000000","state":"ready","mountPoint":"R:","startedUtc":"2026-08-12T00:00:00Z"}]}""");
        var shell = new DesktopShellState();
        shell.ApplySnapshot(new(new(new("activity"), 1), DateTimeOffset.UtcNow, document.RootElement.Clone()));
        shell.ToggleLanguage();

        Assert.Contains(shell.ActivityRows, row => row.Contains("Copy · running · 5/10 bytes", StringComparison.Ordinal));
        Assert.Contains(shell.ActivityRows, row => row.Contains("Mount · ready · R:", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadyDriveMountExposesAWindowsExplorerLocation()
    {
        var mountId = Guid.NewGuid();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { session = "operational", mounts = new[] { new { instanceId = mountId, state = "ready", mountPoint = "R:", startedUtc = DateTimeOffset.UtcNow } }, rclone = new { status = "ready", capabilityBinding = "caps", mountAvailable = true }, winFsp = new { status = "ready" } }));
        var shell = new DesktopShellState();

        shell.ApplySnapshot(new(new(new("mount"), 1), DateTimeOffset.UtcNow, document.RootElement.Clone()));

        Assert.True(shell.CanOpenActiveMount);
        Assert.Equal("R:\\", shell.ActiveMountLocation);
    }

    [Fact]
    public void ActivityPrimaryActionTruthfullyDescribesTheSnapshotRefresh()
    {
        var shell = new DesktopShellState();
        shell.Navigate("Activity");

        Assert.Equal("刷新活动", shell.JourneyPrimaryAction);
        shell.ToggleLanguage();
        Assert.Equal("Refresh activity", shell.JourneyPrimaryAction);
    }

    [Fact]
    public void SchedulesRouteDoesNotPretendEditingIsAvailable()
    {
        var shell = new DesktopShellState();
        shell.Navigate("Schedules");

        Assert.True(shell.IsScheduleJourney);
        Assert.False(shell.IsJourneyPrimaryAvailable);
        Assert.Contains("暂未", shell.JourneyStatus, StringComparison.Ordinal);
        shell.ToggleLanguage();
        Assert.Contains("not yet", shell.JourneyDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsTruthfullyDescribesTheUnavailableOneClickUpdatePath()
    {
        var shell = new DesktopShellState();

        Assert.Contains("尚未开放", shell.SettingsUpdateStatus, StringComparison.Ordinal);
        shell.ToggleLanguage();
        Assert.Contains("not available yet", shell.SettingsUpdateStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActivityCancelsOnlyTheSelectedRunningHostCopy()
    {
        var runId = Guid.NewGuid();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { session = "operational", copyRuns = new[] { new { runId, state = "running", bytes = 5, totalBytes = 10, bytesPerSecond = 1 } }, rclone = new { status = "ready", capabilityBinding = "caps" } }));
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        shell.ApplySnapshot(new(new(new("activity"), 1), DateTimeOffset.UtcNow, document.RootElement.Clone()));

        Assert.True(shell.CanCancelSelectedCopy);
        await controller.CancelSelectedCopyAsync(TestContext.Current.CancellationToken);

        Assert.Contains("cancel-copy", client.CommandTypes);
        Assert.Equal(runId, client.Arguments.GetProperty("runId").GetGuid());
    }

    [Fact]
    public void HomeDashboardUsesRemoteAndTransferSnapshotSummaries()
    {
        using var document = JsonDocument.Parse("""{"session":"operational","remotes":[{"id":"11111111-1111-1111-1111-111111111111","displayName":"FTPS"}],"copyRuns":[{"state":"running","bytes":5,"totalBytes":10,"bytesPerSecond":1}]}""");
        var shell = new DesktopShellState();
        shell.ApplySnapshot(new(new(new("home"), 1), DateTimeOffset.UtcNow, document.RootElement.Clone()));
        shell.ToggleLanguage();

        Assert.Contains("FTPS", shell.HomeRemoteStatus, StringComparison.Ordinal);
        Assert.Contains("running", shell.HomeTransferStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsSummarizeHostManagedComponentState()
    {
        using var document = JsonDocument.Parse("""{"session":"operational","rclone":{"status":"ready","version":"v1.75.0","capabilityBinding":"caps","mountAvailable":true},"winFsp":{"status":"ready","version":"2.1-test"}}""");
        var shell = new DesktopShellState();
        shell.ApplySnapshot(new(new(new("settings"), 1), DateTimeOffset.UtcNow, document.RootElement.Clone()));
        shell.ToggleLanguage();

        Assert.Equal("Unlocked", shell.SettingsVaultStatus);
        Assert.Equal("rclone v1.75.0", shell.SettingsRcloneStatus);
        Assert.Contains("WinFsp 2.1-test ready", shell.SettingsWinFspStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserOpensOnlySelectedDirectoriesBelowTheRemoteRoot()
    {
        var shell = new DesktopShellState { BrowserPath = "parent" };
        shell.ApplyBrowserItems([new("child", true, null), new("file.txt", false, 1)]);
        shell.SelectedBrowserItem = shell.BrowserItems.Single(item => item.Path == "child");

        shell.OpenSelectedBrowserFolder();

        Assert.Equal("parent/child", shell.BrowserPath);
        Assert.Null(shell.SelectedBrowserItem);
    }

    [Fact]
    public void BrowserSelectionOnlyPreparesTransferSource()
    {
        var remote = new DesktopRemoteOption(Guid.NewGuid(), "FTPS");
        var shell = new DesktopShellState { BrowserRemote = remote, BrowserPath = "docs" };
        shell.ApplyBrowserItems([new("manual.pdf", false, 1)]);
        shell.SelectedBrowserItem = shell.BrowserItems.Single();

        shell.UseBrowserSelectionForTransfer();

        Assert.Equal("Transfers", shell.CurrentRoute);
        Assert.Equal(remote, shell.CopySourceRemote);
        Assert.Equal("docs/manual.pdf", shell.CopySourcePath);
    }

    [Fact]
    public void BrowserSelectionPreparesDownloadAndClearsTheOldDestination()
    {
        var remote = new DesktopRemoteOption(Guid.NewGuid(), "FTPS");
        var shell = new DesktopShellState
        {
            BrowserRemote = remote,
            BrowserPath = "docs",
            DownloadDestinationPath = @"C:\\old-downloads"
        };
        shell.ApplyBrowserItems([new("manual.pdf", false, 1)]);
        shell.SelectedBrowserItem = shell.BrowserItems.Single();

        shell.PrepareBrowserSelectionForDownload();

        Assert.Equal("Transfers", shell.CurrentRoute);
        Assert.True(shell.IsDownloadMode);
        Assert.Equal(remote, shell.CopySourceRemote);
        Assert.Equal("docs/manual.pdf", shell.CopySourcePath);
        Assert.Empty(shell.DownloadDestinationPath);
        shell.ToggleLanguage();
        Assert.Equal("Start download", shell.JourneyPrimaryAction);
    }

    [Fact]
    public void BrowserFolderPreparesUploadAndClearsTheOldLocalSource()
    {
        var remote = new DesktopRemoteOption(Guid.NewGuid(), "FTPS");
        var shell = new DesktopShellState
        {
            BrowserRemote = remote,
            BrowserPath = "docs/releases/",
            UploadSourcePath = @"C:\\old-uploads"
        };

        shell.PrepareBrowserFolderForUpload();

        Assert.Equal("Transfers", shell.CurrentRoute);
        Assert.True(shell.IsUploadMode);
        Assert.Equal(remote, shell.CopyDestinationRemote);
        Assert.Equal("docs/releases", shell.CopyDestinationPath);
        Assert.Empty(shell.UploadSourcePath);
        shell.ToggleLanguage();
        Assert.Equal("Start upload", shell.JourneyPrimaryAction);
    }

    [Fact]
    public void BrowserFolderPreparesRemoteCopyDestinationWithoutChangingTheSource()
    {
        var source = new DesktopRemoteOption(Guid.NewGuid(), "Source");
        var destination = new DesktopRemoteOption(Guid.NewGuid(), "Destination");
        var shell = new DesktopShellState { BrowserRemote = destination, BrowserPath = "archive/2026/", CopySourceRemote = source, CopySourcePath = "documents" };

        shell.PrepareBrowserFolderForRemoteCopy();

        Assert.Equal("Transfers", shell.CurrentRoute);
        Assert.True(shell.IsRemoteCopyMode);
        Assert.Equal(source, shell.CopySourceRemote);
        Assert.Equal("documents", shell.CopySourcePath);
        Assert.Equal(destination, shell.CopyDestinationRemote);
        Assert.Equal("archive/2026", shell.CopyDestinationPath);
    }

    [Fact]
    public void BrowserFolderPreparesANewMountProfile()
    {
        var remote = new DesktopRemoteOption(Guid.NewGuid(), "FTPS");
        var shell = new DesktopShellState { BrowserRemote = remote, BrowserPath = "docs/releases/" };

        shell.PrepareBrowserFolderForMount();

        Assert.Equal("Mounts", shell.CurrentRoute);
        Assert.Null(shell.SelectedMountProfile);
        Assert.Equal(remote, shell.MountRemote);
        Assert.Equal("docs/releases", shell.MountSubpath);
        Assert.Equal("FTPS — docs/releases", shell.MountProfileName);
        Assert.Equal("network-drive", shell.MountPresentation.Key);
        Assert.Equal("standard-read-write", shell.MountCachePreset.Key);
    }

    [Fact]
    public void BrowserFilterStaysLocalAndClearsAHiddenSelection()
    {
        var shell = new DesktopShellState();
        shell.ApplyBrowserItems([new("photos", true, null), new("report.pdf", false, 10), new("notes.txt", false, 1)]);
        shell.SelectedBrowserItem = shell.BrowserItems.Single(item => item.Path == "report.pdf");

        shell.BrowserFilter = "note";

        Assert.Single(shell.BrowserItems);
        Assert.Equal("notes.txt", shell.BrowserItems.Single().Path);
        Assert.Null(shell.SelectedBrowserItem);
        shell.BrowserFilter = string.Empty;
        Assert.Equal(3, shell.BrowserItems.Count);
        shell.BrowserFilter = "missing";
        shell.ToggleLanguage();
        Assert.Contains("No items match", shell.BrowserEmptyText, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserSelectionDetailsUseOnlyListedMetadataAndRelocalize()
    {
        var shell = new DesktopShellState { BrowserPath = "docs" };
        shell.ApplyBrowserItems([new("report.pdf", false, 1536), new("photos", true, null)]);
        shell.SelectedBrowserItem = shell.BrowserItems.Single(item => item.Path == "report.pdf");

        Assert.Contains("docs/report.pdf", shell.BrowserSelectionDetails, StringComparison.Ordinal);
        Assert.Contains("1536 B", shell.BrowserSelectionDetails, StringComparison.Ordinal);
        shell.ToggleLanguage();
        Assert.Contains("Type: File", shell.BrowserSelectionDetails, StringComparison.Ordinal);
        shell.SelectedBrowserItem = shell.BrowserItems.Single(item => item.Path == "photos");
        Assert.Contains("Size: Unknown", shell.BrowserSelectionDetails, StringComparison.Ordinal);
    }

    [Fact]
    public void TransferActionLabelsMatchTheSubmittedMode()
    {
        var shell = new DesktopShellState();
        shell.Navigate("Transfers");
        shell.ToggleLanguage();
        Assert.Equal("Start download", shell.JourneyPrimaryAction);

        shell.TransferMode = DesktopTransferMode.RemoteCopy;
        Assert.Equal("Start copy", shell.JourneyPrimaryAction);
    }

    [Fact]
    public void TransferChoicesAreLocalizedAndUploadHidesTheRemoteSource()
    {
        var shell = new DesktopShellState();

        Assert.Equal("下载到此电脑", shell.TransferModeChoice.DisplayName);
        Assert.True(shell.IsRemoteSourceVisible);
        shell.TransferModeChoice = shell.TransferModeOptions.Single(option => option.Key == "upload");
        Assert.Equal(DesktopTransferMode.Upload, shell.TransferMode);
        Assert.False(shell.IsRemoteSourceVisible);
        shell.ToggleLanguage();
        Assert.Equal("Upload a local folder", shell.TransferModeChoice.DisplayName);
        Assert.Contains("not yet offered", shell.TransferModeHelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserActionLabelsFollowTheSelectedLanguage()
    {
        var shell = new DesktopShellState();
        Assert.Equal("打开文件夹", shell.BrowserOpenLabel);
        shell.ToggleLanguage();
        Assert.Equal("Open folder", shell.BrowserOpenLabel);
        Assert.Equal("Use as transfer source", shell.BrowserUseAsTransferSourceLabel);
    }

    [Fact]
    public async Task BrowserFolderCreationUsesTheCurrentRemoteAndPath()
    {
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);
        shell.Navigate("Browser"); shell.BrowserPath = "uploads"; shell.NewBrowserFolderName = "photos";

        await controller.CreateBrowserFolderAsync(TestContext.Current.CancellationToken);

        Assert.Equal("browse-remote", client.CommandType);
        Assert.Empty(shell.NewBrowserFolderName);
    }

    [Fact]
    public async Task BrowserFileDeletionRequiresExactPathAndNeverDeletesADirectory()
    {
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);
        shell.Navigate("Browser"); shell.BrowserPath = "docs"; shell.ApplyBrowserItems([new("report.pdf", false, 1), new("folder", true, null)]);
        shell.SelectedBrowserItem = shell.BrowserItems.Single(item => item.Path == "report.pdf");

        await controller.DeleteBrowserFileAsync(TestContext.Current.CancellationToken);
        Assert.Equal("file-delete-confirmation-required", shell.LastAction);
        shell.BrowserDeleteConfirmation = "docs/report.pdf";
        await controller.DeleteBrowserFileAsync(TestContext.Current.CancellationToken);
        Assert.Contains("delete-remote-file", client.CommandTypes);
        shell.ApplyBrowserItems([new("folder", true, null)]);
        shell.SelectedBrowserItem = shell.BrowserItems.Single(item => item.Path == "folder");
        shell.BrowserDeleteConfirmation = "docs/folder";
        Assert.False(shell.CanDeleteBrowserFile);
    }

    [Fact]
    public async Task BrowserFileRenameRequiresConfirmationAndUsesTheCurrentFolder()
    {
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);
        shell.Navigate("Browser"); shell.BrowserPath = "docs"; shell.ApplyBrowserItems([new("draft.txt", false, 1)]); shell.SelectedBrowserItem = shell.BrowserItems.Single();
        shell.BrowserRenameNewName = "final.txt";

        await controller.RenameBrowserFileAsync(TestContext.Current.CancellationToken);
        Assert.Equal("file-rename-confirmation-required", shell.LastAction);
        shell.BrowserDeleteConfirmation = "docs/draft.txt";
        await controller.RenameBrowserFileAsync(TestContext.Current.CancellationToken);

        Assert.Contains("rename-remote-file", client.CommandTypes);
        Assert.Empty(shell.BrowserRenameNewName);
    }

    [Fact]
    public async Task RemoteDeletionRequiresTypedNameAndUsesSnapshotRevision()
    {
        var client = new RecordingClient(); var shell = new DesktopShellState(); var controller = new DesktopHostController(client, shell);
        await controller.ReconnectAsync(TestContext.Current.CancellationToken);

        await controller.DeleteRemoteAsync(TestContext.Current.CancellationToken);
        Assert.Equal("remote-delete-confirmation-required", shell.LastAction);
        shell.RemoteDeleteConfirmation = shell.SelectedSavedRemote!.DisplayName;
        await controller.DeleteRemoteAsync(TestContext.Current.CancellationToken);

        Assert.Equal("delete-remote", client.CommandType);
        Assert.Equal(1UL, client.Arguments.GetProperty("expectedRevision").GetUInt64());
    }

    private sealed class RecordingClient : IDesktopHostClient
    {
        private readonly string connectionResultType;
        public RecordingClient(string connectionResultType = "remote-added") => this.connectionResultType = connectionResultType;
        internal static readonly Guid SourceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        internal static readonly Guid DestinationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        internal static readonly Guid ProfileId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        public string? CommandType { get; private set; }
        public List<string> CommandTypes { get; } = [];
        public JsonElement Arguments { get; private set; }
        public ValueTask<HostSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { session = "operational", remotes = new[] { new { id = SourceId, revision = 1UL, displayName = "Source" }, new { id = DestinationId, revision = 1UL, displayName = "Destination" } }, mountProfiles = new[] { new { id = new { value = ProfileId }, revision = 1UL, displayName = "Photos", remoteId = SourceId, subpath = "photos", presentationMode = 0, driveLetterSelection = 0, preferredDriveLetter = 'R', fixedDirectoryPath = (string?)null, volumeName = "Cloud", cachePreset = 0, autoMount = false } }, copyRuns = Array.Empty<object>(), mounts = Array.Empty<object>(), rclone = new { status = "ready", capabilityBinding = "caps", mountAvailable = true }, winFsp = new { status = "ready", version = "2.1-test" } }));
            return ValueTask.FromResult(new HostSnapshot(new(new("epoch"), 1), DateTimeOffset.UtcNow, document.RootElement.Clone()));
        }
        public ValueTask<JsonElement> SendCommandAsync(string commandType, JsonElement arguments, CancellationToken cancellationToken)
        {
            CommandType = commandType; CommandTypes.Add(commandType); Arguments = arguments.Clone();
            var resultType = commandType == "add-token-remote" ? "remote-added" : commandType == "add-connection-remote" ? connectionResultType : commandType == "delete-remote" ? "remote-deleted" : commandType == "create-remote-folder" ? "folder-created" : commandType == "delete-remote-file" ? "file-deleted" : commandType == "rename-remote-file" ? "file-renamed" : commandType == "cancel-copy" ? "copy-cancel-requested" : commandType == "browse-remote" ? "browse-completed" : commandType == "save-mount-profile" ? "mount-profile-saved" : commandType == "start-mount-profile" ? "mount-started" : "copy-not-started";
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { resultType, items = commandType == "browse-remote" ? new[] { new { path = "readme.txt", isDirectory = false, size = 42L } } : null, result = new { code = "test" } }));
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
