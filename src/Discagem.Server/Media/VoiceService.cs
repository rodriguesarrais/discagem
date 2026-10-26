using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
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
    /// <summary>A speaker sends 50 packets per second; anything well beyond that is dropped.</summary>
    private const double PacketsPerSecond = 100;
    private const double Burst = 50;

    private double _tokens = Burst;
    private long _lastRefill = Stopwatch.GetTimestamp();

    public MediaSession Session { get; } = session;

    public long ChannelId { get; } = channelId;

    public long UserId => Session.UserId;

    public volatile bool IsMuted;

    public volatile bool IsDeafened;

    public VoiceParticipantDto ToDto() => new(ChannelId, UserId, IsMuted, IsDeafened);

    /// <summary>
    /// Token bucket limiting how many packets this speaker can make the server fan out. Called only
    /// from the session's receive thread.
    /// </summary>
    public bool TryConsumePacket()
    {
        var now = Stopwatch.GetTimestamp();
        _tokens = Math.Min(Burst, _tokens + Stopwatch.GetElapsedTime(_lastRefill, now).TotalSeconds * PacketsPerSecond);
        _lastRefill = now;
        if (_tokens < 1)
        {
            return false;
        }

        _tokens--;
        return true;
    }
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
        // previous session is told so that another device can hang up. This is checked again, atomically,
        // once negotiation is done.
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
                // Not reported to the client as a hang-up: its own connection fails too, and it rejoins.
                _ = LeaveAsync(participant, state == RTCPeerConnectionState.failed ? "connection failed" : "closed");
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

        // Negotiation awaited, so another join by the same user may have completed meanwhile: replace
        // it, and check the capacity, atomically with the insert.
        VoiceParticipant? replaced;
        lock (_sync)
        {
            _byUser.TryGetValue(userId, out replaced);
            var room = Room(channelId);
            var others = replaced is not null && replaced.ChannelId == channelId ? room.Length - 1 : room.Length;
            if (others >= Limits.MaxVoiceParticipants)
            {
                session.Close("channel full");
                throw new MediaException("This voice channel is full.");
            }

            if (replaced is not null)
            {
                Detach(replaced);
            }

            _rooms[channelId] = Room(channelId).Add(participant);
            _byUser[userId] = participant;
            _bySession[session.Id] = participant;
        }

        if (replaced is not null)
        {
            await AnnounceLeftAsync(replaced, "joined from elsewhere", notifyClient: true);
        }

        logger.LogInformation("User {UserId} joined voice channel {ChannelId}", userId, channelId);
        await hub.Clients.All.VoiceStateChanged(participant.ToDto());
        return new MediaSessionAnswer($"voice:{channelId}", session.Id, answer);
    }

    /// <summary>Relays a speaker's packet to everybody else in the room.</summary>
    private void Forward(VoiceParticipant speaker, RTPPacket packet)
    {
        // Every packet is multiplied by the number of listeners: drop anything that is not a plausible
        // Opus frame, and anyone sending far faster than a voice stream.
        if (speaker.IsMuted || speaker.IsDeafened ||
            packet.Payload.Length is 0 or > OpusVoiceEncoder.MaxPacketBytes ||
            !speaker.TryConsumePacket())
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
            removed = Detach(participant);
        }

        if (removed)
        {
            await AnnounceLeftAsync(participant, reason, notifyClient);
        }
        else
        {
            participant.Session.Close(reason);
        }
    }

    /// <summary>Removes a participant from the indexes. Must be called with <see cref="_sync"/> held.</summary>
    private bool Detach(VoiceParticipant participant)
    {
        var room = Room(participant.ChannelId);
        var updated = room.Remove(participant);
        if (updated.IsEmpty)
        {
            _rooms.Remove(participant.ChannelId);
        }
        else
        {
            _rooms[participant.ChannelId] = updated;
        }

        _byUser.TryRemove(new KeyValuePair<long, VoiceParticipant>(participant.UserId, participant));
        _bySession.TryRemove(participant.Session.Id, out _);
        return updated.Length != room.Length;
    }

    /// <summary>Closes a detached participant's session and tells everybody they left.</summary>
    private async Task AnnounceLeftAsync(VoiceParticipant participant, string reason, bool notifyClient)
    {
        // Tell the client why before tearing the connection down, so it does not mistake the hang-up for
        // a network failure and try to rejoin.
        if (notifyClient && !participant.Session.IsClosed)
        {
            await hub.Clients.Client(participant.Session.ConnectionId).MediaSessionClosed(participant.Session.Id, reason);
        }

        participant.Session.Close(reason);
        logger.LogInformation("User {UserId} left voice channel {ChannelId}: {Reason}", participant.UserId, participant.ChannelId, reason);
        await hub.Clients.All.VoiceLeft(participant.ChannelId, participant.UserId);
    }
}
