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

public sealed class Channel
{
    public long Id { get; set; }

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
