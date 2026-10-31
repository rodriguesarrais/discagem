using System.Collections.Immutable;
using System.Diagnostics;
using Discagem.Media.Audio;
using Discagem.Media.Rtp;
using Discagem.Shared;
using SIPSorcery.Net;

namespace Discagem.Server.Media;

/// <summary>
/// One screen share: a publisher session whose packets are forwarded verbatim to every viewer session.
/// The server keeps a short packet history to serve viewer NACKs locally, NACKs the publisher for
/// packets lost on the uplink, and relays key frame requests. Audio packets, if the publisher sends
/// any, are forwarded to the viewers that negotiated audio, tagged like voice packets (see
/// <see cref="VoicePayload"/>) so that receivers see the publisher's own sequence numbers.
/// </summary>
public sealed class LiveStream
{
    private const int KeyFrameRequestMinIntervalMs = 250;

    private readonly object _sync = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Stream audio is 50 packets per second, like voice; anything well beyond that is dropped.</summary>
    private readonly PacketRateLimiter _audioLimiter = new(packetsPerSecond: 100, burst: 50);
    private ImmutableArray<MediaSession> _viewers = [];
    private long _lastKeyFrameRequestMs = -KeyFrameRequestMinIntervalMs;
    private int _ended;

    public LiveStream(string id, MediaSession publisher, string title, int width, int height, int frameRate, DateTimeOffset startedAt, bool hasAudio = false)
    {
        Id = id;
        HasAudio = hasAudio;
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

    /// <summary>Whether the publisher negotiated an audio track.</summary>
    public bool HasAudio { get; }

    public PacketHistory History { get; } = new(8192);

    public NackTracker UpstreamNacks { get; } = new() { InitialDelayMs = 5 };

    public ImmutableArray<MediaSession> Viewers => _viewers;

    public bool HasEnded => Volatile.Read(ref _ended) != 0;

    public long PacketsReceived { get; private set; }

    public long AudioPacketsReceived { get; private set; }

    private long NowMs => _clock.ElapsedMilliseconds;

    /// <summary>Marks the stream ended. Returns false if it already was.</summary>
    public bool MarkEnded()
    {
        lock (_sync)
        {
            return Interlocked.Exchange(ref _ended, 1) == 0;
        }
    }

    /// <summary>Adds a viewer unless the stream has ended (checked under the same lock as <see cref="MarkEnded"/>).</summary>
    public bool AddViewer(MediaSession viewer)
    {
        lock (_sync)
        {
            if (HasEnded)
            {
                return false;
            }

            _viewers = _viewers.Add(viewer);
            return true;
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

    /// <summary>Forwards an audio packet from the publisher (called on the publisher's receive thread).</summary>
    public void OnPublisherAudio(RTPPacket rtp)
    {
        // Drop anything that cannot be a single Opus frame, and anyone sending far faster than 50 packets per second.
        if (!HasAudio || rtp.Payload.Length is 0 or > OpusVoiceEncoder.MaxPacketBytes || !_audioLimiter.TryConsume())
        {
            return;
        }

        AudioPacketsReceived++;
        var payload = VoicePayload.Wrap(rtp.Payload, rtp.Header.SequenceNumber, (uint)UserId);
        foreach (var viewer in _viewers)
        {
            if (viewer.ReceivesAudio)
            {
                viewer.SendAudio(payload, rtp.Header.Timestamp);
            }
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
        new(Id, UserId, Title, Width, Height, FrameRate, StartedAt, _viewers.Select(v => v.UserId).Distinct().ToList(), HasAudio);
}
