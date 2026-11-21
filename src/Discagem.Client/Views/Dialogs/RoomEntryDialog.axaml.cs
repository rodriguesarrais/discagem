using Avalonia.Controls;
using Avalonia.Interactivity;
using Discagem.Client.ViewModels;

namespace Discagem.Client.Views.Dialogs;

public partial class RoomEntryDialog : Window
{
    public RoomEntryDialog()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is RoomEntryViewModel { IsJoin: true })
        {
            CodeBox.Focus();
        }
        else
        {
            NameBox.Focus();
        }
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
