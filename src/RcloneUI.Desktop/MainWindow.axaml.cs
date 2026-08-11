using Avalonia.Controls;
using Avalonia.Interactivity;
using RcloneUI.Desktop.Presentation;

namespace RcloneUI.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly DesktopShellState shell = new();
    public MainWindow() { InitializeComponent(); DataContext = shell; }
    private void NavigationChanged(object? sender, SelectionChangedEventArgs args) { if (sender is ListBox { SelectedItem: ListBoxItem { Tag: string route } }) shell.Navigate(route); }
    private void ShortcutClicked(object? sender, RoutedEventArgs args) { if (sender is Button { Tag: string route }) shell.Navigate(route); }
    private void NewTaskClicked(object? sender, RoutedEventArgs args) => shell.Navigate("Transfers");
    private void LanguageClicked(object? sender, RoutedEventArgs args) => shell.ToggleLanguage();
    private void AttentionClicked(object? sender, RoutedEventArgs args) { if (shell.ConnectionLabel.Contains("中断", StringComparison.Ordinal) || shell.ConnectionLabel.Contains("Disconnected", StringComparison.Ordinal)) shell.ApplyConnection(DesktopConnectionState.Connecting); }
    private void JourneyPrimaryClicked(object? sender, RoutedEventArgs args) { }
    private void ExitClicked(object? sender, RoutedEventArgs args) => Close();
}
