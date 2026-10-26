using System.Net;
using Discagem.Media.Audio;
using SIPSorcery.Net;

namespace Discagem.Media.WebRtc;

/// <summary>Receives one forwarded voice packet: the speaker, their sequence number and the Opus data.</summary>
public delegate void VoicePacketHandler(uint speakerId, ushort sequence, ReadOnlySpan<byte> opus);

/// <summary>
/// Client side of a voice channel: one bidirectional audio stream to the SFU. Outgoing packets are
/// plain Opus; incoming ones carry a <see cref="VoicePayload"/> header naming the speaker.
/// </summary>
public sealed class VoicePeer : MediaPeer
{
    private long _packetsSent;
    private long _packetsReceived;

    public VoicePeer(IEnumerable<IceServerConfig> iceServers)
        : base(iceServers, DiscagemAudio.CreateTrack(MediaStreamStatusEnum.SendRecv))
    {
        PeerConnection.OnRtpPacketReceived += OnRtpPacketReceived;
    }

    /// <summary>Raised on the network thread for every voice packet from another participant.</summary>
    public event VoicePacketHandler? PacketReceived;

    public long PacketsSent => Interlocked.Read(ref _packetsSent);

    public long PacketsReceived => Interlocked.Read(ref _packetsReceived);

    /// <summary>Sends one Opus packet. Packets are dropped until the connection is up.</summary>
    public void Send(ReadOnlySpan<byte> opus, uint timestamp)
    {
        if (IsDisposed || !IsConnected || opus.IsEmpty)
        {
            return;
        }

        try
        {
            PeerConnection.SendRtpRaw(SDPMediaTypesEnum.audio, opus.ToArray(), timestamp, 0, DiscagemAudio.PayloadType);
            Interlocked.Increment(ref _packetsSent);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Raced with the connection closing.
        }
    }

    private void OnRtpPacketReceived(IPEndPoint remote, SDPMediaTypesEnum media, RTPPacket packet)
    {
        if (media != SDPMediaTypesEnum.audio ||
            !VoicePayload.TryUnwrap(packet.Payload, out var sequence, out var speaker, out var opus))
        {
            return;
        }

        Interlocked.Increment(ref _packetsReceived);
        PacketReceived?.Invoke(speaker, sequence, opus.Span);
    }

    protected override void OnDisposing() => PeerConnection.OnRtpPacketReceived -= OnRtpPacketReceived;
}
