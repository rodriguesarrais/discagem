namespace Discagem.Shared;

/// <summary>
/// Public representation of a user account. <c>AvatarVersion</c> is null without a profile photo;
/// otherwise the photo is at <c>/api/users/{id}/avatar</c>, and the number changes with every new photo.
/// The profile fields are empty (or null for the colours) when not set, and from older servers.
/// </summary>
/// <param name="StatusText">Custom status shown under the name, such as "Playing Minecraft".</param>
/// <param name="StatusEmoji">An emoji shown before the custom status.</param>
/// <param name="NameColor">Colour of the display name in messages and lists (#RRGGBB), or null for the theme's.</param>
/// <param name="BannerColor">Colour of the profile card's banner (#RRGGBB), or null for the avatar colour.</param>
public sealed record UserDto(
    long Id,
    string Username,
    string DisplayName,
    string AvatarColor,
    bool IsAdmin,
    DateTimeOffset CreatedAt,
    int? AvatarVersion = null,
    string Bio = "",
    string StatusText = "",
    string StatusEmoji = "",
    string? NameColor = null,
    string? BannerColor = null);

public enum ChannelKind
{
    Text = 0,
    Voice = 1,
}

/// <summary>A text or voice channel of a room.</summary>
public sealed record ChannelDto(
    long Id,
    string Name,
    string Topic,
    int Position,
    DateTimeOffset CreatedAt,
    ChannelKind Kind = ChannelKind.Text,
    long RoomId = 0);

/// <summary>
/// A room: a space with its own channels and members, which people join with its invite code. The
/// owner and server administrators manage it. <c>IconVersion</c> is null without an icon; otherwise
/// the icon is at <c>/api/rooms/{id}/icon</c>, and the number changes with every new icon.
/// </summary>
public sealed record RoomDto(
    long Id,
    string Name,
    long? OwnerId,
    string Color,
    int? IconVersion,
    string InviteCode,
    IReadOnlyList<long> MemberIds,
    DateTimeOffset CreatedAt);

/// <summary>A room with what a member needs to show it: its channels, voice participants and streams.</summary>
public sealed record RoomStateDto(
    RoomDto Room,
    IReadOnlyList<ChannelDto> Channels,
    IReadOnlyList<VoiceParticipantDto> VoiceParticipants,
    IReadOnlyList<StreamInfoDto> Streams);

/// <summary>A user connected to a voice channel.</summary>
public sealed record VoiceParticipantDto(
    long ChannelId,
    long UserId,
    bool IsMuted,
    bool IsDeafened);

