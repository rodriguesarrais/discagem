using System.Collections.Concurrent;
using Discagem.Client.Services.Capture;
using Discagem.Media.Imaging;
using Discagem.Media.Pipeline;
using Discagem.Media.WebRtc;
using Discagem.Shared;
using SIPSorcery.Net;

namespace Discagem.Client.Services;

/// <summary>
/// Client side of screen sharing: publishes the local screen and watches other people's streams,
/// using the chat connection for signalling.
/// </summary>
public sealed class ScreenShareService : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly ChatConnection _hub;
    private readonly IReadOnlyList<IceServerConfig> _iceServers;
    private readonly ConcurrentDictionary<string, MediaPeer> _peers = new();
    private readonly ConcurrentDictionary<string, WatchSession> _watching = new();
    private PublishSession? _publish;

    public ScreenShareService(ChatConnection hub, IReadOnlyList<IceServerConfig> iceServers)
    {
        _hub = hub;
        _iceServers = iceServers;
        _hub.IceCandidate += OnRemoteCandidate;
        _hub.MediaSessionClosed += OnMediaSessionClosed;
    }

    public bool IsLive => _publish is not null;

    public string? LiveStreamId => _publish?.StreamId;

    /// <summary>Raised (on a background thread) with publishing statistics.</summary>
    public event Action<PublishStats>? PublishStatsUpdated;

    /// <summary>Raised (on a background thread) when publishing stops unexpectedly.</summary>
    public event Action<string>? PublishStopped;

    public async Task StartAsync(CaptureSource source, StreamQuality quality, string title)
    {
        await StopAsync();

        var frameSource = source.Create();
        ScreenSharePublisher? publisher = null;
        CaptureEncodePipeline? pipeline = null;
        try
        {
            var firstSize = await Task.Run(() => frameSource.TryCapture(out var first) ? (first.Width, first.Height) : (0, 0));
            if (firstSize.Item1 == 0)
            {
                throw new InvalidOperationException("The selected screen or window could not be captured.");
            }

            var (width, height) = BgraScaler.FitWithin(firstSize.Item1, firstSize.Item2, quality.MaxHeight);
            publisher = new ScreenSharePublisher(_iceServers, quality.BitrateKbpsFor(width, height));
            var relay = new CandidateRelay(_hub);
            publisher.LocalCandidate += relay.OnLocalCandidate;

            var offer = await publisher.CreateOfferAsync();
            var answer = await _hub.StartStreamAsync(new StartStreamRequest(offer, title, width, height, quality.FrameRate));
            _peers[answer.SessionId] = publisher;
            relay.SetSession(answer.SessionId);
            publisher.ApplyAnswer(answer.AnswerSdp);

            if (!await publisher.WaitForConnectionAsync(ConnectTimeout))
            {
                _peers.TryRemove(answer.SessionId, out _);
                await SafeStopStreamAsync();
                throw new InvalidOperationException(
                    "Could not establish the media connection to the server. Make sure the server's UDP media ports are reachable.");
            }

            pipeline = new CaptureEncodePipeline(frameSource, publisher, quality);
            var session = new PublishSession(answer.StreamId, answer.SessionId, frameSource, publisher, pipeline);
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
        }
        catch
        {
            pipeline?.Dispose();
            publisher?.Dispose();
            frameSource.Dispose();
            throw;
        }
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

    /// <summary>Starts watching a stream. The returned session delivers decoded frames.</summary>
    public async Task<WatchSession> WatchAsync(string streamId)
    {
        var viewer = new ScreenShareViewer(_iceServers);
        var decoder = new DecodePipeline();
        viewer.FrameReceived += decoder.Enqueue;
        decoder.DecodeError += viewer.ReportDecodeError;
        var relay = new CandidateRelay(_hub);
        viewer.LocalCandidate += relay.OnLocalCandidate;

        try
        {
            var offer = await viewer.CreateOfferAsync();
            var answer = await _hub.WatchStreamAsync(streamId, offer);
            var session = new WatchSession(this, streamId, answer.SessionId, viewer, decoder);
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
        IFrameSource source,
        ScreenSharePublisher publisher,
        CaptureEncodePipeline pipeline) : IDisposable
    {
        private int _disposed;

        public string StreamId { get; } = streamId;

        public string SessionId { get; } = sessionId;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            pipeline.Dispose();
            publisher.Dispose();
            source.Dispose();
        }
    }

    /// <summary>Holds local ICE candidates until the server has assigned a session id.</summary>
    private sealed class CandidateRelay(ChatConnection hub)
    {
        private readonly List<IceCandidateDto> _pending = [];
        private readonly object _sync = new();
        private string? _sessionId;

        public void OnLocalCandidate(string candidate, string? sdpMid, int? sdpMLineIndex)
        {
            IceCandidateDto dto;
            lock (_sync)
            {
                if (_sessionId is null)
                {
                    _pending.Add(new IceCandidateDto("", candidate, sdpMid, sdpMLineIndex));
                    return;
                }

                dto = new IceCandidateDto(_sessionId, candidate, sdpMid, sdpMLineIndex);
            }

            Send(dto);
        }

        public void SetSession(string sessionId)
        {
            List<IceCandidateDto> pending;
            lock (_sync)
            {
                _sessionId = sessionId;
                pending = _pending.Select(c => c with { SessionId = sessionId }).ToList();
                _pending.Clear();
            }

            foreach (var candidate in pending)
            {
                Send(candidate);
            }
        }

        private void Send(IceCandidateDto candidate) => _ = Task.Run(async () =>
        {
            try
            {
                await hub.AddIceCandidateAsync(candidate);
            }
            catch (Exception)
            {
                // Host candidates are already part of the offer; losing a trickled one is not fatal.
            }
        });
    }
}

/// <summary>An active subscription to someone's stream.</summary>
public sealed class WatchSession : IDisposable
{
    private readonly ScreenShareService _owner;
    private int _disposed;

    internal WatchSession(ScreenShareService owner, string streamId, string sessionId, ScreenShareViewer viewer, DecodePipeline decoder)
    {
        _owner = owner;
        StreamId = streamId;
        SessionId = sessionId;
        Viewer = viewer;
        Decoder = decoder;
    }

    public string StreamId { get; }

    public string SessionId { get; }

    public ScreenShareViewer Viewer { get; }

    public DecodePipeline Decoder { get; }

    /// <summary>Raised when the server closed the session (for example because the stream ended).</summary>
    public event Action<string>? Closed;

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
        Viewer.Dispose();
        Decoder.Dispose();
        return true;
    }
}
