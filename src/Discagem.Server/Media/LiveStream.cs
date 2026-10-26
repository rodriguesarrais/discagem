using System.Collections.Immutable;
using System.Diagnostics;
using Discagem.Media.Rtp;
using Discagem.Shared;
using SIPSorcery.Net;

namespace Discagem.Server.Media;

/// <summary>
/// One screen share: a publisher session whose packets are forwarded verbatim to every viewer session.
/// The server keeps a short packet history to serve viewer NACKs locally, NACKs the publisher for
/// packets lost on the uplink, and relays key frame requests.
/// </summary>
public sealed class LiveStream
{
    private const int KeyFrameRequestMinIntervalMs = 250;

    private readonly object _sync = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private ImmutableArray<MediaSession> _viewers = [];
    private long _lastKeyFrameRequestMs = -KeyFrameRequestMinIntervalMs;
    private int _ended;

    public LiveStream(string id, MediaSession publisher, string title, int width, int height, int frameRate, DateTimeOffset startedAt)
    {
        Id = id;
        Publisher = publisher;
        Title = title;
        Width = width;
        Height = height;
        FrameRate = frameRate;
        StartedAt = startedAt;
    }

    public string Id { get; }

    public MediaSession Publisher { get; }

    public long UserId => Publisher.UserId;

    public string Title { get; }

    public int Width { get; }

    public int Height { get; }

    public int FrameRate { get; }

    public DateTimeOffset StartedAt { get; }

    public PacketHistory History { get; } = new(8192);

    public NackTracker UpstreamNacks { get; } = new() { RetryIntervalMs = 40, InitialDelayMs = 5 };

    public ImmutableArray<MediaSession> Viewers => _viewers;

    public bool HasEnded => Volatile.Read(ref _ended) != 0;

    public long PacketsReceived { get; private set; }

    private long NowMs => _clock.ElapsedMilliseconds;

    public bool MarkEnded() => Interlocked.Exchange(ref _ended, 1) == 0;

    public void AddViewer(MediaSession viewer)
    {
        lock (_sync)
        {
            _viewers = _viewers.Add(viewer);
        }
    }

    public bool RemoveViewer(MediaSession viewer)
    {
        lock (_sync)
        {
            var before = _viewers.Length;
            _viewers = _viewers.Remove(viewer);
            return _viewers.Length != before;
        }
    }

    /// <summary>Handles an RTP packet from the publisher (called on the publisher's receive thread).</summary>
    public void OnPublisherPacket(RTPPacket rtp)
    {
        var packet = VideoPacket.Parse(rtp.Payload, rtp.Header.Timestamp);
        if (packet is null)
        {
            return;
        }

        PacketsReceived++;
        History.Add(packet);
        UpstreamNacks.OnPacket(packet.MediaSeq, NowMs);

        foreach (var viewer in _viewers)
        {
            viewer.Send(packet);
        }
    }

    /// <summary>Handles RTCP from a viewer: NACKs are served from the history, PLIs are relayed upstream.</summary>
    public void OnViewerReport(MediaSession viewer, RTCPCompoundPacket report)
    {
        var feedback = report.Feedback;
        if (RtcpFeedback.IsNack(feedback))
        {
            foreach (var seq in RtcpFeedback.ExpandNack(feedback!.PID, feedback.BLP))
            {
                if (History.TryGet(seq, out var packet))
                {
                    viewer.Send(packet, isRetransmission: true);
                }
            }
        }
        else if (RtcpFeedback.IsPictureLossIndication(feedback))
        {
            RequestKeyFrame();
        }
    }

    /// <summary>Asks the publisher for a key frame, rate limited.</summary>
    public void RequestKeyFrame(bool force = false)
    {
        var now = NowMs;
        lock (_sync)
        {
            if (!force && now - _lastKeyFrameRequestMs < KeyFrameRequestMinIntervalMs)
            {
                return;
            }

            _lastKeyFrameRequestMs = now;
        }

        if (Publisher.IsConnected)
        {
            RtcpFeedback.SendPictureLossIndication(Publisher.PeerConnection);
        }
    }

    /// <summary>Periodic work: NACK packets lost between the publisher and the server.</summary>
    public void Tick()
    {
        if (!Publisher.IsConnected)
        {
            return;
        }

        var nacks = UpstreamNacks.CollectNacks(NowMs);
        if (nacks.Count > 0)
        {
            RtcpFeedback.SendNacks(Publisher.PeerConnection, nacks);
        }
    }

    public StreamInfoDto ToDto() =>
        new(Id, UserId, Title, Width, Height, FrameRate, StartedAt, _viewers.Select(v => v.UserId).Distinct().ToList());
}
