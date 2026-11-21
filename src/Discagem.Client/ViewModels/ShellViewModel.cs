using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Discagem.Client.Services;
using Discagem.Client.Services.Audio;
using Discagem.Media.Pipeline;
using Discagem.Media.Vpx;
using Discagem.Media.WebRtc;
using Discagem.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Discagem.Client.ViewModels;

public enum ConnectionState
{
    Connecting,
    Connected,
    Reconnecting,
    Disconnected,
}

/// <summary>The main window once signed in: channels, messages, members and screen shares.</summary>
public sealed partial class ShellViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan TypingTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan TypingSendInterval = TimeSpan.FromSeconds(3);

    private readonly ClientSession _session;
    private readonly IDialogService _dialogs;
    private readonly ChatConnection _hub;
    private readonly ScreenShareService _media;
    private readonly VoiceService _voice;
    private long _lastMessageSound = long.MinValue / 2;
    private readonly AvatarCache _avatars;
    private readonly RoomIconCache _roomIcons;
    private readonly ImageCache _images;
    private readonly EmojiCatalog _emojiCatalog;
    private readonly Dictionary<long, MemberViewModel> _members = new();
    private readonly Dictionary<(long Channel, long User), DateTime> _typing = new();
    private readonly DispatcherTimer _typingTimer;
    private readonly DispatcherTimer _tokenRefreshTimer;
    private readonly UpdateService _updates;
    private readonly DispatcherTimer _updateTimer;
    private ReleaseVersion? _dismissedUpdate;
    private DateTime _lastTypingSent = DateTime.MinValue;
    private bool _recoveringVoice;
    private TaskCompletionSource _voiceRecoveryWake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public ShellViewModel(ClientSession session, IDialogService dialogs)
    {
        _session = session;
        _dialogs = dialogs;
        _updates = new UpdateService(session.Api.BaseAddress);
        _images = new ImageCache(session.Api);
        _avatars = new AvatarCache(session.Api);
        _roomIcons = new RoomIconCache(session.Api);
        Rooms.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoRooms));
        _me = GetOrAddMember(session.User, isMe: true);
        _showMembers = session.Settings.ShowMembers;

        _hub = new ChatConnection(session.Api.BaseAddress, () => _session.Api.Token);
        var iceServers = BuildIceServers(session);
        var audio = AudioBackends.Create();
        _media = new ScreenShareService(_hub, iceServers, audio, () => session.Settings.VoiceOutputDevice,
            key => StreamMixerViewModel.InitialVolume(session.Settings, key));
        StreamMixer = new StreamMixerViewModel(session.Settings);
        _voice = new VoiceService(_hub, iceServers, session.Settings, audio, session.User.Id);
        _emojiCatalog = new EmojiCatalog(session.Api);
        EmojiCatalog.Current = _emojiCatalog;
        Emojis = new EmojiPickerViewModel(session.Api, _emojiCatalog, dialogs, () => Me, SendStickerAsync, message => ShowBanner(message));
        Soundboard = new SoundboardViewModel(session.Api, _hub, _voice, session.Settings, dialogs, () => Me, message => ShowBanner(message));
        _isMuted = session.Settings.VoiceMuted;
        _isDeafened = session.Settings.VoiceDeafened;
        _typingTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateTypingText());
        // Tokens last for weeks; refresh them so long-running sessions never reconnect with an expired one.
        _tokenRefreshTimer = new DispatcherTimer(TimeSpan.FromHours(6), DispatcherPriority.Background, async (_, _) => await RefreshTokenAsync());
        _updateTimer = new DispatcherTimer(TimeSpan.FromHours(6), DispatcherPriority.Background, async (_, _) => await CheckForUpdatesAutomaticallyAsync());

        WireHubEvents();
        _media.PublishStatsUpdated += stats =>
        {
            var sound = _media.DescribePublishAudio();
            Dispatcher.UIThread.Post(() =>
            {
                LiveStatsText = $"{stats.Height}p · {stats.FramesPerSecond:0} FPS · {stats.SentKbps / 1000:0.0} Mbps";
                LiveSoundText = sound.Length > 0 ? char.ToUpperInvariant(sound[0]) + sound[1..] : "";
                StreamMixer.Refresh(_media.PublishMixer);
            });
        };
        _media.PublishApplicationsChanged += () => Dispatcher.UIThread.Post(() => StreamMixer.Refresh(_media.PublishMixer));
        _media.PublishStopped += reason => Dispatcher.UIThread.Post(() =>
        {
            IsLive = false;
            ShowBanner(reason);
        });
        _media.PublishAudioProblem += message => Dispatcher.UIThread.Post(() => ShowBanner(message));
        _voice.SpeakingChanged += (userId, speaking) => Dispatcher.UIThread.Post(() => OnSpeakingChanged(userId, speaking));
        _voice.Disconnected += (reason, canRejoin) => Dispatcher.UIThread.Post(() =>
        {
            if (canRejoin && ConnectedVoiceChannel is not null)
            {
                // Usually the server went away and the chat connection is about to notice too.
                _ = RecoverVoiceAsync();
                return;
            }

            SetConnectedVoice(null);
            ShowBanner(reason, isError: false);
        });
        _voice.AudioProblem += message => Dispatcher.UIThread.Post(() => ShowBanner(message));
    }

    /// <summary>Raised when the user signs out or the session becomes invalid (with an optional message).</summary>
    public event Action<string?>? LoggedOut;

    /// <summary>The rooms the user is in, in the order they chose.</summary>
    public ObservableCollection<RoomViewModel> Rooms { get; } = [];

    /// <summary>The selected room's text channels.</summary>
    public ObservableCollection<ChannelViewModel> Channels => SelectedRoom?.Channels ?? NoChannels;

    /// <summary>The applications in the sound of your live stream, with their volumes.</summary>
    public StreamMixerViewModel StreamMixer { get; }

    /// <summary>The composer's emoji and sticker picker, with the server's custom emojis.</summary>
    public EmojiPickerViewModel Emojis { get; }

    /// <summary>The voice card's soundboard.</summary>
    public SoundboardViewModel Soundboard { get; }

    partial void OnIsLiveChanged(bool value)
    {
        if (!value)
        {
            StreamMixer.Refresh(null);
        }
    }

    /// <summary>The selected room's voice channels.</summary>
    public ObservableCollection<VoiceChannelViewModel> VoiceChannels => SelectedRoom?.VoiceChannels ?? NoVoiceChannels;

    private static readonly ObservableCollection<ChannelViewModel> NoChannels = [];
    private static readonly ObservableCollection<VoiceChannelViewModel> NoVoiceChannels = [];
    private static readonly ObservableCollection<LiveStreamViewModel> NoStreams = [];

    public ObservableCollection<MemberViewModel> OnlineMembers { get; } = [];

    public ObservableCollection<MemberViewModel> OfflineMembers { get; } = [];

    /// <summary>The selected room's live streams.</summary>
    public ObservableCollection<LiveStreamViewModel> LiveStreams => SelectedRoom?.LiveStreams ?? NoStreams;

    private IEnumerable<ChannelViewModel> AllChannels => Rooms.SelectMany(r => r.Channels);

    private IEnumerable<VoiceChannelViewModel> AllVoiceChannels => Rooms.SelectMany(r => r.VoiceChannels);

    private IEnumerable<LiveStreamViewModel> AllStreams => Rooms.SelectMany(r => r.LiveStreams);

    public ObservableCollection<PendingAttachmentViewModel> PendingAttachments { get; } = [];

    public string ServerName => _session.Server.Name;

    public string ServerInitials => Controls.AvatarView.Initials(_session.Server.Name);

    public bool IsAdmin => Me.IsAdmin;

    /// <summary>The selected room's name; the server's before the user is in any room.</summary>
    public string RoomName => SelectedRoom?.Name ?? ServerName;

    public bool HasRoom => SelectedRoom is not null;

    /// <summary>Once connected, true while the user is in no room at all.</summary>
    public bool HasNoRooms => Rooms.Count == 0 && Connection == ConnectionState.Connected;

    /// <summary>The room's owner and server administrators manage the selected room.</summary>
    public bool CanManageRoom => SelectedRoom is { } room && (room.IsOwner(Me.Id) || Me.IsAdmin);

    public string OnlineHeader => $"ONLINE — {OnlineMembers.Count}";

    public string OfflineHeader => $"OFFLINE — {OfflineMembers.Count}";

    public bool HasOfflineMembers => OfflineMembers.Count > 0;

    public bool HasLiveStreams => SelectedRoom?.HasLiveStreams == true;

    public bool HasPendingAttachments => PendingAttachments.Count > 0;

    public bool HasTyping => !string.IsNullOrEmpty(TypingText);

    public bool IsWatching => ActiveStream is not null;

    public bool IsReconnecting => Connection is ConnectionState.Reconnecting or ConnectionState.Connecting or ConnectionState.Disconnected;

    public string ConnectionText => Connection == ConnectionState.Connecting
        ? "Connecting to the server…"
        : "Connection lost — trying to reconnect…";

    public string WindowTitle
    {
        get
        {
            var unread = AllChannels.Sum(c => c.MentionCount);
            var title = SelectedChannel is null ? $"Discagem — {RoomName}" : $"#{SelectedChannel.Name} — {RoomName}";
            return unread > 0 ? $"({unread}) {title}" : title;
        }
    }

    [ObservableProperty]
    private MemberViewModel _me;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoomName), nameof(HasRoom), nameof(CanManageRoom), nameof(HasLiveStreams), nameof(WindowTitle))]
    private RoomViewModel? _selectedRoom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private ChannelViewModel? _selectedChannel;

    [ObservableProperty]
    private string _composerText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTyping))]
    private string? _typingText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReconnecting), nameof(ConnectionText), nameof(HasNoRooms))]
    private ConnectionState _connection = ConnectionState.Connecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBanner))]
    private string? _bannerMessage;

    public bool HasBanner => !string.IsNullOrEmpty(BannerMessage);

    [ObservableProperty]
    private bool _bannerIsError = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWatching))]
    private StreamViewerViewModel? _activeStream;

    [ObservableProperty]
    private bool _isLive;

    [ObservableProperty]
    private bool _isStartingStream;

    [ObservableProperty]
    private string _liveStatsText = "Starting…";

    /// <summary>What the live stream's sound is doing (captured source, nothing playing, or why there is none).</summary>
    [ObservableProperty]
    private string _liveSoundText = "";

    [ObservableProperty]
    private bool _showMembers;

    [ObservableProperty]
    private bool _isSending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInVoice))]
    private VoiceChannelViewModel? _connectedVoiceChannel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVoiceConnected))]
    private bool _isJoiningVoice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMicrophoneOff))]
    private bool _isMuted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMicrophoneOff))]
    private bool _isDeafened;

    [ObservableProperty]
    private bool _isMeSpeaking;

    public bool IsInVoice => ConnectedVoiceChannel is not null;

    public bool IsVoiceConnected => !IsJoiningVoice;

    /// <summary>Deafening also turns the microphone off.</summary>
    public bool IsMicrophoneOff => IsMuted || IsDeafened;

    partial void OnShowMembersChanged(bool value)
    {
        _session.Settings.ShowMembers = value;
        _session.Settings.Save();
    }

    partial void OnSelectedRoomChanged(RoomViewModel? oldValue, RoomViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
            oldValue.LiveStreams.CollectionChanged -= OnSelectedRoomStreamsChanged;
        }

        // The lists in the sidebar follow the room before a channel of it is selected.
        OnPropertyChanged(nameof(Channels));
        OnPropertyChanged(nameof(VoiceChannels));
        OnPropertyChanged(nameof(LiveStreams));
        SortMembers();
        if (newValue is null)
        {
            SelectedChannel = null;
            return;
        }

        newValue.IsSelected = true;
        newValue.LiveStreams.CollectionChanged += OnSelectedRoomStreamsChanged;
        _session.Settings.LastRoomId = newValue.Id;
        _session.Settings.Save();
        var lastChannel = _session.Settings.LastChannelByRoom.GetValueOrDefault(newValue.Id);
        SelectedChannel = newValue.Channels.FirstOrDefault(c => c.Id == lastChannel) ?? newValue.Channels.FirstOrDefault();
    }

    private void OnSelectedRoomStreamsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(HasLiveStreams));

    partial void OnSelectedChannelChanged(ChannelViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        value.MarkRead();
        _session.Settings.LastChannelId = value.Id;
        _session.Settings.LastChannelByRoom[value.RoomId] = value.Id;
        _session.Settings.Save();
        UpdateTypingText();
        OnPropertyChanged(nameof(WindowTitle));
        if (!value.IsLoaded)
        {
            _ = LoadLatestAsync(value);
        }
    }

    partial void OnComposerTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || SelectedChannel is null || Connection != ConnectionState.Connected)
        {
            return;
        }

        if (DateTime.UtcNow - _lastTypingSent > TypingSendInterval)
        {
            _lastTypingSent = DateTime.UtcNow;
            _ = SafeAsync(() => _hub.TypingAsync(SelectedChannel.Id));
        }
    }

    private static IReadOnlyList<IceServerConfig> BuildIceServers(ClientSession session)
    {
        var servers = new List<IceServerConfig>();
        if (session.Server.StunPort > 0)
        {
            servers.Add(new IceServerConfig($"stun:{session.Api.BaseAddress.Host}:{session.Server.StunPort}"));
        }

        servers.AddRange(session.Server.IceServers.Select(s => new IceServerConfig(s.Urls, s.Username, s.Credential)));
        return servers;
    }

    private async Task RefreshTokenAsync()
    {
        try
        {
            var auth = await _session.Api.RefreshTokenAsync();
            _session.Api.Token = auth.Token;
            _session.Settings.Token = auth.Token;
            _session.Settings.Save();
        }
        catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            SignOut("Your session has expired. Please log in again.");
        }
        catch (ApiException)
        {
            // Offline for now; the next tick tries again.
        }
    }

    public async Task InitializeAsync()
    {
        _typingTimer.Start();
        _tokenRefreshTimer.Start();
        _updateTimer.Start();
        _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(
            _ => Dispatcher.UIThread.Post(async () => await CheckForUpdatesAutomaticallyAsync()), TaskScheduler.Default);
        Connection = ConnectionState.Connecting;
        while (!_disposed)
        {
            try
            {
                await _hub.StartAsync();
                Connection = ConnectionState.Connected;
                await SyncAsync();
                return;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                SignOut("Your session has expired. Please log in again.");
                return;
            }
            catch (Exception ex)
            {
                Connection = ConnectionState.Reconnecting;
                ShowBanner($"Could not connect to the server: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
    }

    private void WireHubEvents()
    {
        _hub.MessageCreated += m => Dispatcher.UIThread.Post(() => OnMessageCreated(m));
        _hub.MessageUpdated += m => Dispatcher.UIThread.Post(() => FindChannel(m.ChannelId)?.Find(m.Id)?.Apply(m));
        _hub.MessageDeleted += (c, id) => Dispatcher.UIThread.Post(() => OnMessageDeleted(c, id));
        _hub.ChannelCreated += c => Dispatcher.UIThread.Post(() => AddOrUpdateChannel(c));
        _hub.ChannelUpdated += c => Dispatcher.UIThread.Post(() => AddOrUpdateChannel(c));
        _hub.ChannelDeleted += id => Dispatcher.UIThread.Post(() => OnChannelDeleted(id));
        _hub.RoomUpdated += r => Dispatcher.UIThread.Post(() => OnRoomUpdated(r));
        _hub.RoomAdded += r => Dispatcher.UIThread.Post(() => AddRoomState(r, select: false));
        _hub.RoomRemoved += id => Dispatcher.UIThread.Post(() => RemoveRoom(id, announce: true));
        _hub.UserCreated += u => Dispatcher.UIThread.Post(() => GetOrAddMember(u));
        _hub.UserUpdated += u => Dispatcher.UIThread.Post(() =>
        {
            var member = GetOrAddMember(u);
            member.Update(u);
            SortMembers();
        });
        _hub.PresenceChanged += (id, online) => Dispatcher.UIThread.Post(() => SetPresence(id, online));
        _hub.UserTyping += (c, u) => Dispatcher.UIThread.Post(() =>
        {
            if (u != Me.Id)
            {
                _typing[(c, u)] = DateTime.UtcNow + TypingTimeout;
                UpdateTypingText();
            }
        });
        _hub.StreamStarted += s => Dispatcher.UIThread.Post(() => AddOrUpdateStream(s));
        _hub.StreamUpdated += s => Dispatcher.UIThread.Post(() => AddOrUpdateStream(s));
        _hub.StreamEnded += id => Dispatcher.UIThread.Post(() => OnStreamEnded(id));
        _hub.VoiceStateChanged += p => Dispatcher.UIThread.Post(() => OnVoiceStateChanged(p, announce: true));
        _hub.VoiceLeft += (c, u) => Dispatcher.UIThread.Post(() => OnVoiceLeft(c, u));
        _hub.EmojiCreated += e => Dispatcher.UIThread.Post(() => Emojis.Add(e));
        _hub.EmojiDeleted += id => Dispatcher.UIThread.Post(() => Emojis.Remove(id));
        _hub.SoundCreated += s => Dispatcher.UIThread.Post(() => Soundboard.Add(s));
        _hub.SoundDeleted += id => Dispatcher.UIThread.Post(() => Soundboard.Remove(id));
        _hub.SoundPlayed += (c, u, s) => Dispatcher.UIThread.Post(() => Soundboard.OnSoundPlayed(c, u, s));
        _hub.Reconnecting += error => Dispatcher.UIThread.Post(() =>
        {
            Connection = ConnectionState.Reconnecting;
            if (IsLive)
            {
                IsLive = false;
                _ = _media.StopAsync();
                ShowBanner("Connection lost. Your screen share was stopped.");
            }

            if (ActiveStream is not null)
            {
                CloseStreamViewer(ActiveStream, null);
            }

            // The server ends the call when the connection drops; rejoin once it is back.
            if (IsInVoice)
            {
                _ = _voice.SuspendAsync();
                _ = RecoverVoiceAsync();
            }
        });
        _hub.Reconnected += () => Dispatcher.UIThread.Post(async () =>
        {
            Connection = ConnectionState.Connected;
            await SafeAsync(SyncAsync);
            _voiceRecoveryWake.TrySetResult();
        });
        _hub.Closed += ex => Dispatcher.UIThread.Post(async () =>
        {
            if (_disposed)
            {
                return;
            }

            // Automatic reconnection gave up (or never started); keep trying.
            Connection = ConnectionState.Disconnected;
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (!_disposed && _hub.State == HubConnectionState.Disconnected)
            {
                await InitializeAsync();
            }
        });
    }

    /// <summary>Loads (or reloads after a reconnect) the full server state.</summary>
    private async Task SyncAsync()
    {
        var state = await _hub.SyncAsync();

        Me.Update(state.Me);
        foreach (var user in state.Users)
        {
            GetOrAddMember(user).Update(user);
        }

        var online = state.OnlineUserIds.ToHashSet();
        online.Add(Me.Id);
        foreach (var member in _members.Values)
        {
            member.IsOnline = online.Contains(member.Id);
        }

        Emojis.Reset(state.Emojis ?? []);
        Soundboard.Reset(state.Sounds);

        // Servers from before rooms have one set of channels: show it as a single room.
        var rooms = state.Rooms ?? [new RoomDto(0, ServerName, null, "#5865F2", null, "", _members.Keys.ToList(), DateTimeOffset.UtcNow)];
        foreach (var stale in Rooms.Where(r => rooms.All(d => d.Id != r.Id)).ToList())
        {
            RemoveRoom(stale.Id, announce: false);
        }

        foreach (var room in rooms)
        {
            AddOrUpdateRoom(room);
        }

        foreach (var channel in state.Channels)
        {
            AddOrUpdateChannel(channel);
        }

        foreach (var room in Rooms)
        {
            foreach (var stale in room.Channels.Where(c => state.Channels.All(s => s.Id != c.Id)).ToList())
            {
                room.Channels.Remove(stale);
            }

            foreach (var stale in room.VoiceChannels.Where(c => state.Channels.All(s => s.Id != c.Id)).ToList())
            {
                room.VoiceChannels.Remove(stale);
            }

            room.LiveStreams.Clear();
        }

        foreach (var channel in AllVoiceChannels)
        {
            channel.Participants.Clear();
        }

        foreach (var participant in state.VoiceParticipants)
        {
            OnVoiceStateChanged(participant, announce: false);
        }

        // Anything may have changed while disconnected: reload history lazily.
        foreach (var channel in AllChannels)
        {
            channel.IsLoaded = false;
        }

        foreach (var member in _members.Values)
        {
            member.IsLive = false;
        }

        foreach (var stream in state.Streams)
        {
            AddOrUpdateStream(stream);
        }

        OnPropertyChanged(nameof(IsAdmin));
        OnPropertyChanged(nameof(CanManageRoom));
        OnPropertyChanged(nameof(HasNoRooms));
        SortMembers();
        var preferredRoom = SelectedRoom
            ?? Rooms.FirstOrDefault(r => r.Id == _session.Settings.LastRoomId)
            ?? Rooms.FirstOrDefault(r => r.Channels.Any(c => c.Id == _session.Settings.LastChannelId))
            ?? Rooms.FirstOrDefault();
        if (preferredRoom != SelectedRoom)
        {
            SelectedRoom = preferredRoom;
        }
        else if (SelectedChannel is { } selected)
        {
            await LoadLatestAsync(selected);
        }
        else
        {
            SelectedChannel = SelectedRoom?.Channels.FirstOrDefault();
        }

        BannerMessage = null;
    }

    private async Task LoadLatestAsync(ChannelViewModel channel)
    {
        channel.IsLoadingHistory = true;
        try
        {
            var messages = await _hub.GetMessagesAsync(channel.Id, null, Limits.MessagePageSize);
            channel.Messages.Clear();
            foreach (var message in messages)
            {
                channel.Messages.Add(CreateMessage(message));
            }

            channel.HasMoreHistory = messages.Count == Limits.MessagePageSize;
            channel.IsLoaded = true;
            channel.RecomputeGrouping();
        }
        catch (Exception ex)
        {
            ShowBanner($"Could not load messages: {Describe(ex)}");
        }
        finally
        {
            channel.IsLoadingHistory = false;
        }
    }

    /// <summary>Loads the previous page of history. Returns the number of messages added.</summary>
    public async Task<int> LoadOlderAsync()
    {
        var channel = SelectedChannel;
        if (channel is null || !channel.IsLoaded || !channel.HasMoreHistory || channel.IsLoadingHistory || channel.Messages.Count == 0)
        {
            return 0;
        }

        channel.IsLoadingHistory = true;
        try
        {
            var before = channel.Messages[0].Id;
            var messages = await _hub.GetMessagesAsync(channel.Id, before, Limits.MessagePageSize);
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                channel.Messages.Insert(0, CreateMessage(messages[i]));
            }

            channel.HasMoreHistory = messages.Count == Limits.MessagePageSize;
            channel.RecomputeGrouping();
            return messages.Count;
        }
        catch (Exception ex)
        {
            ShowBanner($"Could not load older messages: {Describe(ex)}");
            return 0;
        }
        finally
        {
            channel.IsLoadingHistory = false;
        }
    }

    private MessageItemViewModel CreateMessage(MessageDto message)
    {
        var author = _members.TryGetValue(message.AuthorId, out var member)
            ? member
            : GetOrAddMember(new UserDto(message.AuthorId, "unknown", "Unknown user", "#80848E", false, DateTimeOffset.UtcNow));
        var item = new MessageItemViewModel(message, author, Me, a => new AttachmentViewModel(a, _images, _session.Api, _dialogs));
        foreach (var attachment in item.Attachments)
        {
            attachment.EnsureLoaded();
        }

        return item;
    }

    private void OnMessageCreated(MessageDto message)
    {
        var channel = FindChannel(message.ChannelId);
        if (channel is null)
        {
            return;
        }

        _typing.Remove((message.ChannelId, message.AuthorId));
        UpdateTypingText();

        var item = CreateMessage(message);
        if (channel.IsLoaded && channel.Insert(item))
        {
            channel.RecomputeGrouping();
        }

        if (item.IsMine)
        {
            return;
        }

        var visible = channel == SelectedChannel && _dialogs.IsWindowActive;
        if (!visible)
        {
            PlayMessageSound();
            channel.UnreadCount++;
            if (item.MentionsMe)
            {
                channel.MentionCount++;
                OnPropertyChanged(nameof(WindowTitle));
            }

            _dialogs.RequestAttention();
        }
    }

    /// <summary>The theme's message sound, at most once a second however fast messages come in.</summary>
    private void PlayMessageSound()
    {
        var now = Environment.TickCount64;
        if (!_session.Settings.MessageSounds || now - _lastMessageSound < 1000)
        {
            return;
        }

        _lastMessageSound = now;
        _voice.PlayNotification(ThemeManager.Sounds.Message);
    }

    private void OnMessageDeleted(long channelId, long messageId)
    {
        var channel = FindChannel(channelId);
        var message = channel?.Find(messageId);
        if (channel is not null && message is not null)
        {
            channel.Messages.Remove(message);
            channel.RecomputeGrouping();
        }
    }

    private ChannelViewModel? FindChannel(long id) => AllChannels.FirstOrDefault(c => c.Id == id);

    private VoiceChannelViewModel? FindVoiceChannel(long id) => AllVoiceChannels.FirstOrDefault(c => c.Id == id);

    private RoomViewModel? FindRoom(long id) => Rooms.FirstOrDefault(r => r.Id == id);

    private void AddOrUpdateChannel(ChannelDto dto)
    {
        if (dto.Kind == ChannelKind.Voice)
        {
            AddOrUpdateVoiceChannel(dto);
            return;
        }

        if (FindRoom(dto.RoomId) is not { } room)
        {
            return;
        }

        var channels = room.Channels;
        var existing = channels.FirstOrDefault(c => c.Id == dto.Id);
        if (existing is not null)
        {
            existing.Update(dto);
        }
        else
        {
            existing = new ChannelViewModel(dto);
            channels.Add(existing);
        }

        var ordered = channels.OrderBy(c => c.Position).ThenBy(c => c.Id).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = channels.IndexOf(ordered[i]);
            if (current != i)
            {
                channels.Move(current, i);
            }
        }

        OnPropertyChanged(nameof(WindowTitle));
    }

    private void AddOrUpdateVoiceChannel(ChannelDto dto)
    {
        if (FindRoom(dto.RoomId) is not { } room)
        {
            return;
        }

        var channels = room.VoiceChannels;
        var existing = channels.FirstOrDefault(c => c.Id == dto.Id);
        if (existing is not null)
        {
            existing.Update(dto);
        }
        else
        {
            channels.Add(new VoiceChannelViewModel(dto) { IsConnected = ConnectedVoiceChannel?.Id == dto.Id });
        }

        var ordered = channels.OrderBy(c => c.Position).ThenBy(c => c.Id).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = channels.IndexOf(ordered[i]);
            if (current != i)
            {
                channels.Move(current, i);
            }
        }
    }

    private void OnChannelDeleted(long id)
    {
        var voiceChannel = FindVoiceChannel(id);
        if (voiceChannel is not null)
        {
            FindRoom(voiceChannel.RoomId)?.VoiceChannels.Remove(voiceChannel);
            if (ConnectedVoiceChannel?.Id == id)
            {
                // The server normally hangs up first; make sure no call is left in a channel that is gone.
                SetConnectedVoice(null);
                ShowBanner($"{voiceChannel.Name} was deleted.", isError: false);
                _ = SafeAsync(() => _voice.LeaveAsync(id));
            }

            return;
        }

        var channel = FindChannel(id);
        if (channel is null)
        {
            return;
        }

        var wasSelected = channel == SelectedChannel;
        var room = FindRoom(channel.RoomId);
        room?.Channels.Remove(channel);
        if (wasSelected)
        {
            SelectedChannel = room?.Channels.FirstOrDefault();
        }
    }

    private MemberViewModel GetOrAddMember(UserDto user, bool isMe = false)
    {
        if (_members.TryGetValue(user.Id, out var existing))
        {
            return existing;
        }

        var member = new MemberViewModel(user, isMe || user.Id == _session.User.Id, _avatars);
        _members[user.Id] = member;
        if (member.IsMe)
        {
            member.IsOnline = true;
        }

        SortMembers();
        return member;
    }

    private void SetPresence(long userId, bool online)
    {
        if (_members.TryGetValue(userId, out var member) && member.IsOnline != online)
        {
            member.IsOnline = online;
            SortMembers();
        }
    }

    private void SortMembers()
    {
        // The member list shows the selected room's members.
        var room = SelectedRoom;
        foreach (var member in _members.Values)
        {
            member.IsRoomOwner = room?.IsOwner(member.Id) == true;
        }

        var members = _members.Values.Where(m => room?.HasMember(m.Id) == true).ToList();
        Sync(OnlineMembers, members.Where(m => m.IsOnline).OrderBy(m => m.SortKey).ToList());
        Sync(OfflineMembers, members.Where(m => !m.IsOnline && m.Username != "unknown").OrderBy(m => m.SortKey).ToList());
        OnPropertyChanged(nameof(OnlineHeader));
        OnPropertyChanged(nameof(OfflineHeader));
        OnPropertyChanged(nameof(HasOfflineMembers));

        static void Sync(ObservableCollection<MemberViewModel> target, List<MemberViewModel> desired)
        {
            for (var i = target.Count - 1; i >= 0; i--)
            {
                if (!desired.Contains(target[i]))
                {
                    target.RemoveAt(i);
                }
            }

            for (var i = 0; i < desired.Count; i++)
            {
                var index = target.IndexOf(desired[i]);
                if (index < 0)
                {
                    target.Insert(i, desired[i]);
                }
                else if (index != i)
                {
                    target.Move(index, i);
                }
            }
        }
    }

    private void UpdateTypingText()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _typing.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
        {
            _typing.Remove(key);
        }

        var channelId = SelectedChannel?.Id;
        var names = _typing.Keys
            .Where(k => k.Channel == channelId && _members.ContainsKey(k.User))
            .Select(k => _members[k.User].DisplayName)
            .OrderBy(n => n)
            .ToList();

        TypingText = names.Count switch
        {
            0 => null,
            1 => $"{names[0]} is typing…",
            2 => $"{names[0]} and {names[1]} are typing…",
            3 => $"{names[0]}, {names[1]} and {names[2]} are typing…",
            _ => "Several people are typing…",
        };
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var channel = SelectedChannel;
        var content = ComposerText.Trim();
        if (channel is null || IsSending || (content.Length == 0 && PendingAttachments.Count == 0))
        {
            return;
        }

        if (content.Length > Limits.MaxMessageLength)
        {
            ShowBanner($"Messages can be at most {Limits.MaxMessageLength} characters long.");
            return;
        }

        IsSending = true;
        try
        {
            var ids = new List<long>();
            foreach (var pending in PendingAttachments.ToList())
            {
                if (pending.UploadedId is null)
                {
                    pending.IsUploading = true;
                    await using var stream = await pending.Open();
                    var uploaded = await _session.Api.UploadAsync(stream, pending.FileName, pending.ContentType);
                    pending.UploadedId = uploaded.Id;
                    pending.IsUploading = false;
                }

                ids.Add(pending.UploadedId.Value);
            }

            var sent = await _hub.SendMessageAsync(channel.Id, content, ids.ToArray());
            ComposerText = "";
            PendingAttachments.Clear();
            OnPropertyChanged(nameof(HasPendingAttachments));
            _lastTypingSent = DateTime.MinValue;

            // The broadcast normally arrives first, but make sure our own message is shown.
            if (channel.IsLoaded && channel.Insert(CreateMessage(sent)))
            {
                channel.RecomputeGrouping();
            }
        }
        catch (Exception ex)
        {
            foreach (var pending in PendingAttachments)
            {
                pending.IsUploading = false;
            }

            ShowBanner($"Message not sent: {Describe(ex)}");
        }
        finally
        {
            IsSending = false;
        }
    }

    /// <summary>Sends a sticker (a message holding nothing but its <c>:name:</c>) to the selected channel.</summary>
    private async Task SendStickerAsync(string token)
    {
        var channel = SelectedChannel;
        if (channel is null)
        {
            return;
        }

        try
        {
            var sent = await _hub.SendMessageAsync(channel.Id, token, []);
            if (channel.IsLoaded && channel.Insert(CreateMessage(sent)))
            {
                channel.RecomputeGrouping();
            }
        }
        catch (Exception ex)
        {
            ShowBanner($"Sticker not sent: {Describe(ex)}");
        }
    }

    [RelayCommand]
    private async Task AttachFilesAsync()
    {
        var files = await _dialogs.PickFilesAsync();
        await AddAttachmentsAsync(files);
    }

    public async Task AddAttachmentsAsync(IEnumerable<IStorageFile> files)
    {
        foreach (var file in files)
        {
            if (PendingAttachments.Count >= Limits.MaxAttachmentsPerMessage)
            {
                ShowBanner($"You can attach at most {Limits.MaxAttachmentsPerMessage} files per message.");
                break;
            }

            var properties = await file.GetBasicPropertiesAsync();
            var size = (long)(properties.Size ?? 0);
            PendingAttachments.Add(new PendingAttachmentViewModel(file.Name, size, () => file.OpenReadAsync(), GuessContentType(file.Name)));
        }

        OnPropertyChanged(nameof(HasPendingAttachments));
    }

    /// <summary>Attaches an image pasted from the clipboard (already encoded as PNG).</summary>
    public void AddPastedImage(byte[] png)
    {
        if (PendingAttachments.Count >= Limits.MaxAttachmentsPerMessage)
        {
            ShowBanner($"You can attach at most {Limits.MaxAttachmentsPerMessage} files per message.");
            return;
        }

        var name = $"image-{DateTime.Now:yyyyMMdd-HHmmss}.png";
        PendingAttachments.Add(new PendingAttachmentViewModel(name, png.Length, () => Task.FromResult<Stream>(new MemoryStream(png)), "image/png"));
        OnPropertyChanged(nameof(HasPendingAttachments));
    }

    [RelayCommand]
    private void RemoveAttachment(PendingAttachmentViewModel attachment)
    {
        PendingAttachments.Remove(attachment);
        OnPropertyChanged(nameof(HasPendingAttachments));
    }

    private static string? GuessContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".txt" => "text/plain",
        ".pdf" => "application/pdf",
        _ => null,
    };

    [RelayCommand]
    private void BeginEdit(MessageItemViewModel message)
    {
        foreach (var other in SelectedChannel?.Messages.Where(m => m.IsEditing) ?? [])
        {
            other.IsEditing = false;
        }

        message.EditText = message.Content;
        message.IsEditing = true;
    }

    /// <summary>Starts editing the most recent own message (Up arrow in an empty composer).</summary>
    public bool EditLastOwnMessage()
    {
        var last = SelectedChannel?.Messages.LastOrDefault(m => m.IsMine);
        if (last is null)
        {
            return false;
        }

        BeginEdit(last);
        return true;
    }

    [RelayCommand]
    private void CancelEdit(MessageItemViewModel message) => message.IsEditing = false;

    [RelayCommand]
    private async Task SaveEditAsync(MessageItemViewModel message)
    {
        var text = message.EditText.Trim();
        message.IsEditing = false;
        if (text == message.Content)
        {
            return;
        }

        if (text.Length == 0 && !message.HasAttachments)
        {
            await DeleteMessageAsync(message);
            return;
        }

        await SafeAsync(() => _hub.EditMessageAsync(message.Id, text), "Could not edit the message");
    }

    [RelayCommand]
    private async Task DeleteMessageAsync(MessageItemViewModel message)
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "Delete message",
            "Are you sure you want to delete this message? This cannot be undone.",
            "Delete",
            danger: true);
        if (confirmed)
        {
            await SafeAsync(() => _hub.DeleteMessageAsync(message.Id), "Could not delete the message");
        }
    }

    [RelayCommand]
    private Task CreateChannelAsync() => CreateChannelOfKindAsync(ChannelKind.Text);

    [RelayCommand]
    private Task CreateVoiceChannelAsync() => CreateChannelOfKindAsync(ChannelKind.Voice);

    private async Task CreateChannelOfKindAsync(ChannelKind kind)
    {
        var result = await _dialogs.EditChannelAsync(ChannelEditorViewModel.ForCreate(kind));
        if (result is null)
        {
            return;
        }

        try
        {
            if (SelectedRoom is not { } room)
            {
                return;
            }

            var channel = await _hub.CreateChannelAsync(room.Id, result.Name, result.Topic, result.Kind);
            AddOrUpdateChannel(channel);
            if (channel.Kind == ChannelKind.Text)
            {
                SelectedChannel = FindChannel(channel.Id);
            }
        }
        catch (Exception ex)
        {
            ShowBanner($"Could not create the channel: {Describe(ex)}");
        }
    }

    [RelayCommand]
    private async Task EditChannelAsync(ChannelViewModel channel)
    {
        var result = await _dialogs.EditChannelAsync(ChannelEditorViewModel.ForEdit(ChannelKind.Text, channel.Name, channel.Topic));
        if (result is not null)
        {
            await SafeAsync(() => _hub.UpdateChannelAsync(channel.Id, result.Name, result.Topic), "Could not update the channel");
        }
    }

    [RelayCommand]
    private async Task DeleteChannelAsync(ChannelViewModel channel)
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "Delete channel",
            $"Delete #{channel.Name} and all of its messages? This cannot be undone.",
            "Delete channel",
            danger: true);
        if (confirmed)
        {
            await SafeAsync(() => _hub.DeleteChannelAsync(channel.Id), "Could not delete the channel");
        }
    }

    [RelayCommand]
    private async Task EditVoiceChannelAsync(VoiceChannelViewModel channel)
    {
        var result = await _dialogs.EditChannelAsync(ChannelEditorViewModel.ForEdit(ChannelKind.Voice, channel.Name, channel.Topic));
        if (result is not null)
        {
            await SafeAsync(() => _hub.UpdateChannelAsync(channel.Id, result.Name, result.Topic), "Could not update the channel");
        }
    }

    [RelayCommand]
    private async Task DeleteVoiceChannelAsync(VoiceChannelViewModel channel)
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "Delete channel",
            $"Delete the voice channel {channel.Name}? Everyone in it will be disconnected.",
            "Delete channel",
            danger: true);
        if (confirmed)
        {
            await SafeAsync(() => _hub.DeleteChannelAsync(channel.Id), "Could not delete the channel");
        }
    }

    /// <summary>The window regained focus: the visible channel has now been read.</summary>
    public void OnWindowActivated()
    {
        SelectedChannel?.MarkRead();
        OnPropertyChanged(nameof(WindowTitle));
    }

    [RelayCommand]
    private void ToggleMembers() => ShowMembers = !ShowMembers;

    [RelayCommand]
    private void DismissBanner() => BannerMessage = null;

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var settings = new SettingsViewModel(_session.Api, _session.Settings, Me, ServerName, auth =>
        {
            _session.Api.Token = auth.Token;
            _session.Settings.Token = auth.Token;
            _session.Settings.Save();
        }, () => _hub.ConnectionId, _voice, _dialogs, () => CheckForUpdatesAsync(manual: true));
        var logout = false;
        settings.LogoutRequested += () => logout = true;
        try
        {
            await _dialogs.ShowSettingsAsync(settings);
        }
        finally
        {
            settings.Dispose();
        }

        if (logout)
        {
            SignOut(null);
        }
    }

    [RelayCommand]
    private void Logout() => SignOut(null);

    private void SignOut(string? reason)
    {
        _session.Settings.Token = null;
        _session.Settings.Save();
        LoggedOut?.Invoke(reason);
    }

    // ---- Updates ---------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateText), nameof(UpdateActionText), nameof(CanInstallUpdate))]
    private AvailableUpdate? _availableUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateText))]
    private bool _isInstallingUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateText))]
    private double _updateProgress;

    public bool HasUpdate => AvailableUpdate is not null;

    /// <summary>Installed with the Windows installer and the release has one: the update installs itself.</summary>
    public bool CanInstallUpdate => UpdateService.CanInstall && AvailableUpdate?.InstallerUrl is not null;

    public string UpdateText => AvailableUpdate is not { } update
        ? ""
        : IsInstallingUpdate
            ? $"Downloading Discagem {update.Version}… {UpdateProgress:0}%"
            : $"Discagem {update.Version} is available (you have {UpdateService.CurrentVersion}).";

    public string UpdateActionText => CanInstallUpdate ? "Update now" : "Download";

    private async Task CheckForUpdatesAutomaticallyAsync()
    {
        if (!_disposed && _session.Settings.CheckForUpdates && !IsInstallingUpdate)
        {
            await CheckForUpdatesAsync(manual: false);
        }
    }

    /// <summary>Looks for a newer release and shows it in the update bar. Returns a status line for the settings dialog.</summary>
    public async Task<string> CheckForUpdatesAsync(bool manual)
    {
        try
        {
            var update = await _updates.CheckAsync();
            if (update is null)
            {
                return $"You have the latest version ({UpdateService.CurrentVersion}).";
            }

            // "Later" hides a version until the user asks again or a newer one comes out.
            if (manual || _dismissedUpdate != update.Version)
            {
                AvailableUpdate = update;
            }

            return $"Discagem {update.Version} is available.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return $"Could not check for updates: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (AvailableUpdate is not { } update || IsInstallingUpdate)
        {
            return;
        }

        if (!CanInstallUpdate)
        {
            UpdateService.OpenInBrowser(update.PageUrl);
            return;
        }

        if ((IsInVoice || IsLive) && !await _dialogs.ConfirmAsync(
                "Update Discagem",
                "Updating restarts Discagem, which ends your call and your screen share. Update now?",
                "Update now",
                danger: false))
        {
            return;
        }

        IsInstallingUpdate = true;
        UpdateProgress = 0;
        try
        {
            var path = await _updates.DownloadInstallerAsync(update, new Progress<double>(fraction => UpdateProgress = fraction * 100));
            UpdateService.LaunchInstaller(path);
            App.Quit();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            IsInstallingUpdate = false;
            ShowBanner($"Could not update Discagem: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ShowReleaseNotes()
    {
        if (AvailableUpdate is { } update)
        {
            UpdateService.OpenInBrowser(update.PageUrl);
        }
    }

    [RelayCommand]
    private void DismissUpdate()
    {
        _dismissedUpdate = AvailableUpdate?.Version;
        AvailableUpdate = null;
    }

    // ---- Screen sharing -------------------------------------------------------------------

    private bool EnsureCodecAvailable()
    {
        if (VpxLibrary.IsAvailable)
        {
            return true;
        }

        ShowBanner(OperatingSystem.IsWindows()
            ? "Screen sharing needs the Microsoft Visual C++ Redistributable (x64): install it from https://aka.ms/vs/17/release/vc_redist.x64.exe and restart Discagem."
            : "Screen sharing needs libvpx: install it with your package manager (for example: sudo apt install libvpx9) and restart Discagem.");
        return false;
    }

    [RelayCommand]
    private async Task GoLiveAsync()
    {
        if (IsLive || IsStartingStream || !EnsureCodecAvailable())
        {
            return;
        }

        var picker = new ScreenPickerViewModel(
            $"{Me.DisplayName}'s screen",
            _session.Settings.StreamQualityIndex,
            _media.CanShareAudio,
            _voice.Audio.SupportsApplicationCapture,
            _session.Settings.StreamShareAudio);
        var selection = await _dialogs.PickScreenAsync(picker);
        if (selection is null)
        {
            return;
        }

        _session.Settings.StreamQualityIndex = Array.IndexOf(StreamQuality.Presets, selection.Quality);
        if (_media.CanShareAudio)
        {
            _session.Settings.StreamShareAudio = selection.ShareAudio;
        }

        _session.Settings.Save();

        IsStartingStream = true;
        LiveStatsText = "Starting…";
        LiveSoundText = "";
        try
        {
            await _media.StartAsync(selection.Source, selection.Quality, selection.Title, selection.ShareAudio, SelectedRoom?.Id);
            IsLive = true;
        }
        catch (Exception ex)
        {
            ShowBanner($"Could not start screen sharing: {Describe(ex)}");
        }
        finally
        {
            IsStartingStream = false;
        }
    }

    [RelayCommand]
    private async Task StopLiveAsync()
    {
        IsLive = false;
        await SafeAsync(_media.StopAsync, "Could not stop the stream");
    }

    [RelayCommand]
    private async Task WatchAsync(LiveStreamViewModel stream)
    {
        if (stream.IsMine || !EnsureCodecAvailable())
        {
            return;
        }

        if (ActiveStream?.StreamId == stream.StreamId)
        {
            return;
        }

        if (ActiveStream is not null)
        {
            CloseStreamViewer(ActiveStream, null);
        }

        var viewer = new StreamViewerViewModel(stream, _session.Settings) { IsDeafened = IsDeafened };
        viewer.CloseRequested += CloseStreamViewer;
        ActiveStream = viewer;
        try
        {
            var session = await _media.WatchAsync(stream.StreamId);
            if (ActiveStream != viewer)
            {
                session.Dispose();
                return;
            }

            session.AudioProblem += message => Dispatcher.UIThread.Post(() => ShowBanner(message));
            viewer.Attach(session);
        }
        catch (Exception ex)
        {
            CloseStreamViewer(viewer, $"Could not watch the stream: {Describe(ex)}");
        }
    }

    [RelayCommand]
    private void WatchMember(MemberViewModel member)
    {
        var stream = LiveStreams.FirstOrDefault(s => s.Streamer == member) ?? AllStreams.FirstOrDefault(s => s.Streamer == member);
        if (stream is not null)
        {
            WatchCommand.Execute(stream);
        }
    }

    private void CloseStreamViewer(StreamViewerViewModel viewer, string? message)
    {
        viewer.CloseRequested -= CloseStreamViewer;
        viewer.Dispose();
        if (ActiveStream == viewer)
        {
            ActiveStream = null;
        }

        if (message is not null)
        {
            ShowBanner(message);
        }
    }

    private void AddOrUpdateStream(StreamInfoDto dto)
    {
        var existing = AllStreams.FirstOrDefault(s => s.StreamId == dto.StreamId);
        if (existing is not null)
        {
            existing.Update(dto);
            return;
        }

        if (!_members.TryGetValue(dto.UserId, out var streamer) || FindRoom(dto.RoomId) is not { } room)
        {
            return;
        }

        // A user only has one stream; drop any stale entry.
        foreach (var other in Rooms)
        {
            foreach (var stale in other.LiveStreams.Where(s => s.Streamer == streamer).ToList())
            {
                other.LiveStreams.Remove(stale);
            }
        }

        streamer.IsLive = true;
        room.LiveStreams.Add(new LiveStreamViewModel(dto, streamer));
    }

    private void OnStreamEnded(string streamId)
    {
        var stream = AllStreams.FirstOrDefault(s => s.StreamId == streamId);
        if (stream is not null)
        {
            FindRoom(stream.RoomId)?.LiveStreams.Remove(stream);
            stream.Streamer.IsLive = AllStreams.Any(s => s.Streamer == stream.Streamer);
        }

        if (ActiveStream?.StreamId == streamId)
        {
            var viewer = ActiveStream;
            CloseStreamViewer(viewer, null);
            ShowBanner(stream is null ? "The stream has ended." : $"{stream.Streamer.DisplayName}'s stream has ended.", isError: false);
        }

        if (IsLive && _media.LiveStreamId == streamId)
        {
            IsLive = false;
            _ = _media.StopAsync();
        }
    }

    // ---- Rooms ----------------------------------------------------------------------------

    private RoomViewModel AddOrUpdateRoom(RoomDto dto)
    {
        if (FindRoom(dto.Id) is { } existing)
        {
            existing.Update(dto);
            return existing;
        }

        // Rooms go where the user put them; new ones at the end.
        var room = new RoomViewModel(dto, _roomIcons);
        var order = _session.Settings.RoomOrder;
        int Rank(RoomViewModel r) => order.IndexOf(r.Id) is var i and >= 0 ? i : int.MaxValue;
        var index = 0;
        while (index < Rooms.Count && (Rank(Rooms[index]), Rooms[index].Id).CompareTo((Rank(room), room.Id)) < 0)
        {
            index++;
        }

        Rooms.Insert(index, room);
        return room;
    }

    /// <summary>A room the user just created or joined (here or on another device).</summary>
    private RoomViewModel AddRoomState(RoomStateDto state, bool select)
    {
        var room = AddOrUpdateRoom(state.Room);
        foreach (var channel in state.Channels)
        {
            AddOrUpdateChannel(channel);
        }

        foreach (var participant in state.VoiceParticipants)
        {
            OnVoiceStateChanged(participant, announce: false);
        }

        foreach (var stream in state.Streams)
        {
            AddOrUpdateStream(stream);
        }

        if (select || SelectedRoom is null)
        {
            SelectedRoom = room;
        }

        return room;
    }

    private void OnRoomUpdated(RoomDto dto)
    {
        if (FindRoom(dto.Id) is not { } room)
        {
            return;
        }

        room.Update(dto);
        if (room == SelectedRoom)
        {
            SortMembers();
            OnPropertyChanged(nameof(RoomName));
            OnPropertyChanged(nameof(CanManageRoom));
            OnPropertyChanged(nameof(WindowTitle));
        }
    }

    /// <summary>Forgets a room the user left, was removed from, or that was deleted.</summary>
    private void RemoveRoom(long roomId, bool announce)
    {
        if (FindRoom(roomId) is not { } room)
        {
            return;
        }

        if (ConnectedVoiceChannel is { } voice && voice.RoomId == roomId)
        {
            // The server hangs up too; make sure no call is left in a room the user is no longer in.
            SetConnectedVoice(null);
            _ = SafeAsync(() => _voice.LeaveAsync(voice.Id));
        }

        if (ActiveStream is { } viewer && room.LiveStreams.Any(s => s.StreamId == viewer.StreamId))
        {
            CloseStreamViewer(viewer, null);
        }

        var index = Rooms.IndexOf(room);
        Rooms.Remove(room);
        foreach (var stream in room.LiveStreams)
        {
            stream.Streamer.IsLive = AllStreams.Any(s => s.Streamer == stream.Streamer);
        }

        if (room == SelectedRoom)
        {
            SelectedRoom = Rooms.Count == 0 ? null : Rooms[Math.Min(index, Rooms.Count - 1)];
        }

        OnPropertyChanged(nameof(WindowTitle));
        if (announce)
        {
            ShowBanner($"You are no longer in {room.Name}.", isError: false);
        }
    }

    [RelayCommand]
    private void SelectRoom(RoomViewModel room) => SelectedRoom = room;

    /// <summary>Creates a room, or joins one with an invite code.</summary>
    [RelayCommand]
    private async Task AddRoomAsync(string? mode)
    {
        var entry = new RoomEntryViewModel(mode == "join");
        if (!await _dialogs.ShowRoomEntryAsync(entry))
        {
            return;
        }

        try
        {
            var state = entry.IsJoin
                ? await _hub.JoinRoomAsync(entry.InviteCode.Trim())
                : await _hub.CreateRoomAsync(entry.Name.Trim(), entry.Color);
            AddRoomState(state, select: true);
            BannerMessage = null;
        }
        catch (Exception ex)
        {
            ShowBanner(entry.IsJoin ? $"Could not join the room: {Describe(ex)}" : $"Could not create the room: {Describe(ex)}");
        }
    }

    [RelayCommand]
    private async Task CopyInviteAsync(RoomViewModel? room)
    {
        room ??= SelectedRoom;
        if (room is null || string.IsNullOrEmpty(room.InviteCode))
        {
            return;
        }

        var address = _session.Api.BaseAddress.ToString().TrimEnd('/');
        await _dialogs.CopyTextAsync(RoomInvite.Text(room.Name, address, room.InviteCode));
        ShowBanner($"Invite to {room.Name} copied. Send it to whoever you want to invite.", isError: false);
    }

    [RelayCommand]
    private async Task OpenRoomSettingsAsync(RoomViewModel? room)
    {
        room ??= SelectedRoom;
        if (room is null)
        {
            return;
        }

        using var settings = new RoomSettingsViewModel(room, _hub, _session.Api, _dialogs, _members, Me, message => ShowBanner(message));
        await _dialogs.ShowRoomSettingsAsync(settings);
    }

    [RelayCommand]
    private async Task LeaveRoomAsync(RoomViewModel? room)
    {
        room ??= SelectedRoom;
        if (room is null)
        {
            return;
        }

        var last = room.MemberCount <= 1;
        var confirmed = await _dialogs.ConfirmAsync(
            "Leave room",
            last
                ? $"You are the last person in {room.Name}. Leaving deletes it, with all of its messages."
                : $"Leave {room.Name}? You will need a new invite to come back.",
            last ? "Leave and delete" : "Leave room",
            danger: true);
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _hub.LeaveRoomAsync(room.Id);
            RemoveRoom(room.Id, announce: false);
        }
        catch (Exception ex)
        {
            ShowBanner($"Could not leave the room: {Describe(ex)}");
        }
    }

    [RelayCommand]
    private void MoveRoomUp(RoomViewModel room) => MoveRoom(room, -1);

    [RelayCommand]
    private void MoveRoomDown(RoomViewModel room) => MoveRoom(room, +1);

    /// <summary>Moves a room in the rail; the order is the user's own.</summary>
    private void MoveRoom(RoomViewModel room, int offset)
    {
        var from = Rooms.IndexOf(room);
        var to = from + offset;
        if (from < 0 || to < 0 || to >= Rooms.Count)
        {
            return;
        }

        Rooms.Move(from, to);
        _session.Settings.RoomOrder = Rooms.Select(r => r.Id).ToList();
        _session.Settings.Save();
    }

    [RelayCommand]
    private Task MoveChannelUpAsync(ChannelViewModel channel) =>
        MoveChannelAsync(channel.Id, Channels.IndexOf(channel) - 1);

    [RelayCommand]
    private Task MoveChannelDownAsync(ChannelViewModel channel) =>
        MoveChannelAsync(channel.Id, Channels.IndexOf(channel) + 1);

    [RelayCommand]
    private Task MoveVoiceChannelUpAsync(VoiceChannelViewModel channel) =>
        MoveChannelAsync(channel.Id, VoiceChannels.IndexOf(channel) - 1);

    [RelayCommand]
    private Task MoveVoiceChannelDownAsync(VoiceChannelViewModel channel) =>
        MoveChannelAsync(channel.Id, VoiceChannels.IndexOf(channel) + 1);

    private Task MoveChannelAsync(long channelId, int position) =>
        position < 0 ? Task.CompletedTask : SafeAsync(() => _hub.MoveChannelAsync(channelId, position), "Could not move the channel");

    // ---- Voice ----------------------------------------------------------------------------

    [RelayCommand]
    private async Task JoinVoiceAsync(VoiceChannelViewModel channel)
    {
        if (IsJoiningVoice || ConnectedVoiceChannel == channel)
        {
            return;
        }

        await JoinVoiceCoreAsync(channel);
    }

    private async Task JoinVoiceCoreAsync(VoiceChannelViewModel channel)
    {
        SetConnectedVoice(channel);
        IsJoiningVoice = true;
        try
        {
            await _voice.JoinAsync(channel.Id);
        }
        catch (Exception ex)
        {
            if (ConnectedVoiceChannel == channel)
            {
                SetConnectedVoice(null);
            }

            ShowBanner($"Could not join {channel.Name}: {Describe(ex)}");
        }
        finally
        {
            IsJoiningVoice = false;
        }
    }

    /// <summary>
    /// The voice connection was lost: keep the channel selected and rejoin as soon as the chat
    /// connection is up, retrying for about a minute. Reconnecting the chat connection wakes it up.
    /// </summary>
    private async Task RecoverVoiceAsync()
    {
        if (_recoveringVoice)
        {
            return;
        }

        _recoveringVoice = true;
        IsJoiningVoice = true;
        try
        {
            for (var attempt = 0; attempt < 8 && ConnectedVoiceChannel is { } target; attempt++)
            {
                if (Connection == ConnectionState.Connected)
                {
                    if (FindVoiceChannel(target.Id) is not { } channel)
                    {
                        break;
                    }

                    try
                    {
                        await _voice.JoinAsync(channel.Id);
                        if (ConnectedVoiceChannel?.Id != channel.Id)
                        {
                            // The user hung up (and maybe joined elsewhere) while we were reconnecting:
                            // end this call only, not a newer one.
                            await SafeAsync(() => _voice.LeaveAsync(channel.Id));
                        }
                        else if (ConnectedVoiceChannel != channel)
                        {
                            SetConnectedVoice(channel);
                        }

                        return;
                    }
                    catch (HubException ex)
                    {
                        // The server refused (the channel is gone or full): retrying will not help.
                        SetConnectedVoice(null);
                        ShowBanner($"Could not rejoin {channel.Name}: {Describe(ex)}");
                        return;
                    }
                    catch (Exception)
                    {
                        // Try again below.
                    }
                }

                _voiceRecoveryWake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(Math.Min(2 << attempt, 15))), _voiceRecoveryWake.Task);
            }

            if (ConnectedVoiceChannel is { } lost)
            {
                SetConnectedVoice(null);
                ShowBanner($"Could not reconnect to {lost.Name}.");
            }
        }
        finally
        {
            _recoveringVoice = false;
            IsJoiningVoice = false;
        }
    }

    [RelayCommand]
    private async Task LeaveVoiceAsync()
    {
        SetConnectedVoice(null);
        IsJoiningVoice = false;
        await SafeAsync(() => _voice.LeaveAsync());
    }

    [RelayCommand]
    private async Task ToggleMuteAsync()
    {
        // As in Discord, unmuting while deafened also undeafens.
        if (IsDeafened)
        {
            IsDeafened = false;
            IsMuted = false;
            await _voice.SetMutedAsync(false);
            await _voice.SetDeafenedAsync(false);
            return;
        }

        IsMuted = !IsMuted;
        await _voice.SetMutedAsync(IsMuted);
    }

    [RelayCommand]
    private async Task ToggleDeafenAsync()
    {
        IsDeafened = !IsDeafened;
        await _voice.SetDeafenedAsync(IsDeafened);
    }

    partial void OnConnectedVoiceChannelChanged(VoiceChannelViewModel? value) => Soundboard.UpdateState(value is not null, IsMicrophoneOff);

    partial void OnIsMutedChanged(bool value) => Soundboard.UpdateState(IsInVoice, IsMicrophoneOff);

    partial void OnIsDeafenedChanged(bool value)
    {
        Soundboard.UpdateState(IsInVoice, IsMicrophoneOff);
        if (ActiveStream is { } viewer)
        {
            viewer.IsDeafened = value;
        }
    }

    private void SetConnectedVoice(VoiceChannelViewModel? channel)
    {
        ConnectedVoiceChannel = channel;
        foreach (var voiceChannel in AllVoiceChannels)
        {
            voiceChannel.IsConnected = voiceChannel == channel;
            foreach (var participant in voiceChannel.Participants)
            {
                participant.IsSpeaking = false;
            }
        }

        IsMeSpeaking = false;
    }

    private void OnVoiceStateChanged(VoiceParticipantDto dto, bool announce)
    {
        var target = FindVoiceChannel(dto.ChannelId);
        if (target is null || !_members.TryGetValue(dto.UserId, out var member))
        {
            return;
        }

        // A user is in one voice channel at a time.
        foreach (var other in AllVoiceChannels.Where(c => c != target))
        {
            if (other.Find(dto.UserId) is { } moved)
            {
                other.Participants.Remove(moved);
            }
        }

        var participant = target.Find(dto.UserId);
        if (participant is null)
        {
            participant = new VoiceParticipantViewModel(
                member,
                target,
                _voice.GetUserVolume(member.Id) * 100,
                (userId, percent) => _voice.SetUserVolume(userId, (float)(percent / 100)));
            var index = 0;
            while (index < target.Participants.Count &&
                   string.CompareOrdinal(target.Participants[index].Member.SortKey, member.SortKey) < 0)
            {
                index++;
            }

            target.Participants.Insert(index, participant);
            if (announce)
            {
                _voice.OnParticipantJoined(dto.ChannelId, dto.UserId);
            }
        }

        participant.Apply(dto);
    }

    private void OnVoiceLeft(long channelId, long userId)
    {
        var channel = FindVoiceChannel(channelId);
        if (channel?.Find(userId) is { } participant)
        {
            channel.Participants.Remove(participant);
        }

        _voice.OnParticipantLeft(channelId, userId);
    }

    private void OnSpeakingChanged(long userId, bool speaking)
    {
        if (userId == Me.Id)
        {
            IsMeSpeaking = speaking && IsInVoice;
        }

        if (ConnectedVoiceChannel?.Find(userId) is { } participant)
        {
            participant.IsSpeaking = speaking;
        }
    }

    // ---- Helpers ---------------------------------------------------------------------------

    public void ShowBanner(string message, bool isError = true)
    {
        BannerIsError = isError;
        BannerMessage = message;
    }

    private async Task SafeAsync(Func<Task> action, string? context = null)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            if (context is not null)
            {
                ShowBanner($"{context}: {Describe(ex)}");
            }
        }
    }

    private static string Describe(Exception ex) => HubErrors.Describe(ex);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (EmojiCatalog.Current == _emojiCatalog)
            {
                EmojiCatalog.Current = null;
            }

            _typingTimer.Stop();
            _tokenRefreshTimer.Stop();
            _updateTimer.Stop();
        });
        _updates.Dispose();
        ActiveStream?.Dispose();
        await _voice.DisposeAsync();
        await _media.DisposeAsync();
        try
        {
            await _hub.StopAsync();
        }
        catch (Exception)
        {
        }

        await _hub.DisposeAsync();
    }
}
