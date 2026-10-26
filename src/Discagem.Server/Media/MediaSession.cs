using System.Security.Cryptography;
using Discagem.Media.Rtp;
using Discagem.Media.WebRtc;
using SIPSorcery.Net;

namespace Discagem.Server.Media;

/// <summary>A WebRTC peer connection between the server and one client, either publishing or watching a stream.</summary>
public sealed class MediaSession
{
    private readonly object _sendLock = new();
    private int _closed;

    public MediaSession(string connectionId, long userId, RTCPeerConnection peerConnection, bool isPublisher)
    {
        ConnectionId = connectionId;
        UserId = userId;
        PeerConnection = peerConnection;
        IsPublisher = isPublisher;
    }

    public string Id { get; } = "ms_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));

    public string ConnectionId { get; }

    public long UserId { get; }

    public RTCPeerConnection PeerConnection { get; }

    public bool IsPublisher { get; }

    public LiveStream? Stream { get; set; }

    public bool IsConnected => PeerConnection.connectionState == RTCPeerConnectionState.connected;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public long PacketsSent { get; private set; }

    public long PacketsRetransmitted { get; private set; }

    /// <summary>Sends a video packet to this (viewer) session. Safe to call from multiple threads.</summary>
    public void Send(VideoPacket packet, bool isRetransmission = false)
    {
        if (IsClosed || !IsConnected)
        {
            return;
        }

        lock (_sendLock)
        {
            PeerConnection.SendRtpRaw(SDPMediaTypesEnum.video, packet.Payload, packet.Timestamp, packet.Marker ? 1 : 0, DiscagemVideo.PayloadType);
            PacketsSent++;
            if (isRetransmission)
            {
                PacketsRetransmitted++;
            }
        }
    }

    /// <summary>Marks the session closed and tears down the peer connection in the background.</summary>
    public bool Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return false;
        }

        _ = Task.Run(() =>
        {
            try
            {
                PeerConnection.Close(reason);
            }
            catch
            {
                // The connection may already be gone.
            }
        });
        return true;
    }
}
