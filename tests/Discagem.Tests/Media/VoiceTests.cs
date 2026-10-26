using Discagem.Media.Audio;
using Discagem.Media.WebRtc;
using static Discagem.Tests.Media.AudioTestSignals;

namespace Discagem.Tests.Media;

public sealed class VoiceTests
{
    private const int Frame = DiscagemAudio.FrameSamples;

    /// <summary>Encodes <paramref name="frames"/> frames of a tone into Opus packets.</summary>
    private static List<byte[]> EncodeTone(double frequency, int frames, float amplitude = 0.3f)
    {
        using var encoder = new OpusVoiceEncoder();
        var tone = new Tone(frequency, amplitude);
        var packet = new byte[OpusVoiceEncoder.MaxPacketBytes];
        var packets = new List<byte[]>();
        for (var i = 0; i < frames; i++)
        {
            var length = encoder.Encode(tone.Next(Frame), packet);
            packets.Add(packet.AsSpan(0, length).ToArray());
        }

        return packets;
    }

    [Fact]
    public void Opus_RoundTripsATone()
    {
        var packets = EncodeTone(440, 50);
        Assert.All(packets, p => Assert.InRange(p.Length, 10, 400));

        using var decoder = new OpusVoiceDecoder();
        var output = new float[Frame * packets.Count];
        for (var i = 0; i < packets.Count; i++)
        {
            Assert.True(decoder.Decode(packets[i], output.AsSpan(i * Frame, Frame)));
        }

        // Skip the codec's start-up; the tone must come through at roughly its original level.
        var steady = output.AsSpan(Frame * 10);
        Assert.InRange(Amplitude(steady, 440), 0.2, 0.4);
        Assert.True(Amplitude(steady, 440) > 20 * Amplitude(steady, 1000));
    }

    [Fact]
    public void OpusDecoder_RejectsGarbage()
    {
        using var decoder = new OpusVoiceDecoder();
        var frame = new float[Frame];
        Assert.False(decoder.Decode(new byte[] { 0xFF, 0xFF, 0xFF, 0x01, 0x02 }, frame));
        Assert.True(decoder.Conceal(frame));
    }

    [Fact]
    public void VoicePayload_RoundTrips()
    {
        var wrapped = VoicePayload.Wrap(new byte[] { 1, 2, 3 }, 65535, 42);
        Assert.True(VoicePayload.TryUnwrap(wrapped, out var sequence, out var speaker, out var opus));
        Assert.Equal(65535, sequence);
        Assert.Equal(42u, speaker);
        Assert.Equal(new byte[] { 1, 2, 3 }, opus.ToArray());

        Assert.False(VoicePayload.TryUnwrap(new byte[VoicePayload.HeaderSize], out _, out _, out _));
        wrapped[0] = 9;
        Assert.False(VoicePayload.TryUnwrap(wrapped, out _, out _, out _));
    }

    [Fact]
    public void SpeakerStream_BuffersPlaysAndGoesIdle()
    {
        var packets = EncodeTone(440, 30);
        using var stream = new SpeakerStream(7);
        var frame = new float[Frame];
        long now = 0;

        stream.Push(100, packets[0], now);
        Assert.False(stream.Read(frame, now)); // still buffering
        stream.Push(101, packets[1], now += 20);
        Assert.True(stream.Read(frame, now)); // two frames (the minimum target) are enough

        var played = 1;
        for (var i = 2; i < packets.Count; i++)
        {
            stream.Push((ushort)(100 + i), packets[i], now += 20);
            Assert.True(stream.Read(frame, now));
            played++;
        }

        Assert.True(stream.IsSpeaking(now));

        // The talk spurt ends: the last buffered frame plays, then a few concealed frames, then silence.
        var audible = 0;
        for (var i = 0; i < 10; i++)
        {
            if (stream.Read(frame, now += 20))
            {
                audible++;
            }
        }

        Assert.InRange(audible, 1, 5);
        Assert.False(stream.IsSpeaking(now + 300));
        Assert.Equal(packets.Count, stream.FramesDecoded);
        Assert.Equal(0, stream.LatePackets);
    }

