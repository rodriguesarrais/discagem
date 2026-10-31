using System.Diagnostics;
using Discagem.Media.Audio;
using Discagem.Media.Rtp;
using SIPSorcery.Net;

namespace Discagem.Media.WebRtc;

/// <summary>
/// Receives a screen share from the SFU: reorders packets, requests retransmissions (NACK) and key
/// frames (PLI), and emits complete encoded frames. The stream's audio, if any, arrives on a second
/// track and is passed on packet by packet.
/// </summary>
public sealed class ScreenShareViewer : MediaPeer
{
    private readonly object _sync = new();
    private readonly FrameAssembler _assembler = new();
    private readonly NackTracker _nacks = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Thread _maintenance;
    private readonly List<AssembledFrame> _scratch = [];
    private readonly Queue<AssembledFrame> _ready = new();
    private readonly object _deliverLock = new();
    private long _packetsReceived;
    private long _bytesReceived;
    private long _framesReceived;
    private long _audioPacketsReceived;

    public ScreenShareViewer(IEnumerable<IceServerConfig> iceServers)
        : this(iceServers, withAudio: true)
    {
    }

    /// <summary>Test hook: <c>withAudio: false</c> behaves like a client from before streams had audio.</summary>
    internal ScreenShareViewer(IEnumerable<IceServerConfig> iceServers, bool withAudio)
        : base(iceServers, withAudio
            ? [DiscagemVideo.CreateTrack(MediaStreamStatusEnum.RecvOnly), DiscagemAudio.CreateTrack(MediaStreamStatusEnum.RecvOnly)]
            : [DiscagemVideo.CreateTrack(MediaStreamStatusEnum.RecvOnly)])
    {
        PeerConnection.OnRtpPacketReceived += OnRtpPacket;
        _maintenance = new Thread(MaintenanceLoop) { IsBackground = true, Name = "Discagem viewer" };
        _maintenance.Start();
    }

    /// <summary>Raised for every complete frame, in decode order (on a network thread).</summary>
    public event Action<AssembledFrame>? FrameReceived;

    /// <summary>Raised on the network thread for every audio packet (the streamer's sequence number and the Opus data).</summary>
    public event Action<ushort, ReadOnlyMemory<byte>>? AudioReceived;

    public long AudioPacketsReceived => Interlocked.Read(ref _audioPacketsReceived);

    public long PacketsReceived => Interlocked.Read(ref _packetsReceived);

    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    public long FramesDropped
    {
        get
        {
            lock (_sync)
            {
                return _assembler.FramesDropped;
            }
        }
    }

    internal string Diagnostics
    {
        get
        {
            lock (_sync)
            {
                return $"giveUps={_assembler.GiveUps} jumps={_assembler.KeyFrameJumps} lastMissing={_assembler.LastMissingSeq} buffered={_assembler.BufferedPackets} missingTracked={_nacks.MissingCount}";
            }
        }
    }

    /// <summary>Tells the receiver the decoder failed, so it waits for (and requests) a key frame.</summary>
    public void ReportDecodeError()
    {
        lock (_sync)
        {
            _assembler.OnDecodeError();
        }
    }

    private void OnRtpPacket(System.Net.IPEndPoint remote, SDPMediaTypesEnum media, RTPPacket rtp)
    {
        if (media == SDPMediaTypesEnum.audio)
        {
            // The server prefixes the streamer's own sequence number, so losses upstream show as gaps.
            if (VoicePayload.TryUnwrap(rtp.Payload, out var sequence, out _, out var opus))
            {
                Interlocked.Increment(ref _audioPacketsReceived);
                AudioReceived?.Invoke(sequence, opus);
            }

            return;
        }

        if (media != SDPMediaTypesEnum.video)
        {
            return;
        }

        var packet = VideoPacket.Parse(rtp.Payload, rtp.Header.Timestamp);
        if (packet is null)
        {
            return;
        }

        Interlocked.Increment(ref _packetsReceived);
        Interlocked.Add(ref _bytesReceived, packet.Payload.Length);

        bool hasFrames;
        lock (_sync)
        {
            var now = _clock.ElapsedMilliseconds;
            _nacks.OnPacket(packet.MediaSeq, now);
            _assembler.Insert(packet, now, _scratch);
            hasFrames = QueueReady(_scratch);
        }

        if (hasFrames)
        {
            Deliver();
        }
    }

    private bool QueueReady(List<AssembledFrame> frames)
    {
        if (frames.Count == 0)
        {
            return false;
        }

        foreach (var frame in frames)
        {
            _ready.Enqueue(frame);
        }

        frames.Clear();
        return true;
    }

    /// <summary>Delivers queued frames in order, even when called from several threads.</summary>
    private void Deliver()
    {
        lock (_deliverLock)
        {
            while (true)
            {
                AssembledFrame frame;
                lock (_sync)
                {
                    if (!_ready.TryDequeue(out frame))
                    {
                        return;
                    }
                }

                Interlocked.Increment(ref _framesReceived);
                FrameReceived?.Invoke(frame);
            }
        }
    }

    private void MaintenanceLoop()
    {
        var output = new List<AssembledFrame>();
        while (!IsDisposed)
        {
            Thread.Sleep(10);
            if (!IsConnected)
            {
                continue;
            }

            List<ushort> nacks;
            bool requestKeyFrame;
            bool hasFrames;
            lock (_sync)
            {
                var now = _clock.ElapsedMilliseconds;
                _assembler.Tick(now, output);
                hasFrames = QueueReady(output);
                nacks = _nacks.CollectNacks(now);
                requestKeyFrame = _assembler.ShouldRequestKeyFrame(now);
            }

            try
            {
                if (nacks.Count > 0)
                {
                    RtcpFeedback.SendNacks(PeerConnection, nacks);
                }

                if (requestKeyFrame)
                {
                    RtcpFeedback.SendPictureLossIndication(PeerConnection);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Viewer feedback failed: {ex.Message}");
            }

            if (hasFrames)
            {
                Deliver();
            }
        }
    }

    protected override void OnDisposing()
    {
        PeerConnection.OnRtpPacketReceived -= OnRtpPacket;
    }
}
