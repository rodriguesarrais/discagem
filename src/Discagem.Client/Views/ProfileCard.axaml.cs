using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Discagem.Client.ViewModels;

namespace Discagem.Client.Views;

/// <summary>A member's profile: banner, photo, status and bio, with a button to watch their stream.</summary>
public partial class ProfileCard : UserControl
{
    private Action? _watch;

    public ProfileCard()
    {
        InitializeComponent();
    }

    /// <summary>Shows a member's card in a flyout next to <paramref name="target"/>.</summary>
    public static void Show(Control target, MemberViewModel member, PlacementMode placement = PlacementMode.LeftEdgeAlignedTop)
    {
        var shell = target.FindAncestorOfType<ShellView>()?.DataContext as ShellViewModel;
        var card = new ProfileCard { DataContext = member };
        var flyout = new Flyout { Content = card, Placement = placement, FlyoutPresenterClasses = { "profile" } };
        card.WatchButton.IsVisible = member is { IsLive: true, IsMe: false } && shell is not null;
        card._watch = () =>
        {
            flyout.Hide();
            shell?.WatchMemberCommand.Execute(member);
        };
        flyout.ShowAt(target);
    }

    private void OnWatch(object? sender, RoutedEventArgs e) => _watch?.Invoke();
}
