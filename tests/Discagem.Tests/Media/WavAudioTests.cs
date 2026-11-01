using System.Buffers.Binary;
using Discagem.Media.Audio;

namespace Discagem.Tests.Media;

public sealed class WavAudioTests
{
    /// <summary>Builds a WAV file: format 1 (PCM, 16-bit) or 3 (float, 32-bit), interleaved samples.</summary>
    private static byte[] Wav(int format, int channels, int rate, float[] interleaved, bool extraChunk = false, bool extensible = false)
    {
        var bits = format == 3 ? 32 : 16;
        var data = new byte[interleaved.Length * bits / 8];
        for (var i = 0; i < interleaved.Length; i++)
        {
            if (format == 3)
            {
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), interleaved[i]);
            }
            else
            {
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), (short)Math.Round(interleaved[i] * 32767));
            }
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(0);
        writer.Write("WAVE"u8);
        if (extraChunk)
        {
            // An odd-sized chunk before "fmt ", padded to an even length as the format requires.
            writer.Write("LIST"u8);
            writer.Write(3);
            writer.Write(new byte[] { 1, 2, 3, 0 });
        }

        writer.Write("fmt "u8);
        writer.Write(extensible ? 40 : 16);
        writer.Write((short)(extensible ? 0xFFFE : format));
        writer.Write((short)channels);
        writer.Write(rate);
        writer.Write(rate * channels * bits / 8);
        writer.Write((short)(channels * bits / 8));
        writer.Write((short)bits);
        if (extensible)
        {
            writer.Write((short)22);
            writer.Write((short)bits);
            writer.Write(0);
            writer.Write((short)format);
            writer.Write(new byte[14]);
        }

        writer.Write("data"u8);
        writer.Write(data.Length);
        writer.Write(data);
        writer.Flush();
        var file = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), file.Length - 8);
        return file;
    }

    [Fact]
    public void Reads16BitPcmAndMixesStereoDown()
    {
        var stereo = new float[] { 0.5f, -0.5f, 0.25f, 0.75f, -1f, -1f };

        var mono = WavAudio.ReadMono(Wav(1, 2, 48_000, stereo));

        Assert.Equal(3, mono.Length);
        Assert.Equal(0f, mono[0], 3);
        Assert.Equal(0.5f, mono[1], 3);
        Assert.Equal(-1f, mono[2], 3);
    }

    [Fact]
    public void ReadsFloatSamplesAndSkipsOtherChunks()
    {
        var samples = new float[] { 0.1f, -0.2f, 0.3f };

        var mono = WavAudio.ReadMono(Wav(3, 1, 48_000, samples, extraChunk: true));

        Assert.Equal(samples, mono);
    }

    [Fact]
    public void ReadsExtensibleFormat()
    {
        var mono = WavAudio.ReadMono(Wav(1, 1, 48_000, [0.5f, -0.5f], extensible: true));

        Assert.Equal(2, mono.Length);
        Assert.Equal(0.5f, mono[0], 3);
    }

    [Fact]
    public void ResamplesTo48Kilohertz()
    {
        // One second of a 440 Hz tone at 44.1 kHz becomes one second at 48 kHz, still at 440 Hz.
        var tone = new AudioTestSignals.Tone(440, 0.5f, 44_100).Next(44_100);

        var mono = WavAudio.ReadMono(Wav(3, 1, 44_100, tone));

        Assert.InRange(mono.Length, 47_990, 48_000);
        var crossings = 0;
        for (var i = 1; i < mono.Length; i++)
        {
            if (mono[i - 1] < 0 && mono[i] >= 0)
            {
                crossings++;
            }
        }

        Assert.InRange(crossings, 438, 442);
    }

    [Theory]
    [InlineData("not a wav file at all")]
    [InlineData("RIFF\0\0\0\0WAVE")]
    public void RejectsWhatItCannotRead(string content)
    {
        Assert.Throws<InvalidDataException>(() => WavAudio.ReadMono(System.Text.Encoding.ASCII.GetBytes(content)));
    }

    [Fact]
    public void Rejects8BitPcm()
    {
        var file = Wav(1, 1, 48_000, [0f, 0f]);
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(34), 8);

        Assert.Throws<InvalidDataException>(() => WavAudio.ReadMono(file));
    }

    /// <summary>The theme sounds shipped with the client decode to short 48 kHz cues.</summary>
    [Theory]
    [InlineData("join.wav")]
    [InlineData("leave.wav")]
    [InlineData("message.wav")]
    public void ShippedSoundsAreShortCues(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Discagem.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        using var file = File.OpenRead(Path.Combine(directory.FullName, "src", "Discagem.Client", "Assets", "Sounds", name));

        var samples = WavAudio.ReadMono(file);

        Assert.InRange(samples.Length / 48_000.0, 0.3, 1.5);
        var peak = samples.Max(Math.Abs);
        Assert.InRange(peak, 0.05f, 0.5f);
    }
}
