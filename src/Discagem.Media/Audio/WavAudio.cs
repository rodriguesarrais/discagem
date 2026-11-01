using System.Buffers.Binary;
using Discagem.Media.WebRtc;

namespace Discagem.Media.Audio;

/// <summary>Reads short WAV sounds (notification cues) into 48 kHz mono samples for the voice mixer.</summary>
public static class WavAudio
{
    /// <summary>
    /// Decodes a RIFF/WAVE file holding 16-bit PCM or 32-bit float samples, mixing the channels down
    /// to mono and resampling to <see cref="DiscagemAudio.SampleRate"/> when needed.
    /// </summary>
    public static float[] ReadMono(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return ReadMono(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    public static float[] ReadMono(ReadOnlySpan<byte> file)
    {
        if (file.Length < 12 || !file[..4].SequenceEqual("RIFF"u8) || !file[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Not a WAV file.");
        }

        int format = 0, channels = 0, rate = 0, bits = 0;
        ReadOnlySpan<byte> data = default;
        var position = 12;
        while (position + 8 <= file.Length)
        {
            var id = file.Slice(position, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(position + 4, 4));
            var body = position + 8;
            if (size < 0 || body + size > file.Length)
            {
                size = file.Length - body;
            }

            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(body + 2, 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(body + 14, 2));
                if (format == 0xFFFE && size >= 40)
                {
                    // WAVE_FORMAT_EXTENSIBLE: the real format is the first two bytes of the sub-format GUID.
                    format = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(body + 24, 2));
                }
            }
            else if (id.SequenceEqual("data"u8))
            {
                data = file.Slice(body, size);
            }

            position = body + size + (size & 1);
        }

        var pcm16 = format == 1 && bits == 16;
        var float32 = format == 3 && bits == 32;
        if ((!pcm16 && !float32) || channels < 1 || rate < 8000 || rate > 192_000)
        {
            throw new InvalidDataException("Only 16-bit PCM and 32-bit float WAV files are supported.");
        }

        var frameBytes = channels * bits / 8;
        var frames = data.Length / frameBytes;
        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            var frame = data.Slice(i * frameBytes, frameBytes);
            var sum = 0f;
            for (var c = 0; c < channels; c++)
            {
                sum += pcm16
                    ? BinaryPrimitives.ReadInt16LittleEndian(frame.Slice(c * 2, 2)) / 32768f
                    : BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(c * 4, 4));
            }

            mono[i] = sum / channels;
        }

        return rate == DiscagemAudio.SampleRate ? mono : Resample(mono, rate, DiscagemAudio.SampleRate);
    }

    private static float[] Resample(float[] input, int from, int to)
    {
        if (input.Length == 0)
        {
            return input;
        }

        var output = new float[(int)((long)input.Length * to / from)];
        var step = (double)from / to;
        for (var i = 0; i < output.Length; i++)
        {
            var position = i * step;
            var index = (int)position;
            var next = Math.Min(index + 1, input.Length - 1);
            var fraction = (float)(position - index);
            output[i] = input[index] + (input[next] - input[index]) * fraction;
        }

        return output;
    }
}
