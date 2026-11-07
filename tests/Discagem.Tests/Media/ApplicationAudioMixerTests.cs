using Discagem.Media.Audio;

namespace Discagem.Tests.Media;

public sealed class ApplicationAudioMixerTests
{
    private const int Block = ApplicationAudioMixer.BlockSamples;

    /// <summary>One 10 ms block of a constant level on the left, the right, or both channels.</summary>
    private static float[] Constant(float left, float right)
    {
        var block = new float[Block];
        for (var i = 0; i < block.Length; i += 2)
        {
            block[i] = left;
            block[i + 1] = right;
        }

        return block;
    }

    /// <summary>Writes the same block to each input, then mixes; repeated so that buffering has settled.</summary>
    private static float[] MixSteady(ApplicationAudioMixer mixer, params (long Id, float[] Samples)[] inputs)
    {
        var output = new float[Block];
        for (var round = 0; round < 6; round++)
        {
            foreach (var (id, samples) in inputs)
            {
                mixer.Write(id, samples);
            }

            mixer.MixBlock(output);
        }

        return output;
    }

    [Fact]
    public void Applications_AreMixedAtTheirOwnVolumes()
    {
        using var mixer = new ApplicationAudioMixer(_ => { }, key => key == "game" ? 0.5f : 1f);
        mixer.AddInput(1, "game", "Game");
        mixer.AddInput(2, "music", "Music");

        var output = MixSteady(mixer, (1, Constant(0.4f, 0)), (2, Constant(0, 0.1f)));

        Assert.Equal(0.2f, output[0], 4);
        Assert.Equal(0.1f, output[1], 4);
        Assert.Equal(0.2f, output[^2], 4);
        Assert.Equal(0.1f, output[^1], 4);
    }

    [Fact]
    public void ApplicationAtZero_IsLeftOutCompletely()
    {
        using var mixer = new ApplicationAudioMixer(_ => { }, key => key == "sonar" ? 0f : 1f);
        mixer.AddInput(1, "sonar", "SteelSeries Sonar");
        mixer.AddInput(2, "game", "Game");

        var output = MixSteady(mixer, (1, Constant(0.5f, 0.5f)), (2, Constant(0.1f, 0.1f)));

        Assert.All(output, sample => Assert.Equal(0.1f, sample, 5));
    }

    [Fact]
    public void ChangingVolume_RampsOverOneBlock()
    {
        using var mixer = new ApplicationAudioMixer(_ => { });
        mixer.AddInput(1, "game", "Game");
        MixSteady(mixer, (1, Constant(0.5f, 0.5f)));

        mixer.SetVolume("game", 0);
        mixer.Write(1, Constant(0.5f, 0.5f));
        var ramp = new float[Block];
        mixer.MixBlock(ramp);

        // From 100 % at the start of the block down to 0 at its end, without a jump.
        Assert.InRange(ramp[0], 0.49f, 0.5f);
        Assert.Equal(0f, ramp[^1], 5);
        for (var i = 2; i < ramp.Length; i += 2)
        {
            Assert.True(ramp[i] <= ramp[i - 2]);
            Assert.True(ramp[i - 2] - ramp[i] < 0.01f);
        }

        mixer.Write(1, Constant(0.5f, 0.5f));
        var after = new float[Block];
        mixer.MixBlock(after);
        Assert.All(after, sample => Assert.Equal(0f, sample));
        Assert.Equal(0f, Assert.Single(mixer.Applications).Volume);
    }

    [Fact]
    public void LoudApplications_DoNotGoPastFullScale()
    {
        using var mixer = new ApplicationAudioMixer(_ => { });
        mixer.AddInput(1, "a", "A");
        mixer.AddInput(2, "b", "B");

        var output = MixSteady(mixer, (1, Constant(0.8f, -0.8f)), (2, Constant(0.8f, -0.8f)));

        Assert.Equal(1f, output[0]);
        Assert.Equal(-1f, output[1]);
    }

    [Fact]
    public void InputsOfOneApplication_ShareOneEntryAndVolume()
    {
        var changes = 0;
        var asked = new List<string>();
        using var mixer = new ApplicationAudioMixer(_ => { }, key =>
        {
            asked.Add(key);
            return 0.25f;
        });
        mixer.ApplicationsChanged += () => changes++;

        mixer.AddInput(1, "firefox", "Firefox");
        mixer.AddInput(2, "firefox", "Firefox");
        mixer.AddInput(3, "spotify", "Spotify");

        Assert.Equal(["firefox", "spotify"], mixer.Applications.Select(a => a.Key));
        Assert.All(mixer.Applications, a => Assert.Equal(0.25f, a.Volume));
        Assert.Equal(["firefox", "spotify"], asked);
        Assert.Equal(2, changes);

        mixer.SetVolume("firefox", 1);
        var output = MixSteady(mixer, (1, Constant(0.1f, 0)), (2, Constant(0, 0.2f)));
        Assert.Equal(0.1f, output[0], 4);
        Assert.Equal(0.2f, output[1], 4);

        mixer.RemoveInput(1);
        Assert.Equal(2, mixer.Applications.Count);
        mixer.RemoveInput(2);
        Assert.Equal(["spotify"], mixer.Applications.Select(a => a.Key));
        Assert.Equal(3, changes);

        // Coming back keeps the volume it had, without asking for the initial one again.
        mixer.AddInput(4, "firefox", "Firefox");
        Assert.Equal(1f, mixer.Applications.Single(a => a.Key == "firefox").Volume);
        Assert.Equal(["firefox", "spotify"], asked);
    }

    [Fact]
    public void IsPlaying_FollowsWhatTheApplicationPlays()
    {
        using var mixer = new ApplicationAudioMixer(_ => { });
        mixer.AddInput(1, "quiet", "Quiet");
        mixer.AddInput(2, "loud", "Loud");

        MixSteady(mixer, (1, Constant(0, 0)), (2, Constant(0.3f, 0.3f)));

        var applications = mixer.Applications.ToDictionary(a => a.Key);
        Assert.False(applications["quiet"].IsPlaying);
        Assert.True(applications["loud"].IsPlaying);
    }

    [Fact]
    public void Input_StartsOnceTwentyMillisecondsAreQueued()
    {
        using var mixer = new ApplicationAudioMixer(_ => { });
        mixer.AddInput(1, "game", "Game");
        var output = new float[Block];

        mixer.Write(1, Constant(0.5f, 0.5f));
        mixer.MixBlock(output);
        Assert.All(output, sample => Assert.Equal(0f, sample));

        mixer.Write(1, Constant(0.5f, 0.5f));
        mixer.MixBlock(output);
        Assert.All(output, sample => Assert.Equal(0.5f, sample, 5));
    }

    [Fact]
    public void Start_HandsBlocksToTheSinkInRealTime()
    {
        var blocks = 0;
        using var mixer = new ApplicationAudioMixer(block =>
        {
            Assert.Equal(Block, block.Length);
            Interlocked.Increment(ref blocks);
        });
        mixer.Start();
        Thread.Sleep(300);
        mixer.Dispose();

        Assert.InRange(Volatile.Read(ref blocks), 15, 45);
    }
}
