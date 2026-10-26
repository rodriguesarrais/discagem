using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Discagem.Client.ViewModels;

namespace Discagem.Client.Views;

public partial class StreamWindow : Window
{
    private StreamViewerViewModel? _viewer;

    public StreamWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewer is not null)
        {
            _viewer.CloseRequested -= OnCloseRequested;
        }

        _viewer = DataContext as StreamViewerViewModel;
        if (_viewer is not null)
        {
            _viewer.CloseRequested += OnCloseRequested;
        }
    }

    private void OnCloseRequested(StreamViewerViewModel viewer, string? reason) => Close();

    public void ToggleFullScreen() =>
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && WindowState == WindowState.FullScreen)
        {
            WindowState = WindowState.Normal;
            e.Handled = true;
        }
        else if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_viewer is not null)
        {
            _viewer.CloseRequested -= OnCloseRequested;
        }
    }
}
