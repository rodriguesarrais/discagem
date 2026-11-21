using Avalonia.Controls;
using Discagem.Client.Services;
using Discagem.Client.ViewModels;

namespace Discagem.Client.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Activated += (_, _) => ((DataContext as MainWindowViewModel)?.CurrentView as ShellViewModel)?.OnWindowActivated();
        ThemeManager.Changed += ApplyAppearance;
        ApplyAppearance();
    }

    /// <summary>Zoom scales everything; the compact class shrinks messages (see Controls.axaml).</summary>
    private void ApplyAppearance()
    {
        var appearance = ThemeManager.Appearance;
        Zoom.LayoutTransform = Math.Abs(appearance.Zoom - 1) < 0.001 ? null : new Avalonia.Media.ScaleTransform(appearance.Zoom, appearance.Zoom);
        Classes.Set("compact", appearance.CompactMessages);
    }

    public void RestoreSize(ClientSettings settings)
    {
        if (settings.WindowWidth >= MinWidth && settings.WindowHeight >= MinHeight)
        {
            Width = settings.WindowWidth;
            Height = settings.WindowHeight;
        }

        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    public void SaveSize(ClientSettings settings)
    {
        settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            settings.WindowWidth = Width;
            settings.WindowHeight = Height;
        }

        settings.Save();
    }
}
