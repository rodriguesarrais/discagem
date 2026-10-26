using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Discagem.Client.Views.Dialogs;

public partial class ScreenPickerDialog : Window
{
    public ScreenPickerDialog()
    {
        InitializeComponent();
    }

    private void OnGoLive(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
