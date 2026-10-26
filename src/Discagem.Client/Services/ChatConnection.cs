using Discagem.Shared;
using Microsoft.AspNetCore.SignalR.Client;

namespace Discagem.Client.Services;

/// <summary>
/// Typed wrapper around the SignalR hub connection. Events are raised on background threads.
/// </summary>
public sealed class ChatConnection : IAsyncDisposable
{
    private readonly HubConnection _hub;

    public ChatConnection(Uri serverAddress, Func<string?> tokenProvider)
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(serverAddress, HubPaths.Chat.TrimStart('/')), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult(tokenProvider());
            })
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .Build();
        _hub.ServerTimeout = TimeSpan.FromSeconds(40);
        _hub.KeepAliveInterval = TimeSpan.FromSeconds(15);

        _hub.On<MessageDto>(nameof(IChatClient.MessageCreated), m => MessageCreated?.Invoke(m));
        _hub.On<MessageDto>(nameof(IChatClient.MessageUpdated), m => MessageUpdated?.Invoke(m));
        _hub.On<long, long>(nameof(IChatClient.MessageDeleted), (c, m) => MessageDeleted?.Invoke(c, m));
        _hub.On<ChannelDto>(nameof(IChatClient.ChannelCreated), c => ChannelCreated?.Invoke(c));
        _hub.On<ChannelDto>(nameof(IChatClient.ChannelUpdated), c => ChannelUpdated?.Invoke(c));
        _hub.On<long>(nameof(IChatClient.ChannelDeleted), c => ChannelDeleted?.Invoke(c));
        _hub.On<UserDto>(nameof(IChatClient.UserCreated), u => UserCreated?.Invoke(u));
        _hub.On<UserDto>(nameof(IChatClient.UserUpdated), u => UserUpdated?.Invoke(u));
        _hub.On<long, bool>(nameof(IChatClient.PresenceChanged), (u, online) => PresenceChanged?.Invoke(u, online));
        _hub.On<long, long>(nameof(IChatClient.UserTyping), (c, u) => UserTyping?.Invoke(c, u));
        _hub.On<StreamInfoDto>(nameof(IChatClient.StreamStarted), s => StreamStarted?.Invoke(s));
        _hub.On<StreamInfoDto>(nameof(IChatClient.StreamUpdated), s => StreamUpdated?.Invoke(s));
        _hub.On<string>(nameof(IChatClient.StreamEnded), s => StreamEnded?.Invoke(s));
        _hub.On<IceCandidateDto>(nameof(IChatClient.IceCandidate), c => IceCandidate?.Invoke(c));
        _hub.On<string, string>(nameof(IChatClient.MediaSessionClosed), (s, r) => MediaSessionClosed?.Invoke(s, r));

        _hub.Reconnecting += ex =>
        {
            Reconnecting?.Invoke(ex);
            return Task.CompletedTask;
        };
        _hub.Reconnected += _ =>
        {
            Reconnected?.Invoke();
            return Task.CompletedTask;
        };
        _hub.Closed += ex =>
        {
            Closed?.Invoke(ex);
            return Task.CompletedTask;
        };
    }

    public event Action<MessageDto>? MessageCreated;
    public event Action<MessageDto>? MessageUpdated;
    public event Action<long, long>? MessageDeleted;
    public event Action<ChannelDto>? ChannelCreated;
    public event Action<ChannelDto>? ChannelUpdated;
    public event Action<long>? ChannelDeleted;
    public event Action<UserDto>? UserCreated;
    public event Action<UserDto>? UserUpdated;
    public event Action<long, bool>? PresenceChanged;
    public event Action<long, long>? UserTyping;
    public event Action<StreamInfoDto>? StreamStarted;
    public event Action<StreamInfoDto>? StreamUpdated;
    public event Action<string>? StreamEnded;
    public event Action<IceCandidateDto>? IceCandidate;
    public event Action<string, string>? MediaSessionClosed;
    public event Action<Exception?>? Reconnecting;
    public event Action? Reconnected;
    public event Action<Exception?>? Closed;

    public HubConnectionState State => _hub.State;

    public Task StartAsync(CancellationToken cancellationToken = default) => _hub.StartAsync(cancellationToken);

    public Task StopAsync() => _hub.StopAsync();

    public Task<SyncStateDto> SyncAsync() => _hub.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync));

    public Task<IReadOnlyList<MessageDto>> GetMessagesAsync(long channelId, long? before, int limit) =>
        _hub.InvokeAsync<IReadOnlyList<MessageDto>>(nameof(IChatHub.GetMessages), channelId, before, limit);

    public Task<MessageDto> SendMessageAsync(long channelId, string content, long[] attachmentIds) =>
        _hub.InvokeAsync<MessageDto>(nameof(IChatHub.SendMessage), channelId, content, attachmentIds);

    public Task EditMessageAsync(long messageId, string content) =>
        _hub.InvokeAsync(nameof(IChatHub.EditMessage), messageId, content);

    public Task DeleteMessageAsync(long messageId) => _hub.InvokeAsync(nameof(IChatHub.DeleteMessage), messageId);

    public Task TypingAsync(long channelId) => _hub.SendAsync(nameof(IChatHub.Typing), channelId);

    public Task<ChannelDto> CreateChannelAsync(string name, string topic) =>
        _hub.InvokeAsync<ChannelDto>(nameof(IChatHub.CreateChannel), name, topic);

    public Task UpdateChannelAsync(long channelId, string name, string topic) =>
        _hub.InvokeAsync(nameof(IChatHub.UpdateChannel), channelId, name, topic);

    public Task DeleteChannelAsync(long channelId) => _hub.InvokeAsync(nameof(IChatHub.DeleteChannel), channelId);

    public Task<MediaSessionAnswer> StartStreamAsync(StartStreamRequest request) =>
        _hub.InvokeAsync<MediaSessionAnswer>(nameof(IChatHub.StartStream), request);

    public Task StopStreamAsync() => _hub.InvokeAsync(nameof(IChatHub.StopStream));

    public Task<MediaSessionAnswer> WatchStreamAsync(string streamId, string offerSdp) =>
        _hub.InvokeAsync<MediaSessionAnswer>(nameof(IChatHub.WatchStream), streamId, offerSdp);

    public Task StopWatchingAsync(string sessionId) => _hub.InvokeAsync(nameof(IChatHub.StopWatching), sessionId);

    public Task AddIceCandidateAsync(IceCandidateDto candidate) => _hub.SendAsync(nameof(IChatHub.AddIceCandidate), candidate);

    public ValueTask DisposeAsync() => _hub.DisposeAsync();

    /// <summary>Keeps trying to reconnect with a capped back-off.</summary>
    private sealed class ForeverRetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] Delays =
        [
            TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
        ];

        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            retryContext.PreviousRetryCount < Delays.Length ? Delays[retryContext.PreviousRetryCount] : TimeSpan.FromSeconds(15);
    }
}
