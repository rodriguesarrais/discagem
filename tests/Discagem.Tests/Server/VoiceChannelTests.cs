using System.Diagnostics;
using Discagem.Media.Audio;
using Discagem.Media.WebRtc;
using Discagem.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using static Discagem.Tests.Media.AudioTestSignals;

namespace Discagem.Tests.Server;

public sealed class VoiceChannelTests(Xunit.Abstractions.ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly DiscagemFactory _factory = new();
    private readonly List<Participant> _participants = [];
    private HubConnection _alice = null!;
    private HubConnection _bob = null!;
    private HubConnection _carol = null!;
    private AuthResponse _aliceAuth = null!;
    private AuthResponse _bobAuth = null!;
    private long _lounge;

    public async Task InitializeAsync()
    {
        (_, _aliceAuth) = await _factory.RegisterAsync("alice");
        (_, _bobAuth) = await _factory.RegisterAsync("bob");
        var (_, carol) = await _factory.RegisterAsync("carol");
        _alice = await _factory.ConnectHubAsync(_aliceAuth.Token);
        _bob = await _factory.ConnectHubAsync(_bobAuth.Token);
        _carol = await _factory.ConnectHubAsync(carol.Token);
        var state = await _alice.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync));
        _lounge = state.Channels.Single(c => c.Kind == ChannelKind.Voice).Id;
    }

    public async Task DisposeAsync()
    {
        foreach (var participant in _participants)
        {
            participant.Dispose();
        }

        await _alice.DisposeAsync();
        await _bob.DisposeAsync();
        await _carol.DisposeAsync();
        _factory.Dispose();
    }

    /// <summary>
    /// A simulated voice client: a real-time loop feeds its microphone (a tone, or silence) into the
    /// engine every 20 ms and records what its speakers would play.
    /// </summary>
    private sealed class Participant : IDisposable
    {
        private readonly Thread _thread;
        private readonly List<float> _recording = [];
        private volatile bool _stopped;

        public Participant(VoicePeer peer, VoiceEngine engine, string sessionId, double? toneHz)
        {
            Peer = peer;
            Engine = engine;
            SessionId = sessionId;
            Tone = toneHz is { } hz ? new Tone(hz) : null;
            _thread = new Thread(Run) { IsBackground = true, Name = "Voice test participant" };
            _thread.Start();
        }

        public VoicePeer Peer { get; }

        public VoiceEngine Engine { get; }

        public string SessionId { get; }

        public Tone? Tone { get; }

        private void Run()
        {
            var clock = Stopwatch.StartNew();
            var microphone = new float[DiscagemAudio.FrameSamples];
            var speaker = new float[DiscagemAudio.FrameSamples];
            var next = 0.0;
            while (!_stopped)
            {
                var wait = next - clock.Elapsed.TotalMilliseconds;
                if (wait > 1)
                {
                    Thread.Sleep((int)wait);
                    continue;
                }

                next += DiscagemAudio.FrameMilliseconds;
                if (Tone is null)
                {
                    Array.Clear(microphone);
                }
                else
                {
                    Tone.Fill(microphone);
                }

                Engine.WriteCapture(microphone);
                Engine.ReadPlayback(speaker);
                lock (_recording)
                {
                    _recording.AddRange(speaker);
                }
            }
        }

        /// <summary>Discards what was played so far.</summary>
        public void ResetRecording()
        {
            lock (_recording)
            {
                _recording.Clear();
            }
        }

        public float[] Recording()
        {
            lock (_recording)
            {
                return _recording.ToArray();
            }
        }

        public void Dispose()
        {
            _stopped = true;
            _thread.Join();
            Peer.Dispose();
            Engine.Dispose();
        }
    }

    private async Task<Participant> JoinAsync(HubConnection hub, long channelId, double? toneHz)
    {
        var peer = new VoicePeer([]);
        var engine = new VoiceEngine();
        engine.PacketReady = peer.Send;
        peer.PacketReceived += engine.Mixer.Push;

        var offer = await peer.CreateOfferAsync();
        var answer = await hub.InvokeAsync<MediaSessionAnswer>(nameof(IChatHub.JoinVoice), channelId, offer);
        Assert.Equal($"voice:{channelId}", answer.StreamId);
        peer.ApplyAnswer(answer.AnswerSdp);
        Assert.True(await peer.WaitForConnectionAsync(Timeout), "voice peer did not connect");

        var participant = new Participant(peer, engine, answer.SessionId, toneHz);
        _participants.Add(participant);
        return participant;
    }

    /// <summary>Lets the audio settle, then records <paramref name="seconds"/> of everyone's playback.</summary>
    private static async Task RecordAsync(double seconds, params Participant[] participants)
    {
        await Task.Delay(TimeSpan.FromSeconds(0.8));
        foreach (var participant in participants)
        {
            participant.ResetRecording();
        }

        await Task.Delay(TimeSpan.FromSeconds(seconds));
    }

    private void Report(string name, float[] recording, params double[] frequencies)
    {
        var levels = string.Join(", ", frequencies.Select(f => $"{f} Hz: {Amplitude(recording, f):0.000}"));
        output.WriteLine($"{name}: {recording.Length / (double)Rate:0.00} s, rms {Rms(recording):0.000}, {levels}");
    }

    [Fact]
    public async Task Voice_IsForwardedToEveryoneElse()
    {
        var joined = new List<VoiceParticipantDto>();
        _carol.On<VoiceParticipantDto>(nameof(IChatClient.VoiceStateChanged), p =>
        {
            lock (joined)
            {
                joined.Add(p);
            }
        });

        var alice = await JoinAsync(_alice, _lounge, 440);
        var bob = await JoinAsync(_bob, _lounge, 1000);
        var carol = await JoinAsync(_carol, _lounge, null);

        await RecordAsync(1.5, alice, bob, carol);
        var heardByAlice = alice.Recording();
        var heardByBob = bob.Recording();
        var heardByCarol = carol.Recording();
        Report("alice", heardByAlice, 440, 1000);
        Report("bob", heardByBob, 440, 1000);
        Report("carol", heardByCarol, 440, 1000);

        Assert.True(heardByCarol.Length > Rate);
        Assert.InRange(Amplitude(heardByCarol, 440), 0.15, 0.45);
        Assert.InRange(Amplitude(heardByCarol, 1000), 0.15, 0.45);

        // Nobody hears themselves.
        Assert.InRange(Amplitude(heardByAlice, 1000), 0.15, 0.45);
        Assert.True(Amplitude(heardByAlice, 440) < 0.02);
        Assert.InRange(Amplitude(heardByBob, 440), 0.15, 0.45);
        Assert.True(Amplitude(heardByBob, 1000) < 0.02);

        // Carol is silent, so she sends nothing at all.
        Assert.Equal(0, carol.Peer.PacketsSent);

        var state = await _carol.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync));
        Assert.Equal(3, state.VoiceParticipants.Count);
        Assert.All(state.VoiceParticipants, p => Assert.Equal(_lounge, p.ChannelId));
        lock (joined)
        {
            Assert.Contains(joined, p => p.UserId == _aliceAuth.User.Id && p.ChannelId == _lounge);
        }
    }

    [Fact]
    public async Task MutedAndDeafenedParticipants_AreSkippedByTheServer()
    {
        var alice = await JoinAsync(_alice, _lounge, 440);
        var bob = await JoinAsync(_bob, _lounge, 1000);
        var carol = await JoinAsync(_carol, _lounge, null);

        var muted = new TaskCompletionSource<VoiceParticipantDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _alice.On<VoiceParticipantDto>(nameof(IChatClient.VoiceStateChanged), p =>
        {
            if (p.UserId == _bobAuth.User.Id && p.IsMuted)
            {
                muted.TrySetResult(p);
            }
        });

        // Only the server is told: Bob's client keeps sending, and the server must drop it.
        await _bob.InvokeAsync(nameof(IChatHub.SetVoiceState), true, false);
        Assert.False((await muted.Task.WaitAsync(Timeout)).IsDeafened);
        await RecordAsync(1.0, carol);
        var heardByCarol = carol.Recording();
        Report("carol (bob muted)", heardByCarol, 440, 1000);
        Assert.InRange(Amplitude(heardByCarol, 440), 0.15, 0.45);
        Assert.True(Amplitude(heardByCarol, 1000) < 0.02);
        Assert.True(bob.Peer.PacketsSent > 0);

        // Deafened: the server stops forwarding to Carol.
        await _carol.InvokeAsync(nameof(IChatHub.SetVoiceState), false, true);
        await Task.Delay(500);
        var before = carol.Peer.PacketsReceived;
        var aliceBefore = alice.Peer.PacketsReceived;
        await Task.Delay(1000);
        Assert.True(carol.Peer.PacketsReceived - before <= 2, $"deafened participant still received {carol.Peer.PacketsReceived - before} packets");

        // With Bob muted and Carol silent, Alice receives nothing either.
        Assert.True(alice.Peer.PacketsReceived - aliceBefore <= 2, $"Alice received {alice.Peer.PacketsReceived - aliceBefore} packets");

        // Another connection of the same user cannot change someone's voice state.
        await using var otherDevice = await _factory.ConnectHubAsync(_bobAuth.Token);
        await otherDevice.InvokeAsync(nameof(IChatHub.SetVoiceState), false, false);
        var state = await _alice.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync));
        Assert.True(state.VoiceParticipants.Single(p => p.UserId == _bobAuth.User.Id).IsMuted);
    }

    [Fact]
    public async Task LeavingMovingAndDeletingChannels_AreAnnounced()
    {
        var left = new List<(long Channel, long User)>();
        _carol.On<long, long>(nameof(IChatClient.VoiceLeft), (channel, user) =>
        {
            lock (left)
            {
                left.Add((channel, user));
            }
        });
        var closed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _alice.On<string, string>(nameof(IChatClient.MediaSessionClosed), (session, _) => closed.TrySetResult(session));

        var alice = await JoinAsync(_alice, _lounge, 440);
        await JoinAsync(_bob, _lounge, null);

        // Text channels cannot be joined.
        var state = await _alice.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync));
        var general = state.Channels.First(c => c.Kind == ChannelKind.Text).Id;
        await Assert.ThrowsAsync<HubException>(() => _alice.InvokeAsync<MediaSessionAnswer>(nameof(IChatHub.JoinVoice), general, "v=0"));

        // Joining another channel moves Alice; her previous session is closed.
        var second = await _alice.InvokeAsync<ChannelDto>(nameof(IChatHub.CreateChannel), "Second room", "", ChannelKind.Voice);
        await JoinAsync(_alice, second.Id, null);
        Assert.Equal(alice.SessionId, await closed.Task.WaitAsync(Timeout));

        // Bob leaves explicitly.
        await _bob.InvokeAsync(nameof(IChatHub.LeaveVoice));

        // Deleting the channel disconnects Alice.
        await _alice.InvokeAsync(nameof(IChatHub.DeleteChannel), second.Id);

        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (left)
            {
                if (left.Count >= 3)
                {
                    break;
                }
            }

            await Task.Delay(50);
        }

        lock (left)
        {
            Assert.Contains((_lounge, _aliceAuth.User.Id), left);
            Assert.Contains((_lounge, _bobAuth.User.Id), left);
            Assert.Contains((second.Id, _aliceAuth.User.Id), left);
        }

        state = await _carol.InvokeAsync<SyncStateDto>(nameof(IChatHub.Sync));
        Assert.Empty(state.VoiceParticipants);
    }
}
