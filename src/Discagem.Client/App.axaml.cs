using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Discagem.Client.Services;
using Discagem.Client.ViewModels;
using Discagem.Client.Views;

namespace Discagem.Client;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = ClientSettings.Load();
            var window = new MainWindow();
            var dialogs = new DialogService(window);
            var viewModel = new MainWindowViewModel(settings, dialogs);
            window.DataContext = viewModel;
            window.RestoreSize(settings);
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) =>
            {
                window.SaveSize(settings);
                viewModel.Shutdown();
            };
            _ = viewModel.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
