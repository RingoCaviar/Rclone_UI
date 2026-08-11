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
        if (client is not null) controller = new(client, shell);
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
    private async void SaveMountProfileClicked(object? sender, RoutedEventArgs args) { if (controller is not null) await controller.SaveMountProfileAsync(); }
    private async void DeleteMountProfileClicked(object? sender, RoutedEventArgs args) { if (controller is not null) await controller.DeleteMountProfileAsync(); }
    private void NewMountProfileClicked(object? sender, RoutedEventArgs args) => shell.BeginNewMountProfile();
    private async void JourneyPrimaryClicked(object? sender, RoutedEventArgs args) { if (controller is not null) await controller.ActivatePrimaryAsync(); }
    private async void PickDownloadFolderClicked(object? sender, RoutedEventArgs args)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Select download folder", AllowMultiple = false });
        if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } path) shell.DownloadDestinationPath = path;
    }
    private async void PickMountDirectoryClicked(object? sender, RoutedEventArgs args)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = shell.PickMountDirectoryLabel, AllowMultiple = false });
        if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } path) shell.MountFixedDirectoryPath = path;
    }
    private void ExitClicked(object? sender, RoutedEventArgs args) => Close();
}
