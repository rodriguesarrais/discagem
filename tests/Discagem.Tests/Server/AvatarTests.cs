using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Discagem.Shared;
using Microsoft.AspNetCore.SignalR.Client;

namespace Discagem.Tests.Server;

public sealed class AvatarTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 5, 6, 7, 8];

    private readonly DiscagemFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static MultipartFormDataContent Photo(byte[] data, string name = "photo.png")
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(data) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } }, "file", name);
        return form;
    }

    [Fact]
    public async Task Photos_CanBeUploadedReplacedAndRemoved()
    {
        var (alice, aliceAuth) = await _factory.RegisterAsync("alice");
        var (bob, bobAuth) = await _factory.RegisterAsync("bob");
        Assert.Null(aliceAuth.User.AvatarVersion);

        var hub = await _factory.ConnectHubAsync(bobAuth.Token);
        await using var _ = hub;
        var updates = new List<UserDto>();
        hub.On<UserDto>(nameof(IChatClient.UserUpdated), u => { lock (updates) updates.Add(u); });

        var first = await (await alice.PutAsync("/api/me/avatar", Photo(Png))).Content.ReadFromJsonAsync<UserDto>();
        Assert.NotNull(first!.AvatarVersion);

        // Anyone signed in can fetch it, as the type the data actually is.
        var download = await bob.GetAsync($"/api/users/{aliceAuth.User.Id}/avatar?v={first.AvatarVersion}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("image/png", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());

        var second = await (await alice.PutAsync("/api/me/avatar", Photo(Jpeg, "photo.jpg"))).Content.ReadFromJsonAsync<UserDto>();
        Assert.True(second!.AvatarVersion > first.AvatarVersion);
        download = await bob.GetAsync($"/api/users/{aliceAuth.User.Id}/avatar");
        Assert.Equal("image/jpeg", download.Content.Headers.ContentType?.MediaType);

        var removed = await (await alice.DeleteAsync("/api/me/avatar")).Content.ReadFromJsonAsync<UserDto>();
        Assert.Null(removed!.AvatarVersion);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/users/{aliceAuth.User.Id}/avatar")).StatusCode);

        // Other clients hear about every change, and the sync state carries the version.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && updates.Count < 3)
        {
            await Task.Delay(50);
        }

        lock (updates)
        {
            Assert.Equal([first.AvatarVersion, second.AvatarVersion, null], updates.Select(u => u.AvatarVersion));
        }

        await alice.PutAsync("/api/me/avatar", Photo(Png));
        var sync = await hub.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync));
        Assert.NotNull(sync.Users.Single(u => u.Id == aliceAuth.User.Id).AvatarVersion);
    }

    [Fact]
    public async Task Photos_AreValidated()
    {
        var (alice, aliceAuth) = await _factory.RegisterAsync("alice");

        var html = await alice.PutAsync("/api/me/avatar", Photo("<html><script>alert(1)</script></html>"u8.ToArray(), "photo.png"));
        Assert.Equal(HttpStatusCode.BadRequest, html.StatusCode);

        var large = new byte[1024 * 1024 + 1];
        Png.CopyTo(large, 0);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await alice.PutAsync("/api/me/avatar", Photo(large))).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/users/{aliceAuth.User.Id}/avatar")).StatusCode);

        // Only signed-in users can upload or see photos.
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsync("/api/me/avatar", Photo(Png))).StatusCode);
        await alice.PutAsync("/api/me/avatar", Photo(Png));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/users/{aliceAuth.User.Id}/avatar")).StatusCode);
    }
}
