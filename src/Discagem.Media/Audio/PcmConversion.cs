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

    /// <summary>
    /// Converts interleaved samples to interleaved stereo (mono is duplicated; surround is downmixed
    /// with the centre and surround channels at -3 dB). Returns the number of frames written.
    /// </summary>
    public static int ToStereo(ReadOnlySpan<byte> interleaved, PcmFormat format, Span<float> stereo)
    {
        var frames = interleaved.Length / format.BlockAlign;
        if (stereo.Length < frames * 2)
        {
            throw new ArgumentException("The output buffer is too small.", nameof(stereo));
        }

        const float Minus3Db = 0.7071f;
        var bytesPerSample = format.BytesPerSample;
        for (var frame = 0; frame < frames; frame++)
        {
            var samples = interleaved.Slice(frame * format.BlockAlign, format.BlockAlign);

            float left, right;
            if (format.Channels == 1)
            {
                left = right = Read(samples, format.SampleType);
            }
            else
            {
                left = Read(samples, format.SampleType);
                right = Read(samples[bytesPerSample..], format.SampleType);
                if (format.Channels >= 3)
                {
                    var centre = Read(samples[(2 * bytesPerSample)..], format.SampleType) * Minus3Db;
                    left += centre;
                    right += centre;
                }

                // Channel 3 is the LFE; the rest are surround pairs (back, then side).
                for (var channel = 4; channel + 1 < format.Channels; channel += 2)
                {
                    left += Read(samples[(channel * bytesPerSample)..], format.SampleType) * Minus3Db;
                    right += Read(samples[((channel + 1) * bytesPerSample)..], format.SampleType) * Minus3Db;
                }
            }

            stereo[frame * 2] = left;
            stereo[frame * 2 + 1] = right;
        }

        return frames;
    }

    /// <summary>
    /// Writes interleaved stereo samples (<c>stereo.Length / 2</c> frames) to a device buffer: left and
    /// right go to the first two channels and any others stay silent; a mono device gets the average.
    /// </summary>
    public static void FromStereo(ReadOnlySpan<float> stereo, PcmFormat format, Span<byte> interleaved)
    {
        var frames = stereo.Length / 2;
        if (interleaved.Length < frames * format.BlockAlign)
        {
            throw new ArgumentException("The output buffer is too small.", nameof(interleaved));
        }

        var bytesPerSample = format.BytesPerSample;
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = frame * format.BlockAlign;
            var left = stereo[frame * 2];
            var right = stereo[frame * 2 + 1];
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var value = format.Channels == 1 ? (left + right) * 0.5f : channel switch
                {
                    0 => left,
                    1 => right,
                    _ => 0f,
                };
                Write(interleaved.Slice(offset + channel * bytesPerSample, bytesPerSample), format.SampleType, value);
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

/// <summary>
/// Produces playback samples at the device rate from a 48 kHz source pulled on demand (mono, or
/// interleaved with <c>channels</c> channels).
/// </summary>
public sealed class PlaybackResampler
{
    private const int BlockFrames = 480;

    private readonly AudioSource _source;
    private readonly InterleavedResampler _resampler;
    private readonly float[] _input;
    private readonly float[] _converted;
    private int _start;
    private int _count;

    public PlaybackResampler(int deviceRate, AudioSource source, int channels = 1)
    {
        _source = source;
        _resampler = new InterleavedResampler(channels, DiscagemAudio.SampleRate, deviceRate);
        _input = new float[BlockFrames * channels];
        _converted = new float[_resampler.MaxOutputFor(_input.Length)];
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

/// <summary>Resamples interleaved audio by converting each channel with its own <see cref="LinearResampler"/>.</summary>
public sealed class InterleavedResampler
{
    private readonly LinearResampler[] _channels;
    private float[] _planar = [];
    private float[] _converted = [];

    public InterleavedResampler(int channels, int inputRate, int outputRate)
    {
        if (channels < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        _channels = new LinearResampler[channels];
        for (var i = 0; i < channels; i++)
        {
            _channels[i] = new LinearResampler(inputRate, outputRate);
        }
    }

    public int Channels => _channels.Length;

    public bool IsPassThrough => _channels[0].IsPassThrough;

    /// <summary>Upper bound of the samples produced from <paramref name="inputCount"/> interleaved input samples.</summary>
    public int MaxOutputFor(int inputCount) => _channels[0].MaxOutputFor(inputCount / Channels) * Channels;

    /// <summary>Converts a block of whole frames. Returns the number of samples written.</summary>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        var channels = Channels;
        if (channels == 1)
        {
            return _channels[0].Process(input, output);
        }

        var frames = input.Length / channels;
        if (output.Length < MaxOutputFor(input.Length))
        {
            throw new ArgumentException("The output buffer is too small.", nameof(output));
        }

        var maxFrames = _channels[0].MaxOutputFor(frames);
        if (_planar.Length < frames)
        {
            _planar = new float[frames];
        }

        if (_converted.Length < maxFrames)
        {
            _converted = new float[maxFrames];
        }

        var written = 0;
        for (var channel = 0; channel < channels; channel++)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                _planar[frame] = input[frame * channels + channel];
            }

            // Every channel advances identically, so they all produce the same number of frames.
            written = _channels[channel].Process(_planar.AsSpan(0, frames), _converted);
            for (var frame = 0; frame < written; frame++)
            {
                output[frame * channels + channel] = _converted[frame];
            }
        }

        return written * channels;
    }
}
