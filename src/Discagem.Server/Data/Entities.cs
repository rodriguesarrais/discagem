using Discagem.Shared;

namespace Discagem.Server.Data;

public sealed class User
{
    public long Id { get; set; }

    public required string Username { get; set; }

    /// <summary>Upper-invariant username used for case-insensitive uniqueness.</summary>
    public required string NormalizedUsername { get; set; }

    public required string DisplayName { get; set; }

    public string PasswordHash { get; set; } = "";

    public required string AvatarColor { get; set; }

    /// <summary>Whether the user has uploaded a profile photo (stored in the avatars folder under the user id).</summary>
    public bool HasAvatar { get; set; }

    /// <summary>Incremented whenever the photo changes, so that clients know to fetch it again.</summary>
    public int AvatarVersion { get; set; }

    public bool IsAdmin { get; set; }

    /// <summary>Incremented to invalidate previously issued tokens (e.g. after a password change).</summary>
    public int TokenVersion { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>A room: channels and members of their own; people join with its invite code.</summary>
public sealed class Room
{
    public long Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Null only for a room whose owner's account no longer exists; administrators manage it.</summary>
    public long? OwnerId { get; set; }

    /// <summary>Colour of the room's initials when it has no icon (#RRGGBB).</summary>
    public required string Color { get; set; }

    /// <summary>Whether an icon was uploaded (stored in the room-icons folder under the room id).</summary>
    public bool HasIcon { get; set; }

    /// <summary>Incremented whenever the icon changes, so that clients know to fetch it again.</summary>
    public int IconVersion { get; set; }

    /// <summary>Unique; replaced when the owner resets it.</summary>
    public required string InviteCode { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<RoomMember> Members { get; set; } = [];

    public List<Channel> Channels { get; set; } = [];
}

public sealed class RoomMember
{
    public long RoomId { get; set; }

    public Room? Room { get; set; }

    public long UserId { get; set; }

    public DateTime JoinedAt { get; set; }
}

public sealed class Channel
{
    public long Id { get; set; }

    public long RoomId { get; set; }

    public Room? Room { get; set; }

    public required string Name { get; set; }

    public string Topic { get; set; } = "";

    public int Position { get; set; }

    public ChannelKind Kind { get; set; } = ChannelKind.Text;

    public DateTime CreatedAt { get; set; }

    public List<Message> Messages { get; set; } = [];
}

public sealed class Message
{
    public long Id { get; set; }

    public long ChannelId { get; set; }

    public Channel? Channel { get; set; }

    public long AuthorId { get; set; }

    public User? Author { get; set; }

    public string Content { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    public DateTime? EditedAt { get; set; }

    public List<Attachment> Attachments { get; set; } = [];
}

public sealed class Attachment
{
    public long Id { get; set; }

    public long UploaderId { get; set; }

    public long? MessageId { get; set; }

    public Message? Message { get; set; }

    public required string FileName { get; set; }

    public required string ContentType { get; set; }

    public long Size { get; set; }

    /// <summary>Name of the file inside the uploads directory.</summary>
    public required string StorageName { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>A custom emoji or sticker; the picture is stored in the emojis folder under its id.</summary>
public sealed class CustomEmoji
{
    public long Id { get; set; }

    /// <summary>Unique among emojis and stickers, lowercase (see <see cref="Discagem.Shared.Text.EmojiNames"/>).</summary>
    public required string Name { get; set; }

    public EmojiKind Kind { get; set; }

    public long UploaderId { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>A soundboard sound; the WAV file is stored in the sounds folder under its id.</summary>
public sealed class Sound
{
    public long Id { get; set; }

    public required string Name { get; set; }

    public long UploaderId { get; set; }

    public int DurationMs { get; set; }

    public DateTime CreatedAt { get; set; }
}
