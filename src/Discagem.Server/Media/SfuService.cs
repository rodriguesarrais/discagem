using System.Collections.Concurrent;
using System.Security.Cryptography;
using Discagem.Media.WebRtc;
using Discagem.Server.Hubs;
using Discagem.Server.Options;
using Discagem.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using SIPSorcery.Net;

namespace Discagem.Server.Media;

/// <summary>Raised for user-facing media errors; the hub forwards the message to the client.</summary>
public sealed class MediaException(string message) : Exception(message);

/// <summary>
/// Selective forwarding unit for screen shares. Each client keeps one WebRTC connection per
/// stream it publishes or watches; the server answers the client's offers and relays RTP.
/// </summary>
public sealed class SfuService : IHostedService
{
    private readonly PeerConnectionFactory _factory;
    private readonly IHubContext<ChatHub, IChatClient> _hub;
    private readonly MediaOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SfuService> _logger;
    private readonly ConcurrentDictionary<string, LiveStream> _streams = new();
    private readonly ConcurrentDictionary<string, MediaSession> _sessions = new();
    private readonly CancellationTokenSource _stopping = new();
    private Thread? _ticker;

    public SfuService(
        PeerConnectionFactory factory,
        IHubContext<ChatHub, IChatClient> hub,
        IOptions<DiscagemOptions> options,
        TimeProvider time,
        ILogger<SfuService> logger)
    {
        _factory = factory;
        _hub = hub;
        _options = options.Value.Media;
        _time = time;
        _logger = logger;
    }

    public IReadOnlyList<StreamInfoDto> GetStreams() =>
        _streams.Values.Where(s => !s.HasEnded).OrderBy(s => s.StartedAt).Select(s => s.ToDto()).ToList();

    public LiveStream? FindStream(string streamId) => _streams.TryGetValue(streamId, out var stream) ? stream : null;

    public async Task<MediaSessionAnswer> StartStreamAsync(long userId, string connectionId, StartStreamRequest request)
    {
        var title = (request.Title ?? "").Trim();
        if (title.Length > Limits.MaxStreamTitleLength)
        {
            title = title[..Limits.MaxStreamTitleLength];
        }

        if (request.Width is < 16 or > 7680 || request.Height is < 16 or > 4320 || request.FrameRate is < 1 or > 120)
        {
            throw new MediaException("Unsupported stream format.");
        }

        if (string.IsNullOrWhiteSpace(request.OfferSdp))
        {
            throw new MediaException("Missing session description.");
        }

        // A user can only have one live stream at a time.
        foreach (var existing in _streams.Values.Where(s => s.UserId == userId).ToList())
        {
            await EndStreamAsync(existing, "replaced");
        }

        var pc = _factory.Create();
        pc.addTrack(DiscagemVideo.CreateTrack(MediaStreamStatusEnum.RecvOnly));
        var session = new MediaSession(connectionId, userId, pc, isPublisher: true);
        var stream = new LiveStream(
            "st_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
            session,
            title,
            request.Width,
            request.Height,
            request.FrameRate,
            _time.GetUtcNow());
        session.Stream = stream;

        pc.OnRtpPacketReceived += (_, media, packet) =>
        {
            if (media == SDPMediaTypesEnum.video)
            {
                stream.OnPublisherPacket(packet);
            }
        };
        pc.onicecandidate += candidate => SendCandidate(session, candidate);
        pc.onconnectionstatechange += state =>
        {
            _logger.LogDebug("Publisher session {SessionId} is {State}", session.Id, state);
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
            {
                _ = EndStreamAsync(stream, state == RTCPeerConnectionState.failed ? "connection failed" : "closed");
            }
        };

        string answer;
        try
        {
            answer = await NegotiateAsync(pc, request.OfferSdp);
        }
        catch
        {
            session.Close("negotiation failed");
            throw;
        }

        _sessions[session.Id] = session;
        _streams[stream.Id] = stream;
        _logger.LogInformation("User {UserId} started stream {StreamId} ({Width}x{Height}@{Fps})", userId, stream.Id, request.Width, request.Height, request.FrameRate);
        await _hub.Clients.All.StreamStarted(stream.ToDto());
        return new MediaSessionAnswer(stream.Id, session.Id, answer);
    }

