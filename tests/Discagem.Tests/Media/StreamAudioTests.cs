using Discagem.Media.Audio;
using Discagem.Media.WebRtc;

namespace Discagem.Tests.Media;

public sealed class StreamAudioTests
{
    private const int Rate = AudioTestSignals.Rate;

    /// <summary>Interleaved stereo: a tone on each channel.</summary>
    private static float[] StereoTones(double leftHz, double rightHz, int frames, int rate = Rate, float amplitude = 0.3f)
    {
        var left = new AudioTestSignals.Tone(leftHz, amplitude, rate).Next(frames);
        var right = new AudioTestSignals.Tone(rightHz, amplitude, rate).Next(frames);
        var stereo = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            stereo[i * 2] = left[i];
            stereo[i * 2 + 1] = right[i];
        }

        return stereo;
    }

    private static (float[] Left, float[] Right) Split(ReadOnlySpan<float> stereo)
    {
        var left = new float[stereo.Length / 2];
        var right = new float[stereo.Length / 2];
        for (var i = 0; i < left.Length; i++)
        {
            left[i] = stereo[i * 2];
            right[i] = stereo[i * 2 + 1];
        }

        return (left, right);
    }

    [Fact]
    public void SenderAndReceiver_KeepTheChannelsApart()
    {
        var packets = new List<byte[]>();
        using var sender = new StreamAudioSender((packet, _) => packets.Add(packet.ToArray()));

        // One second of audio, delivered in uneven blocks as a capture device would.
        var input = StereoTones(440, 1000, Rate);
        var random = new Random(7);
        for (var offset = 0; offset < input.Length;)
        {
            var count = Math.Min(input.Length - offset, random.Next(1, 600) * 2);
            sender.Write(input.AsSpan(offset, count));
            offset += count;
        }

        Assert.Equal(50, packets.Count);

        using var receiver = new StreamAudioReceiver(1);
        var started = 0;
        receiver.Started += () => started++;
        var output = new List<float>();
        // One 20 ms frame per packet, read in blocks that do not line up with the frames.
        var block = new float[1920];
        for (var i = 0; i < packets.Count; i++)
        {
            receiver.Push((ushort)(i + 1000), packets[i]);
            receiver.Read(block.AsSpan(0, 960));
            receiver.Read(block.AsSpan(960, 480));
            receiver.Read(block.AsSpan(1440));
            output.AddRange(block);
        }

        Assert.Equal(1, started);
        Assert.Equal(0, receiver.FramesConcealed);
        Assert.True(receiver.FramesDecoded >= 40, $"decoded {receiver.FramesDecoded} frames");

        // Skip the start (buffering and the codec's warm-up) and look at the steady state.
        var (left, right) = Split(output.ToArray().AsSpan(output.Count / 2));
        Assert.True(AudioTestSignals.Amplitude(left, 440) > 0.2, "left channel lost its tone");
        Assert.True(AudioTestSignals.Amplitude(right, 1000) > 0.2, "right channel lost its tone");
        Assert.True(AudioTestSignals.Amplitude(left, 1000) < 0.03, "right channel leaked into the left");
        Assert.True(AudioTestSignals.Amplitude(right, 440) < 0.03, "left channel leaked into the right");
    }

    [Fact]
    public void Receiver_AppliesVolumeAndMute()
    {
        var packets = new List<byte[]>();
        using var sender = new StreamAudioSender((packet, _) => packets.Add(packet.ToArray()));
        sender.Write(StereoTones(440, 440, Rate));

        using var receiver = new StreamAudioReceiver(1) { Volume = 0.5f };
        var block = new float[1920];
        double RmsAfter(int index)
        {
            receiver.Push((ushort)index, packets[index]);
            receiver.Read(block);
            return AudioTestSignals.Rms(block);
        }

        for (var i = 0; i < 20; i++)
        {
            RmsAfter(i);
        }

        var half = RmsAfter(20);
        Assert.InRange(half, 0.3 * 0.707 * 0.5 * 0.8, 0.3 * 0.707 * 0.5 * 1.2);

        receiver.IsMuted = true;
        Assert.Equal(0, RmsAfter(21));

        receiver.IsMuted = false;
        receiver.Volume = 1f;
        Assert.InRange(RmsAfter(22), 0.3 * 0.707 * 0.8, 0.3 * 0.707 * 1.2);
    }

    [Fact]
    public void Sender_PausesDuringSilence()
    {
        var sent = 0;
        using var sender = new StreamAudioSender((_, _) => sent++);

        sender.Write(new float[Rate * 2 * 2]);
        Assert.Equal(25, sent);
        Assert.Equal(75, sender.FramesSkipped);

        sender.Write(StereoTones(440, 440, Rate / 10));
        Assert.Equal(30, sent);
    }

    [Fact]
    public void ToStereo_DownmixesSurroundAndUpmixesMono()
    {
        var format51 = new PcmFormat(48000, 6, PcmSampleType.Float32);
        var frame51 = new float[] { 0.1f, 0.2f, 0.4f, 0.9f, 0.05f, 0.06f };
        var bytes = new byte[frame51.Length * 4];
        Buffer.BlockCopy(frame51, 0, bytes, 0, bytes.Length);
        var stereo = new float[2];
        Assert.Equal(1, PcmConversion.ToStereo(bytes, format51, stereo));
        Assert.Equal(0.1f + 0.4f * 0.7071f + 0.05f * 0.7071f, stereo[0], 4);
        Assert.Equal(0.2f + 0.4f * 0.7071f + 0.06f * 0.7071f, stereo[1], 4);

        var mono = new PcmFormat(48000, 1, PcmSampleType.Int16);
        Assert.Equal(1, PcmConversion.ToStereo([0x00, 0x40], mono, stereo));
        Assert.Equal(0.5f, stereo[0], 4);
        Assert.Equal(0.5f, stereo[1], 4);
    }

    [Fact]
    public void FromStereo_FillsTheFirstTwoChannels()
    {
        var quad = new PcmFormat(48000, 4, PcmSampleType.Float32);
        var output = new byte[2 * quad.BlockAlign];
        PcmConversion.FromStereo([0.25f, -0.5f, 0.1f, 0.2f], quad, output);
        var samples = new float[8];
        Buffer.BlockCopy(output, 0, samples, 0, output.Length);
        Assert.Equal([0.25f, -0.5f, 0f, 0f, 0.1f, 0.2f, 0f, 0f], samples);

        var mono = new PcmFormat(48000, 1, PcmSampleType.Float32);
        var monoOutput = new byte[4];
        PcmConversion.FromStereo([0.25f, -0.5f], mono, monoOutput);
        Assert.Equal(-0.125f, BitConverter.ToSingle(monoOutput));
    }

    [Fact]
    public void InterleavedResampler_KeepsChannelsApart()
    {
        var resampler = new InterleavedResampler(2, 44100, Rate);
        var input = StereoTones(440, 1000, 44100, 44100);
        var output = new float[resampler.MaxOutputFor(input.Length)];
        var written = 0;
        for (var offset = 0; offset < input.Length; offset += 882)
        {
            var count = Math.Min(882, input.Length - offset);
            var scratch = new float[resampler.MaxOutputFor(count)];
            var produced = resampler.Process(input.AsSpan(offset, count), scratch);
            scratch.AsSpan(0, produced).CopyTo(output.AsSpan(written));
            written += produced;
        }

        Assert.InRange(written, Rate * 2 - 8, Rate * 2 + 8);
        var (left, right) = Split(output.AsSpan(Rate / 2 * 2, Rate));
        Assert.True(AudioTestSignals.Amplitude(left, 440) > 0.25);
        Assert.True(AudioTestSignals.Amplitude(right, 1000) > 0.25);
        Assert.True(AudioTestSignals.Amplitude(left, 1000) < 0.02);
        Assert.True(AudioTestSignals.Amplitude(right, 440) < 0.02);
    }

    [Fact]
    public void PlaybackResampler_ProducesStereoAtTheDeviceRate()
    {
        var tones = new Queue<float>(StereoTones(440, 1000, Rate));
        var resampler = new PlaybackResampler(44100, buffer =>
        {
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = tones.Count > 0 ? tones.Dequeue() : 0;
            }
        }, channels: 2);

        var output = new float[44100];
        resampler.Read(output);
        var (left, right) = Split(output.AsSpan(22050));
        Assert.True(AudioTestSignals.Amplitude(left, 440, 44100) > 0.25);
        Assert.True(AudioTestSignals.Amplitude(right, 1000, 44100) > 0.25);
        Assert.True(AudioTestSignals.Amplitude(left, 1000, 44100) < 0.02);
    }
}
