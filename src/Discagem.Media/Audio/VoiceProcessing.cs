namespace Discagem.Media.Audio;

/// <summary>
/// Level-based voice activity detection ("input sensitivity"): the microphone is transmitted while
/// its level is above a threshold, plus a short hangover so that word endings are not cut off.
/// </summary>
public sealed class VoiceActivityDetector
{
    public const float SilenceDb = -100f;

    private int _hangover;

    /// <summary>Level above which the input counts as speech, in dBFS.</summary>
    public float ThresholdDb { get; set; } = -50f;

    /// <summary>Frames to keep transmitting after the level drops (15 x 20 ms = 300 ms).</summary>
    public int HangoverFrames { get; set; } = 15;

    /// <summary>RMS level of the last frame, in dBFS.</summary>
    public float LevelDb { get; private set; } = SilenceDb;

    public bool IsActive { get; private set; }

    public bool Process(ReadOnlySpan<float> frame)
    {
        LevelDb = RmsDb(frame);
        if (LevelDb >= ThresholdDb)
        {
            _hangover = HangoverFrames;
            IsActive = true;
        }
        else if (_hangover > 0)
        {
            _hangover--;
            IsActive = true;
        }
        else
        {
            IsActive = false;
        }

        return IsActive;
    }

    public void Reset()
    {
        _hangover = 0;
        IsActive = false;
        LevelDb = SilenceDb;
    }

    public static float RmsDb(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return SilenceDb;
        }

        double sum = 0;
        foreach (var s in samples)
        {
            sum += s * s;
        }

        var rms = Math.Sqrt(sum / samples.Length);
        return rms < 1e-5 ? SilenceDb : (float)(20 * Math.Log10(rms));
    }
}

/// <summary>First-order DC blocker / high-pass filter (cut-off around 40 Hz at 48 kHz) for microphone input.</summary>
public sealed class HighPassFilter
{
    private const float Pole = 0.995f;
    private float _previousInput;
    private float _previousOutput;

    public void Process(Span<float> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var x = samples[i];
            var y = x - _previousInput + Pole * _previousOutput;
            _previousInput = x;
            _previousOutput = y;
            samples[i] = y;
        }
    }
}

/// <summary>
/// Streaming linear-interpolation sample rate converter for mono audio. Linear interpolation is
/// adequate for speech; most devices run at 48 kHz anyway, in which case samples pass through.
/// </summary>
public sealed class LinearResampler
{
    private readonly double _step;
    private double _position = -1;
    private float _last;

    public LinearResampler(int inputRate, int outputRate)
    {
        if (inputRate <= 0 || outputRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputRate), "Sample rates must be positive.");
        }

        InputRate = inputRate;
        OutputRate = outputRate;
        _step = (double)inputRate / outputRate;
    }

    public int InputRate { get; }

    public int OutputRate { get; }

    public bool IsPassThrough => InputRate == OutputRate;

    /// <summary>Upper bound of the samples produced from <paramref name="inputCount"/> input samples.</summary>
    public int MaxOutputFor(int inputCount) => (int)Math.Ceiling((inputCount + 1) / _step) + 2;

    /// <summary>
    /// Converts a block of input. <paramref name="output"/> must hold at least
    /// <see cref="MaxOutputFor"/> samples. Returns the number of samples written.
    /// </summary>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.IsEmpty)
        {
            return 0;
        }

        if (output.Length < MaxOutputFor(input.Length))
        {
            throw new ArgumentException("The output buffer is too small.", nameof(output));
        }

        if (IsPassThrough)
        {
            input.CopyTo(output);
            return input.Length;
        }

        // Position -1 refers to the last sample of the previous block.
        var written = 0;
        while (true)
        {
            var index = (int)Math.Floor(_position);
            if (index + 1 >= input.Length)
            {
                break;
            }

            var fraction = (float)(_position - index);
            var a = index < 0 ? _last : input[index];
            var b = input[index + 1];
            output[written++] = a + (b - a) * fraction;
            _position += _step;
        }

        _position -= input.Length;
        _last = input[^1];
        return written;
    }
}

/// <summary>Short synthesised notification sounds (someone joined or left the voice channel).</summary>
public static class VoiceCues
{
    public static float[] Join { get; } = Chime([523.25, 783.99]);

    public static float[] Leave { get; } = Chime([783.99, 523.25]);

    public static float[] Mute { get; } = Chime([440.0], noteSeconds: 0.06);

    public static float[] Unmute { get; } = Chime([659.25], noteSeconds: 0.06);

    private static float[] Chime(double[] notes, double noteSeconds = 0.09, float volume = 0.18f)
    {
        var rate = Discagem.Media.WebRtc.DiscagemAudio.SampleRate;
        var noteSamples = (int)(rate * noteSeconds);
        var result = new float[noteSamples * notes.Length];
        for (var n = 0; n < notes.Length; n++)
        {
            for (var i = 0; i < noteSamples; i++)
            {
                var t = (double)i / rate;
                // Quick attack, exponential decay: a soft "blip" rather than a beep.
                var envelope = Math.Min(1.0, i / (rate * 0.004)) * Math.Exp(-t * 28);
                result[n * noteSamples + i] = (float)(Math.Sin(2 * Math.PI * notes[n] * t) * envelope * volume);
            }
        }

        return result;
    }
}
