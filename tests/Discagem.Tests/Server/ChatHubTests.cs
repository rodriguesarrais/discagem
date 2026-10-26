using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Discagem.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Discagem.Tests.Server;

public sealed class ChatHubTests : IAsyncLifetime
{
    private readonly DiscagemFactory _factory = new();
    private HubConnection _alice = null!;
    private HubConnection _bob = null!;
    private AuthResponse _aliceAuth = null!;
    private AuthResponse _bobAuth = null!;
    private HttpClient _aliceHttp = null!;
    private HttpClient _bobHttp = null!;

    public async Task InitializeAsync()
    {
        (_aliceHttp, _aliceAuth) = await _factory.RegisterAsync("alice", "Alice");
        (_bobHttp, _bobAuth) = await _factory.RegisterAsync("bob", "Bob");
        _alice = await _factory.ConnectHubAsync(_aliceAuth.Token);
        _bob = await _factory.ConnectHubAsync(_bobAuth.Token);
    }

    public async Task DisposeAsync()
    {
        await _alice.DisposeAsync();
        await _bob.DisposeAsync();
        _factory.Dispose();
    }

    private static async Task<T> WithTimeout<T>(Task<T> task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(task, completed);
        return await task;
    }

    [Fact]
    public async Task Sync_ReturnsInitialState()
    {
        var state = await _alice.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync));
        Assert.Equal(_aliceAuth.User.Id, state.Me.Id);
        Assert.Equal(2, state.Users.Count);
        Assert.Contains(state.Channels, c => c.Name == "general" && c.Kind == ChannelKind.Text);
        Assert.Contains(state.Channels, c => c.Name == "Lounge" && c.Kind == ChannelKind.Voice);
        Assert.Contains(_bobAuth.User.Id, state.OnlineUserIds);
        Assert.Empty(state.Streams);
        Assert.Empty(state.VoiceParticipants);
    }

    [Fact]
    public async Task Messages_AreBroadcastEditedAndDeleted()
    {
        var state = await _alice.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync));
        var general = state.Channels.Single(c => c.Name == "general");

        var created = new TaskCompletionSource<MessageDto>();
        var updated = new TaskCompletionSource<MessageDto>();
        var deleted = new TaskCompletionSource<long>();
        _bob.On<MessageDto>(nameof(IChatClient.MessageCreated), m => created.TrySetResult(m));
        _bob.On<MessageDto>(nameof(IChatClient.MessageUpdated), m => updated.TrySetResult(m));
        _bob.On<long, long>(nameof(IChatClient.MessageDeleted), (_, id) => deleted.TrySetResult(id));

        var sent = await _alice.InvokeAsync<MessageDto>(nameof(IChatHub.SendMessage), general.Id, "  hello **bob**  ", Array.Empty<long>());
        Assert.Equal("hello **bob**", sent.Content);
        var received = await WithTimeout(created.Task);
        Assert.Equal(sent.Id, received.Id);
        Assert.Equal(_aliceAuth.User.Id, received.AuthorId);

        // Bob cannot edit or delete Alice's message.
        await Assert.ThrowsAsync<HubException>(() => _bob.InvokeAsync(nameof(IChatHub.EditMessage), sent.Id, "hacked"));
        await Assert.ThrowsAsync<HubException>(() => _bob.InvokeAsync(nameof(IChatHub.DeleteMessage), sent.Id));

        await _alice.InvokeAsync(nameof(IChatHub.EditMessage), sent.Id, "hello again");
        var edited = await WithTimeout(updated.Task);
        Assert.Equal("hello again", edited.Content);
        Assert.NotNull(edited.EditedAt);

        await _alice.InvokeAsync(nameof(IChatHub.DeleteMessage), sent.Id);
        Assert.Equal(sent.Id, await WithTimeout(deleted.Task));
    }

    [Fact]
    public async Task GetMessages_PagesBackwards()
    {
        var state = await _alice.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync));
        var channelId = state.Channels[0].Id;
        for (var i = 0; i < 12; i++)
        {
            await _alice.InvokeAsync<MessageDto>(nameof(IChatHub.SendMessage), channelId, $"m{i}", Array.Empty<long>());
        }

        var latest = await _bob.InvokeAsync<IReadOnlyList<MessageDto>>(nameof(IChatHub.GetMessages), channelId, (long?)null, 5);
        Assert.Equal(["m7", "m8", "m9", "m10", "m11"], latest.Select(m => m.Content));

        var older = await _bob.InvokeAsync<IReadOnlyList<MessageDto>>(nameof(IChatHub.GetMessages), channelId, (long?)latest[0].Id, 5);
        Assert.Equal(["m2", "m3", "m4", "m5", "m6"], older.Select(m => m.Content));
    }

    [Fact]
    public async Task SendMessage_RejectsInvalidInput()
    {
        var state = await _alice.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync));
        var channelId = state.Channels[0].Id;
        await Assert.ThrowsAsync<HubException>(() => _alice.InvokeAsync<MessageDto>(nameof(IChatHub.SendMessage), channelId, "   ", Array.Empty<long>()));
        await Assert.ThrowsAsync<HubException>(() => _alice.InvokeAsync<MessageDto>(nameof(IChatHub.SendMessage), channelId, new string('x', Limits.MaxMessageLength + 1), Array.Empty<long>()));
        await Assert.ThrowsAsync<HubException>(() => _alice.InvokeAsync<MessageDto>(nameof(IChatHub.SendMessage), 9999L, "hi", Array.Empty<long>()));
    }

    [Fact]
    public async Task Channels_OnlyAdminsCanManage()
    {
        var created = new TaskCompletionSource<ChannelDto>();
        _bob.On<ChannelDto>(nameof(IChatClient.ChannelCreated), c => created.TrySetResult(c));

        await Assert.ThrowsAsync<HubException>(() => _bob.InvokeAsync<ChannelDto>(nameof(IChatHub.CreateChannel), "bob's channel", "", ChannelKind.Text));

        var channel = await _alice.InvokeAsync<ChannelDto>(nameof(IChatHub.CreateChannel), "  Game Night!! ", "Fridays", ChannelKind.Text);
        Assert.Equal("game-night", channel.Name);
        Assert.Equal("Fridays", channel.Topic);
        Assert.Equal(ChannelKind.Text, channel.Kind);
        Assert.Equal(channel.Id, (await WithTimeout(created.Task)).Id);

        // Voice channel names keep their spelling.
        var voice = await _alice.InvokeAsync<ChannelDto>(nameof(IChatHub.CreateChannel), "  Squad   Room ", "", ChannelKind.Voice);
        Assert.Equal("Squad Room", voice.Name);
        Assert.Equal(ChannelKind.Voice, voice.Kind);
        await Assert.ThrowsAsync<HubException>(() => _alice.InvokeAsync<MessageDto>(nameof(IChatHub.SendMessage), voice.Id, "hello?", Array.Empty<long>()));
        await Assert.ThrowsAsync<HubException>(() => _alice.InvokeAsync<ChannelDto>(nameof(IChatHub.CreateChannel), "odd", "", (ChannelKind)7));

        await _alice.InvokeAsync(nameof(IChatHub.UpdateChannel), channel.Id, "movies", "Sundays");
        await _alice.InvokeAsync(nameof(IChatHub.DeleteChannel), channel.Id);
        await _alice.InvokeAsync(nameof(IChatHub.DeleteChannel), voice.Id);
        var state = await _alice.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync));
        Assert.DoesNotContain(state.Channels, c => c.Id == channel.Id || c.Id == voice.Id);

        // The last text channel cannot be removed; voice channels can all go.
        await Assert.ThrowsAsync<HubException>(() => _alice.InvokeAsync(nameof(IChatHub.DeleteChannel), state.Channels.Single(c => c.Kind == ChannelKind.Text).Id));
        await _alice.InvokeAsync(nameof(IChatHub.DeleteChannel), state.Channels.Single(c => c.Kind == ChannelKind.Voice).Id);
    }

    [Fact]
    public async Task Typing_AndPresence_AreBroadcast()
    {
        var typing = new TaskCompletionSource<(long, long)>();
        _bob.On<long, long>(nameof(IChatClient.UserTyping), (c, u) => typing.TrySetResult((c, u)));
        await _alice.InvokeAsync(nameof(IChatHub.Typing), 42L);
        Assert.Equal((42L, _aliceAuth.User.Id), await WithTimeout(typing.Task));

        var offline = new TaskCompletionSource<(long, bool)>();
        _bob.On<long, bool>(nameof(IChatClient.PresenceChanged), (u, online) => offline.TrySetResult((u, online)));
        await _alice.StopAsync();
        Assert.Equal((_aliceAuth.User.Id, false), await WithTimeout(offline.Task));
    }

    [Fact]
    public async Task Attachments_CanBeUploadedAttachedAndDownloaded()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(content, "file", "../../evil name.png");

        var upload = await _aliceHttp.PostAsync("/api/attachments", form);
        upload.EnsureSuccessStatusCode();
        var attachment = (await upload.Content.ReadFromJsonAsync<AttachmentDto>())!;
        Assert.Equal("evil name.png", attachment.FileName);
        Assert.True(attachment.IsImage);

        // Until it is posted, only the uploader can fetch it.
        Assert.Equal(HttpStatusCode.NotFound, (await _bobHttp.GetAsync($"/api/attachments/{attachment.Id}/x")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _aliceHttp.GetAsync($"/api/attachments/{attachment.Id}/x")).StatusCode);

        var state = await _alice.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync));
        var channelId = state.Channels.First(c => c.Kind == ChannelKind.Text).Id;

        // Bob cannot attach Alice's upload.
        await Assert.ThrowsAsync<HubException>(() => _bob.InvokeAsync<MessageDto>(nameof(IChatHub.SendMessage), channelId, "mine", new[] { attachment.Id }));

        var message = await _alice.InvokeAsync<MessageDto>(nameof(IChatHub.SendMessage), channelId, "", new[] { attachment.Id });
        Assert.Single(message.Attachments);

        // It can only be used once.
        await Assert.ThrowsAsync<HubException>(() => _alice.InvokeAsync<MessageDto>(nameof(IChatHub.SendMessage), channelId, "again", new[] { attachment.Id }));

        var download = await _bobHttp.GetAsync($"/api/attachments/{attachment.Id}/{attachment.FileName}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("image/png", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());

        var anonymous = await _factory.CreateClient().GetAsync($"/api/attachments/{attachment.Id}/x");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
