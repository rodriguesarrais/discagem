using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace Discagem.Media.WebRtc;

/// <summary>SDP-level description of the Discagem voice track (Opus, 48 kHz, 20 ms frames).</summary>
public static class DiscagemAudio
{
    public const int PayloadType = 111;

    public const int SampleRate = 48000;

    /// <summary>Samples per 20 ms frame at 48 kHz.</summary>
    public const int FrameSamples = 960;

    public const int FrameMilliseconds = 20;

    public static AudioFormat Format => new(AudioCodecsEnum.OPUS, PayloadType, SampleRate, 2, "minptime=10;useinbandfec=1");

    public static MediaStreamTrack CreateTrack(MediaStreamStatusEnum direction) => new(Format, direction);
}
