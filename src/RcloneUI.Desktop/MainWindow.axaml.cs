using Avalonia.Controls;
using Avalonia.Interactivity;
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
        Opened += async (_, _) => { if (controller is not null) await controller.ReconnectAsync(); else shell.ApplyConnection(DesktopConnectionState.Disconnected); };
    }
    private void NavigationChanged(object? sender, SelectionChangedEventArgs args) { if (sender is ListBox { SelectedItem: ListBoxItem { Tag: string route } }) shell.Navigate(route); }
    private void ShortcutClicked(object? sender, RoutedEventArgs args) { if (sender is Button { Tag: string route }) shell.Navigate(route); }
    private void NewTaskClicked(object? sender, RoutedEventArgs args) => shell.Navigate("Transfers");
    private void LanguageClicked(object? sender, RoutedEventArgs args) => shell.ToggleLanguage();
    private async void AttentionClicked(object? sender, RoutedEventArgs args) { if (controller is not null) { if (shell.IsVaultLocked) await controller.UnlockAsync(); else await controller.ReconnectAsync(); } }
    private async void JourneyPrimaryClicked(object? sender, RoutedEventArgs args) { if (controller is not null) await controller.ActivatePrimaryAsync(); }
    private void ExitClicked(object? sender, RoutedEventArgs args) => Close();
}
