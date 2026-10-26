using System.Collections.Concurrent;
using System.Collections.Immutable;
using Discagem.Media.Audio;
using Discagem.Media.WebRtc;
using Discagem.Server.Hubs;
using Discagem.Shared;
using Microsoft.AspNetCore.SignalR;
using SIPSorcery.Net;

namespace Discagem.Server.Media;

/// <summary>A user connected to a voice channel.</summary>
public sealed class VoiceParticipant(MediaSession session, long channelId)
{
    public MediaSession Session { get; } = session;

    public long ChannelId { get; } = channelId;

    public long UserId => Session.UserId;

    public volatile bool IsMuted;

    public volatile bool IsDeafened;

    public VoiceParticipantDto ToDto() => new(ChannelId, UserId, IsMuted, IsDeafened);
}

/// <summary>
/// Voice channels. Every participant sends one Opus stream; the server forwards each packet to the
/// other participants of the room, tagged with the speaker (see <see cref="VoicePayload"/>), so no
/// audio is decoded or mixed on the server.
/// </summary>
public sealed class VoiceService(
    PeerConnectionFactory factory,
    IHubContext<ChatHub, IChatClient> hub,
    ILogger<VoiceService> logger)
{
    private readonly object _sync = new();
    private readonly Dictionary<long, ImmutableArray<VoiceParticipant>> _rooms = new();
    private readonly ConcurrentDictionary<long, VoiceParticipant> _byUser = new();
    private readonly ConcurrentDictionary<string, VoiceParticipant> _bySession = new();

    public IReadOnlyList<VoiceParticipantDto> GetParticipants() =>
        _byUser.Values.OrderBy(p => p.ChannelId).ThenBy(p => p.Session.Id).Select(p => p.ToDto()).ToList();

    private ImmutableArray<VoiceParticipant> Room(long channelId)
    {
        lock (_sync)
        {
            return _rooms.TryGetValue(channelId, out var room) ? room : [];
        }
    }

    public async Task<MediaSessionAnswer> JoinAsync(long userId, string connectionId, long channelId, string offerSdp)
    {
        if (string.IsNullOrWhiteSpace(offerSdp))
        {
            throw new MediaException("Missing session description.");
        }

        // One voice connection per user: joining elsewhere (or from another device) moves them, and the
        // previous session is told so that another device can hang up.
        if (_byUser.TryGetValue(userId, out var existing))
        {
            await LeaveAsync(existing, "joined from elsewhere", notifyClient: true);
        }

        if (Room(channelId).Length >= Limits.MaxVoiceParticipants)
        {
            throw new MediaException("This voice channel is full.");
        }

        var pc = factory.Create();
        pc.addTrack(DiscagemAudio.CreateTrack(MediaStreamStatusEnum.SendRecv));
        var session = new MediaSession(connectionId, userId, pc);
        var participant = new VoiceParticipant(session, channelId);

        pc.OnRtpPacketReceived += (_, media, packet) =>
        {
            if (media == SDPMediaTypesEnum.audio)
            {
                Forward(participant, packet);
            }
        };
        pc.onicecandidate += candidate =>
        {
            if (!session.IsClosed)
            {
                _ = hub.Clients.Client(connectionId).IceCandidate(
                    new IceCandidateDto(session.Id, candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex));
            }
        };
        pc.onconnectionstatechange += state =>
        {
            logger.LogDebug("Voice session {SessionId} is {State}", session.Id, state);
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
            {
                _ = LeaveAsync(participant, state == RTCPeerConnectionState.failed ? "connection failed" : "closed", notifyClient: true);
            }
        };

        string answer;
        try
        {
            var result = pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp });
            if (result != SetDescriptionResultEnum.OK)
            {
                throw new MediaException($"The session description was rejected ({result}).");
            }

            var init = pc.createAnswer();
            await pc.setLocalDescription(init);
            answer = init.sdp;
        }
        catch (Exception ex)
        {
            session.Close("negotiation failed");
            throw ex as MediaException ?? new MediaException("The session description could not be parsed.");
        }

        lock (_sync)
        {
            _rooms[channelId] = Room(channelId).Add(participant);
        }

        _byUser[userId] = participant;
        _bySession[session.Id] = participant;
        logger.LogInformation("User {UserId} joined voice channel {ChannelId}", userId, channelId);
        await hub.Clients.All.VoiceStateChanged(participant.ToDto());
        return new MediaSessionAnswer($"voice:{channelId}", session.Id, answer);
    }

    /// <summary>Relays a speaker's packet to everybody else in the room.</summary>
    private void Forward(VoiceParticipant speaker, RTPPacket packet)
    {
        if (speaker.IsMuted || speaker.IsDeafened || packet.Payload.Length == 0)
        {
            return;
        }

        var payload = VoicePayload.Wrap(packet.Payload, packet.Header.SequenceNumber, (uint)speaker.UserId);
        foreach (var listener in Room(speaker.ChannelId))
        {
            if (!ReferenceEquals(listener, speaker) && !listener.IsDeafened)
            {
                listener.Session.SendAudio(payload, packet.Header.Timestamp);
            }
        }
    }

    /// <summary>Updates the mute and deafen flags of the user's voice session, if it belongs to this connection.</summary>
    public async Task SetStateAsync(long userId, string connectionId, bool muted, bool deafened)
    {
        if (!_byUser.TryGetValue(userId, out var participant) || participant.Session.ConnectionId != connectionId)
        {
            return;
        }

        // Deafened users cannot speak either, as in Discord.
        participant.IsDeafened = deafened;
        participant.IsMuted = muted || deafened;
        await hub.Clients.All.VoiceStateChanged(participant.ToDto());
    }

    /// <summary>Leaves voice, if the user's voice session belongs to this connection.</summary>
    public async Task LeaveAsync(long userId, string connectionId)
    {
        if (_byUser.TryGetValue(userId, out var participant) && participant.Session.ConnectionId == connectionId)
        {
            await LeaveAsync(participant, "left");
        }
    }

    public async Task OnConnectionClosedAsync(string connectionId)
    {
        foreach (var participant in _byUser.Values.Where(p => p.Session.ConnectionId == connectionId).ToList())
        {
            await LeaveAsync(participant, "disconnected");
        }
    }

    /// <summary>Disconnects everybody from a channel that is being deleted.</summary>
    public async Task CloseChannelAsync(long channelId)
    {
        foreach (var participant in Room(channelId))
        {
            await LeaveAsync(participant, "channel deleted", notifyClient: true);
        }
    }

    public bool AddRemoteCandidate(string connectionId, IceCandidateDto candidate)
    {
        if (!_bySession.TryGetValue(candidate.SessionId, out var participant) ||
            participant.Session.ConnectionId != connectionId ||
            participant.Session.IsClosed ||
            string.IsNullOrWhiteSpace(candidate.Candidate) ||
            candidate.Candidate.Length > 1024)
        {
            return false;
        }

        try
        {
            participant.Session.PeerConnection.addIceCandidate(new RTCIceCandidateInit
            {
                candidate = candidate.Candidate,
                sdpMid = candidate.SdpMid ?? "0",
                sdpMLineIndex = (ushort)Math.Max(0, candidate.SdpMLineIndex ?? 0),
            });
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ignoring malformed ICE candidate for voice session {SessionId}", participant.Session.Id);
        }

        return true;
    }

    private async Task LeaveAsync(VoiceParticipant participant, string reason, bool notifyClient = false)
    {
        bool removed;
        lock (_sync)
        {
            var room = Room(participant.ChannelId);
            var updated = room.Remove(participant);
            removed = updated.Length != room.Length;
            if (updated.IsEmpty)
            {
                _rooms.Remove(participant.ChannelId);
            }
            else
            {
                _rooms[participant.ChannelId] = updated;
            }
        }

        _byUser.TryRemove(new KeyValuePair<long, VoiceParticipant>(participant.UserId, participant));
        _bySession.TryRemove(participant.Session.Id, out _);

        // Tell the client why before tearing the connection down, so it does not mistake the hang-up for
        // a network failure and try to rejoin.
        if (notifyClient && !participant.Session.IsClosed)
        {
            await hub.Clients.Client(participant.Session.ConnectionId).MediaSessionClosed(participant.Session.Id, reason);
        }

        participant.Session.Close(reason);
        if (!removed)
        {
            return;
        }

        logger.LogInformation("User {UserId} left voice channel {ChannelId}: {Reason}", participant.UserId, participant.ChannelId, reason);
        await hub.Clients.All.VoiceLeft(participant.ChannelId, participant.UserId);
    }
}
