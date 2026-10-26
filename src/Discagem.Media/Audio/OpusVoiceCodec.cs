using Concentus;
using Concentus.Enums;
using Discagem.Media.WebRtc;

namespace Discagem.Media.Audio;

/// <summary>Encodes 20 ms frames of 48 kHz mono speech with Opus.</summary>
public sealed class OpusVoiceEncoder : IDisposable
{
    /// <summary>Largest Opus packet for a single frame (RFC 6716, section 3.4).</summary>
    public const int MaxPacketBytes = 1275;

    private readonly IOpusEncoder _encoder;

    public OpusVoiceEncoder(int bitrate = 40_000)
    {
        _encoder = OpusCodecFactory.CreateEncoder(DiscagemAudio.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = bitrate;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        _encoder.UseVBR = true;
        _encoder.Complexity = 8;

        // In-band forward error correction lets receivers rebuild a lost frame from the next packet.
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 10;
    }

    /// <summary>Encodes one frame of <see cref="DiscagemAudio.FrameSamples"/> samples. Returns the packet length.</summary>
    public int Encode(ReadOnlySpan<float> frame, Span<byte> packet)
    {
        if (frame.Length != DiscagemAudio.FrameSamples)
        {
            throw new ArgumentException($"Expected {DiscagemAudio.FrameSamples} samples.", nameof(frame));
        }

        return _encoder.Encode(frame, DiscagemAudio.FrameSamples, packet, packet.Length);
    }

    public void Dispose() => (_encoder as IDisposable)?.Dispose();
}

/// <summary>Decodes one speaker's Opus stream into 20 ms frames of 48 kHz mono audio.</summary>
public sealed class OpusVoiceDecoder : IDisposable
{
    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(DiscagemAudio.SampleRate, 1);

    /// <summary>Decodes a packet into <paramref name="frame"/>. Returns false for packets that are not a valid 20 ms frame.</summary>
    public bool Decode(ReadOnlySpan<byte> packet, Span<float> frame) => Run(packet, frame, fec: false);

    /// <summary>Rebuilds the frame before <paramref name="nextPacket"/> from its forward error correction data.</summary>
    public bool DecodeFec(ReadOnlySpan<byte> nextPacket, Span<float> frame) => Run(nextPacket, frame, fec: true);

    /// <summary>Synthesises a replacement for a lost frame (packet loss concealment).</summary>
    public bool Conceal(Span<float> frame) => Run(ReadOnlySpan<byte>.Empty, frame, fec: false);

    private bool Run(ReadOnlySpan<byte> packet, Span<float> frame, bool fec)
    {
        var samples = frame[..DiscagemAudio.FrameSamples];
        try
        {
            if (_decoder.Decode(packet, samples, DiscagemAudio.FrameSamples, fec) == DiscagemAudio.FrameSamples)
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is OpusException or ArgumentException or IndexOutOfRangeException)
        {
            // Corrupt or oversized packet (anyone in the channel can send arbitrary bytes).
        }

        samples.Clear();
        return false;
    }

    public void Dispose() => (_decoder as IDisposable)?.Dispose();
}
