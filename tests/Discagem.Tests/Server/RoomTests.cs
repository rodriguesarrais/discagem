using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Discagem.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Discagem.Tests.Server;

public sealed class RoomTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private readonly DiscagemFactory _factory = new();
    private HttpClient _aliceHttp = null!;
    private HttpClient _bobHttp = null!;
    private HttpClient _carolHttp = null!;
    private AuthResponse _aliceAuth = null!;
    private AuthResponse _bobAuth = null!;
    private AuthResponse _carolAuth = null!;
    private HubConnection _alice = null!;
    private HubConnection _bob = null!;
    private HubConnection _carol = null!;

    public async Task InitializeAsync()
    {
        // Alice registers first, so she is also the server administrator.
        (_aliceHttp, _aliceAuth) = await _factory.RegisterAsync("alice");
        (_bobHttp, _bobAuth) = await _factory.RegisterAsync("bob");
        (_carolHttp, _carolAuth) = await _factory.RegisterAsync("carol");
        _alice = await _factory.ConnectHubAsync(_aliceAuth.Token);
        _bob = await _factory.ConnectHubAsync(_bobAuth.Token);
        _carol = await _factory.ConnectHubAsync(_carolAuth.Token);
    }

    public async Task DisposeAsync()
    {
        await _alice.DisposeAsync();
        await _bob.DisposeAsync();
        await _carol.DisposeAsync();
        _factory.Dispose();
    }

    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static MultipartFormDataContent Picture(byte[] data)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(content, "file", "icon.png");
        return form;
    }

    [Fact]
    public async Task Rooms_AreCreatedAndJoinedWithTheirInviteCode()
    {
        var room = await _bob.InvokeAsync<RoomStateDto>(nameof(IChatHub.CreateRoom), "  Game   Night ", "#E67E22");
        Assert.Equal("Game Night", room.Room.Name);
        Assert.Equal("#E67E22", room.Room.Color);
        Assert.Equal(_bobAuth.User.Id, room.Room.OwnerId);
        Assert.Equal([_bobAuth.User.Id], room.Room.MemberIds);
        Assert.Equal(Limits.InviteCodeLength, room.Room.InviteCode.Length);
        Assert.Contains(room.Channels, c => c is { Name: "general", Kind: ChannelKind.Text });
        Assert.Contains(room.Channels, c => c is { Name: "General", Kind: ChannelKind.Voice });
        Assert.All(room.Channels, c => Assert.Equal(room.Room.Id, c.RoomId));

        // The owner hears about new members.
        var updated = Signal<RoomDto>();
        _bob.On<RoomDto>(nameof(IChatClient.RoomUpdated), r =>
        {
            if (r.MemberIds.Count == 3)
            {
                updated.TrySetResult(r);
            }
        });

        // Codes work in any case, alone or at the end of a link.
        var joined = await _alice.InvokeAsync<RoomStateDto>(nameof(IChatHub.JoinRoom), room.Room.InviteCode.ToLowerInvariant());
        Assert.Equal(room.Room.Id, joined.Room.Id);
        Assert.Equal(room.Channels.Count, joined.Channels.Count);
        await _carol.InvokeAsync<RoomStateDto>(nameof(IChatHub.JoinRoom), $"https://chat.example.com/invite/{room.Room.InviteCode}/");
        Assert.Equal("AB3DE7FG", Discagem.Server.Hubs.ChatHub.ParseInviteCode(
            "Join Friends on Discagem: open Discagem, sign in to https://chat.example.com, choose \"Join a room\" and enter the code ab3de7fg."));
        Assert.Equal(
            new[] { _aliceAuth.User.Id, _bobAuth.User.Id, _carolAuth.User.Id }.Order(),
            (await updated.Task.WaitAsync(Timeout)).MemberIds);

        // Joining again changes nothing; unknown codes are refused.
        await _carol.InvokeAsync<RoomStateDto>(nameof(IChatHub.JoinRoom), room.Room.InviteCode);
        await Assert.ThrowsAsync<HubException>(() => _carol.InvokeAsync<RoomStateDto>(nameof(IChatHub.JoinRoom), "NOPE1234"));

        var state = await _carol.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync));
        Assert.Equal(room.Room.Id, Assert.Single(state.Rooms!).Id);
    }

    [Fact]
    public async Task OtherRooms_AreInvisibleAndClosed()
    {
        var room = await DiscagemFactory.ShareRoomAsync(_alice, _bob);
        var general = room.Channels.Single(c => c.Kind == ChannelKind.Text).Id;
        var voice = room.Channels.Single(c => c.Kind == ChannelKind.Voice).Id;

        var carolHeard = false;
        _carol.On<MessageDto>(nameof(IChatClient.MessageCreated), _ => carolHeard = true);
        _carol.On<long, long>(nameof(IChatClient.UserTyping), (_, _) => carolHeard = true);
        _carol.On<ChannelDto>(nameof(IChatClient.ChannelCreated), _ => carolHeard = true);
        var bobGotMessage = Signal<MessageDto>();
        _bob.On<MessageDto>(nameof(IChatClient.MessageCreated), m => bobGotMessage.TrySetResult(m));

        // A posted attachment is visible to the room's members only.
        var upload = await _aliceHttp.PostAsync("/api/attachments", Picture(Png));
        var attachment = (await upload.Content.ReadFromJsonAsync<AttachmentDto>())!;
        await _alice.InvokeAsync<MessageDto>(nameof(IChatHub.SendMessage), general, "secret", new[] { attachment.Id });
        await _alice.InvokeAsync(nameof(IChatHub.Typing), general);
        await _alice.InvokeAsync<ChannelDto>(nameof(IChatHub.CreateChannel), room.Room.Id, "plans", "", ChannelKind.Text);
        await bobGotMessage.Task.WaitAsync(Timeout);
        Assert.Equal(HttpStatusCode.OK, (await _bobHttp.GetAsync($"/api/attachments/{attachment.Id}/x")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _carolHttp.GetAsync($"/api/attachments/{attachment.Id}/x")).StatusCode);

        // Carol, who is not in the room, sees none of it and cannot use it.
        var state = await _carol.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync));
        Assert.Empty(state.Rooms!);
        Assert.Empty(state.Channels);
        Assert.False(carolHeard);
        await Assert.ThrowsAsync<HubException>(() => _carol.InvokeAsync<IReadOnlyList<MessageDto>>(nameof(IChatHub.GetMessages), general, (long?)null, 10));
        await Assert.ThrowsAsync<HubException>(() => _carol.InvokeAsync<MessageDto>(nameof(IChatHub.SendMessage), general, "hi", Array.Empty<long>()));
        await Assert.ThrowsAsync<HubException>(() => _carol.InvokeAsync<MediaSessionAnswer>(nameof(IChatHub.JoinVoice), voice, "v=0"));
        await Assert.ThrowsAsync<HubException>(() => _carol.InvokeAsync<ChannelDto>(nameof(IChatHub.CreateChannel), room.Room.Id, "mine", "", ChannelKind.Text));
        await Assert.ThrowsAsync<HubException>(() => _carol.InvokeAsync(nameof(IChatHub.LeaveRoom), room.Room.Id));
        await Assert.ThrowsAsync<HubException>(() => _carol.InvokeAsync(nameof(IChatHub.DeleteRoom), room.Room.Id));

        // Members who are not the owner cannot manage it.
        await Assert.ThrowsAsync<HubException>(() => _bob.InvokeAsync<ChannelDto>(nameof(IChatHub.CreateChannel), room.Room.Id, "bobs", "", ChannelKind.Text));
        await Assert.ThrowsAsync<HubException>(() => _bob.InvokeAsync(nameof(IChatHub.UpdateRoom), room.Room.Id, "Bob's", "#000000"));
        await Assert.ThrowsAsync<HubException>(() => _bob.InvokeAsync<string>(nameof(IChatHub.ResetRoomInvite), room.Room.Id));
        await Assert.ThrowsAsync<HubException>(() => _bob.InvokeAsync(nameof(IChatHub.RemoveRoomMember), room.Room.Id, _aliceAuth.User.Id));
        await Assert.ThrowsAsync<HubException>(() => _bob.InvokeAsync(nameof(IChatHub.DeleteRoom), room.Room.Id));
    }

    [Fact]
    public async Task Owners_ManageTheirRoom()
    {
        // Bob owns the room; Alice is a member (and the server administrator).
        var room = await DiscagemFactory.ShareRoomAsync(_bob, _carol, _alice);
        var id = room.Room.Id;

        await _bob.InvokeAsync(nameof(IChatHub.UpdateRoom), id, "  Bob's   place ", "#1ABC9C");
        await Assert.ThrowsAsync<HubException>(() => _bob.InvokeAsync(nameof(IChatHub.UpdateRoom), id, "x", "blue"));
        await Assert.ThrowsAsync<HubException>(() => _bob.InvokeAsync(nameof(IChatHub.UpdateRoom), id, "   ", "#1ABC9C"));

        // A new invite code replaces the old one.
        var code = await _bob.InvokeAsync<string>(nameof(IChatHub.ResetRoomInvite), id);
        Assert.NotEqual(room.Room.InviteCode, code);

        // Removing Carol takes the room away from her.
        var removed = Signal<long>();
        _carol.On<long>(nameof(IChatClient.RoomRemoved), r => removed.TrySetResult(r));
        await Assert.ThrowsAsync<HubException>(() => _bob.InvokeAsync(nameof(IChatHub.RemoveRoomMember), id, _bobAuth.User.Id));
        await _bob.InvokeAsync(nameof(IChatHub.RemoveRoomMember), id, _carolAuth.User.Id);
        Assert.Equal(id, await removed.Task.WaitAsync(Timeout));
        Assert.Empty((await _carol.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync))).Rooms!);
        await Assert.ThrowsAsync<HubException>(() => _carol.InvokeAsync<RoomStateDto>(nameof(IChatHub.JoinRoom), room.Room.InviteCode));
        await _carol.InvokeAsync<RoomStateDto>(nameof(IChatHub.JoinRoom), code);

        // Channels can be put in order.
        var second = await _bob.InvokeAsync<ChannelDto>(nameof(IChatHub.CreateChannel), id, "second", "", ChannelKind.Text);
        await _bob.InvokeAsync(nameof(IChatHub.MoveChannel), second.Id, 0);
        var texts = (await _bob.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync))).Channels
            .Where(c => c.Kind == ChannelKind.Text).OrderBy(c => c.Position).Select(c => c.Name);
        Assert.Equal(["second", "general"], texts);

        // The owner can hand the room over; the server administrator can manage any room they are in.
        await _bob.InvokeAsync(nameof(IChatHub.TransferRoom), id, _carolAuth.User.Id);
        var state = await _alice.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync));
        var updated = Assert.Single(state.Rooms!);
        Assert.Equal("Bob's place", updated.Name);
        Assert.Equal("#1ABC9C", updated.Color);
        Assert.Equal(_carolAuth.User.Id, updated.OwnerId);
        await Assert.ThrowsAsync<HubException>(() => _bob.InvokeAsync(nameof(IChatHub.UpdateRoom), id, "Mine", "#000000"));
        await _alice.InvokeAsync(nameof(IChatHub.UpdateRoom), id, "Admin's", "#000000");
    }

    [Fact]
    public async Task LeavingAndDeleting_HandTheRoomOnOrRemoveIt()
    {
        var room = await DiscagemFactory.ShareRoomAsync(_bob, _carol);
        var general = room.Channels.Single(c => c.Kind == ChannelKind.Text).Id;
        await _bob.InvokeAsync<MessageDto>(nameof(IChatHub.SendMessage), general, "hello", Array.Empty<long>());

        // When the owner leaves, the longest-standing member takes over.
        var carolSawBobLeave = Signal<RoomDto>();
        _carol.On<RoomDto>(nameof(IChatClient.RoomUpdated), r =>
        {
            if (!r.MemberIds.Contains(_bobAuth.User.Id))
            {
                carolSawBobLeave.TrySetResult(r);
            }
        });
        await _bob.InvokeAsync(nameof(IChatHub.LeaveRoom), room.Room.Id);
        Assert.Equal(_carolAuth.User.Id, (await carolSawBobLeave.Task.WaitAsync(Timeout)).OwnerId);
        await Assert.ThrowsAsync<HubException>(() => _bob.InvokeAsync<IReadOnlyList<MessageDto>>(nameof(IChatHub.GetMessages), general, (long?)null, 10));
        Assert.Single(await _carol.InvokeAsync<IReadOnlyList<MessageDto>>(nameof(IChatHub.GetMessages), general, (long?)null, 10));

        // The last member leaving deletes the room.
        await _carol.InvokeAsync(nameof(IChatHub.LeaveRoom), room.Room.Id);
        await Assert.ThrowsAsync<HubException>(() => _carol.InvokeAsync<RoomStateDto>(nameof(IChatHub.JoinRoom), room.Room.InviteCode));

        // Deleting a room tells every member.
        var other = await DiscagemFactory.ShareRoomAsync(_bob, _carol);
        var deleted = Signal<long>();
        _carol.On<long>(nameof(IChatClient.RoomRemoved), r => deleted.TrySetResult(r));
        await _bob.InvokeAsync(nameof(IChatHub.DeleteRoom), other.Room.Id);
        Assert.Equal(other.Room.Id, await deleted.Task.WaitAsync(Timeout));
        Assert.Empty((await _carol.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync))).Rooms!);
    }

    [Fact]
    public async Task JoiningFromOneConnection_ReachesTheUsersOtherConnections()
    {
        var room = await DiscagemFactory.ShareRoomAsync(_alice);
        await using var second = await _factory.ConnectHubAsync(_bobAuth.Token);
        var added = Signal<RoomStateDto>();
        var message = Signal<MessageDto>();
        second.On<RoomStateDto>(nameof(IChatClient.RoomAdded), r => added.TrySetResult(r));
        second.On<MessageDto>(nameof(IChatClient.MessageCreated), m => message.TrySetResult(m));

        await _bob.InvokeAsync<RoomStateDto>(nameof(IChatHub.JoinRoom), room.Room.InviteCode);
        Assert.Equal(room.Room.Id, (await added.Task.WaitAsync(Timeout)).Room.Id);

        // The other connection is now in the room's group too.
        await _alice.InvokeAsync<MessageDto>(nameof(IChatHub.SendMessage), room.Channels.Single(c => c.Kind == ChannelKind.Text).Id, "welcome", Array.Empty<long>());
        Assert.Equal("welcome", (await message.Task.WaitAsync(Timeout)).Content);
    }

    [Fact]
    public async Task RoomIcons_AreSetByTheOwnerAndShownToMembers()
    {
        var room = await DiscagemFactory.ShareRoomAsync(_bob, _alice);
        var url = $"/api/rooms/{room.Room.Id}/icon";

        // Rooms one is not in do not exist; icons must be pictures.
        Assert.Equal(HttpStatusCode.NotFound, (await _carolHttp.PutAsync(url, Picture(Png))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _bobHttp.PutAsync(url, Picture([1, 2, 3]))).StatusCode);

        // Bob owns the room; Alice is only a member here, but also the server administrator.
        var set = await _bobHttp.PutAsync(url, Picture(Png));
        set.EnsureSuccessStatusCode();
        var dto = (await set.Content.ReadFromJsonAsync<RoomDto>())!;
        Assert.Equal(1, dto.IconVersion);

        var download = await _aliceHttp.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Png, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await _carolHttp.GetAsync(url)).StatusCode);

        // Once Bob is no longer the owner, he cannot change the icon.
        await _alice.InvokeAsync(nameof(IChatHub.TransferRoom), room.Room.Id, _aliceAuth.User.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await _bobHttp.DeleteAsync(url)).StatusCode);
        var removed = await _aliceHttp.DeleteAsync(url);
        Assert.Null((await removed.Content.ReadFromJsonAsync<RoomDto>())!.IconVersion);
        Assert.Equal(HttpStatusCode.NotFound, (await _aliceHttp.GetAsync(url)).StatusCode);
    }
}
