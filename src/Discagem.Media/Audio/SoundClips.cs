using System.Buffers.Binary;
using Discagem.Media.WebRtc;

namespace Discagem.Media.Audio;

/// <summary>
/// Soundboard sounds: decodes the file a member picked (WAV, MP3 or Ogg Vorbis), trims it to the
/// allowed length and encodes it as the small 48 kHz mono WAV file that is uploaded and played.
/// </summary>
public static class SoundClips
{
    /// <summary>File extensions <see cref="Decode"/> understands.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".wav", ".mp3", ".ogg", ".oga"];

    /// <summary>Samples below this level at the start and end are silence, which is cut.</summary>
    private const float SilenceLevel = 0.004f;

    /// <summary>Peak level a clip is limited to, so that loud files do not clip in the mix.</summary>
    private const float MaxPeak = 0.9f;

    private const int FadeOutSamples = DiscagemAudio.SampleRate / 50;

    /// <summary>
    /// Decodes up to <paramref name="maxSeconds"/> of a sound file into 48 kHz mono samples, choosing
    /// the decoder from the file name's extension. Throws <see cref="InvalidDataException"/> for files
    /// that cannot be decoded.
    /// </summary>
    public static float[] Decode(Stream stream, string fileName, double maxSeconds = 30)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            return extension switch
            {
                ".wav" => Limit(WavAudio.ReadMono(stream), maxSeconds),
                ".mp3" => DecodeMp3(stream, maxSeconds),
                ".ogg" or ".oga" => DecodeVorbis(stream, maxSeconds),
                _ => throw new InvalidDataException("Choose a WAV, MP3 or Ogg Vorbis file."),
            };
        }
        catch (Exception ex) when (ex is not InvalidDataException and (ArgumentException or InvalidOperationException or IndexOutOfRangeException or NotSupportedException or EndOfStreamException))
        {
            throw new InvalidDataException("That sound file could not be read.", ex);
        }
    }

    /// <summary>
    /// Makes a soundboard clip: cuts silence at both ends, keeps at most
    /// <paramref name="maxMilliseconds"/> (fading out where it is cut) and lowers clips whose peak is
    /// above <see cref="MaxPeak"/>. Returns an empty array for silence.
    /// </summary>
    public static float[] Prepare(ReadOnlySpan<float> samples, int maxMilliseconds)
    {
        var start = 0;
        while (start < samples.Length && Math.Abs(samples[start]) < SilenceLevel)
        {
            start++;
        }

        var end = samples.Length;
        while (end > start && Math.Abs(samples[end - 1]) < SilenceLevel)
        {
            end--;
        }

        var maxSamples = (int)((long)maxMilliseconds * DiscagemAudio.SampleRate / 1000);
        var cut = end - start > maxSamples;
        var clip = samples.Slice(start, Math.Min(end - start, maxSamples)).ToArray();
        if (cut)
        {
            var fade = Math.Min(FadeOutSamples, clip.Length);
            for (var i = 0; i < fade; i++)
            {
                clip[clip.Length - fade + i] *= 1f - (i + 1f) / fade;
            }
        }

        var peak = 0f;
        foreach (var sample in clip)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        if (peak > MaxPeak)
        {
            var gain = MaxPeak / peak;
            for (var i = 0; i < clip.Length; i++)
            {
                clip[i] *= gain;
            }
        }

        return clip;
    }

    /// <summary>Encodes mono samples as a 48 kHz 16-bit PCM WAV file.</summary>
    public static byte[] EncodeWav(ReadOnlySpan<float> samples)
    {
        const int rate = DiscagemAudio.SampleRate;
        var file = new byte[44 + samples.Length * 2];
        var span = file.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], file.Length - 8);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], rate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], samples.Length * 2);
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + i * 2)..], value);
        }

        return file;
    }

    private static float[] Limit(float[] samples, double maxSeconds)
    {
        var max = (int)(maxSeconds * DiscagemAudio.SampleRate);
        return samples.Length <= max ? samples : samples[..max];
    }

    private static float[] DecodeMp3(Stream stream, double maxSeconds)
    {
        using var mp3 = new NLayer.MpegFile(stream);
        return ReadAll(mp3.SampleRate, mp3.Channels, maxSeconds, buffer => mp3.ReadSamples(buffer, 0, buffer.Length));
    }

    private static float[] DecodeVorbis(Stream stream, double maxSeconds)
    {
        using var vorbis = new NVorbis.VorbisReader(stream, closeOnDispose: false);
        return ReadAll(vorbis.SampleRate, vorbis.Channels, maxSeconds, buffer => vorbis.ReadSamples(buffer, 0, buffer.Length));
    }

    /// <summary>Reads interleaved samples until the end (or the limit), mixing them down to 48 kHz mono.</summary>
    private static float[] ReadAll(int rate, int channels, double maxSeconds, Func<float[], int> read)
    {
        if (channels < 1 || rate < 8000 || rate > 192_000)
        {
            throw new InvalidDataException("That sound file could not be read.");
        }

        var limit = (long)(maxSeconds * rate);
        var mono = new List<float>();
        var buffer = new float[4096 * channels];
        while (mono.Count < limit)
        {
            var count = read(buffer);
            if (count <= 0)
            {
                break;
            }

            for (var i = 0; i + channels <= count; i += channels)
            {
                var sum = 0f;
                for (var c = 0; c < channels; c++)
                {
                    sum += buffer[i + c];
                }

                mono.Add(sum / channels);
            }
        }

        if (mono.Count == 0)
        {
            throw new InvalidDataException("That sound file holds no sound.");
        }

        var samples = mono.Count > limit ? mono.GetRange(0, (int)limit).ToArray() : mono.ToArray();
        return rate == DiscagemAudio.SampleRate ? samples : WavAudio.Resample(samples, rate, DiscagemAudio.SampleRate);
    }
}
