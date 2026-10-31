using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Discagem.Client.Controls;
using Discagem.Client.Services;
using Discagem.Shared;

namespace Discagem.Client.ViewModels;

/// <summary>A user as shown in the member list, in messages and on streams.</summary>
public sealed partial class MemberViewModel : ObservableObject
{
    private readonly AvatarCache? _avatars;
    private int? _avatarVersion;

    public MemberViewModel(UserDto user, bool isMe, AvatarCache? avatars = null)
    {
        Id = user.Id;
        IsMe = isMe;
        _avatars = avatars;
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

    /// <summary>The profile photo, once loaded (null without one).</summary>
    [ObservableProperty]
    private Bitmap? _avatarImage;

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
        if (user.AvatarVersion != _avatarVersion)
        {
            _avatarVersion = user.AvatarVersion;
            if (user.AvatarVersion is { } version && _avatars is not null)
            {
                _ = LoadAvatarAsync(version);
            }
            else
            {
                AvatarImage = null;
            }
        }
    }

    private async Task LoadAvatarAsync(int version)
    {
        var image = await _avatars!.GetAsync(Id, version);
        Dispatcher.UIThread.Post(() =>
        {
            // A newer photo may have arrived while this one loaded.
            if (_avatarVersion == version)
            {
                AvatarImage = image;
            }
        });
    }
}
