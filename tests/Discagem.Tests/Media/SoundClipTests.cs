using Discagem.Media.Audio;
using Discagem.Media.WebRtc;

namespace Discagem.Tests.Media;

public sealed class SoundClipTests
{
    private const int Rate = DiscagemAudio.SampleRate;

    private static float[] Tone(double seconds, float amplitude, double hz = 440) =>
        Enumerable.Range(0, (int)(seconds * Rate)).Select(i => (float)(Math.Sin(2 * Math.PI * hz * i / Rate) * amplitude)).ToArray();

    [Fact]
    public void Prepare_CutsSilenceAndLimitsTheLength()
    {
        var samples = new float[Rate / 2].Concat(Tone(8, 0.5f)).Concat(new float[Rate]).ToArray();

        var clip = SoundClips.Prepare(samples, maxMilliseconds: 5000);

        Assert.Equal(5 * Rate, clip.Length);
        Assert.True(Math.Abs(clip[1]) > 0.004f, "the leading silence was not cut");
        // Cut in the middle of the tone, so it fades out instead of clicking.
        Assert.Equal(0f, clip[^1], 3);
    }

    [Fact]
    public void Prepare_KeepsShortClipsWholeAndLowersLoudOnes()
    {
        var quiet = SoundClips.Prepare(Tone(1, 0.3f), maxMilliseconds: 5000);
        Assert.InRange(quiet.Length, Rate - 100, Rate);
        Assert.InRange(quiet.Max(), 0.29f, 0.3f);

        var loud = SoundClips.Prepare(Tone(1, 1.5f), maxMilliseconds: 5000);
        Assert.InRange(loud.Max(Math.Abs), 0.85f, 0.9f);

        Assert.Empty(SoundClips.Prepare(new float[Rate], maxMilliseconds: 5000));
    }

    [Fact]
    public void EncodedClips_ReadBackAsTheSameSound()
    {
        var clip = Tone(0.25, 0.5f);
        var wav = SoundClips.EncodeWav(clip);

        Assert.Equal(44 + clip.Length * 2, wav.Length);
        var decoded = SoundClips.Decode(new MemoryStream(wav), "clip.WAV");
        Assert.Equal(clip.Length, decoded.Length);
        Assert.All(clip.Zip(decoded), pair => Assert.Equal(pair.First, pair.Second, 0.001f));

        // Long files are only decoded as far as needed.
        var limited = SoundClips.Decode(new MemoryStream(SoundClips.EncodeWav(Tone(3, 0.5f))), "long.wav", maxSeconds: 1);
        Assert.Equal(Rate, limited.Length);
    }

    [Theory]
    [InlineData("sound.flac")]
    [InlineData("sound.mp3")]
    [InlineData("sound.ogg")]
    public void Decode_RefusesFilesItCannotRead(string fileName)
    {
        var garbage = new MemoryStream("This is not a sound file at all, just some text."u8.ToArray());
        Assert.Throws<InvalidDataException>(() => SoundClips.Decode(garbage, fileName));
    }

    [Fact]
    public void Mixer_PlaysSoundsWithTheVoicesAndSilencesThemWhenDeafened()
    {
        using var mixer = new VoiceMixer(() => 0);
        var sound = Enumerable.Repeat(0.25f, DiscagemAudio.FrameSamples * 3).ToArray();
        var output = new float[DiscagemAudio.FrameSamples];

        mixer.Volume = 2f;
        mixer.PlaySound(sound, gain: 0.5f);
        mixer.Read(output);
        Assert.All(output, s => Assert.Equal(0.25f, s, 4));

        // Deafened: not heard, but the sound keeps its pace.
        mixer.MuteVoices = true;
        mixer.Read(output);
        Assert.All(output, s => Assert.Equal(0f, s));

        mixer.MuteVoices = false;
        mixer.Read(output);
        Assert.All(output, s => Assert.Equal(0.25f, s, 4));
        mixer.Read(output);
        Assert.All(output, s => Assert.Equal(0f, s));

        mixer.PlaySound(sound, gain: 1f);
        mixer.StopSounds();
        mixer.Read(output);
        Assert.All(output, s => Assert.Equal(0f, s));
    }
}
