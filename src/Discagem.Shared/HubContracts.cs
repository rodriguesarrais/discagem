namespace Discagem.Shared;

/// <summary>
/// Methods the server invokes on connected clients through the SignalR hub.
/// The client registers handlers using these method names.
/// </summary>
public interface IChatClient
{
    Task MessageCreated(MessageDto message);
    Task MessageUpdated(MessageDto message);
    Task MessageDeleted(long channelId, long messageId);

    Task ChannelCreated(ChannelDto channel);
    Task ChannelUpdated(ChannelDto channel);
    Task ChannelDeleted(long channelId);

    Task UserCreated(UserDto user);
    Task UserUpdated(UserDto user);
    Task PresenceChanged(long userId, bool online);
    Task UserTyping(long channelId, long userId);

    Task StreamStarted(StreamInfoDto stream);
    Task StreamUpdated(StreamInfoDto stream);
    Task StreamEnded(string streamId);

    /// <summary>A user joined a voice channel or changed their mute/deafen state.</summary>
    Task VoiceStateChanged(VoiceParticipantDto participant);

    /// <summary>A user left a voice channel.</summary>
    Task VoiceLeft(long channelId, long userId);

    /// <summary>A server-side ICE candidate for one of the client's media sessions.</summary>
    Task IceCandidate(IceCandidateDto candidate);

    /// <summary>The server closed one of the client's media sessions (e.g. the stream ended).</summary>
    Task MediaSessionClosed(string sessionId, string reason);
}

/// <summary>
/// Methods clients invoke on the server hub. Kept as an interface so both sides
/// share the method names and signatures.
/// </summary>
public interface IChatHub
{
    Task<SyncStateDto> Sync();

    Task<IReadOnlyList<MessageDto>> GetMessages(long channelId, long? before, int limit);
    Task<MessageDto> SendMessage(long channelId, string content, long[] attachmentIds);
    Task EditMessage(long messageId, string content);
    Task DeleteMessage(long messageId);
    Task Typing(long channelId);

    Task<ChannelDto> CreateChannel(string name, string topic, ChannelKind kind);
    Task UpdateChannel(long channelId, string name, string topic);
    Task DeleteChannel(long channelId);

    Task<MediaSessionAnswer> StartStream(StartStreamRequest request);
    Task StopStream();
    Task<MediaSessionAnswer> WatchStream(string streamId, string offerSdp);
    Task StopWatching(string sessionId);
    Task AddIceCandidate(IceCandidateDto candidate);

    /// <summary>Joins a voice channel (leaving any other one) with the client's WebRTC audio offer.</summary>
    Task<MediaSessionAnswer> JoinVoice(long channelId, string offerSdp);
    Task LeaveVoice();
    Task SetVoiceState(bool muted, bool deafened);
}

public static class HubPaths
{
    public const string Chat = "/hubs/chat";
}

public static class Limits
{
    public const int MaxMessageLength = 4000;
    public const int MaxChannelNameLength = 48;
    public const int MaxChannelTopicLength = 256;
    public const int MaxDisplayNameLength = 32;
    public const int MinUsernameLength = 3;
    public const int MaxUsernameLength = 24;
    public const int MinPasswordLength = 8;
    public const int MaxAttachmentsPerMessage = 10;
    public const int MaxStreamTitleLength = 64;
    public const int MessagePageSize = 50;
    public const int MaxVoiceParticipants = 25;
}
