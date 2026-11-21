using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Discagem.Client.ViewModels;

namespace Discagem.Client.Views;

public partial class ShellView : UserControl
{
    public ShellView()
    {
        InitializeComponent();
    }

    private void OnSoundboardOpened(object? sender, EventArgs e) => (DataContext as ShellViewModel)?.Soundboard.OnOpened();

    /// <summary>
    /// Adding a sound opens a file picker: close the soundboard. Closed once the button's command has
    /// run, since Click comes first.
    /// </summary>
    private void OnSoundboardActionClick(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => SoundboardButton.Flyout?.Hide());
}
