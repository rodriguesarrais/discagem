using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Discagem.Client.Services;
using Discagem.Client.ViewModels;
using Discagem.Client.Views;

namespace Discagem.Client;

public sealed class App : Application
{
    private bool _quitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = ClientSettings.Load();
            ThemeManager.Apply(settings.ThemeValue);
            var window = new MainWindow();
            var dialogs = new DialogService(window);
            var viewModel = new MainWindowViewModel(settings, dialogs);
            window.DataContext = viewModel;
            window.RestoreSize(settings);
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) =>
            {
                _quitting = true;
                window.SaveSize(settings);
                viewModel.Shutdown();
            };

            // Closing the window only hides it while Discagem lives on in the notification area.
            window.Closing += (_, e) =>
            {
                if (!_quitting && settings.CloseToTrayEnabled && !e.IsProgrammatic && e.CloseReason == WindowCloseReason.WindowClosing)
                {
                    e.Cancel = true;
                    window.SaveSize(settings);
                    window.Hide();
                }
            };

            CreateTrayIcon(desktop, window);
            if (Program.Instance is { } instance)
            {
                instance.ActivationRequested += () => Dispatcher.UIThread.Post(() => ShowWindow(window));
                instance.Listen();
            }

            _ = viewModel.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, Window window)
    {
        // On Linux the icon is registered with the desktop over D-Bus, and Avalonia reports a refusal
        // (a desktop or sandbox that does not allow it) as an unhandled exception: run without the icon.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            if (e.Exception.StackTrace?.Contains("DBusTrayIconImpl", StringComparison.Ordinal) == true)
            {
                ClientLog.Error($"The notification area icon is not available: {e.Exception.Message}");
                e.Handled = true;
            }
        };

        var open = new NativeMenuItem("Open Discagem");
        open.Click += (_, _) => ShowWindow(window);
        var quit = new NativeMenuItem("Quit Discagem");
        quit.Click += (_, _) => Quit();

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Discagem/Assets/discagem.ico"))),
            ToolTipText = "Discagem",
            Menu = [open, new NativeMenuItemSeparator(), quit],
        };
        tray.Clicked += (_, _) => ShowWindow(window);
        TrayIcon.SetIcons(this, [tray]);
    }

    /// <summary>Quits for real, also when closing the window would only hide it in the notification area.</summary>
    public static void Quit()
    {
        if (Current is App app && app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // TryShutdown, unlike Shutdown, raises ShutdownRequested so that the session is saved and closed.
            app._quitting = true;
            desktop.TryShutdown();
        }
    }

    private static void ShowWindow(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }
}
