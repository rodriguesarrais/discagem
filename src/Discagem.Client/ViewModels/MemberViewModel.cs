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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBio))]
    private string _bio = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus), nameof(StatusLine))]
    private string _statusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus), nameof(StatusLine))]
    private string _statusEmoji = "";

    /// <summary>#RRGGBB chosen by the member for their name, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameForeground))]
    private string? _nameColor;

    /// <summary>#RRGGBB chosen by the member for their profile card's banner, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BannerBrush))]
    private string? _bannerColor;

    [ObservableProperty]
    private DateTimeOffset _createdAt;

    public bool HasBio => !string.IsNullOrWhiteSpace(Bio);

    public bool HasStatus => StatusLine.Length > 0;

    /// <summary>The custom status, emoji first ("♟️ Playing chess").</summary>
    public string StatusLine => $"{StatusEmoji} {StatusText}".Trim();

    /// <summary>The member's name colour, or unset so that the theme's colour applies.</summary>
    public object NameForeground =>
        NameColor is not null && Avalonia.Media.Color.TryParse(NameColor, out var color)
            ? new Avalonia.Media.SolidColorBrush(color)
            : Avalonia.AvaloniaProperty.UnsetValue;

    /// <summary>The profile card's banner: the chosen colour, or the avatar colour.</summary>
    public Avalonia.Media.IBrush BannerBrush =>
        Avalonia.Media.Color.TryParse(BannerColor ?? AvatarColor, out var color)
            ? new Avalonia.Media.SolidColorBrush(color)
            : Avalonia.Media.Brushes.SlateBlue;

    public string MemberSince => $"Member since {CreatedAt.ToLocalTime():d MMM yyyy}";

    partial void OnAvatarColorChanged(string value) => OnPropertyChanged(nameof(BannerBrush));

    /// <summary>Whether the member owns the room being shown (marked with a crown).</summary>
    [ObservableProperty]
    private bool _isRoomOwner;

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
        Bio = user.Bio ?? "";
        StatusText = user.StatusText ?? "";
        StatusEmoji = user.StatusEmoji ?? "";
        NameColor = user.NameColor;
        BannerColor = user.BannerColor;
        CreatedAt = user.CreatedAt;
        OnPropertyChanged(nameof(MemberSince));
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
