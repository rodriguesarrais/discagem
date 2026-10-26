using Discagem.Media.Rtp;
using SIPSorcery.Net;

namespace Discagem.Media.WebRtc;

/// <summary>
/// Sends encoded video to the SFU: packetises frames, paces them onto the network, keeps a history
/// to answer NACKs and surfaces key frame requests (PLI).
/// </summary>
public sealed class ScreenSharePublisher : MediaPeer
{
    /// <summary>
    /// A packet is retransmitted at most once per this interval. The server spaces its NACKs by at least
    /// one round trip, so a repeat inside this window is a duplicate that crossed the retransmission.
    /// </summary>
    private const int MinResendIntervalMs = 40;

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly VideoPacketizer _packetizer = new();
    private readonly PacketHistory _history = new(8192);
    private readonly PacedSender _pacer;
    private long _packetsSent;
    private long _bytesSent;
    private long _packetsRetransmitted;
    private long _nackedPackets;

    public ScreenSharePublisher(IEnumerable<IceServerConfig> iceServers, int bitrateKbps)
        : base(iceServers, DiscagemVideo.CreateTrack(MediaStreamStatusEnum.SendOnly))
    {
        _pacer = new PacedSender(SendNow, PacingRateFor(bitrateKbps));
        _pacer.Overflowed += () =>
        {
            // The flushed packets are not worth retransmitting: a key frame replaces them.
            _history.Clear();
            KeyFrameRequested?.Invoke();
        };
        PeerConnection.OnReceiveReport += OnReceiveReport;
    }

    /// <summary>Raised when the server asks for a key frame, or the send queue overflowed.</summary>
    public event Action? KeyFrameRequested;

    public long PacketsSent => Interlocked.Read(ref _packetsSent);

    public long BytesSent => Interlocked.Read(ref _bytesSent);

    public long PacketsRetransmitted => Interlocked.Read(ref _packetsRetransmitted);

    /// <summary>Number of distinct packets the server reported as lost (NACKed).</summary>
    public long NackedPackets => Interlocked.Read(ref _nackedPackets);

    public long QueuedBytes => _pacer.QueuedBytes;

    /// <summary>Test hook: packets for which this returns true are silently dropped (simulated loss).</summary>
    internal Func<VideoPacket, bool>? DropFilter { get; set; }

    public void SetBitrate(int bitrateKbps) => _pacer.RateKbps = PacingRateFor(bitrateKbps);

    private static int PacingRateFor(int bitrateKbps) => Math.Max(3000, bitrateKbps * 5 / 2);

    /// <summary>Queues an encoded frame for transmission.</summary>
    public void SendFrame(ReadOnlySpan<byte> frame, uint rtpTimestamp, bool isKeyFrame)
    {
        if (IsDisposed || !IsConnected)
        {
            return;
        }

        var packets = _packetizer.Packetize(frame, rtpTimestamp, isKeyFrame);
        foreach (var packet in packets)
        {
            _history.Add(packet);
        }

        _pacer.Enqueue(packets);
    }

    private void SendNow(VideoPacket packet)
    {
        if (IsDisposed || !IsConnected)
        {
            return;
        }

        if (DropFilter?.Invoke(packet) == true)
        {
            Interlocked.Increment(ref _packetsSent);
            return;
        }

        PeerConnection.SendRtpRaw(SDPMediaTypesEnum.video, packet.Payload, packet.Timestamp, packet.Marker ? 1 : 0, DiscagemVideo.PayloadType);
        Interlocked.Increment(ref _packetsSent);
        Interlocked.Add(ref _bytesSent, packet.Payload.Length);
    }

    private void OnReceiveReport(System.Net.IPEndPoint remote, SDPMediaTypesEnum media, RTCPCompoundPacket report)
    {
        if (media != SDPMediaTypesEnum.video)
        {
            return;
        }

        var feedback = report.Feedback;
        if (RtcpFeedback.IsNack(feedback))
        {
            var now = _clock.ElapsedMilliseconds;
            foreach (var seq in RtcpFeedback.ExpandNack(feedback!.PID, feedback.BLP))
            {
                if (!_history.TryGetForResend(seq, now, MinResendIntervalMs, out var packet, out var isFirstRequest))
                {
                    continue;
                }

                // Count each lost packet once, however often it is requested, so that the loss estimate
                // driving the bitrate does not scale with the round-trip time.
                if (isFirstRequest)
                {
                    Interlocked.Increment(ref _nackedPackets);
                }

                if (_pacer.EnqueueRetransmission(packet))
                {
                    Interlocked.Increment(ref _packetsRetransmitted);
                }
            }
        }
        else if (RtcpFeedback.IsPictureLossIndication(feedback))
        {
            KeyFrameRequested?.Invoke();
        }
    }

    protected override void OnDisposing()
    {
        PeerConnection.OnReceiveReport -= OnReceiveReport;
        _pacer.Dispose();
    }
}
