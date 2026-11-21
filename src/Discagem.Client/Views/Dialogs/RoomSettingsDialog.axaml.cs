using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Discagem.Client.Views.Dialogs;

public partial class RoomSettingsDialog : Window
{
    public RoomSettingsDialog()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
