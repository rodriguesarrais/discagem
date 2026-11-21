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
/// Real-time hub: chat, presence, typing indicators, the soundboard and WebRTC signalling for screen
/// shares and voice.
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
        foreach (var roomId in await MyRoomIdsAsync())
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroups.Name(roomId));
        }

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
        var rooms = await db.Rooms.AsNoTracking().Include(r => r.Members)
            .Where(r => r.Members.Any(m => m.UserId == UserId)).OrderBy(r => r.Id).ToListAsync();
        var roomIds = rooms.Select(r => r.Id).ToHashSet();
        var channels = await db.Channels.AsNoTracking().Where(c => roomIds.Contains(c.RoomId))
            .OrderBy(c => c.RoomId).ThenBy(c => c.Position).ThenBy(c => c.Id).ToListAsync();
        var emojis = await db.Emojis.AsNoTracking().OrderBy(e => e.Name).ToListAsync();
        var sounds = await db.Sounds.AsNoTracking().OrderBy(s => s.Id).ToListAsync();
        return new SyncStateDto(
            me.ToDto(),
            users.Select(u => u.ToDto()).ToList(),
            channels.Select(c => c.ToDto()).ToList(),
            presence.OnlineUserIds(),
            sfu.GetStreams(roomIds),
            voice.GetParticipants(roomIds),
            emojis.Select(e => e.ToDto()).ToList(),
            sounds.Select(s => s.ToDto()).ToList(),
            rooms.Select(r => r.ToDto()).ToList());
    }

    public async Task<IReadOnlyList<MessageDto>> GetMessages(long channelId, long? before, int limit)
    {
        await RequireChannelAsync(channelId);
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

        var channel = await RequireChannelAsync(channelId);
        if (channel.Kind != ChannelKind.Text)
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
        await RoomClients(channel.RoomId).MessageCreated(dto);
        return dto;
    }

    public async Task EditMessage(long messageId, string content)
    {
        content = NormalizeContent(content);
        var message = await db.Messages.Include(m => m.Attachments).Include(m => m.Channel).SingleOrDefaultAsync(m => m.Id == messageId)
            ?? throw new HubException("That message does not exist.");
        if (message.AuthorId != UserId)
        {
            throw new HubException("You can only edit your own messages.");
        }

        if (!await IsMemberAsync(message.Channel!.RoomId))
        {
            throw new HubException("That message does not exist.");
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
        await RoomClients(message.Channel.RoomId).MessageUpdated(message.ToDto());
    }

    public async Task DeleteMessage(long messageId)
    {
        var message = await db.Messages.Include(m => m.Attachments).Include(m => m.Channel).ThenInclude(c => c!.Room)
            .SingleOrDefaultAsync(m => m.Id == messageId)
            ?? throw new HubException("That message does not exist.");
        var room = message.Channel!.Room!;
        if (!await IsMemberAsync(room.Id))
        {
            throw new HubException("That message does not exist.");
        }

        if (message.AuthorId != UserId && !CanManage(room))
        {
            throw new HubException("You can only delete your own messages.");
        }

        var files = message.Attachments.Select(a => a.StorageName).ToList();
        db.Attachments.RemoveRange(message.Attachments);
        db.Messages.Remove(message);
        await db.SaveChangesAsync();
        DeleteUploads(files);
        await RoomClients(room.Id).MessageDeleted(message.ChannelId, message.Id);
    }

    public async Task Typing(long channelId)
    {
        var roomId = await db.Channels.Where(c => c.Id == channelId).Select(c => (long?)c.RoomId).SingleOrDefaultAsync();
        if (roomId is { } id && await IsMemberAsync(id))
        {
            await Clients.OthersInGroup(RoomGroups.Name(id)).UserTyping(channelId, UserId);
        }
    }

    public async Task<ChannelDto> CreateChannel(long roomId, string name, string topic, ChannelKind kind)
    {
        var room = await RequireManagedRoomAsync(roomId, "manage channels");
        if (!Enum.IsDefined(kind))
        {
            throw new HubException("Unknown channel type.");
        }

        if (await db.Channels.CountAsync(c => c.RoomId == roomId) >= Limits.MaxChannelsPerRoom)
        {
            throw new HubException($"A room can have at most {Limits.MaxChannelsPerRoom} channels.");
        }

        var channel = new Channel
        {
            RoomId = room.Id,
            Name = NormalizeChannelName(name, kind),
            Topic = NormalizeTopic(topic),
            Kind = kind,
            Position = (await db.Channels.Where(c => c.RoomId == roomId).MaxAsync(c => (int?)c.Position) ?? -1) + 1,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        var dto = channel.ToDto();
        await RoomClients(room.Id).ChannelCreated(dto);
        return dto;
    }

    public async Task UpdateChannel(long channelId, string name, string topic)
    {
        var channel = await RequireChannelAsync(channelId);
        await RequireManagedRoomAsync(channel.RoomId, "manage channels");
        channel.Name = NormalizeChannelName(name, channel.Kind);
        channel.Topic = NormalizeTopic(topic);
        await db.SaveChangesAsync();
        await RoomClients(channel.RoomId).ChannelUpdated(channel.ToDto());
    }

    public async Task MoveChannel(long channelId, int position)
    {
        var channel = await RequireChannelAsync(channelId);
        await RequireManagedRoomAsync(channel.RoomId, "manage channels");

        // Renumber the room's channels of that kind in their new order.
        var siblings = await db.Channels.Where(c => c.RoomId == channel.RoomId && c.Kind == channel.Kind)
            .OrderBy(c => c.Position).ThenBy(c => c.Id).ToListAsync();
        siblings.Remove(channel);
        siblings.Insert(Math.Clamp(position, 0, siblings.Count), channel);
        var changed = new List<Channel>();
        for (var i = 0; i < siblings.Count; i++)
        {
            if (siblings[i].Position != i)
            {
                siblings[i].Position = i;
                changed.Add(siblings[i]);
            }
        }

        await db.SaveChangesAsync();
        foreach (var moved in changed)
        {
            await RoomClients(channel.RoomId).ChannelUpdated(moved.ToDto());
        }
    }

    public async Task DeleteChannel(long channelId)
    {
        var channel = await RequireChannelAsync(channelId);
        await RequireManagedRoomAsync(channel.RoomId, "manage channels");
        if (channel.Kind == ChannelKind.Text &&
            await db.Channels.CountAsync(c => c.RoomId == channel.RoomId && c.Kind == ChannelKind.Text) <= 1)
        {
            throw new HubException("The last text channel of a room cannot be deleted.");
        }

        var files = await db.Attachments.Where(a => a.Message != null && a.Message.ChannelId == channelId).Select(a => a.StorageName).ToListAsync();
        await db.Attachments.Where(a => a.Message != null && a.Message.ChannelId == channelId).ExecuteDeleteAsync();
        db.Channels.Remove(channel);
        await db.SaveChangesAsync();
        DeleteUploads(files);

        // Disconnect voice only once the channel is gone, so a join racing with the deletion either
        // shows up here or fails its own check in JoinVoice.
        if (channel.Kind == ChannelKind.Voice)
        {
            await voice.CloseChannelAsync(channelId);
        }

        await RoomClients(channel.RoomId).ChannelDeleted(channelId);
    }

    // ---- Rooms --------------------------------------------------------------------------------

    public async Task<RoomStateDto> CreateRoom(string name, string color)
    {
        if (await db.RoomMembers.CountAsync(m => m.UserId == UserId) >= Limits.MaxRoomsPerUser)
        {
            throw new HubException($"You can be in at most {Limits.MaxRoomsPerUser} rooms.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        var room = new Room
        {
            Name = NormalizeRoomName(name),
            Color = AccountRules.IsValidColor(color) ? color : AccountRules.RandomAvatarColor(),
            OwnerId = UserId,
            InviteCode = NewInviteCode(),
            CreatedAt = now,
        };
        room.Members.Add(new RoomMember { UserId = UserId, JoinedAt = now });
        room.Channels.Add(new Channel { Name = "general", Topic = "Say hello!", Kind = ChannelKind.Text, Position = 0, CreatedAt = now });
        room.Channels.Add(new Channel { Name = "General", Kind = ChannelKind.Voice, Position = 0, CreatedAt = now });
        db.Rooms.Add(room);
        await db.SaveChangesAsync();

        await AddToRoomGroupAsync(UserId, room.Id);
        var state = await RoomStateAsync(room.Id);
        await OtherConnectionsOfMine().RoomAdded(state);
        return state;
    }

    public async Task<RoomStateDto> JoinRoom(string inviteCode)
    {
        var code = ParseInviteCode(inviteCode);
        var room = await db.Rooms.Include(r => r.Members).SingleOrDefaultAsync(r => r.InviteCode == code)
            ?? throw new HubException("That invite code is not valid. Ask for a new one.");
        if (room.Members.Any(m => m.UserId == UserId))
        {
            return await RoomStateAsync(room.Id);
        }

        if (await db.RoomMembers.CountAsync(m => m.UserId == UserId) >= Limits.MaxRoomsPerUser)
        {
            throw new HubException($"You can be in at most {Limits.MaxRoomsPerUser} rooms.");
        }

        room.Members.Add(new RoomMember { UserId = UserId, JoinedAt = time.GetUtcNow().UtcDateTime });
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Joined from another connection at the same moment.
        }

        await AddToRoomGroupAsync(UserId, room.Id);
        await RoomClients(room.Id).RoomUpdated(room.ToDto());
        var state = await RoomStateAsync(room.Id);
        await OtherConnectionsOfMine().RoomAdded(state);
        return state;
    }

    public async Task LeaveRoom(long roomId)
    {
        var room = await RequireRoomAsync(roomId);
        await RemoveMemberAsync(room, UserId);
    }

    public async Task UpdateRoom(long roomId, string name, string color)
    {
        var room = await RequireManagedRoomAsync(roomId, "change the room");
        room.Name = NormalizeRoomName(name);
        if (!AccountRules.IsValidColor(color))
        {
            throw new HubException("Colours must be written #RRGGBB.");
        }

        room.Color = color;
        await db.SaveChangesAsync();
        await RoomClients(room.Id).RoomUpdated(room.ToDto());
    }

    public async Task<string> ResetRoomInvite(long roomId)
    {
        var room = await RequireManagedRoomAsync(roomId, "reset the invite code");
        room.InviteCode = NewInviteCode();
        await db.SaveChangesAsync();
        await RoomClients(room.Id).RoomUpdated(room.ToDto());
        return room.InviteCode;
    }

    public async Task RemoveRoomMember(long roomId, long userId)
    {
        var room = await RequireManagedRoomAsync(roomId, "remove members");
        if (userId == room.OwnerId)
        {
            throw new HubException("The owner cannot be removed. Transfer the room first.");
        }

        if (room.Members.All(m => m.UserId != userId))
        {
            throw new HubException("That person is not in this room.");
        }

        await RemoveMemberAsync(room, userId);
    }

    public async Task TransferRoom(long roomId, long userId)
    {
        var room = await RequireManagedRoomAsync(roomId, "transfer the room");
        if (room.Members.All(m => m.UserId != userId))
        {
            throw new HubException("The new owner must be a member of the room.");
        }

        room.OwnerId = userId;
        await db.SaveChangesAsync();
        await RoomClients(room.Id).RoomUpdated(room.ToDto());
    }

    public async Task DeleteRoom(long roomId)
    {
        var room = await RequireRoomAsync(roomId);
        if (room.OwnerId != UserId && !IsAdmin)
        {
            throw new HubException("Only the room's owner can delete it.");
        }

        await DeleteRoomAsync(room);
    }

    /// <summary>Removes a member; the owner leaving passes the room on, and the last member leaving deletes it.</summary>
    private async Task RemoveMemberAsync(Room room, long userId)
    {
        var member = room.Members.SingleOrDefault(m => m.UserId == userId);
        if (member is null)
        {
            return;
        }

        if (room.Members.Count == 1)
        {
            await DeleteRoomAsync(room);
            return;
        }

        room.Members.Remove(member);
        db.RoomMembers.Remove(member);
        if (room.OwnerId == userId)
        {
            // The longest-standing member takes over.
            room.OwnerId = room.Members.OrderBy(m => m.JoinedAt).ThenBy(m => m.UserId).First().UserId;
        }

        await db.SaveChangesAsync();
        await voice.LeaveRoomAsync(userId, room.Id);
        await sfu.LeaveRoomAsync(userId, room.Id);
        await RemoveFromRoomGroupAsync(userId, room.Id);
        await Clients.Clients(presence.ConnectionIds(userId)).RoomRemoved(room.Id);
        await RoomClients(room.Id).RoomUpdated(room.ToDto());
    }

    private async Task DeleteRoomAsync(Room room)
    {
        var channelIds = await db.Channels.Where(c => c.RoomId == room.Id).Select(c => c.Id).ToListAsync();
        var voiceIds = await db.Channels.Where(c => c.RoomId == room.Id && c.Kind == ChannelKind.Voice).Select(c => c.Id).ToListAsync();
        var files = await db.Attachments.Where(a => a.Message != null && channelIds.Contains(a.Message.ChannelId)).Select(a => a.StorageName).ToListAsync();
        await db.Attachments.Where(a => a.Message != null && channelIds.Contains(a.Message.ChannelId)).ExecuteDeleteAsync();
        db.Rooms.Remove(room);
        await db.SaveChangesAsync();
        DeleteUploads(files);
        AttachmentCleanupService.TryDelete(RoomIconPath(options.Value, room.Id));

        foreach (var channelId in voiceIds)
        {
            await voice.CloseChannelAsync(channelId);
        }

        await sfu.CloseRoomAsync(room.Id);
        await RoomClients(room.Id).RoomRemoved(room.Id);
        foreach (var member in room.Members)
        {
            await RemoveFromRoomGroupAsync(member.UserId, room.Id);
        }
    }

    private async Task<RoomStateDto> RoomStateAsync(long roomId)
    {
        var room = await db.Rooms.AsNoTracking().Include(r => r.Members).SingleAsync(r => r.Id == roomId);
        var channels = await db.Channels.AsNoTracking().Where(c => c.RoomId == roomId)
            .OrderBy(c => c.Position).ThenBy(c => c.Id).ToListAsync();
        var rooms = new HashSet<long> { roomId };
        return new RoomStateDto(room.ToDto(), channels.Select(c => c.ToDto()).ToList(), voice.GetParticipants(rooms), sfu.GetStreams(rooms));
    }

    internal static string RoomIconPath(DiscagemOptions options, long roomId) => Path.Combine(DataPaths.RoomIcons(options), $"{roomId}");

    public async Task<MediaSessionAnswer> StartStream(StartStreamRequest request)
    {
        try
        {
            // Older clients name no room: their stream goes to their first room.
            var roomId = request.RoomId ?? (await MyRoomIdsAsync()).FirstOrDefault();
            if (roomId == 0 || !await IsMemberAsync(roomId))
            {
                throw new HubException("Join a room to share your screen.");
            }

            return await sfu.StartStreamAsync(UserId, Context.ConnectionId, request, roomId);
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
            if (sfu.FindStream(streamId) is { } stream && !await IsMemberAsync(stream.RoomId))
            {
                throw new HubException("That stream is no longer live.");
            }

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
        var channel = await RequireChannelAsync(channelId);
        if (channel.Kind != ChannelKind.Voice)
        {
            throw new HubException("That voice channel does not exist.");
        }

        MediaSessionAnswer answer;
        try
        {
            answer = await voice.JoinAsync(UserId, Context.ConnectionId, channelId, channel.RoomId, offerSdp);
        }
        catch (MediaException ex)
        {
            throw new HubException(ex.Message);
        }

        // The channel may have been deleted while the connection was being negotiated (see DeleteChannel).
        if (!await db.Channels.AnyAsync(c => c.Id == channelId))
        {
            await voice.LeaveAsync(UserId, Context.ConnectionId);
            throw new HubException("That voice channel does not exist.");
        }

        return answer;
    }

    public Task LeaveVoice() => voice.LeaveAsync(UserId, Context.ConnectionId);

    public Task SetVoiceState(bool muted, bool deafened) => voice.SetStateAsync(UserId, Context.ConnectionId, muted, deafened);

    public async Task PlaySound(long soundId)
    {
        if (!await db.Sounds.AnyAsync(s => s.Id == soundId))
        {
            throw new HubException("That sound no longer exists.");
        }

        try
        {
            await voice.PlaySoundAsync(UserId, Context.ConnectionId, soundId);
        }
        catch (MediaException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    // ---- Membership -----------------------------------------------------------------------------

    private IChatClient RoomClients(long roomId) => Clients.Group(RoomGroups.Name(roomId));

    private IChatClient OtherConnectionsOfMine() =>
        Clients.Clients(presence.ConnectionIds(UserId).Where(id => id != Context.ConnectionId).ToList());

    private Task<List<long>> MyRoomIdsAsync() =>
        db.RoomMembers.Where(m => m.UserId == UserId).OrderBy(m => m.RoomId).Select(m => m.RoomId).ToListAsync();

    private Task<bool> IsMemberAsync(long roomId) => db.RoomMembers.AnyAsync(m => m.RoomId == roomId && m.UserId == UserId);

    /// <summary>The owner and server administrators manage a room.</summary>
    private bool CanManage(Room room) => room.OwnerId == UserId || IsAdmin;

    /// <summary>A room the caller is a member of, with its members; rooms they are not in do not exist for them.</summary>
    private async Task<Room> RequireRoomAsync(long roomId)
    {
        var room = await db.Rooms.Include(r => r.Members).SingleOrDefaultAsync(r => r.Id == roomId);
        if (room is null || room.Members.All(m => m.UserId != UserId))
        {
            throw new HubException("That room does not exist.");
        }

        return room;
    }

    private async Task<Room> RequireManagedRoomAsync(long roomId, string action)
    {
        var room = await RequireRoomAsync(roomId);
        if (!CanManage(room))
        {
            throw new HubException($"Only the room's owner can {action}.");
        }

        return room;
    }

    /// <summary>A channel of a room the caller is a member of.</summary>
    private async Task<Channel> RequireChannelAsync(long channelId)
    {
        var channel = await db.Channels.FindAsync(channelId);
        if (channel is null || !await IsMemberAsync(channel.RoomId))
        {
            throw new HubException("That channel does not exist.");
        }

        return channel;
    }

    private async Task AddToRoomGroupAsync(long userId, long roomId)
    {
        foreach (var connectionId in presence.ConnectionIds(userId).Append(Context.ConnectionId).Distinct())
        {
            await Groups.AddToGroupAsync(connectionId, RoomGroups.Name(roomId));
        }
    }

    private async Task RemoveFromRoomGroupAsync(long userId, long roomId)
    {
        foreach (var connectionId in presence.ConnectionIds(userId))
        {
            await Groups.RemoveFromGroupAsync(connectionId, RoomGroups.Name(roomId));
        }
    }

    private static string NewInviteCode()
    {
        // No 0/O or 1/I/L, so that codes read aloud or copied by hand work.
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        return string.Create(Limits.InviteCodeLength, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)];
            }
        });
    }

    /// <summary>
    /// Accepts the code alone, in any case, or any text that ends with it: a link, or the invite
    /// message the client copies ("… enter the code AB3DE7FG").
    /// </summary>
    internal static string ParseInviteCode(string? text)
    {
        var words = (text ?? "").Split([' ', '\t', '\r', '\n', '/'], StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? "" : words[^1].Trim('.', ',', '"', '\'', '!', ')', '(').ToUpperInvariant();
    }

    internal static string NormalizeRoomName(string? name)
    {
        var value = string.Join(' ', new string((name ?? "").Where(c => !char.IsControl(c)).ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (value.Length == 0)
        {
            throw new HubException("Rooms need a name.");
        }

        return value.Length > Limits.MaxRoomNameLength ? value[..Limits.MaxRoomNameLength].TrimEnd() : value;
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
