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
    private readonly ImageCache _images;
    private readonly Dictionary<long, MemberViewModel> _members = new();
    private readonly Dictionary<(long Channel, long User), DateTime> _typing = new();
    private readonly DispatcherTimer _typingTimer;
    private readonly DispatcherTimer _tokenRefreshTimer;
    private readonly UpdateService _updates = new();
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
        _images = new ImageCache(session.Api);
        _me = GetOrAddMember(session.User, isMe: true);
        _showMembers = session.Settings.ShowMembers;

        _hub = new ChatConnection(session.Api.BaseAddress, () => _session.Api.Token);
        var iceServers = BuildIceServers(session);
        var audio = AudioBackends.Create();
        _media = new ScreenShareService(_hub, iceServers, audio, () => session.Settings.VoiceOutputDevice);
        _voice = new VoiceService(_hub, iceServers, session.Settings, audio, session.User.Id);
        _isMuted = session.Settings.VoiceMuted;
        _isDeafened = session.Settings.VoiceDeafened;
        _typingTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateTypingText());
        // Tokens last for weeks; refresh them so long-running sessions never reconnect with an expired one.
        _tokenRefreshTimer = new DispatcherTimer(TimeSpan.FromHours(6), DispatcherPriority.Background, async (_, _) => await RefreshTokenAsync());
        _updateTimer = new DispatcherTimer(TimeSpan.FromHours(6), DispatcherPriority.Background, async (_, _) => await CheckForUpdatesAutomaticallyAsync());

        WireHubEvents();
        _media.PublishStatsUpdated += stats => Dispatcher.UIThread.Post(() =>
            LiveStatsText = $"{stats.Height}p · {stats.FramesPerSecond:0} FPS · {stats.SentKbps / 1000:0.0} Mbps");
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

    public ObservableCollection<ChannelViewModel> Channels { get; } = [];

    public ObservableCollection<VoiceChannelViewModel> VoiceChannels { get; } = [];

    public ObservableCollection<MemberViewModel> OnlineMembers { get; } = [];

    public ObservableCollection<MemberViewModel> OfflineMembers { get; } = [];

    public ObservableCollection<LiveStreamViewModel> LiveStreams { get; } = [];

    public ObservableCollection<PendingAttachmentViewModel> PendingAttachments { get; } = [];

    public string ServerName => _session.Server.Name;

    public string ServerInitials => Controls.AvatarView.Initials(_session.Server.Name);

    public bool IsAdmin => Me.IsAdmin;

    public string OnlineHeader => $"ONLINE — {OnlineMembers.Count}";

    public string OfflineHeader => $"OFFLINE — {OfflineMembers.Count}";

    public bool HasOfflineMembers => OfflineMembers.Count > 0;

    public bool HasLiveStreams => LiveStreams.Count > 0;

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
            var unread = Channels.Sum(c => c.MentionCount);
            var title = SelectedChannel is null ? $"Discagem — {ServerName}" : $"#{SelectedChannel.Name} — {ServerName}";
            return unread > 0 ? $"({unread}) {title}" : title;
        }
    }

    [ObservableProperty]
    private MemberViewModel _me;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private ChannelViewModel? _selectedChannel;

    [ObservableProperty]
    private string _composerText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTyping))]
    private string? _typingText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReconnecting), nameof(ConnectionText))]
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

    partial void OnSelectedChannelChanged(ChannelViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        value.MarkRead();
        _session.Settings.LastChannelId = value.Id;
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

        SortMembers();

        foreach (var channel in state.Channels)
        {
            AddOrUpdateChannel(channel);
        }

        foreach (var stale in Channels.Where(c => state.Channels.All(s => s.Id != c.Id)).ToList())
        {
            Channels.Remove(stale);
        }

        foreach (var stale in VoiceChannels.Where(c => state.Channels.All(s => s.Id != c.Id)).ToList())
        {
            VoiceChannels.Remove(stale);
        }

        foreach (var channel in VoiceChannels)
        {
            channel.Participants.Clear();
        }

        foreach (var participant in state.VoiceParticipants)
        {
            OnVoiceStateChanged(participant, announce: false);
        }

        // Anything may have changed while disconnected: reload history lazily.
        foreach (var channel in Channels)
        {
            channel.IsLoaded = false;
        }

        LiveStreams.Clear();
        foreach (var member in _members.Values)
        {
            member.IsLive = false;
        }

        foreach (var stream in state.Streams)
        {
            AddOrUpdateStream(stream);
        }

        OnPropertyChanged(nameof(IsAdmin));
        var preferred = SelectedChannel ?? Channels.FirstOrDefault(c => c.Id == _session.Settings.LastChannelId) ?? Channels.FirstOrDefault();
        if (preferred is not null)
        {
            if (SelectedChannel == preferred)
            {
                await LoadLatestAsync(preferred);
            }
            else
            {
                SelectedChannel = preferred;
            }
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
            channel.UnreadCount++;
            if (item.MentionsMe)
            {
                channel.MentionCount++;
                OnPropertyChanged(nameof(WindowTitle));
            }

            _dialogs.RequestAttention();
        }
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

    private ChannelViewModel? FindChannel(long id) => Channels.FirstOrDefault(c => c.Id == id);

    private void AddOrUpdateChannel(ChannelDto dto)
    {
        if (dto.Kind == ChannelKind.Voice)
        {
            AddOrUpdateVoiceChannel(dto);
            return;
        }

        var existing = FindChannel(dto.Id);
        if (existing is not null)
        {
            existing.Update(dto);
        }
        else
        {
            existing = new ChannelViewModel(dto);
            Channels.Add(existing);
        }

        var ordered = Channels.OrderBy(c => c.Position).ThenBy(c => c.Id).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = Channels.IndexOf(ordered[i]);
            if (current != i)
            {
                Channels.Move(current, i);
            }
        }

        OnPropertyChanged(nameof(WindowTitle));
    }

    private void AddOrUpdateVoiceChannel(ChannelDto dto)
    {
        var existing = VoiceChannels.FirstOrDefault(c => c.Id == dto.Id);
        if (existing is not null)
        {
            existing.Update(dto);
        }
        else
        {
            VoiceChannels.Add(new VoiceChannelViewModel(dto) { IsConnected = ConnectedVoiceChannel?.Id == dto.Id });
        }

        var ordered = VoiceChannels.OrderBy(c => c.Position).ThenBy(c => c.Id).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = VoiceChannels.IndexOf(ordered[i]);
            if (current != i)
            {
                VoiceChannels.Move(current, i);
            }
        }
    }

    private void OnChannelDeleted(long id)
    {
        var voiceChannel = VoiceChannels.FirstOrDefault(c => c.Id == id);
        if (voiceChannel is not null)
        {
            VoiceChannels.Remove(voiceChannel);
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
        Channels.Remove(channel);
        if (wasSelected)
        {
            SelectedChannel = Channels.FirstOrDefault();
        }
    }

    private MemberViewModel GetOrAddMember(UserDto user, bool isMe = false)
    {
        if (_members.TryGetValue(user.Id, out var existing))
        {
            return existing;
        }

        var member = new MemberViewModel(user, isMe || user.Id == _session.User.Id);
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
        Sync(OnlineMembers, _members.Values.Where(m => m.IsOnline).OrderBy(m => m.SortKey).ToList());
        Sync(OfflineMembers, _members.Values.Where(m => !m.IsOnline && m.Username != "unknown").OrderBy(m => m.SortKey).ToList());
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
            var channel = await _hub.CreateChannelAsync(result.Name, result.Topic, result.Kind);
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
        }, () => _hub.ConnectionId, _voice, () => CheckForUpdatesAsync(manual: true));
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
        try
        {
            await _media.StartAsync(selection.Source, selection.Quality, selection.Title, selection.ShareAudio);
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
        var stream = LiveStreams.FirstOrDefault(s => s.Streamer == member);
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
        var existing = LiveStreams.FirstOrDefault(s => s.StreamId == dto.StreamId);
        if (existing is not null)
        {
            existing.Update(dto);
            return;
        }

        if (!_members.TryGetValue(dto.UserId, out var streamer))
        {
            return;
        }

        // A user only has one stream; drop any stale entry.
        foreach (var stale in LiveStreams.Where(s => s.Streamer == streamer).ToList())
        {
            LiveStreams.Remove(stale);
        }

        streamer.IsLive = true;
        LiveStreams.Add(new LiveStreamViewModel(dto, streamer));
        OnPropertyChanged(nameof(HasLiveStreams));
    }

    private void OnStreamEnded(string streamId)
    {
        var stream = LiveStreams.FirstOrDefault(s => s.StreamId == streamId);
        if (stream is not null)
        {
            LiveStreams.Remove(stream);
            stream.Streamer.IsLive = LiveStreams.Any(s => s.Streamer == stream.Streamer);
            OnPropertyChanged(nameof(HasLiveStreams));
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
                    if (VoiceChannels.FirstOrDefault(c => c.Id == target.Id) is not { } channel)
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

    partial void OnIsDeafenedChanged(bool value)
    {
        if (ActiveStream is { } viewer)
        {
            viewer.IsDeafened = value;
        }
    }

    private void SetConnectedVoice(VoiceChannelViewModel? channel)
    {
        ConnectedVoiceChannel = channel;
        foreach (var voiceChannel in VoiceChannels)
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
        var target = VoiceChannels.FirstOrDefault(c => c.Id == dto.ChannelId);
        if (target is null || !_members.TryGetValue(dto.UserId, out var member))
        {
            return;
        }

        // A user is in one voice channel at a time.
        foreach (var other in VoiceChannels.Where(c => c != target))
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
        var channel = VoiceChannels.FirstOrDefault(c => c.Id == channelId);
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

    private static string Describe(Exception ex)
    {
        var message = ex switch
        {
            HubException hub => StripHubPrefix(hub.Message),
            ApiException api => api.Message,
            InvalidOperationException op => op.Message,
            _ => ex.Message,
        };
        return message.TrimEnd('.') + ".";

        static string StripHubPrefix(string text)
        {
            const string marker = "HubException: ";
            var index = text.IndexOf(marker, StringComparison.Ordinal);
            return index >= 0 ? text[(index + marker.Length)..] : text;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Dispatcher.UIThread.Post(() =>
        {
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
