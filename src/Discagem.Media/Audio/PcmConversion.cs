using System.Buffers.Binary;
using Discagem.Media.WebRtc;

namespace Discagem.Media.Audio;

/// <summary>Fills a buffer with audio (48 kHz mono float samples unless stated otherwise).</summary>
public delegate void AudioSource(Span<float> samples);

/// <summary>Consumes audio (48 kHz mono float samples unless stated otherwise).</summary>
public delegate void AudioSink(ReadOnlySpan<float> samples);

public enum PcmSampleType
{
    Float32,
    Int16,
    Int24,
    Int32,
}

/// <summary>An interleaved little-endian PCM format, as used by audio devices.</summary>
public readonly record struct PcmFormat(int SampleRate, int Channels, PcmSampleType SampleType)
{
    public int BytesPerSample => SampleType switch
    {
        PcmSampleType.Int16 => 2,
        PcmSampleType.Int24 => 3,
        _ => 4,
    };

    public int BlockAlign => BytesPerSample * Channels;
}

/// <summary>Converts between device PCM formats and the mono float samples used for voice.</summary>
public static class PcmConversion
{
    /// <summary>Downmixes interleaved samples to mono. Returns the number of frames written.</summary>
    public static int ToMono(ReadOnlySpan<byte> interleaved, PcmFormat format, Span<float> mono)
    {
        var frames = interleaved.Length / format.BlockAlign;
        if (mono.Length < frames)
        {
            throw new ArgumentException("The output buffer is too small.", nameof(mono));
        }

        var bytesPerSample = format.BytesPerSample;
        var scale = 1f / format.Channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = frame * format.BlockAlign;
            var sum = 0f;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                sum += Read(interleaved.Slice(offset + channel * bytesPerSample, bytesPerSample), format.SampleType);
            }

            mono[frame] = sum * scale;
        }

        return frames;
    }

    /// <summary>Writes mono samples to every channel of an interleaved buffer (<c>mono.Length</c> frames).</summary>
    public static void FromMono(ReadOnlySpan<float> mono, PcmFormat format, Span<byte> interleaved)
    {
        if (interleaved.Length < mono.Length * format.BlockAlign)
        {
            throw new ArgumentException("The output buffer is too small.", nameof(interleaved));
        }

        var bytesPerSample = format.BytesPerSample;
        for (var frame = 0; frame < mono.Length; frame++)
        {
            var offset = frame * format.BlockAlign;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                Write(interleaved.Slice(offset + channel * bytesPerSample, bytesPerSample), format.SampleType, mono[frame]);
            }
        }
    }

    private static float Read(ReadOnlySpan<byte> data, PcmSampleType type) => type switch
    {
        PcmSampleType.Float32 => BinaryPrimitives.ReadSingleLittleEndian(data),
        PcmSampleType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(data) / 32768f,
        PcmSampleType.Int24 => (((data[0] | (data[1] << 8) | (data[2] << 16)) << 8) >> 8) / 8388608f,
        _ => BinaryPrimitives.ReadInt32LittleEndian(data) / 2147483648f,
    };

    private static void Write(Span<byte> data, PcmSampleType type, float value)
    {
        value = Math.Clamp(value, -1f, 1f);
        switch (type)
        {
            case PcmSampleType.Float32:
                BinaryPrimitives.WriteSingleLittleEndian(data, value);
                break;
            case PcmSampleType.Int16:
                BinaryPrimitives.WriteInt16LittleEndian(data, (short)Math.Clamp(MathF.Round(value * 32768f), short.MinValue, short.MaxValue));
                break;
            case PcmSampleType.Int24:
                var v24 = (int)Math.Clamp(MathF.Round(value * 8388608f), -8388608, 8388607);
                data[0] = (byte)v24;
                data[1] = (byte)(v24 >> 8);
                data[2] = (byte)(v24 >> 16);
                break;
            default:
                BinaryPrimitives.WriteInt32LittleEndian(data, (int)Math.Clamp(value * 2147483648.0, int.MinValue, int.MaxValue));
                break;
        }
    }
}

/// <summary>Converts microphone samples from the device rate to 48 kHz before handing them on.</summary>
public sealed class CaptureResampler(int deviceRate, AudioSink sink)
{
    private readonly LinearResampler _resampler = new(deviceRate, DiscagemAudio.SampleRate);
    private float[] _buffer = [];

    public void Write(ReadOnlySpan<float> samples)
    {
        if (_resampler.IsPassThrough)
        {
            sink(samples);
            return;
        }

        var needed = _resampler.MaxOutputFor(samples.Length);
        if (_buffer.Length < needed)
        {
            _buffer = new float[needed];
        }

        var written = _resampler.Process(samples, _buffer);
        sink(_buffer.AsSpan(0, written));
    }
}

/// <summary>Produces playback samples at the device rate from a 48 kHz source pulled on demand.</summary>
public sealed class PlaybackResampler
{
    private const int BlockSamples = 480;

    private readonly AudioSource _source;
    private readonly LinearResampler _resampler;
    private readonly float[] _input = new float[BlockSamples];
    private readonly float[] _converted;
    private int _start;
    private int _count;

    public PlaybackResampler(int deviceRate, AudioSource source)
    {
        _source = source;
        _resampler = new LinearResampler(DiscagemAudio.SampleRate, deviceRate);
        _converted = new float[_resampler.MaxOutputFor(BlockSamples)];
    }

    public void Read(Span<float> output)
    {
        if (_resampler.IsPassThrough)
        {
            _source(output);
            return;
        }

        while (!output.IsEmpty)
        {
            if (_count == 0)
            {
                _source(_input);
                _count = _resampler.Process(_input, _converted);
                _start = 0;
            }

            var count = Math.Min(_count, output.Length);
            _converted.AsSpan(_start, count).CopyTo(output);
            _start += count;
            _count -= count;
            output = output[count..];
        }
    }
}
