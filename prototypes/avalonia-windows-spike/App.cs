using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using System.Runtime.InteropServices;

namespace AvaloniaWindowsSpike;

public sealed class App : Application
{
    private MainWindow? _window;
    private TrayIcon? _trayIcon;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _window = new MainWindow();
            _window.RequestExit += (_, _) => desktop.Shutdown();
            _window.Closing += (_, args) =>
            {
                if (!_window.AllowClose)
                {
                    args.Cancel = true;
                    _window.Hide();
                    _window.Report("Window hidden; application and tray lifetime continue.");
                }
            };
            desktop.MainWindow = _window;

            var show = new NativeMenuItem("Show Rclone UI spike");
            show.Click += (_, _) => ShowWindow();
            var exit = new NativeMenuItem("Exit");
            exit.Click += (_, _) => desktop.Shutdown();
            _trayIcon = new TrayIcon
            {
                Icon = CreateIcon(),
                ToolTipText = "Rclone UI — Avalonia spike",
                Menu = new NativeMenu { Items = { show, new NativeMenuItemSeparator(), exit } },
                IsVisible = true
            };
            _trayIcon.Clicked += (_, _) => ShowWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ShowWindow()
    {
        _window?.Show();
        _window?.Activate();
        _window?.Report("Window restored from the application-lifetime tray icon.");
    }

    private static WindowIcon CreateIcon()
    {
        const int size = 16;
        var bitmap = new WriteableBitmap(
            new PixelSize(size, size),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0xD4;
            pixels[i + 1] = 0x78;
            pixels[i + 2] = 0x2F;
            pixels[i + 3] = 0xFF;
        }
        using var framebuffer = bitmap.Lock();
        Marshal.Copy(pixels, 0, framebuffer.Address, pixels.Length);
        return new WindowIcon(bitmap);
    }
}
