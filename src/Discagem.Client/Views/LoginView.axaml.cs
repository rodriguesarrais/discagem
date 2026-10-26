using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Discagem.Client.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        var target = string.IsNullOrEmpty(ServerBox.Text) ? ServerBox : string.IsNullOrEmpty(UsernameBox.Text) ? UsernameBox : PasswordBox;
        target.Focus();
    }
}
