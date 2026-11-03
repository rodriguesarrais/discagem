using System.Collections.Concurrent;
using Discagem.Client.Resources;
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
    private readonly ConcurrentDictionary<string, MediaPeer> _peers = new();
    private readonly ConcurrentDictionary<string, WatchSession> _watching = new();
    private PublishSession? _publish;

    public ScreenShareService(ChatConnection hub, IReadOnlyList<IceServerConfig> iceServers, IAudioBackend audio, Func<string?> outputDevice)
    {
        _hub = hub;
        _iceServers = iceServers;
        _audio = audio;
        _outputDevice = outputDevice;
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
            return Strings.Stream_SoundOff;
        }

        if (session.AudioError is { } error)
        {
            return string.Format(Strings.Stream_NoSound, error);
        }

        if (session.AudioDescription is not { } description)
        {
            return Strings.Stream_SoundStarting;
        }

        var sent = session.Publisher.AudioPacketsSent;
        var sending = sent > session.LastAudioPacketsSent;
        session.LastAudioPacketsSent = sent;
        return string.Format(sending ? Strings.Stream_SoundSending : Strings.Stream_SoundNothingPlaying, description);
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
                throw new InvalidOperationException(Strings.Stream_CaptureFailed);
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
                throw new InvalidOperationException(Strings.Stream_MediaConnectionFailed);
            }

            // From here on the pipeline owns the frame source.
            pipeline = new CaptureEncodePipeline(frameSource, publisher, quality);
            var session = new PublishSession(answer.StreamId, answer.SessionId, publisher, pipeline) { WantsAudio = shareAudio };
            pipeline.StatsUpdated += stats => PublishStatsUpdated?.Invoke(stats);
            pipeline.Failed += reason => _ = StopWithReasonAsync(session, string.Format(Strings.Stream_SharingStopped, reason));
            publisher.StateChanged += state =>
            {
                if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
                {
                    _ = StopWithReasonAsync(session, Strings.Stream_SharingConnectionLost);
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
                session.AudioError = Strings.Stream_CannotCaptureSound;
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
        IAudioStream capture;
        try
        {
            capture = _audio.OpenSystemCapture(processId, sender.Write);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever went wrong, say so: a stream that is silent without explanation looks like a bug.
            sender.Dispose();
            var reason = ex is AudioDeviceException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            session.AudioError = reason.TrimEnd('.');
            ClientLog.Error($"Stream audio capture failed (process {processId?.ToString() ?? "none"}): {ex}");
            PublishAudioProblem?.Invoke(string.Format(Strings.Stream_NoSoundProblem, reason));
            return;
        }

        ClientLog.Info($"Stream audio: capturing {capture.DeviceName} with {_audio.Name}.");
        capture.Failed += reason =>
        {
            session.AudioError = string.Format(Strings.Stream_CaptureStopped, reason);
            ClientLog.Error($"Stream audio capture stopped: {reason}");
            PublishAudioProblem?.Invoke(string.Format(Strings.Stream_SoundStoppedProblem, reason));
        };
        session.AudioDescription = capture.DeviceName;
        session.AttachAudio(capture, sender);
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
            session.CloseFromServer(ServerMessages.TranslateReason(reason));
        }
        else if (_publish is { } publish && publish.SessionId == sessionId)
        {
            _ = StopWithReasonAsync(publish, string.Format(Strings.Stream_SharingStoppedByServer, ServerMessages.TranslateReason(reason)));
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
        private IAudioStream? _audioCapture;
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

        /// <summary>Hands over the audio capture, which is opened after the stream started (or disposes it if the stream has stopped since).</summary>
        public void AttachAudio(IAudioStream capture, StreamAudioSender sender)
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
            IAudioStream? capture;
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
            AudioProblem?.Invoke(string.Format(Strings.Stream_PlaybackFailed, ex.Message));
            return;
        }

        ClientLog.Info($"Stream audio: playing on {output.DeviceName}.");
        output.Failed += reason =>
        {
            ClientLog.Error($"Stream audio playback stopped: {reason}");
            AudioProblem?.Invoke(string.Format(Strings.Stream_PlaybackStopped, reason));
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
