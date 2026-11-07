using System.Collections.Concurrent;
using Discagem.Client.Services.Audio;
using Discagem.Client.Services.Capture;
using Discagem.Media.Audio;
using Discagem.Media.Imaging;
using Discagem.Media.Pipeline;
using Discagem.Media.WebRtc;
using Discagem.Shared;
using SIPSorcery.Net;

namespace Discagem.Client.Services;

/// <summary>
/// Client side of screen sharing: publishes the local screen (and, optionally, what the computer
/// plays) and watches other people's streams, using the chat connection for signalling.
/// </summary>
public sealed class ScreenShareService : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly ChatConnection _hub;
    private readonly IReadOnlyList<IceServerConfig> _iceServers;
    private readonly IAudioBackend _audio;
    private readonly Func<string?> _outputDevice;
    private readonly Func<string, float>? _applicationVolume;
    private readonly ConcurrentDictionary<string, MediaPeer> _peers = new();
    private readonly ConcurrentDictionary<string, WatchSession> _watching = new();
    private PublishSession? _publish;

    /// <param name="applicationVolume">Starting volume of an application in the sound of a stream, from its key.</param>
    public ScreenShareService(
        ChatConnection hub,
        IReadOnlyList<IceServerConfig> iceServers,
        IAudioBackend audio,
        Func<string?> outputDevice,
        Func<string, float>? applicationVolume = null)
    {
        _hub = hub;
        _iceServers = iceServers;
        _audio = audio;
        _outputDevice = outputDevice;
        _applicationVolume = applicationVolume;
        _hub.IceCandidate += OnRemoteCandidate;
        _hub.MediaSessionClosed += OnMediaSessionClosed;
    }

    public bool IsLive => _publish is not null;

    public string? LiveStreamId => _publish?.StreamId;

    /// <summary>Raised (on a background thread) with publishing statistics.</summary>
    public event Action<PublishStats>? PublishStatsUpdated;

    /// <summary>Raised (on a background thread) when publishing stops unexpectedly.</summary>
    public event Action<string>? PublishStopped;

    /// <summary>Raised (on a background thread) when the stream's audio could not be captured; the video carries on.</summary>
    public event Action<string>? PublishAudioProblem;

    /// <summary>Whether this computer can send its audio with a screen share.</summary>
    public bool CanShareAudio => _audio.SupportsSystemCapture;

    /// <summary>The mixer of the live stream's sound, once its capture runs; null otherwise.</summary>
    public ApplicationAudioMixer? PublishMixer => _publish?.AudioMixer;

    /// <summary>Raised (on any thread) when the applications in the live stream's sound change, or its capture starts.</summary>
    public event Action? PublishApplicationsChanged;

    /// <summary>
    /// A short description of the live stream's sound for the streamer: what is captured and whether
    /// anything is being sent right now (compared with the previous call).
    /// </summary>
    public string DescribePublishAudio()
    {
        if (_publish is not { } session)
        {
            return "";
        }

        if (!session.WantsAudio)
        {
            return "sound off";
        }

        if (session.AudioError is { } error)
        {
            return $"no sound: {error}";
        }

        if (session.AudioDescription is not { } description)
        {
            return "starting sound…";
        }

        var sent = session.Publisher.AudioPacketsSent;
        var sending = sent > session.LastAudioPacketsSent;
        session.LastAudioPacketsSent = sent;
        return sending ? $"sound: {description}" : $"sound: {description} (nothing playing)";
    }

    public async Task StartAsync(CaptureSource source, StreamQuality quality, string title, bool shareAudio = false)
    {
        await StopAsync();

        var frameSource = source.Create();
        ScreenSharePublisher? publisher = null;
        CaptureEncodePipeline? pipeline = null;
        MediaSessionAnswer? answer = null;
        try
        {
            var firstSize = await Task.Run(() => frameSource.TryCapture(out var first) ? (first.Width, first.Height) : (0, 0));
            if (firstSize.Item1 == 0)
            {
                throw new InvalidOperationException("The selected screen or window could not be captured.");
            }

            var (width, height) = BgraScaler.FitWithin(firstSize.Item1, firstSize.Item2, quality.MaxHeight);
            var withAudio = shareAudio && CanShareAudio;
            publisher = new ScreenSharePublisher(_iceServers, quality.BitrateKbpsFor(width, height), withAudio);
            var relay = new CandidateRelay(_hub);
            publisher.LocalCandidate += relay.OnLocalCandidate;

            var offer = await publisher.CreateOfferAsync();
            answer = await _hub.StartStreamAsync(new StartStreamRequest(offer, title, width, height, quality.FrameRate));
            _peers[answer.SessionId] = publisher;
            relay.SetSession(answer.SessionId);
            publisher.ApplyAnswer(answer.AnswerSdp);

            if (!await publisher.WaitForConnectionAsync(ConnectTimeout))
            {
                throw new InvalidOperationException(
                    "Could not establish the media connection to the server. Make sure the server's UDP media ports are reachable.");
            }

            // From here on the pipeline owns the frame source.
            pipeline = new CaptureEncodePipeline(frameSource, publisher, quality);
            var session = new PublishSession(answer.StreamId, answer.SessionId, publisher, pipeline) { WantsAudio = shareAudio };
            pipeline.StatsUpdated += stats => PublishStatsUpdated?.Invoke(stats);
            pipeline.Failed += reason => _ = StopWithReasonAsync(session, $"Screen sharing stopped: {reason}");
            publisher.StateChanged += state =>
            {
                if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
                {
                    _ = StopWithReasonAsync(session, "Screen sharing stopped: the media connection was lost.");
                }
            };

            _publish = session;
            pipeline.Start();
            if (withAudio)
            {
                _ = Task.Run(() => StartAudio(session, publisher, source.Kind == CaptureKind.Window ? source.ProcessId : null));
            }
            else if (shareAudio)
            {
                session.AudioError = "this computer cannot capture its sound";
            }
        }
        catch
        {
            if (pipeline is null)
            {
                frameSource.Dispose();
            }
            else
            {
                pipeline.Dispose();
            }

            publisher?.Dispose();
            if (answer is not null)
            {
                // The server already announced the stream; take it down instead of leaving a ghost behind.
                _peers.TryRemove(answer.SessionId, out _);
                await SafeStopStreamAsync();
            }

            throw;
        }
    }

    /// <summary>Captures the computer's audio (or the shared window's application) and sends it with the stream.</summary>
    private void StartAudio(PublishSession session, ScreenSharePublisher publisher, int? processId)
    {
        var sender = new StreamAudioSender(publisher.SendAudio);
        IMixedAudioStream capture;
        try
        {
            capture = _audio.OpenSystemCapture(processId, sender.Write, _applicationVolume);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever went wrong, say so: a stream that is silent without explanation looks like a bug.
            sender.Dispose();
            var reason = ex is AudioDeviceException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            session.AudioError = reason.TrimEnd('.');
            ClientLog.Error($"Stream audio capture failed (process {processId?.ToString() ?? "none"}): {ex}");
            PublishAudioProblem?.Invoke($"Your stream has no sound: {reason}");
            return;
        }

        ClientLog.Info($"Stream audio: capturing {capture.DeviceName} with {_audio.Name}.");
        capture.Failed += reason =>
        {
            session.AudioError = $"capture stopped ({reason})";
            ClientLog.Error($"Stream audio capture stopped: {reason}");
            PublishAudioProblem?.Invoke($"Your stream's sound stopped: {reason}");
        };
        session.AudioDescription = capture.DeviceName;
        capture.Mixer.ApplicationsChanged += () => PublishApplicationsChanged?.Invoke();
        session.AttachAudio(capture, sender);
        PublishApplicationsChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        var session = Interlocked.Exchange(ref _publish, null);
        if (session is null)
        {
            return;
        }

        session.Dispose();
        _peers.TryRemove(session.SessionId, out _);
        await SafeStopStreamAsync();
    }

    private async Task StopWithReasonAsync(PublishSession session, string reason)
    {
        if (Interlocked.CompareExchange(ref _publish, null, session) != session)
        {
            return;
        }

        session.Dispose();
        _peers.TryRemove(session.SessionId, out _);
        await SafeStopStreamAsync();
        PublishStopped?.Invoke(reason);
    }

    private async Task SafeStopStreamAsync()
    {
        try
        {
            await _hub.StopStreamAsync();
        }
        catch (Exception)
        {
            // The server ends the stream on its own when the connection drops.
        }
    }

    /// <summary>Starts watching a stream. The returned session delivers decoded frames and plays the stream's audio.</summary>
    public async Task<WatchSession> WatchAsync(string streamId)
    {
        var viewer = new ScreenShareViewer(_iceServers);
        var decoder = new DecodePipeline();
        var audio = new StreamAudioReceiver(0);
        viewer.FrameReceived += decoder.Enqueue;
        viewer.AudioReceived += (sequence, opus) => audio.Push(sequence, opus.Span);
        decoder.DecodeError += viewer.ReportDecodeError;
        var relay = new CandidateRelay(_hub);
        viewer.LocalCandidate += relay.OnLocalCandidate;

        try
        {
            var offer = await viewer.CreateOfferAsync();
            var answer = await _hub.WatchStreamAsync(streamId, offer);
            var session = new WatchSession(this, streamId, answer.SessionId, viewer, decoder, audio, source => _audio.OpenOutput(_outputDevice(), source, channels: 2));
            _peers[answer.SessionId] = viewer;
            _watching[answer.SessionId] = session;
            relay.SetSession(answer.SessionId);
            viewer.ApplyAnswer(answer.AnswerSdp);
            return session;
        }
        catch
        {
            viewer.Dispose();
            decoder.Dispose();
            audio.Dispose();
            throw;
        }
    }

    internal void OnWatchSessionDisposed(WatchSession session, bool notifyServer)
    {
        _peers.TryRemove(session.SessionId, out _);
        _watching.TryRemove(session.SessionId, out _);
        if (notifyServer)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _hub.StopWatchingAsync(session.SessionId);
                }
                catch (Exception)
                {
                    // Best effort.
                }
            });
        }
    }

    private void OnRemoteCandidate(IceCandidateDto candidate)
    {
        if (_peers.TryGetValue(candidate.SessionId, out var peer))
        {
            peer.AddRemoteCandidate(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
        }
    }

    private void OnMediaSessionClosed(string sessionId, string reason)
    {
        if (_watching.TryGetValue(sessionId, out var session))
        {
            session.CloseFromServer(reason);
        }
        else if (_publish is { } publish && publish.SessionId == sessionId)
        {
            _ = StopWithReasonAsync(publish, $"Screen sharing stopped: {reason}.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _hub.IceCandidate -= OnRemoteCandidate;
        _hub.MediaSessionClosed -= OnMediaSessionClosed;
        foreach (var session in _watching.Values.ToList())
        {
            session.Dispose();
        }

        await StopAsync();
    }

    private sealed class PublishSession(
        string streamId,
        string sessionId,
        ScreenSharePublisher publisher,
        CaptureEncodePipeline pipeline) : IDisposable
    {
        private readonly object _sync = new();
        private IMixedAudioStream? _audioCapture;
        private StreamAudioSender? _audioSender;
        private bool _disposed;

        public string StreamId { get; } = streamId;

        public string SessionId { get; } = sessionId;

        public ScreenSharePublisher Publisher { get; } = publisher;

        public bool WantsAudio { get; init; }

        /// <summary>What the audio capture records, once it runs.</summary>
        public volatile string? AudioDescription;

        public volatile string? AudioError;

        public long LastAudioPacketsSent;

        public ApplicationAudioMixer? AudioMixer
        {
            get
            {
                lock (_sync)
                {
                    return _disposed ? null : _audioCapture?.Mixer;
                }
            }
        }

        /// <summary>Hands over the audio capture, which is opened after the stream started (or disposes it if the stream has stopped since).</summary>
        public void AttachAudio(IMixedAudioStream capture, StreamAudioSender sender)
        {
            lock (_sync)
            {
                if (!_disposed)
                {
                    _audioCapture = capture;
                    _audioSender = sender;
                    return;
                }
            }

            capture.Dispose();
            sender.Dispose();
        }

        public void Dispose()
        {
            IMixedAudioStream? capture;
            StreamAudioSender? sender;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                capture = _audioCapture;
                sender = _audioSender;
            }

            capture?.Dispose();
            sender?.Dispose();

            // The pipeline disposes the frame source once its capture thread has exited.
            pipeline.Dispose();
            Publisher.Dispose();
        }
    }
}

/// <summary>An active subscription to someone's stream.</summary>
public sealed class WatchSession : IDisposable
{
    private readonly ScreenShareService _owner;
    private readonly Func<AudioSource, IAudioStream> _openOutput;
    private readonly object _audioSync = new();
    private IAudioStream? _output;
    private int _disposed;

    internal WatchSession(
        ScreenShareService owner,
        string streamId,
        string sessionId,
        ScreenShareViewer viewer,
        DecodePipeline decoder,
        StreamAudioReceiver audio,
        Func<AudioSource, IAudioStream> openOutput)
    {
        _owner = owner;
        _openOutput = openOutput;
        StreamId = streamId;
        SessionId = sessionId;
        Viewer = viewer;
        Decoder = decoder;
        Audio = audio;

        // The speakers are only opened once the stream actually sends sound.
        audio.Started += () => Task.Run(OpenOutput);
    }

    public string StreamId { get; }

    public string SessionId { get; }

    public ScreenShareViewer Viewer { get; }

    public DecodePipeline Decoder { get; }

    /// <summary>The stream's audio: volume and mute apply here.</summary>
    public StreamAudioReceiver Audio { get; }

    /// <summary>Raised when the server closed the session (for example because the stream ended).</summary>
    public event Action<string>? Closed;

    /// <summary>Raised (on a background thread) when the stream's audio cannot be played.</summary>
    public event Action<string>? AudioProblem;

    private void OpenOutput()
    {
        IAudioStream output;
        try
        {
            output = _openOutput(Audio.Read);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ClientLog.Error($"Stream audio playback failed: {ex}");
            AudioProblem?.Invoke($"The stream's sound cannot be played: {ex.Message}");
            return;
        }

        ClientLog.Info($"Stream audio: playing on {output.DeviceName}.");
        output.Failed += reason =>
        {
            ClientLog.Error($"Stream audio playback stopped: {reason}");
            AudioProblem?.Invoke($"The stream's sound stopped: {reason}");
        };
        lock (_audioSync)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _output = output;
                return;
            }
        }

        output.Dispose();
    }

    internal void CloseFromServer(string reason)
    {
        if (Dispose(notifyServer: false))
        {
            Closed?.Invoke(reason);
        }
    }

    public void Dispose() => Dispose(notifyServer: true);

    private bool Dispose(bool notifyServer)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return false;
        }

        _owner.OnWatchSessionDisposed(this, notifyServer);
        IAudioStream? output;
        lock (_audioSync)
        {
            output = _output;
            _output = null;
        }

        // Stop the speakers before the receiver they read from.
        output?.Dispose();
        Viewer.Dispose();
        Decoder.Dispose();
        Audio.Dispose();
        return true;
    }
}
