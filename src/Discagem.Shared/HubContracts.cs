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

    /// <summary>A room the user is a member of changed (name, colour, icon, owner, invite code or members).</summary>
    Task RoomUpdated(RoomDto room);

    /// <summary>
    /// The user joined a room from another of their connections, or was added to one: everything
    /// needed to show it.
    /// </summary>
    Task RoomAdded(RoomStateDto room);

    /// <summary>The user is no longer in the room: they left it, were removed from it, or it was deleted.</summary>
    Task RoomRemoved(long roomId);

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

    /// <summary>Someone uploaded a custom emoji or sticker.</summary>
    Task EmojiCreated(EmojiDto emoji);

    Task EmojiDeleted(long emojiId);

    /// <summary>Someone uploaded a soundboard sound.</summary>
    Task SoundCreated(SoundDto sound);

    Task SoundDeleted(long soundId);

    /// <summary>
    /// Someone in the receiving client's voice channel played a soundboard sound; every client plays
    /// it locally. Sent only to the people in that channel, including the one who played it.
    /// </summary>
    Task SoundPlayed(long channelId, long userId, long soundId);
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

    Task<ChannelDto> CreateChannel(long roomId, string name, string topic, ChannelKind kind);
    Task UpdateChannel(long channelId, string name, string topic);
    Task DeleteChannel(long channelId);

    /// <summary>Moves a channel to a new position among its room's channels of the same kind.</summary>
    Task MoveChannel(long channelId, int position);

    /// <summary>Creates a room owned by the caller, with a text and a voice channel.</summary>
    Task<RoomStateDto> CreateRoom(string name, string color);

    /// <summary>Joins a room with its invite code (or a link that ends in it).</summary>
    Task<RoomStateDto> JoinRoom(string inviteCode);
    Task LeaveRoom(long roomId);
    Task UpdateRoom(long roomId, string name, string color);

    /// <summary>Replaces the room's invite code; the old one stops working.</summary>
    Task<string> ResetRoomInvite(long roomId);
    Task RemoveRoomMember(long roomId, long userId);
    Task TransferRoom(long roomId, long userId);
    Task DeleteRoom(long roomId);

    Task<MediaSessionAnswer> StartStream(StartStreamRequest request);
    Task StopStream();
    Task<MediaSessionAnswer> WatchStream(string streamId, string offerSdp);
    Task StopWatching(string sessionId);
    Task AddIceCandidate(IceCandidateDto candidate);

    /// <summary>Joins a voice channel (leaving any other one) with the client's WebRTC audio offer.</summary>
    Task<MediaSessionAnswer> JoinVoice(long channelId, string offerSdp);
    Task LeaveVoice();
    Task SetVoiceState(bool muted, bool deafened);

    /// <summary>Plays a soundboard sound to everyone in the caller's voice channel.</summary>
    Task PlaySound(long soundId);
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
    public const int MinEmojiNameLength = 2;
    public const int MaxEmojiNameLength = 32;
    public const int MaxEmojis = 200;
    public const int MaxStickers = 100;
    public const int MaxEmojiBytes = 256 * 1024;
    public const int MaxStickerBytes = 512 * 1024;
    public const int MaxSoundNameLength = 32;
    public const int MaxSounds = 100;
    public const int MaxSoundMilliseconds = 5000;
    public const int MaxSoundBytes = 1024 * 1024;
    public const int MaxRoomNameLength = 32;
    public const int MaxRoomsPerUser = 50;
    public const int MaxChannelsPerRoom = 100;
    public const int MaxRoomIconBytes = 1024 * 1024;
    public const int InviteCodeLength = 8;
}
