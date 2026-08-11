using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace RcloneUI.Desktop;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dataRoot = desktop.Args?.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "data");
            desktop.MainWindow = new MainWindow(new Presentation.NamedPipeDesktopHostClient(dataRoot));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
