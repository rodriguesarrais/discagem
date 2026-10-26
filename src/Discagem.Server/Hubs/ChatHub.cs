using System.Text;
using Discagem.Server.Auth;
using Discagem.Server.Data;
using Discagem.Server.Media;
using Discagem.Server.Options;
using Discagem.Server.Services;
using Discagem.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Discagem.Server.Hubs;

/// <summary>
/// Real-time hub: chat, presence, typing indicators and WebRTC signalling for screen shares.
/// </summary>
[Authorize]
public sealed class ChatHub(
    AppDbContext db,
    PresenceTracker presence,
    SfuService sfu,
    VoiceService voice,
    IOptions<DiscagemOptions> options,
    TimeProvider time) : Hub<IChatClient>, IChatHub
{
    private long UserId => Context.User!.GetUserId();

    private bool IsAdmin => Context.User!.IsAdmin();

    public override async Task OnConnectedAsync()
    {
        if (presence.Connect(UserId, Context))
        {
            await Clients.Others.PresenceChanged(UserId, true);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await sfu.OnConnectionClosedAsync(Context.ConnectionId);
        await voice.OnConnectionClosedAsync(Context.ConnectionId);
        if (presence.Disconnect(UserId, Context.ConnectionId))
        {
            await Clients.Others.PresenceChanged(UserId, false);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task<SyncStateDto> Sync()
    {
        var me = await db.Users.FindAsync(UserId) ?? throw new HubException("Your account no longer exists.");
        var users = await db.Users.OrderBy(u => u.Id).ToListAsync();
        var channels = await db.Channels.OrderBy(c => c.Position).ThenBy(c => c.Id).ToListAsync();
        return new SyncStateDto(
            me.ToDto(),
            users.Select(u => u.ToDto()).ToList(),
            channels.Select(c => c.ToDto()).ToList(),
            presence.OnlineUserIds(),
            sfu.GetStreams(),
            voice.GetParticipants());
    }

    public async Task<IReadOnlyList<MessageDto>> GetMessages(long channelId, long? before, int limit)
    {
        limit = Math.Clamp(limit, 1, 100);
        var query = db.Messages.AsNoTracking().Include(m => m.Attachments).Where(m => m.ChannelId == channelId);
        if (before is not null)
        {
            query = query.Where(m => m.Id < before.Value);
        }

        var page = await query.OrderByDescending(m => m.Id).Take(limit).ToListAsync();
        page.Reverse();
        return page.Select(m => m.ToDto()).ToList();
    }

    public async Task<MessageDto> SendMessage(long channelId, string content, long[] attachmentIds)
    {
        content = NormalizeContent(content);
        attachmentIds ??= [];
        if (attachmentIds.Length > Limits.MaxAttachmentsPerMessage)
        {
            throw new HubException($"At most {Limits.MaxAttachmentsPerMessage} attachments per message.");
        }

        if (content.Length == 0 && attachmentIds.Length == 0)
        {
            throw new HubException("Messages cannot be empty.");
        }

        if (!await db.Channels.AnyAsync(c => c.Id == channelId && c.Kind == ChannelKind.Text))
        {
            throw new HubException("That text channel does not exist.");
        }

        var attachments = attachmentIds.Length == 0
            ? []
            : await db.Attachments.Where(a => attachmentIds.Contains(a.Id) && a.UploaderId == UserId && a.MessageId == null).ToListAsync();
        if (attachments.Count != attachmentIds.Distinct().Count())
        {
            throw new HubException("One or more attachments are invalid.");
        }

        var message = new Message
        {
            ChannelId = channelId,
            AuthorId = UserId,
            Content = content,
            CreatedAt = time.GetUtcNow().UtcDateTime,
            Attachments = attachments,
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync();

        var dto = message.ToDto();
        await Clients.All.MessageCreated(dto);
        return dto;
    }

    public async Task EditMessage(long messageId, string content)
    {
        content = NormalizeContent(content);
        var message = await db.Messages.Include(m => m.Attachments).SingleOrDefaultAsync(m => m.Id == messageId)
            ?? throw new HubException("That message does not exist.");
        if (message.AuthorId != UserId)
        {
            throw new HubException("You can only edit your own messages.");
        }

        if (content.Length == 0 && message.Attachments.Count == 0)
        {
            throw new HubException("Messages cannot be empty.");
        }

        if (content == message.Content)
        {
            return;
        }

        message.Content = content;
        message.EditedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
        await Clients.All.MessageUpdated(message.ToDto());
    }

    public async Task DeleteMessage(long messageId)
    {
        var message = await db.Messages.Include(m => m.Attachments).SingleOrDefaultAsync(m => m.Id == messageId)
            ?? throw new HubException("That message does not exist.");
        if (message.AuthorId != UserId && !IsAdmin)
        {
            throw new HubException("You can only delete your own messages.");
        }

        var files = message.Attachments.Select(a => a.StorageName).ToList();
        db.Attachments.RemoveRange(message.Attachments);
        db.Messages.Remove(message);
        await db.SaveChangesAsync();
        DeleteUploads(files);
        await Clients.All.MessageDeleted(message.ChannelId, message.Id);
    }

    public async Task Typing(long channelId)
    {
        await Clients.Others.UserTyping(channelId, UserId);
    }

    public async Task<ChannelDto> CreateChannel(string name, string topic, ChannelKind kind)
    {
        RequireAdmin();
        if (!Enum.IsDefined(kind))
        {
            throw new HubException("Unknown channel type.");
        }

        var channel = new Channel
        {
            Name = NormalizeChannelName(name, kind),
            Topic = NormalizeTopic(topic),
            Kind = kind,
            Position = (await db.Channels.MaxAsync(c => (int?)c.Position) ?? -1) + 1,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        var dto = channel.ToDto();
        await Clients.All.ChannelCreated(dto);
        return dto;
    }

    public async Task UpdateChannel(long channelId, string name, string topic)
    {
        RequireAdmin();
        var channel = await db.Channels.FindAsync(channelId) ?? throw new HubException("That channel does not exist.");
        channel.Name = NormalizeChannelName(name, channel.Kind);
        channel.Topic = NormalizeTopic(topic);
        await db.SaveChangesAsync();
        await Clients.All.ChannelUpdated(channel.ToDto());
    }

    public async Task DeleteChannel(long channelId)
    {
        RequireAdmin();
        var channel = await db.Channels.FindAsync(channelId) ?? throw new HubException("That channel does not exist.");
        if (channel.Kind == ChannelKind.Text && await db.Channels.CountAsync(c => c.Kind == ChannelKind.Text) <= 1)
        {
            throw new HubException("The last text channel cannot be deleted.");
        }

        if (channel.Kind == ChannelKind.Voice)
        {
            await voice.CloseChannelAsync(channelId);
        }
        var files = await db.Attachments.Where(a => a.Message != null && a.Message.ChannelId == channelId).Select(a => a.StorageName).ToListAsync();
        await db.Attachments.Where(a => a.Message != null && a.Message.ChannelId == channelId).ExecuteDeleteAsync();
        db.Channels.Remove(channel);
        await db.SaveChangesAsync();
        DeleteUploads(files);
        await Clients.All.ChannelDeleted(channelId);
    }

    public async Task<MediaSessionAnswer> StartStream(StartStreamRequest request)
    {
        try
        {
            return await sfu.StartStreamAsync(UserId, Context.ConnectionId, request);
        }
        catch (MediaException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public Task StopStream() => sfu.StopStreamAsync(UserId);

    public async Task<MediaSessionAnswer> WatchStream(string streamId, string offerSdp)
    {
        try
        {
            return await sfu.WatchAsync(UserId, Context.ConnectionId, streamId, offerSdp);
        }
        catch (MediaException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public Task StopWatching(string sessionId) => sfu.StopWatchingAsync(Context.ConnectionId, sessionId);

    public Task AddIceCandidate(IceCandidateDto candidate)
    {
        if (!voice.AddRemoteCandidate(Context.ConnectionId, candidate))
        {
            sfu.AddRemoteCandidate(Context.ConnectionId, candidate);
        }

        return Task.CompletedTask;
    }

    public async Task<MediaSessionAnswer> JoinVoice(long channelId, string offerSdp)
    {
        if (!await db.Channels.AnyAsync(c => c.Id == channelId && c.Kind == ChannelKind.Voice))
        {
            throw new HubException("That voice channel does not exist.");
        }

        try
        {
            return await voice.JoinAsync(UserId, Context.ConnectionId, channelId, offerSdp);
        }
        catch (MediaException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public Task LeaveVoice() => voice.LeaveAsync(UserId, Context.ConnectionId);

    public Task SetVoiceState(bool muted, bool deafened) => voice.SetStateAsync(UserId, Context.ConnectionId, muted, deafened);

    private void RequireAdmin()
    {
        if (!IsAdmin)
        {
            throw new HubException("Only administrators can manage channels.");
        }
    }

    private void DeleteUploads(IEnumerable<string> storageNames)
    {
        var uploads = DataPaths.Uploads(options.Value);
        foreach (var name in storageNames)
        {
            AttachmentCleanupService.TryDelete(Path.Combine(uploads, name));
        }
    }

    internal static string NormalizeContent(string? content)
    {
        content = (content ?? "").Replace("\r\n", "\n").Trim();
        if (content.Length > Limits.MaxMessageLength)
        {
            throw new HubException($"Messages can be at most {Limits.MaxMessageLength} characters long.");
        }

        return content;
    }

    /// <summary>
    /// Discord-style channel names: text channels are lowercase words separated by dashes,
    /// voice channels keep their spelling.
    /// </summary>
    internal static string NormalizeChannelName(string? name, ChannelKind kind = ChannelKind.Text)
    {
        if (kind == ChannelKind.Voice)
        {
            var display = string.Join(' ', new string((name ?? "").Where(c => !char.IsControl(c)).ToArray())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (display.Length == 0)
            {
                throw new HubException("Channel names cannot be empty.");
            }

            return display.Length > Limits.MaxChannelNameLength ? display[..Limits.MaxChannelNameLength].TrimEnd() : display;
        }

        var builder = new StringBuilder();
        foreach (var c in (name ?? "").Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                builder.Append(c);
            }
            else if ((char.IsWhiteSpace(c) || c == '-') && builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var result = builder.ToString().Trim('-');
        if (result.Length == 0)
        {
            throw new HubException("Channel names must contain letters or digits.");
        }

        return result.Length > Limits.MaxChannelNameLength ? result[..Limits.MaxChannelNameLength].TrimEnd('-') : result;
    }

    private static string NormalizeTopic(string? topic)
    {
        var value = (topic ?? "").Trim().ReplaceLineEndings(" ");
        return value.Length > Limits.MaxChannelTopicLength ? value[..Limits.MaxChannelTopicLength] : value;
    }
}
