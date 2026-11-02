namespace Discagem.Shared;

/// <summary>
/// Public representation of a user account. <c>AvatarVersion</c> is null without a profile photo;
/// otherwise the photo is at <c>/api/users/{id}/avatar</c>, and the number changes with every new photo.
/// </summary>
public sealed record UserDto(
    long Id,
    string Username,
    string DisplayName,
    string AvatarColor,
    bool IsAdmin,
    DateTimeOffset CreatedAt,
    int? AvatarVersion = null);

public enum ChannelKind
{
    Text = 0,
    Voice = 1,
}

/// <summary>A text or voice channel.</summary>
public sealed record ChannelDto(
    long Id,
    string Name,
    string Topic,
    int Position,
    DateTimeOffset CreatedAt,
    ChannelKind Kind = ChannelKind.Text);

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
    bool HasAudio = false);

/// <summary>Snapshot of the server state sent to a client right after it connects.</summary>
public sealed record SyncStateDto(
    UserDto Me,
    IReadOnlyList<UserDto> Users,
    IReadOnlyList<ChannelDto> Channels,
    IReadOnlyList<long> OnlineUserIds,
    IReadOnlyList<StreamInfoDto> Streams,
    IReadOnlyList<VoiceParticipantDto> VoiceParticipants);

/// <summary>A trickled ICE candidate for a media session.</summary>
public sealed record IceCandidateDto(
    string SessionId,
    string Candidate,
    string? SdpMid,
    int? SdpMLineIndex);

/// <summary>Request sent by a client that wants to go live.</summary>
public sealed record StartStreamRequest(
    string OfferSdp,
    string Title,
    int Width,
    int Height,
    int FrameRate);

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

public sealed record UpdateProfileRequest(string? DisplayName, string? AvatarColor);

/// <param name="KeepConnectionId">The caller's own hub connection, which stays open; all other connections of the user are closed.</param>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword, string? KeepConnectionId = null);

/// <summary>RFC 7807-style error body returned by the REST API.</summary>
public sealed record ApiError(string Error);
