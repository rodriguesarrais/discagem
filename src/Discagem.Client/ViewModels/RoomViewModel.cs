using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Discagem.Client.Services;
using Discagem.Shared;

namespace Discagem.Client.ViewModels;

/// <summary>
/// A room the user is a member of: a tile in the room rail, with the room's channels, voice channels
/// and live streams.
/// </summary>
public sealed partial class RoomViewModel : ObservableObject
{
    private readonly RoomIconCache _icons;
    private int? _iconVersion;

    public RoomViewModel(RoomDto room, RoomIconCache icons)
    {
        Id = room.Id;
        _icons = icons;
        Channels.CollectionChanged += (_, e) =>
        {
            foreach (ChannelViewModel channel in e.NewItems ?? Array.Empty<ChannelViewModel>())
            {
                channel.PropertyChanged += OnChannelChanged;
            }

            foreach (ChannelViewModel channel in e.OldItems ?? Array.Empty<ChannelViewModel>())
            {
                channel.PropertyChanged -= OnChannelChanged;
            }

            UpdateUnread();
        };
        LiveStreams.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLiveStreams));
        Update(room);
    }

    public long Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initials))]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorBrush))]
    private string _color = "#5865F2";

    [ObservableProperty]
    private long? _ownerId;

    [ObservableProperty]
    private string _inviteCode = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private Bitmap? _icon;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>What the room notifies about (the user's choice; Default means every message).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotifiesAll), nameof(NotifiesMentions), nameof(NotifiesNothing))]
    private NotificationLevel _notifications;

    public bool NotifiesAll => Notifications is NotificationLevel.All or NotificationLevel.Default;

    public bool NotifiesMentions => Notifications == NotificationLevel.Mentions;

    public bool NotifiesNothing => Notifications == NotificationLevel.Nothing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMentions))]
    private int _mentionCount;

    [ObservableProperty]
    private bool _hasUnread;

    public IReadOnlyList<long> MemberIds { get; private set; } = [];

    public int MemberCount => MemberIds.Count;

    public string Initials => Controls.AvatarView.Initials(Name);

    public IBrush ColorBrush => Avalonia.Media.Color.TryParse(Color, out var color) ? new SolidColorBrush(color) : Brushes.SlateBlue;

    public bool HasIcon => Icon is not null;

    public bool HasMentions => MentionCount > 0;

    public bool HasLiveStreams => LiveStreams.Count > 0;

    public ObservableCollection<ChannelViewModel> Channels { get; } = [];

    public ObservableCollection<VoiceChannelViewModel> VoiceChannels { get; } = [];

    public ObservableCollection<LiveStreamViewModel> LiveStreams { get; } = [];

    public bool IsOwner(long userId) => OwnerId == userId;

    public bool HasMember(long userId) => MemberIds.Contains(userId);

    public void Update(RoomDto room)
    {
        Name = room.Name;
        Color = room.Color;
        OwnerId = room.OwnerId;
        InviteCode = room.InviteCode;
        MemberIds = room.MemberIds;
        OnPropertyChanged(nameof(MemberIds));
        OnPropertyChanged(nameof(MemberCount));
        if (room.IconVersion != _iconVersion)
        {
            _iconVersion = room.IconVersion;
            _ = LoadIconAsync(room.IconVersion);
        }
    }

    private async Task LoadIconAsync(int? version)
    {
        if (version is not { } v)
        {
            Icon = null;
            return;
        }

        var icon = await _icons.GetAsync(Id, v);
        if (_iconVersion == version)
        {
            Icon = icon;
        }
    }

    private void OnChannelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChannelViewModel.UnreadCount) or nameof(ChannelViewModel.MentionCount))
        {
            UpdateUnread();
        }
    }

    private void UpdateUnread()
    {
        MentionCount = Channels.Sum(c => c.MentionCount);
        HasUnread = Channels.Any(c => c.HasUnread);
    }
}
