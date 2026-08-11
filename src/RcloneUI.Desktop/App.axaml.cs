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
            var selectedDataRoot = desktop.Args?.FirstOrDefault();
            var location = Presentation.PortableHostBootstrap.Resolve(AppContext.BaseDirectory, selectedDataRoot);
            desktop.MainWindow = new MainWindow(new Presentation.BootstrappingDesktopHostClient(location));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
