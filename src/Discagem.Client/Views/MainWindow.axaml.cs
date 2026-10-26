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
