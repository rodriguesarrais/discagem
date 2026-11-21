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

    /// <summary>Shows the member's profile card (with a button to watch their stream, if they are live).</summary>
    private void OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MemberViewModel member)
        {
            ProfileCard.Show(this, member);
        }
    }
}
