using Discagem.Media.WebRtc;

namespace Discagem.Tests.Media;

/// <summary>Test signals and a Goertzel detector to tell which tones are present in an audio buffer.</summary>
internal static class AudioTestSignals
{
    public const int Rate = DiscagemAudio.SampleRate;

    /// <summary>Continuous sine generator (keeps phase between calls).</summary>
    public sealed class Tone(double frequency, float amplitude = 0.3f, int rate = Rate)
    {
        private long _sample;

        public void Fill(Span<float> buffer)
        {
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = (float)(Math.Sin(2 * Math.PI * frequency * _sample++ / rate) * amplitude);
            }
        }

        public float[] Next(int count)
        {
            var buffer = new float[count];
            Fill(buffer);
            return buffer;
        }
    }

    /// <summary>Power of a single frequency (Goertzel algorithm), normalised by the block length.</summary>
    public static double Power(ReadOnlySpan<float> samples, double frequency, int rate = Rate)
    {
        var coefficient = 2 * Math.Cos(2 * Math.PI * frequency / rate);
        double s1 = 0, s2 = 0;
        foreach (var x in samples)
        {
            var s0 = x + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        var power = s1 * s1 + s2 * s2 - coefficient * s1 * s2;
        return power / samples.Length / samples.Length;
    }

    /// <summary>Approximate amplitude of a tone at <paramref name="frequency"/> in the block.</summary>
    public static double Amplitude(ReadOnlySpan<float> samples, double frequency, int rate = Rate) =>
        2 * Math.Sqrt(Power(samples, frequency, rate));

    public static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var s in samples)
        {
            sum += s * s;
        }

        return samples.IsEmpty ? 0 : Math.Sqrt(sum / samples.Length);
    }
}
