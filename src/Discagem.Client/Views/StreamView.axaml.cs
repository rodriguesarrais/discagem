using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Discagem.Client.ViewModels;

namespace Discagem.Client.Views;

public partial class StreamView : UserControl
{
    public StreamView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Inside the pop-out window the pop-out button makes no sense.
        PopOutButton.IsVisible = TopLevel.GetTopLevel(this) is not StreamWindow;
    }

    private void OnPopOutClick(object? sender, RoutedEventArgs e) => PopOut();

    private void OnFullScreenClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is StreamWindow window)
        {
            window.ToggleFullScreen();
        }
        else
        {
            PopOut(fullScreen: true);
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e) => OnFullScreenClick(sender, e);

    private void PopOut(bool fullScreen = false)
    {
        if (DataContext is not StreamViewerViewModel viewer || viewer.IsPoppedOut)
        {
            return;
        }

        var window = new StreamWindow { DataContext = viewer };
        viewer.IsPoppedOut = true;
        window.Closed += (_, _) => viewer.IsPoppedOut = false;
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }

        if (fullScreen)
        {
            window.ToggleFullScreen();
        }
    }
}
