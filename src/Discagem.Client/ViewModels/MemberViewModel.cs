using CommunityToolkit.Mvvm.ComponentModel;
using Discagem.Client.Controls;
using Discagem.Shared;

namespace Discagem.Client.ViewModels;

/// <summary>A user as shown in the member list, in messages and on streams.</summary>
public sealed partial class MemberViewModel : ObservableObject
{
    public MemberViewModel(UserDto user, bool isMe)
    {
        Id = user.Id;
        IsMe = isMe;
        Update(user);
    }

    public long Id { get; }

    public bool IsMe { get; }

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortKey))]
    private string _displayName = "";

    [ObservableProperty]
    private string _avatarColor = "#7C6CFF";

    [ObservableProperty]
    private bool _isAdmin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(Opacity))]
    private bool _isOnline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private bool _isLive;

    public PresenceStatus Status => IsLive ? PresenceStatus.Live : IsOnline ? PresenceStatus.Online : PresenceStatus.Offline;

    public double Opacity => IsOnline ? 1.0 : 0.45;

    public string SortKey => DisplayName.ToUpperInvariant();

    public void Update(UserDto user)
    {
        Username = user.Username;
        DisplayName = user.DisplayName;
        AvatarColor = user.AvatarColor;
        IsAdmin = user.IsAdmin;
    }
}
