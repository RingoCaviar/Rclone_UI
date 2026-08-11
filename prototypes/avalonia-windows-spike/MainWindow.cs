using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace AvaloniaWindowsSpike;

public sealed class MainWindow : Window
{
    private readonly TextBlock _title = new();
    private readonly TextBlock _description = new();
    private readonly TextBlock _state = new();
    private readonly Button _language = new();
    private bool _chinese = true;
    public bool AllowClose { get; private set; }
    public event EventHandler? RequestExit;

    public MainWindow()
    {
        Title = "Rclone UI — Avalonia Windows integration spike";
        Width = 900;
        Height = 650;
        MinWidth = 720;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _title.FontSize = 28;
        _title.FontWeight = FontWeight.SemiBold;
        _description.TextWrapping = TextWrapping.Wrap;
        _state.TextWrapping = TextWrapping.Wrap;
        _state.FontFamily = FontFamily.Parse("Consolas");
        AutomationProperties.SetName(_state, "Integration probe state");
        AutomationProperties.SetLiveSetting(_state, AutomationLiveSetting.Polite);

        var theme = Button("切换明暗主题 / Toggle theme", ToggleTheme);
        _language = Button("English", ToggleLanguage);
        var uac = Button("测试 UAC / Probe UAC", ProbeUac);
        var process = Button("测试子进程树 / Probe process tree", ProbeProcessTree);
        var handoff = Button("模拟更新交接 / Simulate update handoff", SimulateHandoff);
        var hide = Button("隐藏到托盘 / Hide to tray", () => Hide());
        var exit = Button("真正退出 / Exit", () => { AllowClose = true; RequestExit?.Invoke(this, EventArgs.Empty); });

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(theme);
        buttons.Children.Add(_language);
        buttons.Children.Add(uac);
        buttons.Children.Add(process);
        buttons.Children.Add(handoff);
        buttons.Children.Add(hide);
        buttons.Children.Add(exit);

        var card = new Border
        {
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            Child = _state
        };
        var stack = new StackPanel { Spacing = 18 };
        stack.Children.Add(_title);
        stack.Children.Add(_description);
        stack.Children.Add(buttons);
        stack.Children.Add(card);
        Content = new ScrollViewer { Padding = new Thickness(28), Content = stack };

        Opened += (_, _) => ReportEnvironment();
        ApplyLanguage();
    }

    public void Report(string message) => _state.Text = $"{DateTime.Now:HH:mm:ss}  {message}\n\n{EnvironmentSummary()}";

    private static Button Button(string label, Action action)
    {
        var button = new Button { Content = label, MinHeight = 40, Margin = new Thickness(0, 0, 10, 10) };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }

    private void ApplyLanguage()
    {
        _title.Text = _chinese ? "Avalonia Windows 集成验证" : "Avalonia Windows integration spike";
        _description.Text = _chinese
            ? "可抛弃原型：验证 Windows 10/11 便携发布、托盘生命周期、UAC、子进程树、中英切换、DPI、无障碍和更新交接。关闭窗口只会隐藏到托盘。"
            : "Throwaway spike for portable publishing, tray lifetime, UAC, child process trees, localization, DPI, accessibility, and updater handoff. Closing the window hides it to the tray.";
        _language.Content = _chinese ? "English" : "简体中文";
    }

    private void ToggleLanguage()
    {
        _chinese = !_chinese;
        ApplyLanguage();
        Report(_chinese ? "语言切换为简体中文。" : "Language switched to English.");
    }

    private void ToggleTheme()
    {
        var app = Application.Current!;
        app.RequestedThemeVariant = app.ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
        Report($"Theme requested: {app.RequestedThemeVariant}");
    }

    private void ProbeUac()
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = true, Verb = "runas" });
            Report("UAC consent process launched. The main GUI remains unelevated.");
        }
        catch (Exception ex)
        {
            Report($"UAC was rejected or unavailable: {ex.Message}");
        }
    }

    private void ProbeProcessTree()
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 30 > nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        child?.Kill(entireProcessTree: true);
        child?.WaitForExit(3000);
        Report($"Owned child process tree terminated; exit code: {child?.ExitCode}.");
    }

    private void SimulateHandoff() =>
        Report("Updater handoff contract reached: staged files verified; GUI would exit and an external helper would replace the inactive version directory.");

    private void ReportEnvironment() => Report("Spike started successfully.");

    private string EnvironmentSummary() =>
        $"OS: {Environment.OSVersion}\n" +
        $"Runtime: {Environment.Version} / {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\n" +
        $"Render scaling: {RenderScaling:0.##}\n" +
        $"Theme: {Application.Current?.ActualThemeVariant}\n" +
        $"Culture: {System.Globalization.CultureInfo.CurrentUICulture.Name}\n" +
        "Accessibility: named controls, keyboard focus, and polite live status are enabled.";
}
