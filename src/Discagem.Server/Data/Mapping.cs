using Discagem.Shared;

namespace Discagem.Server.Data;

public static class Mapping
{
    public static DateTimeOffset ToOffset(this DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    public static UserDto ToDto(this User user) =>
        new(user.Id, user.Username, user.DisplayName, user.AvatarColor, user.IsAdmin, user.CreatedAt.ToOffset());

    public static ChannelDto ToDto(this Channel channel) =>
        new(channel.Id, channel.Name, channel.Topic, channel.Position, channel.CreatedAt.ToOffset());

    public static AttachmentDto ToDto(this Attachment attachment) =>
        new(attachment.Id, attachment.FileName, attachment.ContentType, attachment.Size);

    public static MessageDto ToDto(this Message message) =>
        new(
            message.Id,
            message.ChannelId,
            message.AuthorId,
            message.Content,
            message.CreatedAt.ToOffset(),
            message.EditedAt?.ToOffset(),
            message.Attachments.OrderBy(a => a.Id).Select(a => a.ToDto()).ToList());
}
