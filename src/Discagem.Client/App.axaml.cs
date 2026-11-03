using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Discagem.Client.Resources;
using Discagem.Client.Services;
using Discagem.Client.ViewModels;
using Discagem.Client.Views;

namespace Discagem.Client;

public sealed class App : Application
{
    private bool _quitting;
    private bool _trayAvailable = true;

    /// <summary>The state to bring the window back in after it was minimized (perhaps into the tray).</summary>
    private WindowState _restoreState = WindowState.Normal;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = ClientSettings.Load();

            // Before any window is built: the views take their text from the resources as they load.
            Localization.Apply(settings.Language);
            Autostart.Refresh();
            ThemeManager.Apply(settings.ThemeValue);
            var window = new MainWindow();
            var dialogs = new DialogService(window);
            var viewModel = new MainWindowViewModel(settings, dialogs);
            window.DataContext = viewModel;
            window.RestoreSize(settings);
            _restoreState = settings.WindowMaximized ? WindowState.Maximized : WindowState.Normal;

            // Started with the computer: stay in the notification area (minimized where tray icons
            // may not be shown) until the user asks for the window. The lifetime shows only the main
            // window, so it is set when first shown.
            if (Program.StartedAtLogin && settings.StartMinimized)
            {
                if (!OperatingSystem.IsWindows())
                {
                    window.WindowState = WindowState.Minimized;
                    desktop.MainWindow = window;
                }
            }
            else
            {
                desktop.MainWindow = window;
            }

            desktop.ShutdownRequested += (_, _) =>
            {
                _quitting = true;
                window.SaveSize(settings);
                viewModel.Shutdown();
            };

            // Closing the window only hides it while Discagem lives on in the notification area.
            window.Closing += (_, e) =>
            {
                if (!_quitting && settings.CloseToTrayEnabled && _trayAvailable && !e.IsProgrammatic && e.CloseReason == WindowCloseReason.WindowClosing)
                {
                    e.Cancel = true;
                    window.SaveSize(settings);
                    window.Hide();
                }
            };

            window.PropertyChanged += (_, e) =>
            {
                if (e.Property != Window.WindowStateProperty)
                {
                    return;
                }

                if (window.WindowState != WindowState.Minimized)
                {
                    _restoreState = window.WindowState;
                }
                else if (settings.MinimizeToTray && _trayAvailable)
                {
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
                _trayAvailable = false;
                e.Handled = true;
            }
        };

        var open = new NativeMenuItem(Strings.Tray_Open);
        open.Click += (_, _) => ShowWindow(window);
        var quit = new NativeMenuItem(Strings.Tray_Quit);
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

    /// <summary>
    /// Whether <see cref="Restart"/> can start the new client: not inside a Flatpak sandbox, which
    /// ends with the client that quits.
    /// </summary>
    public static bool CanRestart =>
        Environment.ProcessPath is not null && Environment.GetEnvironmentVariable("FLATPAK_ID") is null;

    /// <summary>Starts a new client, which waits for this one to quit, and quits.</summary>
    public static void Restart()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, Program.RestartArgument) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ClientLog.Error($"Could not restart Discagem: {ex.Message}");
            return;
        }

        Quit();
    }

    private void ShowWindow(Window window)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: null } desktop)
        {
            desktop.MainWindow = window;
        }

        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = _restoreState;
        }

        window.Activate();
    }
}
