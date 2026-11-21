using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Discagem.Media.WebRtc;
using Discagem.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Discagem.Tests.Server;

public sealed class EmojiAndSoundTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private readonly DiscagemFactory _factory = new();
    private readonly List<IDisposable> _peers = [];

    public void Dispose()
    {
        foreach (var peer in _peers)
        {
            peer.Dispose();
        }

        _factory.Dispose();
    }

    private static MultipartFormDataContent Emoji(string name, byte[] data, EmojiKind kind = EmojiKind.Emoji)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(name), "name" },
            { new StringContent(kind.ToString()), "kind" },
            { new ByteArrayContent(data) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } }, "file", "emoji.png" },
        };
        return form;
    }

    private static MultipartFormDataContent Sound(string name, byte[] wav)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(name), "name" },
            { new ByteArrayContent(wav) { Headers = { ContentType = new MediaTypeHeaderValue("audio/wav") } }, "file", "sound.wav" },
        };
        return form;
    }

    /// <summary>A 48 kHz mono 16-bit WAV file of the given length.</summary>
    private static byte[] Wav(double seconds)
    {
        var samples = (int)(DiscagemAudio.SampleRate * seconds);
        var file = new byte[44 + samples * 2];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), file.Length - 8);
        "WAVEfmt "u8.CopyTo(file.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(24), DiscagemAudio.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(28), DiscagemAudio.SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(34), 16);
        "data"u8.CopyTo(file.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(40), samples * 2);
        for (var i = 0; i < samples; i++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * 440 * i / DiscagemAudio.SampleRate) * 8000);
            BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(44 + i * 2), value);
        }

        return file;
    }

    private static async Task<T> WaitForAsync<T>(TaskCompletionSource<T> source) => await source.Task.WaitAsync(Timeout);

    private async Task JoinVoiceAsync(HubConnection hub, long channelId)
    {
        var peer = new VoicePeer([]);
        _peers.Add(peer);
        var offer = await peer.CreateOfferAsync();
        var answer = await hub.InvokeAsync<MediaSessionAnswer>(nameof(IChatHub.JoinVoice), channelId, offer);
        peer.ApplyAnswer(answer.AnswerSdp);
    }

    [Fact]
    public async Task Emojis_CanBeAddedListedAndRemoved()
    {
        var (alice, aliceAuth) = await _factory.RegisterAsync("alice");
        var (bob, bobAuth) = await _factory.RegisterAsync("bob");
        var hub = await _factory.ConnectHubAsync(bobAuth.Token);
        await using var _ = hub;
        var created = new TaskCompletionSource<EmojiDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleted = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<EmojiDto>(nameof(IChatClient.EmojiCreated), e => created.TrySetResult(e));
        hub.On<long>(nameof(IChatClient.EmojiDeleted), id => deleted.TrySetResult(id));

        // Names are normalised: lowercase, with underscores.
        var response = await alice.PostAsync("/api/emojis", Emoji("Party Parrot", Png));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var emoji = (await response.Content.ReadFromJsonAsync<EmojiDto>())!;
        Assert.Equal("party_parrot", emoji.Name);
        Assert.Equal(EmojiKind.Emoji, emoji.Kind);
        Assert.Equal(aliceAuth.User.Id, emoji.UploaderId);
        Assert.Equal(emoji, await WaitForAsync(created));

        var sticker = (await (await alice.PostAsync("/api/emojis", Emoji("wave", Png, EmojiKind.Sticker))).Content.ReadFromJsonAsync<EmojiDto>())!;
        Assert.Equal(EmojiKind.Sticker, sticker.Kind);

        var sync = await hub.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync));
        Assert.Equal(["party_parrot", "wave"], sync.Emojis!.Select(e => e.Name));

        var image = await bob.GetAsync($"/api/emojis/{emoji.Id}/image");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await image.Content.ReadAsByteArrayAsync());

        // Names are unique across emojis and stickers.
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PostAsync("/api/emojis", Emoji("party_parrot", Png, EmojiKind.Sticker))).StatusCode);

        // Only the uploader (or an administrator) can remove it. Alice registered first: she is the administrator.
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.DeleteAsync($"/api/emojis/{emoji.Id}")).StatusCode);
        var bobs = (await (await bob.PostAsync("/api/emojis", Emoji("bobs", Png))).Content.ReadFromJsonAsync<EmojiDto>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/api/emojis/{bobs.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/api/emojis/{emoji.Id}")).StatusCode);
        Assert.Contains(await WaitForAsync(deleted), new[] { bobs.Id, emoji.Id });
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/emojis/{emoji.Id}/image")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.DeleteAsync($"/api/emojis/{emoji.Id}")).StatusCode);

        sync = await hub.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync));
        Assert.Equal(["wave"], sync.Emojis!.Select(e => e.Name));
    }

    [Fact]
    public async Task Emojis_AreValidated()
    {
        var (alice, _) = await _factory.RegisterAsync("alice");

        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsync("/api/emojis", Emoji("x", Png))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsync("/api/emojis", Emoji("!!!", Png))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await alice.PostAsync("/api/emojis", Emoji("html", "<svg onload=alert(1)>"u8.ToArray()))).StatusCode);

        var large = new byte[Limits.MaxEmojiBytes + 1];
        Png.CopyTo(large, 0);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await alice.PostAsync("/api/emojis", Emoji("large", large))).StatusCode);

        // Stickers may be larger than emojis.
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync("/api/emojis", Emoji("large", large, EmojiKind.Sticker))).StatusCode);

        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/emojis", Emoji("anon", Png))).StatusCode);
    }

    [Fact]
    public async Task Sounds_CanBeAddedAndRemoved()
    {
        var (alice, _) = await _factory.RegisterAsync("alice");
        var (bob, bobAuth) = await _factory.RegisterAsync("bob");
        var hub = await _factory.ConnectHubAsync(bobAuth.Token);
        await using var _ = hub;
        var created = new TaskCompletionSource<SoundDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<SoundDto>(nameof(IChatClient.SoundCreated), s => created.TrySetResult(s));

        var wav = Wav(1.5);
        var response = await bob.PostAsync("/api/sounds", Sound("  Air\thorn  ", wav));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sound = (await response.Content.ReadFromJsonAsync<SoundDto>())!;
        Assert.Equal("Airhorn", sound.Name);
        Assert.Equal(1500, sound.DurationMs);
        Assert.Equal(sound, await WaitForAsync(created));

        var audio = await alice.GetAsync($"/api/sounds/{sound.Id}/audio");
        Assert.Equal("audio/wav", audio.Content.Headers.ContentType?.MediaType);
        Assert.Equal(wav, await audio.Content.ReadAsByteArrayAsync());
        Assert.Single((await hub.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync))).Sounds!);

        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PostAsync("/api/sounds", Sound("long", Wav(5.5)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PostAsync("/api/sounds", Sound("text", "RIFF....WAVEnope"u8.ToArray()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PostAsync("/api/sounds", Sound("   ", wav))).StatusCode);

        // Bob added it, so he can remove it.
        Assert.Equal(HttpStatusCode.NoContent, (await bob.DeleteAsync($"/api/sounds/{sound.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/sounds/{sound.Id}/audio")).StatusCode);
        Assert.Empty((await hub.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync))).Sounds!);
    }

    [Fact]
    public async Task PlayedSounds_ReachOnlyTheVoiceChannel()
    {
        var (alice, aliceAuth) = await _factory.RegisterAsync("alice");
        var (_, bobAuth) = await _factory.RegisterAsync("bob");
        var (_, carolAuth) = await _factory.RegisterAsync("carol");
        var aliceHub = await _factory.ConnectHubAsync(aliceAuth.Token);
        var bobHub = await _factory.ConnectHubAsync(bobAuth.Token);
        var carolHub = await _factory.ConnectHubAsync(carolAuth.Token);
        await using var a = aliceHub;
        await using var b = bobHub;
        await using var c = carolHub;

        var sound = (await (await alice.PostAsync("/api/sounds", Sound("ding", Wav(0.5)))).Content.ReadFromJsonAsync<SoundDto>())!;
        // Carol is in the room but not in the call, so she must not hear the sound.
        var room = await DiscagemFactory.ShareRoomAsync(aliceHub, bobHub, carolHub);
        var lounge = room.Channels.Single(ch => ch.Kind == ChannelKind.Voice).Id;

        // Outside a voice channel there is nobody to play to.
        var outside = await Assert.ThrowsAsync<HubException>(() => aliceHub.InvokeAsync(nameof(IChatHub.PlaySound), sound.Id));
        Assert.Contains("Join a voice channel", outside.Message);

        await JoinVoiceAsync(aliceHub, lounge);
        await JoinVoiceAsync(bobHub, lounge);

        var heardByAlice = new TaskCompletionSource<(long, long, long)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heardByBob = new TaskCompletionSource<(long, long, long)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heardByCarol = false;
        aliceHub.On<long, long, long>(nameof(IChatClient.SoundPlayed), (ch, u, s) => heardByAlice.TrySetResult((ch, u, s)));
        bobHub.On<long, long, long>(nameof(IChatClient.SoundPlayed), (ch, u, s) => heardByBob.TrySetResult((ch, u, s)));
        carolHub.On<long, long, long>(nameof(IChatClient.SoundPlayed), (_, _, _) => heardByCarol = true);

        await bobHub.InvokeAsync(nameof(IChatHub.PlaySound), sound.Id);
        Assert.Equal((lounge, bobAuth.User.Id, sound.Id), await WaitForAsync(heardByBob));
        Assert.Equal((lounge, bobAuth.User.Id, sound.Id), await WaitForAsync(heardByAlice));
        await carolHub.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync));
        Assert.False(heardByCarol);

        // Unknown sounds, muted players and spamming are refused.
        await Assert.ThrowsAsync<HubException>(() => bobHub.InvokeAsync(nameof(IChatHub.PlaySound), sound.Id + 100));
        await aliceHub.InvokeAsync(nameof(IChatHub.SetVoiceState), true, false);
        var muted = await Assert.ThrowsAsync<HubException>(() => aliceHub.InvokeAsync(nameof(IChatHub.PlaySound), sound.Id));
        Assert.Contains("Unmute", muted.Message);
        await aliceHub.InvokeAsync(nameof(IChatHub.SetVoiceState), false, true);
        var deafened = await Assert.ThrowsAsync<HubException>(() => aliceHub.InvokeAsync(nameof(IChatHub.PlaySound), sound.Id));
        Assert.Contains("Unmute", deafened.Message);

        var refused = false;
        for (var i = 0; i < 5 && !refused; i++)
        {
            try
            {
                await bobHub.InvokeAsync(nameof(IChatHub.PlaySound), sound.Id);
            }
            catch (HubException ex) when (ex.Message.Contains("too quickly"))
            {
                refused = true;
            }
        }

        Assert.True(refused);
    }
}
