using Discagem.Media.WebRtc;
using SIPSorcery.Net;

namespace Discagem.Tests.Media;

/// <summary>Clients with stream audio talking to a server from before it (which only negotiates video).</summary>
public sealed class StreamCompatibilityTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task PublisherWithAudio_StillSendsVideoToAServerWithoutAudio()
    {
        using var publisher = new ScreenSharePublisher([], 2500, withAudio: true);

        // What an older server does: a receive-only video track and nothing else.
        var server = new RTCPeerConnection(new RTCConfiguration { X_UseRtpFeedbackProfile = true });
        server.addTrack(DiscagemVideo.CreateTrack(MediaStreamStatusEnum.RecvOnly));
        var videoPackets = 0;
        var audioPackets = 0;
        server.OnRtpPacketReceived += (_, media, _) =>
        {
            if (media == SDPMediaTypesEnum.video)
            {
                Interlocked.Increment(ref videoPackets);
            }
            else
            {
                Interlocked.Increment(ref audioPackets);
            }
        };

        publisher.LocalCandidate += (candidate, mid, index) =>
            server.addIceCandidate(new RTCIceCandidateInit { candidate = candidate, sdpMid = mid ?? "0", sdpMLineIndex = (ushort)(index ?? 0) });
        server.onicecandidate += candidate =>
        {
            if (candidate is not null)
            {
                publisher.AddRemoteCandidate(candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex);
            }
        };

        var offer = await publisher.CreateOfferAsync();
        Assert.Equal(SetDescriptionResultEnum.OK, server.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offer }));
        var answer = server.createAnswer();
        await server.setLocalDescription(answer);
        publisher.ApplyAnswer(answer.sdp);
        Assert.True(await publisher.WaitForConnectionAsync(Timeout), "publisher did not connect");

        var frame = new byte[3000];
        for (var i = 0; i < 20; i++)
        {
            publisher.SendFrame(frame, (uint)(i * 3000), isKeyFrame: i == 0);
            publisher.SendAudio([0xF8, 0xFF, 0xFE], (uint)(i * 960));
            await Task.Delay(20);
        }

        var deadline = DateTime.UtcNow + Timeout;
        while (Volatile.Read(ref videoPackets) < 20 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(videoPackets >= 20, $"only {videoPackets} video packets arrived");
        Assert.Equal(0, audioPackets);
        server.Close("done");
    }
}