    [Fact]
    public void SpeakerStream_RecoversLostFramesWithFec()
    {
        var packets = EncodeTone(440, 40);
        using var stream = new SpeakerStream(1);
        var frame = new float[Frame];
        long now = 0;

        // Packet 10 is lost; 11 arrives on time and carries 10's FEC data.
        for (var i = 0; i < packets.Count; i++)
        {
            if (i != 10)
            {
                stream.Push((ushort)(65530 + i), packets[i], now);
            }

            if (i >= 1)
            {
                stream.Read(frame, now);
            }

            now += 20;
        }

        Assert.Equal(1, stream.FramesRecovered);
        Assert.Equal(0, stream.FramesConcealed);
    }

    [Fact]
    public void SpeakerStream_ReordersAndDropsLatePackets()
    {
        var packets = EncodeTone(300, 12);
        using var stream = new SpeakerStream(1);
        var frame = new float[Frame];

        // 0, 2, 1 arrive before playback starts: played in order.
        stream.Push(0, packets[0], 0);
        stream.Push(2, packets[2], 0);
        stream.Push(1, packets[1], 0);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(stream.Read(frame, 20));
        }

        Assert.Equal(3, stream.FramesDecoded);

        // A duplicate of an already played packet is late.
        stream.Push(1, packets[1], 40);
        Assert.Equal(1, stream.LatePackets);
    }

    [Fact]
    public void SpeakerStream_HandlesAStreamRestart()
    {
        var packets = EncodeTone(300, 20);
        using var stream = new SpeakerStream(1);
        var frame = new float[Frame];
        long now = 0;
        for (var i = 0; i < 5; i++)
        {
            stream.Push((ushort)(30000 + i), packets[i], now);
            stream.Read(frame, now += 20);
        }

        // The speaker rejoined and their sequence numbers restarted far below.
        for (var i = 5; i < 15; i++)
        {
            stream.Push((ushort)(10 + i), packets[i], now);
            stream.Read(frame, now += 20);
        }

        Assert.True(stream.FramesDecoded >= 12);
    }

    [Fact]
    public void SpeakerStream_SkipsFramesWhenTooFarBehind()
    {
        var packets = EncodeTone(300, 30);
        using var stream = new SpeakerStream(1);
        var frame = new float[Frame];

        // A burst of 20 packets after a stall would add 400 ms of latency.
        for (var i = 0; i < 20; i++)
        {
            stream.Push((ushort)i, packets[i], 0);
        }

        for (var i = 0; i < 10; i++)
        {
            stream.Read(frame, 20 + i * 20);
        }

        Assert.True(stream.FramesSkipped > 0);
        Assert.True(stream.BufferedPackets <= stream.TargetFrames + 3);
    }

    [Fact]
    public void Mixer_CombinesSpeakersAndReportsWhoIsSpeaking()
    {
        var a = EncodeTone(440, 40, 0.2f);
        var b = EncodeTone(1000, 40, 0.2f);
        long now = 0;
        using var mixer = new VoiceMixer(() => now);
        var speaking = new List<(uint, bool)>();
        mixer.SpeakingChanged += (id, on) => speaking.Add((id, on));

        var output = new float[Frame * 40];
        for (var i = 0; i < 40; i++)
        {
            mixer.Push(1, (ushort)i, a[i]);
            mixer.Push(2, (ushort)(500 + i), b[i]);

            // Read in odd block sizes, as audio devices do.
            var block = output.AsSpan(i * Frame, Frame);
            mixer.Read(block[..333]);
            mixer.Read(block[333..]);
            now += 20;
        }

        var steady = output.AsSpan(Frame * 15);
        Assert.InRange(Amplitude(steady, 440), 0.1, 0.3);
        Assert.InRange(Amplitude(steady, 1000), 0.1, 0.3);
        Assert.Contains((1u, true), speaking);
        Assert.Contains((2u, true), speaking);

        // Per-speaker volume, and removal.
        mixer.SetSpeakerGain(2, 0);
        mixer.RemoveSpeaker(1);
        Assert.Contains((1u, false), speaking);
        Assert.Equal(0, mixer.GetSpeakerGain(2));
        Assert.Single(mixer.Speakers);
    }

    [Fact]
    public void Mixer_MuteVoicesStillPlaysCues()
    {
        var packets = EncodeTone(440, 20);
        long now = 0;
        using var mixer = new VoiceMixer(() => now) { MuteVoices = true };
        var output = new float[Frame];
        for (var i = 0; i < 20; i++)
        {
            mixer.Push(1, (ushort)i, packets[i]);
            mixer.Read(output);
            now += 20;
            Assert.Equal(0, Rms(output));
        }

        mixer.PlayCue(VoiceCues.Join);
        mixer.Read(output);
        Assert.True(Rms(output) > 0.01);
    }

    [Fact]
    public void Engine_TransmitsOnlySpeechAndHonoursMute()
    {
        using var engine = new VoiceEngine { InputThresholdDb = -30 };
        var sent = new List<(byte[] Packet, uint Timestamp)>();
        engine.PacketReady = (packet, timestamp) => sent.Add((packet.ToArray(), timestamp));

        // Near-silence is not transmitted.
        engine.WriteCapture(new Tone(440, 0.001f).Next(Frame * 10));
        Assert.Empty(sent);
        Assert.False(engine.IsTransmitting);

        // Speech is, starting with one frame of pre-roll; timestamps advance by one frame.
        engine.WriteCapture(new Tone(440, 0.3f).Next(Frame * 5));
        Assert.Equal(6, sent.Count);
        Assert.True(engine.IsTransmitting);
        for (var i = 1; i < sent.Count; i++)
        {
            Assert.Equal((uint)Frame, sent[i].Timestamp - sent[i - 1].Timestamp);
        }

        // The hangover keeps the gate open briefly after the speaker stops.
        sent.Clear();
        engine.WriteCapture(new float[Frame * 30]);
        Assert.Equal(15, sent.Count);
        Assert.False(engine.IsTransmitting);

        // Muted: nothing goes out, but the level is still measured for the meter.
        sent.Clear();
        engine.IsMuted = true;
        engine.WriteCapture(new Tone(440, 0.3f).Next(Frame * 5));
        Assert.Empty(sent);
        Assert.True(engine.InputLevelDb > -20);
    }

    [Fact]
    public void Resampler_PreservesFrequencyAndDuration()
    {
        var resampler = new LinearResampler(44_100, 48_000);
        var tone = new Tone(1000, 0.5f, 44_100);
        var output = new List<float>();
        var buffer = new float[resampler.MaxOutputFor(441)];
        for (var i = 0; i < 100; i++)
        {
            var written = resampler.Process(tone.Next(441), buffer);
            output.AddRange(buffer.AsSpan(0, written).ToArray());
        }

        // One second in, one second out (give or take a sample).
        Assert.InRange(output.Count, 47_998, 48_001);
        var samples = output.ToArray().AsSpan(4800);
        Assert.InRange(Amplitude(samples, 1000), 0.45, 0.55);
        Assert.True(Amplitude(samples, 1000) > 50 * Amplitude(samples, 1102.5));

        var same = new LinearResampler(48_000, 48_000);
        var input = new float[] { 0.1f, 0.2f, 0.3f };
        var copy = new float[same.MaxOutputFor(3)];
        Assert.Equal(3, same.Process(input, copy));
        Assert.Equal(input, copy.Take(3));
    }

    [Fact]
    public void VoiceActivityDetector_MeasuresLevel()
    {
        var vad = new VoiceActivityDetector { ThresholdDb = -30, HangoverFrames = 2 };
        Assert.False(vad.Process(new float[Frame]));
        Assert.Equal(VoiceActivityDetector.SilenceDb, vad.LevelDb);

        // A full-scale sine has an RMS of -3 dBFS.
        Assert.True(vad.Process(new Tone(500, 1f).Next(Frame)));
        Assert.InRange(vad.LevelDb, -3.5, -2.5);

        Assert.True(vad.Process(new float[Frame]));
        Assert.True(vad.Process(new float[Frame]));
        Assert.False(vad.Process(new float[Frame]));
    }
}