/// <summary>A file uploaded to the server and (optionally) linked to a message.</summary>
public sealed record AttachmentDto(
    long Id,
    string FileName,
    string ContentType,
    long Size)
{
    public bool IsImage => ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A chat message.</summary>
public sealed record MessageDto(
    long Id,
    long ChannelId,
    long AuthorId,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    IReadOnlyList<AttachmentDto> Attachments);

/// <summary>An active screen share. <c>HasAudio</c> is false for streams from clients without audio support.</summary>
public sealed record StreamInfoDto(
    string StreamId,
    long UserId,
    string Title,
    int Width,
    int Height,
    int FrameRate,
    DateTimeOffset StartedAt,
    IReadOnlyList<long> ViewerIds,
    bool HasAudio = false,
    long RoomId = 0);

public enum EmojiKind
{
    /// <summary>A small picture used inline in messages as <c>:name:</c>.</summary>
    Emoji = 0,

    /// <summary>A larger picture sent on its own as a message.</summary>
    Sticker = 1,
}

/// <summary>
/// A custom emoji or sticker uploaded by a member. Messages refer to it as <c>:name:</c>; the picture
/// is at <c>/api/emojis/{id}/image</c> and never changes.
/// </summary>
public sealed record EmojiDto(
    long Id,
    string Name,
    EmojiKind Kind,
    long UploaderId,
    DateTimeOffset CreatedAt);

/// <summary>
/// A soundboard sound uploaded by a member: a short 48 kHz WAV file at <c>/api/sounds/{id}/audio</c>,
/// which never changes.
/// </summary>
public sealed record SoundDto(
    long Id,
    string Name,
    long UploaderId,
    int DurationMs,
    DateTimeOffset CreatedAt);

/// <summary>
/// Snapshot of the server state sent to a client right after it connects: the rooms the user is a
/// member of, with their channels, voice participants and streams. <c>Emojis</c>, <c>Sounds</c> and
/// <c>Rooms</c> are null from servers that predate them.
/// </summary>
public sealed record SyncStateDto(
    UserDto Me,
    IReadOnlyList<UserDto> Users,
    IReadOnlyList<ChannelDto> Channels,
    IReadOnlyList<long> OnlineUserIds,
    IReadOnlyList<StreamInfoDto> Streams,
    IReadOnlyList<VoiceParticipantDto> VoiceParticipants,
    IReadOnlyList<EmojiDto>? Emojis = null,
    IReadOnlyList<SoundDto>? Sounds = null,
    IReadOnlyList<RoomDto>? Rooms = null);

/// <summary>A trickled ICE candidate for a media session.</summary>
public sealed record IceCandidateDto(
    string SessionId,
    string Candidate,
    string? SdpMid,
    int? SdpMLineIndex);

/// <summary>
/// Request sent by a client that wants to go live, in a room it is a member of (older clients send
/// no room; their stream goes to their first room).
/// </summary>
public sealed record StartStreamRequest(
    string OfferSdp,
    string Title,
    int Width,
    int Height,
    int FrameRate,
    long? RoomId = null);

/// <summary>Server answer for a publish or watch request.</summary>
public sealed record MediaSessionAnswer(
    string StreamId,
    string SessionId,
    string AnswerSdp);

/// <summary>
/// The desktop client of the server's release (the server image carries it), which clients update to
/// and people can download from the server.
/// </summary>
public sealed record ClientReleaseDto(string Version, IReadOnlyList<ClientDownloadDto> Downloads);

/// <param name="Platform">One of <see cref="ClientPlatforms"/>.</param>
/// <param name="Url">Where to download it, relative to the server's address.</param>
public sealed record ClientDownloadDto(string Platform, string FileName, long Size, string Sha256, string Url);

public static class ClientPlatforms
{
    /// <summary>The Windows installer, which installed clients run to update themselves.</summary>
    public const string WindowsInstaller = "windows-installer";

    public const string Deb = "deb";

    public const string Flatpak = "flatpak";
}

/// <summary>Public, unauthenticated information about the server.</summary>
public sealed record ServerInfoDto(
    string Name,
    string Version,
    bool RegistrationRequiresInvite,
    bool RegistrationOpen,
    int StunPort,
    IReadOnlyList<IceServerDto> IceServers);

/// <summary>An ICE server the client should use when establishing media sessions.</summary>
public sealed record IceServerDto(string Urls, string? Username, string? Credential);

public sealed record LoginRequest(string Username, string Password);

public sealed record RegisterRequest(string Username, string DisplayName, string Password, string? InviteCode);

public sealed record AuthResponse(string Token, DateTimeOffset ExpiresAt, UserDto User);

/// <summary>
/// Profile changes: null leaves a field as it is; an empty string clears the text fields and resets the
/// name and banner colours to the default.
/// </summary>
public sealed record UpdateProfileRequest(
    string? DisplayName,
    string? AvatarColor,
    string? Bio = null,
    string? StatusText = null,
    string? StatusEmoji = null,
    string? NameColor = null,
    string? BannerColor = null);

/// <param name="KeepConnectionId">The caller's own hub connection, which stays open; all other connections of the user are closed.</param>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword, string? KeepConnectionId = null);

/// <summary>RFC 7807-style error body returned by the REST API.</summary>
public sealed record ApiError(string Error);
