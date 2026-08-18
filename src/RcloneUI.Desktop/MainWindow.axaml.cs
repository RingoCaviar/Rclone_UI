using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RcloneUI.Desktop.Presentation;

namespace RcloneUI.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly DesktopShellState shell;
    private readonly DesktopHostController? controller;
    public MainWindow() : this(null) { }
    public MainWindow(IDesktopHostClient? client)
    {
        shell = new();
        DataContext = shell;
        InitializeComponent();
        if (client is not null) controller = new(client, shell, client is BootstrappingDesktopHostClient bootstrap ? new OfficialStableWinFspInstaller(bootstrap.DataRootPath) : null);
        Opened += async (_, _) => { if (controller is not null) await controller.InitializeDesktopSessionAsync(); else shell.ApplyConnection(DesktopConnectionState.Disconnected); };
    }
    private void NavigationChanged(object? sender, SelectionChangedEventArgs args) { if (sender is ListBox { SelectedItem: ListBoxItem { Tag: string route } }) shell.Navigate(route); }
    private void ShortcutClicked(object? sender, RoutedEventArgs args) { if (sender is Button { Tag: string route }) shell.Navigate(route); }
    private void NewTaskClicked(object? sender, RoutedEventArgs args) => shell.Navigate("Transfers");
    private void LanguageClicked(object? sender, RoutedEventArgs args) => shell.ToggleLanguage();
    private void AdvancedOptionsClicked(object? sender, RoutedEventArgs args) => shell.ToggleAdvancedOptions();
    private async void AttentionClicked(object? sender, RoutedEventArgs args) { if (controller is not null) { if (shell.IsVaultLocked) await controller.UnlockAsync(); else await controller.ReconnectAsync(); } }
    private async void LockVaultClicked(object? sender, RoutedEventArgs args) { if (controller is not null) await controller.LockAsync(); }
    private async void RedetectClicked(object? sender, RoutedEventArgs args) { if (controller is not null) await controller.ReconnectAsync(); }
    private async void InstallWinFspClicked(object? sender, RoutedEventArgs args) { if (controller is not null) await controller.InstallWinFspAsync(); }
    private async void SaveMountProfileClicked(object? sender, RoutedEventArgs args) { if (controller is not null) await controller.SaveMountProfileAsync(); }
    private async void DeleteMountProfileClicked(object? sender, RoutedEventArgs args) { if (controller is not null) await controller.DeleteMountProfileAsync(); }
    private async void DeleteRemoteClicked(object? sender, RoutedEventArgs args) { if (controller is not null) await controller.DeleteRemoteAsync(); }
    private void BrowseSelectedRemoteClicked(object? sender, RoutedEventArgs args) => shell.BrowseSelectedRemote();
    private void MountSelectedRemoteClicked(object? sender, RoutedEventArgs args) => shell.PrepareSelectedRemoteForMount();
    private void DownloadSelectedRemoteClicked(object? sender, RoutedEventArgs args) => shell.PrepareSelectedRemoteForDownload();
    private void NewMountProfileClicked(object? sender, RoutedEventArgs args) => shell.BeginNewMountProfile();
    private async void JourneyPrimaryClicked(object? sender, RoutedEventArgs args) { if (controller is not null) await controller.ActivatePrimaryAsync(); }
    private async void BrowseParentClicked(object? sender, RoutedEventArgs args) { if (controller is not null) await controller.BrowseParentAsync(); }
    private async void OpenBrowserFolderClicked(object? sender, RoutedEventArgs args) { if (controller is not null) await controller.OpenBrowserFolderAsync(); }
    private async void CreateBrowserFolderClicked(object? sender, RoutedEventArgs args) { if (controller is not null) await controller.CreateBrowserFolderAsync(); }
    private async void DeleteBrowserFileClicked(object? sender, RoutedEventArgs args) { if (controller is not null) await controller.DeleteBrowserFileAsync(); }
    private async void RenameBrowserFileClicked(object? sender, RoutedEventArgs args) { if (controller is not null) await controller.RenameBrowserFileAsync(); }
    private async void CancelSelectedCopyClicked(object? sender, RoutedEventArgs args) { if (controller is not null) await controller.CancelSelectedCopyAsync(); }
    private void UseBrowserSelectionClicked(object? sender, RoutedEventArgs args) => ((DesktopShellState)DataContext!).UseBrowserSelectionForTransfer();
    private void DownloadBrowserSelectionClicked(object? sender, RoutedEventArgs args) => ((DesktopShellState)DataContext!).PrepareBrowserSelectionForDownload();
    private void UploadToBrowserFolderClicked(object? sender, RoutedEventArgs args) => ((DesktopShellState)DataContext!).PrepareBrowserFolderForUpload();
    private void CopyToBrowserFolderClicked(object? sender, RoutedEventArgs args) => ((DesktopShellState)DataContext!).PrepareBrowserFolderForRemoteCopy();
    private void MountBrowserFolderClicked(object? sender, RoutedEventArgs args) => ((DesktopShellState)DataContext!).PrepareBrowserFolderForMount();
    private async void RefreshBrowserClicked(object? sender, RoutedEventArgs args) { if (controller is not null) await controller.RefreshBrowserAsync(); }
    private async void SaveAndMountClicked(object? sender, RoutedEventArgs args) { if (controller is not null) await controller.SaveAndStartMountProfileAsync(); }
    private void OpenMountLocationClicked(object? sender, RoutedEventArgs args)
    {
        if (shell.ActiveMountLocation is not { } location) return;
        try { Process.Start(new ProcessStartInfo { FileName = location, UseShellExecute = true }); }
        catch (Exception) { shell.ApplyAction("mount-location-open-failed"); }
    }
    private async void PickDownloadFolderClicked(object? sender, RoutedEventArgs args)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Select download folder", AllowMultiple = false });
        if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } path) shell.DownloadDestinationPath = path;
    }
    private async void PickUploadFolderClicked(object? sender, RoutedEventArgs args)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Select folder to upload", AllowMultiple = false });
        if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } path) shell.UploadSourcePath = path;
    }
    private async void PickMountDirectoryClicked(object? sender, RoutedEventArgs args)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = shell.PickMountDirectoryLabel, AllowMultiple = false });
        if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } path) shell.MountFixedDirectoryPath = path;
    }
    private void ExitClicked(object? sender, RoutedEventArgs args) => Close();
    private async void QuitAllClicked(object? sender, RoutedEventArgs args) { if (controller is null || await controller.ShutdownHostAsync()) Close(); }
}
