using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Discagem.Client.Views.Dialogs;

public partial class SettingsDialog : Window
{
    public SettingsDialog()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
