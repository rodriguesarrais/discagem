using Discagem.Client.Services.Audio;
using Discagem.Media.Audio;
using Discagem.Media.WebRtc;
using Discagem.Shared;
using SIPSorcery.Net;

namespace Discagem.Client.Services;

/// <summary>
/// Client side of voice channels: one call at a time, made of a <see cref="VoicePeer"/> to the
/// server, a <see cref="VoiceEngine"/> and the microphone and speaker streams. Events are raised on
/// background threads.
/// </summary>
public sealed class VoiceService : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DeviceRetryDelay = TimeSpan.FromSeconds(1);
    private const int MaxDeviceRetries = 3;

    private readonly ChatConnection _hub;
    private readonly IReadOnlyList<IceServerConfig> _iceServers;
    private readonly ClientSettings _settings;
    private readonly long _myUserId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Call? _call;
    private MicrophoneTest? _micTest;
    private string? _droppedSessionId;

    public VoiceService(ChatConnection hub, IReadOnlyList<IceServerConfig> iceServers, ClientSettings settings, IAudioBackend audio, long myUserId)
    {
        _hub = hub;
        _iceServers = iceServers;
        _settings = settings;
        _myUserId = myUserId;
        Audio = audio;
        _hub.IceCandidate += OnRemoteCandidate;
        _hub.MediaSessionClosed += OnMediaSessionClosed;
    }

    public IAudioBackend Audio { get; }

    public long? ChannelId => _call?.ChannelId;

    public bool IsInCall => _call is not null;

    public bool IsMuted => _settings.VoiceMuted;

    public bool IsDeafened => _settings.VoiceDeafened;

    /// <summary>Current microphone level in dBFS (in a call or while testing the microphone).</summary>
    public float InputLevelDb => _call?.Engine.InputLevelDb ?? _micTest?.LevelDb ?? VoiceActivityDetector.SilenceDb;

    /// <summary>A user started or stopped being heard (including the local user transmitting).</summary>
    public event Action<long, bool>? SpeakingChanged;

    /// <summary>
    /// The call ended without the user asking. The flag tells whether rejoining makes sense: true when
    /// the connection was lost, false when the server ended the call (channel deleted, joined elsewhere).
    /// </summary>
    public event Action<string, bool>? Disconnected;

    /// <summary>A microphone or speaker problem that does not end the call.</summary>
    public event Action<string>? AudioProblem;

    public async Task JoinAsync(long channelId)
    {
        await _gate.WaitAsync();
        try
        {
            await EndCallAsync(notifyServer: true, playCue: false);
            StopMicrophoneTest();

            var engine = new VoiceEngine
            {
                IsMuted = _settings.VoiceMuted,
                IsDeafened = _settings.VoiceDeafened,
                InputThresholdDb = _settings.VoiceThresholdDb,
                OutputVolume = _settings.VoiceOutputVolume,
            };
            foreach (var (userId, gain) in _settings.UserVolumes)
            {
                engine.Mixer.SetSpeakerGain((uint)userId, gain);
            }

            var peer = new VoicePeer(_iceServers);
            var call = new Call(channelId, peer, engine);
            var relay = new CandidateRelay(_hub);
            peer.LocalCandidate += relay.OnLocalCandidate;
            engine.PacketReady = peer.Send;
            peer.PacketReceived += engine.Mixer.Push;
            engine.Mixer.SpeakingChanged += (speaker, speaking) => SpeakingChanged?.Invoke(speaker, speaking);
            engine.TransmittingChanged += speaking => SpeakingChanged?.Invoke(_myUserId, speaking);
            peer.StateChanged += state =>
            {
                if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
                {
                    _ = DropAsync(call, "The voice connection was lost.", canRejoin: true);
                }
            };

            var joined = false;
            try
            {
                var offer = await peer.CreateOfferAsync();
                var answer = await _hub.JoinVoiceAsync(channelId, offer);
                joined = true;
                call.SessionId = answer.SessionId;
                _call = call;
                relay.SetSession(answer.SessionId);
                peer.ApplyAnswer(answer.AnswerSdp);
                if (_settings.VoiceMuted || _settings.VoiceDeafened)
                {
                    await _hub.SetVoiceStateAsync(_settings.VoiceMuted, _settings.VoiceDeafened);
                }

                if (!await peer.WaitForConnectionAsync(ConnectTimeout))
                {
                    throw new InvalidOperationException(
                        "Could not establish the voice connection. Make sure the server's UDP media ports are reachable.");
                }
            }
            catch
            {
                _call = null;
                call.Dispose();
                if (joined)
                {
                    await SafeHubAsync(_hub.LeaveVoiceAsync);
                }

                throw;
            }

            OpenMicrophone(call, attempt: 0);
            OpenSpeakers(call, attempt: 0);
            engine.Mixer.PlayCue(VoiceCues.Join);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task LeaveAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await EndCallAsync(notifyServer: true, playCue: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The chat connection dropped, so the server has already ended the call: hang up quietly.
    /// The caller rejoins once the connection is back.
    /// </summary>
    public async Task SuspendAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await EndCallAsync(notifyServer: false, playCue: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetMutedAsync(bool muted)
    {
        _settings.VoiceMuted = muted;
        _settings.Save();
        await ApplyStateAsync(muted ? VoiceCues.Mute : VoiceCues.Unmute);
    }

    public async Task SetDeafenedAsync(bool deafened)
    {
        _settings.VoiceDeafened = deafened;
        _settings.Save();
        await ApplyStateAsync(deafened ? VoiceCues.Mute : VoiceCues.Unmute);
    }

    private async Task ApplyStateAsync(float[] cue)
    {
        if (_call is not { } call)
        {
            return;
        }

        call.Engine.IsMuted = _settings.VoiceMuted;
        call.Engine.IsDeafened = _settings.VoiceDeafened;
        call.Engine.Mixer.PlayCue(cue);
        await SafeHubAsync(() => _hub.SetVoiceStateAsync(_settings.VoiceMuted, _settings.VoiceDeafened));
    }

    /// <summary>Applies changed audio settings (devices, sensitivity, volume) to the current call.</summary>
    public void ApplyAudioSettings(bool devicesChanged)
    {
        if (_call is { } call)
        {
            call.Engine.InputThresholdDb = _settings.VoiceThresholdDb;
            call.Engine.OutputVolume = _settings.VoiceOutputVolume;
            if (devicesChanged)
            {
                OpenMicrophone(call, attempt: 0);
                OpenSpeakers(call, attempt: 0);
            }
        }

        if (_micTest is not null && devicesChanged)
        {
            StopMicrophoneTest();
            StartMicrophoneTest();
        }
    }

    public float GetUserVolume(long userId) => _settings.UserVolumes.GetValueOrDefault(userId, 1f);

    public void SetUserVolume(long userId, float gain)
    {
        gain = Math.Clamp(gain, 0f, 2f);
        if (Math.Abs(gain - 1f) < 0.01f)
        {
            _settings.UserVolumes.Remove(userId);
        }
        else
        {
            _settings.UserVolumes[userId] = gain;
        }

        _settings.Save();
        _call?.Engine.Mixer.SetSpeakerGain((uint)userId, gain);
    }

    /// <summary>Someone left the call: drop their audio state and play the leave cue.</summary>
    public void OnParticipantLeft(long channelId, long userId)
    {
        if (_call is { } call && call.ChannelId == channelId && userId != _myUserId)
        {
            call.Engine.Mixer.RemoveSpeaker((uint)userId);
            call.Engine.Mixer.PlayCue(VoiceCues.Leave);
        }
    }

    /// <summary>Someone joined the call: play the join cue.</summary>
    public void OnParticipantJoined(long channelId, long userId)
    {
        if (_call is { } call && call.ChannelId == channelId && userId != _myUserId)
        {
            call.Engine.Mixer.PlayCue(VoiceCues.Join);
        }
    }

    /// <summary>Opens the microphone outside a call so the settings dialog can show its level.</summary>
    public void StartMicrophoneTest()
    {
        if (_call is not null || _micTest is not null)
        {
            return;
        }

        try
        {
            _micTest = new MicrophoneTest(Audio, _settings.VoiceInputDevice);
        }
        catch (AudioDeviceException ex)
        {
            AudioProblem?.Invoke(ex.Message);
        }
    }

    public void StopMicrophoneTest()
    {
        Interlocked.Exchange(ref _micTest, null)?.Dispose();
    }

    private void OpenMicrophone(Call call, int attempt)
    {
        call.ReplaceInput(null);
        try
        {
            var input = Audio.OpenInput(_settings.VoiceInputDevice, call.Engine.WriteCapture);
            input.Failed += reason => _ = RetryDeviceAsync(call, input: true, attempt, reason);
            if (!call.ReplaceInput(input))
            {
                input.Dispose();
            }
        }
        catch (AudioDeviceException ex)
        {
            AudioProblem?.Invoke($"{ex.Message} Others will not hear you.");
        }
    }

    private void OpenSpeakers(Call call, int attempt)
    {
        call.ReplaceOutput(null);
        try
        {
            var output = Audio.OpenOutput(_settings.VoiceOutputDevice, call.Engine.ReadPlayback);
            output.Failed += reason => _ = RetryDeviceAsync(call, input: false, attempt, reason);
            if (!call.ReplaceOutput(output))
            {
                output.Dispose();
            }
        }
        catch (AudioDeviceException ex)
        {
            AudioProblem?.Invoke($"{ex.Message} You will not hear the others.");
        }
    }

    /// <summary>A device stopped (typically unplugged): reopen it, falling back to the default device.</summary>
    private async Task RetryDeviceAsync(Call call, bool input, int attempt, string reason)
    {
        if (attempt >= MaxDeviceRetries)
        {
            AudioProblem?.Invoke($"The {(input ? "microphone" : "audio output")} stopped working: {reason}");
            return;
        }

        await Task.Delay(DeviceRetryDelay);
        if (_call != call)
        {
            return;
        }

        if (input)
        {
            OpenMicrophone(call, attempt + 1);
        }
        else
        {
            OpenSpeakers(call, attempt + 1);
        }
    }

    private async Task DropAsync(Call call, string reason, bool canRejoin)
    {
        await _gate.WaitAsync();
        try
        {
            if (_call != call)
            {
                return;
            }

            _droppedSessionId = call.SessionId;
            await EndCallAsync(notifyServer: true, playCue: false);
        }
        finally
        {
            _gate.Release();
        }

        Disconnected?.Invoke(reason, canRejoin);
    }

    /// <summary>Ends the current call. Must be called with the gate held.</summary>
    private async Task EndCallAsync(bool notifyServer, bool playCue)
    {
        var call = _call;
        if (call is null)
        {
            return;
        }

        _call = null;
        if (playCue)
        {
            call.Engine.Mixer.PlayCue(VoiceCues.Leave);
        }

        if (notifyServer)
        {
            await SafeHubAsync(_hub.LeaveVoiceAsync);
        }

        if (playCue)
        {
            // Let the cue play before the speakers close.
            await Task.Delay(220);
        }

        var speakers = call.Engine.Mixer.Speakers.Select(s => s.SpeakerId).ToList();
        call.Dispose();

        // Nobody is heard any more.
        SpeakingChanged?.Invoke(_myUserId, false);
        foreach (var speaker in speakers)
        {
            SpeakingChanged?.Invoke(speaker, false);
        }
    }

    private void OnRemoteCandidate(IceCandidateDto candidate)
    {
        if (_call is { } call && call.SessionId == candidate.SessionId)
        {
            call.Peer.AddRemoteCandidate(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
        }
    }

    private void OnMediaSessionClosed(string sessionId, string reason)
    {
        if (_call is { } call && call.SessionId == sessionId)
        {
            _ = DropAsync(call, $"Disconnected from voice: {reason}.", canRejoin: false);
        }
        else if (sessionId == _droppedSessionId)
        {
            // The media connection closed first and looked like a network failure; it was the server
            // hanging up on purpose, so do not rejoin.
            Disconnected?.Invoke($"Disconnected from voice: {reason}.", false);
        }
    }

    private static async Task SafeHubAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception)
        {
            // The server ends voice sessions on its own when the connection drops.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _hub.IceCandidate -= OnRemoteCandidate;
        _hub.MediaSessionClosed -= OnMediaSessionClosed;
        StopMicrophoneTest();
        await _gate.WaitAsync();
        try
        {
            await EndCallAsync(notifyServer: true, playCue: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>One voice call and the resources it owns.</summary>
    private sealed class Call(long channelId, VoicePeer peer, VoiceEngine engine) : IDisposable
    {
        private readonly object _sync = new();
        private IAudioStream? _input;
        private IAudioStream? _output;
        private bool _disposed;

        public long ChannelId { get; } = channelId;

        public string? SessionId { get; set; }

        public VoicePeer Peer { get; } = peer;

        public VoiceEngine Engine { get; } = engine;

        /// <summary>Swaps the microphone stream. Returns false if the call has already ended.</summary>
        public bool ReplaceInput(IAudioStream? input) => Replace(ref _input, input);

        public bool ReplaceOutput(IAudioStream? output) => Replace(ref _output, output);

        private bool Replace(ref IAudioStream? slot, IAudioStream? value)
        {
            IAudioStream? previous;
            lock (_sync)
            {
                if (_disposed && value is not null)
                {
                    return false;
                }

                previous = slot;
                slot = value;
            }

            previous?.Dispose();
            return true;
        }

        public void Dispose()
        {
            IAudioStream? input;
            IAudioStream? output;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                input = _input;
                output = _output;
                _input = _output = null;
            }

            // Devices first, so no audio thread touches the engine once it is disposed.
            input?.Dispose();
            output?.Dispose();
            Peer.Dispose();
            Engine.Dispose();
        }
    }

    /// <summary>Measures the microphone level outside a call.</summary>
    private sealed class MicrophoneTest : IDisposable
    {
        private readonly VoiceActivityDetector _vad = new();
        private readonly HighPassFilter _filter = new();
        private readonly float[] _frame = new float[DiscagemAudio.FrameSamples];
        private readonly IAudioStream _stream;
        private int _fill;

        public MicrophoneTest(IAudioBackend audio, string deviceId)
        {
            _stream = audio.OpenInput(deviceId, OnSamples);
        }

        public float LevelDb => _vad.LevelDb;

        private void OnSamples(ReadOnlySpan<float> samples)
        {
            while (!samples.IsEmpty)
            {
                var count = Math.Min(_frame.Length - _fill, samples.Length);
                samples[..count].CopyTo(_frame.AsSpan(_fill));
                _fill += count;
                samples = samples[count..];
                if (_fill == _frame.Length)
                {
                    _filter.Process(_frame);
                    _vad.Process(_frame);
                    _fill = 0;
                }
            }
        }

        public void Dispose() => _stream.Dispose();
    }
}
