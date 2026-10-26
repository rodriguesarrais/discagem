using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Discagem.Client.Services;
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
    private readonly ImageCache _images;
    private readonly Dictionary<long, MemberViewModel> _members = new();
    private readonly Dictionary<(long Channel, long User), DateTime> _typing = new();
    private readonly DispatcherTimer _typingTimer;
    private readonly DispatcherTimer _tokenRefreshTimer;
    private DateTime _lastTypingSent = DateTime.MinValue;
    private bool _disposed;

    public ShellViewModel(ClientSession session, IDialogService dialogs)
    {
        _session = session;
        _dialogs = dialogs;
        _images = new ImageCache(session.Api);
        _me = GetOrAddMember(session.User, isMe: true);
        _showMembers = session.Settings.ShowMembers;

        _hub = new ChatConnection(session.Api.BaseAddress, () => _session.Api.Token);
        _media = new ScreenShareService(_hub, BuildIceServers(session));
        _typingTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateTypingText());
        // Tokens last for weeks; refresh them so long-running sessions never reconnect with an expired one.
        _tokenRefreshTimer = new DispatcherTimer(TimeSpan.FromHours(6), DispatcherPriority.Background, async (_, _) => await RefreshTokenAsync());

        WireHubEvents();
        _media.PublishStatsUpdated += stats => Dispatcher.UIThread.Post(() =>
            LiveStatsText = $"{stats.Height}p · {stats.FramesPerSecond:0} FPS · {stats.SentKbps / 1000:0.0} Mbps");
        _media.PublishStopped += reason => Dispatcher.UIThread.Post(() =>
        {
            IsLive = false;
            ShowBanner(reason);
        });
    }

    /// <summary>Raised when the user signs out or the session becomes invalid (with an optional message).</summary>
    public event Action<string?>? LoggedOut;

    public ObservableCollection<ChannelViewModel> Channels { get; } = [];

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
        });
        _hub.Reconnected += () => Dispatcher.UIThread.Post(async () =>
        {
            Connection = ConnectionState.Connected;
            await SafeAsync(SyncAsync);
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

    private void OnChannelDeleted(long id)
    {
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
    private async Task CreateChannelAsync()
    {
        var result = await _dialogs.EditChannelAsync(new ChannelEditorViewModel("Create channel", "Create channel"));
        if (result is null)
        {
            return;
        }

        try
        {
            var channel = await _hub.CreateChannelAsync(result.Name, result.Topic);
            AddOrUpdateChannel(channel);
            SelectedChannel = FindChannel(channel.Id);
        }
        catch (Exception ex)
        {
            ShowBanner($"Could not create the channel: {Describe(ex)}");
        }
    }

    [RelayCommand]
    private async Task EditChannelAsync(ChannelViewModel channel)
    {
        var result = await _dialogs.EditChannelAsync(new ChannelEditorViewModel("Edit channel", "Save changes", channel.Name, channel.Topic));
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
        });
        var logout = false;
        settings.LogoutRequested += () => logout = true;
        await _dialogs.ShowSettingsAsync(settings);
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

        var picker = new ScreenPickerViewModel($"{Me.DisplayName}'s screen", _session.Settings.StreamQualityIndex);
        var selection = await _dialogs.PickScreenAsync(picker);
        if (selection is null)
        {
            return;
        }

        _session.Settings.StreamQualityIndex = Array.IndexOf(StreamQuality.Presets, selection.Quality);
        _session.Settings.Save();

        IsStartingStream = true;
        LiveStatsText = "Starting…";
        try
        {
            await _media.StartAsync(selection.Source, selection.Quality, selection.Title);
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

        var viewer = new StreamViewerViewModel(stream);
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
        });
        ActiveStream?.Dispose();
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
