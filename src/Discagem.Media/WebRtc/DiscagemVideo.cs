using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace Discagem.Media.WebRtc;

/// <summary>SDP-level description of the Discagem screen share video track.</summary>
public static class DiscagemVideo
{
    public const int PayloadType = 96;

    public static VideoFormat Format => new(VideoCodecsEnum.VP8, PayloadType, 90000);

    public static MediaStreamTrack CreateTrack(MediaStreamStatusEnum direction) =>
        new(Format, direction);

    /// <summary>RTP clock rate used for timestamps.</summary>
    public const int ClockRate = 90000;
}
