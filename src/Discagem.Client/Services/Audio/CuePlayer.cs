namespace Discagem.Client.Services.Audio;

/// <summary>
/// Plays short notification sounds (a message arrived) while no call is open, on the voice output
/// device. The device is opened on demand and closed again shortly after the last sound ends.
/// </summary>
internal sealed class CuePlayer : IDisposable
{
    private const int MaxPlaying = 4;
    private const int SampleRate = Discagem.Media.WebRtc.DiscagemAudio.SampleRate;
    private static readonly TimeSpan CloseDelay = TimeSpan.FromSeconds(1.5);

    private readonly object _sync = new();
    private readonly IAudioBackend _backend;
    private readonly Func<string> _device;
    private readonly Func<float> _volume;
    private readonly List<Cue> _playing = [];
    private readonly Timer _closeTimer;
    private IAudioStream? _output;
    private bool _opening;
    private bool _disposed;

    public CuePlayer(IAudioBackend backend, Func<string> device, Func<float> volume)
    {
        _backend = backend;
        _device = device;
        _volume = volume;
        _closeTimer = new Timer(_ => CloseIfIdle());
    }

    public void Play(float[] samples)
    {
        lock (_sync)
        {
            if (_disposed || samples.Length == 0 || _playing.Count >= MaxPlaying)
            {
                return;
            }

            _playing.Add(new Cue(samples));
            _closeTimer.Change(TimeSpan.FromSeconds((double)samples.Length / SampleRate) + CloseDelay, Timeout.InfiniteTimeSpan);
            if (_output is not null || _opening)
            {
                return;
            }

            _opening = true;
        }

        // Opening a device can take a moment (PulseAudio connects to its server): not on the UI thread.
        _ = Task.Run(Open);
    }

    private void Open()
    {
        IAudioStream? output = null;
        try
        {
            output = _backend.OpenOutput(_device(), Read);
            output.Failed += _ => Task.Run(Close);
        }
        catch (AudioDeviceException ex)
        {
            ClientLog.Error($"Could not play a notification sound: {ex.Message}");
        }

        lock (_sync)
        {
            _opening = false;
            if (output is null)
            {
                _playing.Clear();
                return;
            }

            if (!_disposed)
            {
                _output = output;
                return;
            }
        }

        output.Dispose();
    }

    private void Read(Span<float> output)
    {
        output.Clear();
        lock (_sync)
        {
            var volume = Math.Clamp(_volume(), 0f, 2f);
            for (var i = _playing.Count - 1; i >= 0; i--)
            {
                var cue = _playing[i];
                var count = Math.Min(output.Length, cue.Samples.Length - cue.Position);
                for (var s = 0; s < count; s++)
                {
                    output[s] += cue.Samples[cue.Position + s] * volume;
                }

                cue.Position += count;
                if (cue.Position >= cue.Samples.Length)
                {
                    _playing.RemoveAt(i);
                }
            }
        }
    }

    private void CloseIfIdle()
    {
        lock (_sync)
        {
            if (_playing.Count > 0)
            {
                _closeTimer.Change(CloseDelay, Timeout.InfiniteTimeSpan);
                return;
            }
        }

        Close();
    }

    private void Close()
    {
        IAudioStream? output;
        lock (_sync)
        {
            output = _output;
            _output = null;
            _playing.Clear();
        }

        output?.Dispose();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
        }

        _closeTimer.Dispose();
        Close();
    }

    private sealed class Cue(float[] samples)
    {
        public float[] Samples { get; } = samples;

        public int Position { get; set; }
    }
}
