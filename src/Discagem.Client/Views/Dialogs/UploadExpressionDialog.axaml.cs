using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Discagem.Client.Views.Dialogs;

public partial class UploadExpressionDialog : Window
{
    public UploadExpressionDialog()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
