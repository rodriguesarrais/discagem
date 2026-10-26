using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Discagem.Client.ViewModels;

namespace Discagem.Client.Views;

public partial class MemberRow : UserControl
{
    public MemberRow()
    {
        InitializeComponent();
    }

    private void OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MemberViewModel { IsLive: true, IsMe: false } member &&
            this.FindAncestorOfType<ShellView>()?.DataContext is ShellViewModel shell)
        {
            shell.WatchMemberCommand.Execute(member);
        }
    }
}