    public async Task<MediaSessionAnswer> WatchAsync(long userId, string connectionId, string streamId, string offerSdp)
    {
        var stream = FindStream(streamId);
        if (stream is null || stream.HasEnded)
        {
            throw new MediaException("That stream is no longer live.");
        }

        if (string.IsNullOrWhiteSpace(offerSdp))
        {
            throw new MediaException("Missing session description.");
        }

        // Replace an earlier session for the same stream from the same connection.
        foreach (var old in stream.Viewers.Where(v => v.ConnectionId == connectionId).ToList())
        {
            await RemoveViewerAsync(old, "replaced", notifyClient: false);
        }

        if (stream.Viewers.Length >= _options.MaxViewersPerStream)
        {
            throw new MediaException("This stream has reached its viewer limit.");
        }

        var pc = _factory.Create();
        pc.addTrack(DiscagemVideo.CreateTrack(MediaStreamStatusEnum.SendOnly));
        var session = new MediaSession(connectionId, userId, pc, isPublisher: false) { Stream = stream };

        pc.OnReceiveReport += (_, media, report) =>
        {
            if (media == SDPMediaTypesEnum.video)
            {
                stream.OnViewerReport(session, report);
            }
        };
        pc.onicecandidate += candidate => SendCandidate(session, candidate);
        pc.onconnectionstatechange += state =>
        {
            _logger.LogDebug("Viewer session {SessionId} is {State}", session.Id, state);
            if (state == RTCPeerConnectionState.connected)
            {
                // Give the new viewer a decodable starting point right away.
                stream.RequestKeyFrame(force: true);
            }
            else if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
            {
                _ = RemoveViewerAsync(session, state == RTCPeerConnectionState.failed ? "connection failed" : "closed", notifyClient: true);
            }
        };

        string answer;
        try
        {
            answer = await NegotiateAsync(pc, offerSdp);
        }
        catch
        {
            session.Close("negotiation failed");
            throw;
        }

        // Registered before joining the stream so that an EndStreamAsync racing with this call either
        // sees the viewer (and removes it) or makes AddViewer fail.
        _sessions[session.Id] = session;
        if (!stream.AddViewer(session))
        {
            _sessions.TryRemove(session.Id, out _);
            session.Close("stream ended");
            throw new MediaException("That stream is no longer live.");
        }
        _logger.LogInformation("User {UserId} is watching stream {StreamId}", userId, stream.Id);
        await _hub.Clients.All.StreamUpdated(stream.ToDto());
        return new MediaSessionAnswer(stream.Id, session.Id, answer);
    }

    public async Task StopWatchingAsync(string connectionId, string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session) && session.ConnectionId == connectionId && !session.IsPublisher)
        {
            await RemoveViewerAsync(session, "stopped watching", notifyClient: false);
        }
    }

    public async Task StopStreamAsync(long userId)
    {
        foreach (var stream in _streams.Values.Where(s => s.UserId == userId).ToList())
        {
            await EndStreamAsync(stream, "stopped by publisher");
        }
    }

    public async Task OnConnectionClosedAsync(string connectionId)
    {
        foreach (var session in _sessions.Values.Where(s => s.ConnectionId == connectionId).ToList())
        {
            if (session.IsPublisher && session.Stream is not null)
            {
                await EndStreamAsync(session.Stream, "publisher disconnected");
            }
            else
            {
                await RemoveViewerAsync(session, "viewer disconnected", notifyClient: false);
            }
        }
    }

    public void AddRemoteCandidate(string connectionId, IceCandidateDto candidate)
    {
        if (!_sessions.TryGetValue(candidate.SessionId, out var session) || session.ConnectionId != connectionId || session.IsClosed)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(candidate.Candidate) || candidate.Candidate.Length > 1024)
        {
            return;
        }

        try
        {
            session.PeerConnection.addIceCandidate(new RTCIceCandidateInit
            {
                candidate = candidate.Candidate,
                sdpMid = candidate.SdpMid ?? "0",
                sdpMLineIndex = (ushort)Math.Max(0, candidate.SdpMLineIndex ?? 0),
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring malformed ICE candidate for session {SessionId}", session.Id);
        }
    }

    private static async Task<string> NegotiateAsync(RTCPeerConnection pc, string offerSdp)
    {
        SetDescriptionResultEnum result;
        try
        {
            result = pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp });
        }
        catch (Exception)
        {
            throw new MediaException("The session description could not be parsed.");
        }

        if (result != SetDescriptionResultEnum.OK)
        {
            throw new MediaException($"The session description was rejected ({result}).");
        }

        var answer = pc.createAnswer();
        await pc.setLocalDescription(answer);
        return answer.sdp;
    }

    private void SendCandidate(MediaSession session, RTCIceCandidate candidate)
    {
        if (session.IsClosed)
        {
            return;
        }

        var dto = new IceCandidateDto(session.Id, candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex);
        _ = _hub.Clients.Client(session.ConnectionId).IceCandidate(dto);
    }

    private async Task RemoveViewerAsync(MediaSession session, string reason, bool notifyClient)
    {
        _sessions.TryRemove(session.Id, out _);
        var closed = session.Close(reason);
        var stream = session.Stream;
        if (stream is not null && stream.RemoveViewer(session) && !stream.HasEnded)
        {
            await _hub.Clients.All.StreamUpdated(stream.ToDto());
        }

        if (closed && notifyClient)
        {
            await _hub.Clients.Client(session.ConnectionId).MediaSessionClosed(session.Id, reason);
        }
    }

    private async Task EndStreamAsync(LiveStream stream, string reason)
    {
        if (!stream.MarkEnded())
        {
            return;
        }

        _streams.TryRemove(stream.Id, out _);
        _sessions.TryRemove(stream.Publisher.Id, out _);
        stream.Publisher.Close(reason);

        foreach (var viewer in stream.Viewers)
        {
            _sessions.TryRemove(viewer.Id, out _);
            viewer.Close("stream ended");
            stream.RemoveViewer(viewer);
        }

        _logger.LogInformation("Stream {StreamId} ended: {Reason}", stream.Id, reason);
        await _hub.Clients.All.StreamEnded(stream.Id);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ticker = new Thread(TickLoop) { IsBackground = true, Name = "Discagem SFU" };
        _ticker.Start();
        return Task.CompletedTask;
    }

    private void TickLoop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            foreach (var stream in _streams.Values)
            {
                try
                {
                    stream.Tick();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "SFU tick failed for stream {StreamId}", stream.Id);
                }
            }

            _stopping.Token.WaitHandle.WaitOne(10);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        await _stopping.CancelAsync();
        foreach (var stream in _streams.Values.ToList())
        {
            await EndStreamAsync(stream, "server shutting down");
        }
    }
}
