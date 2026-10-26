using SIPSorcery.Net;

namespace Discagem.Media.Rtp;

/// <summary>Helpers for the RTCP feedback messages used by Discagem (generic NACK and PLI).</summary>
/// <remarks>
/// NACK packet ids refer to the media sequence numbers of <see cref="VideoPayload"/>,
/// not to RTP sequence numbers.
/// </remarks>
public static class RtcpFeedback
{
    /// <summary>Groups sequence numbers (in ascending order) into (PID, BLP) pairs as defined by RFC 4585.</summary>
    public static List<(ushort Pid, ushort Blp)> BuildNackItems(IReadOnlyList<ushort> sequenceNumbers)
    {
        var items = new List<(ushort, ushort)>();
        var i = 0;
        while (i < sequenceNumbers.Count)
        {
            var pid = sequenceNumbers[i];
            ushort blp = 0;
            i++;
            while (i < sequenceNumbers.Count)
            {
                var diff = (ushort)(sequenceNumbers[i] - pid);
                if (diff is < 1 or > 16)
                {
                    break;
                }

                blp |= (ushort)(1 << (diff - 1));
                i++;
            }

            items.Add((pid, blp));
        }

        return items;
    }

    public static IEnumerable<ushort> ExpandNack(ushort pid, ushort blp)
    {
        yield return pid;
        for (var bit = 0; bit < 16; bit++)
        {
            if ((blp & (1 << bit)) != 0)
            {
                yield return (ushort)(pid + bit + 1);
            }
        }
    }

    public static void SendNacks(RTCPeerConnection pc, IReadOnlyList<ushort> sequenceNumbers)
    {
        if (sequenceNumbers.Count == 0)
        {
            return;
        }

        var (localSsrc, remoteSsrc) = GetVideoSsrcs(pc);
        foreach (var (pid, blp) in BuildNackItems(sequenceNumbers))
        {
            pc.SendRtcpFeedback(SDPMediaTypesEnum.video, new RTCPFeedback(localSsrc, remoteSsrc, RTCPFeedbackTypesEnum.NACK, pid, blp));
        }
    }

    public static void SendPictureLossIndication(RTCPeerConnection pc)
    {
        var (localSsrc, remoteSsrc) = GetVideoSsrcs(pc);
        pc.SendRtcpFeedback(SDPMediaTypesEnum.video, new RTCPFeedback(localSsrc, remoteSsrc, PSFBFeedbackTypesEnum.PLI));
    }

    public static bool IsPictureLossIndication(RTCPFeedback? feedback) =>
        feedback is not null &&
        feedback.Header.PacketType == RTCPReportTypesEnum.PSFB &&
        feedback.Header.PayloadFeedbackMessageType == PSFBFeedbackTypesEnum.PLI;

    public static bool IsNack(RTCPFeedback? feedback) =>
        feedback is not null &&
        feedback.Header.PacketType == RTCPReportTypesEnum.RTPFB &&
        feedback.Header.FeedbackMessageType == RTCPFeedbackTypesEnum.NACK;

    private static (uint Local, uint Remote) GetVideoSsrcs(RTCPeerConnection pc)
    {
        var stream = pc.VideoStream;
        return (stream?.LocalTrack?.Ssrc ?? 0, stream?.RemoteTrack?.Ssrc ?? 0);
    }
}
