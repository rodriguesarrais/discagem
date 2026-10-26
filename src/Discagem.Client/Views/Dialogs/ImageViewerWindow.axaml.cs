using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;

namespace Discagem.Client.Views.Dialogs;

public partial class ImageViewerWindow : Window
{
    public ImageViewerWindow()
    {
        InitializeComponent();
    }

    public ImageViewerWindow(Bitmap bitmap, string title)
        : this()
    {
        ImageView.Source = bitmap;
        Title = title;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void OnBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == sender)
        {
            Close();
        }
    }
}
