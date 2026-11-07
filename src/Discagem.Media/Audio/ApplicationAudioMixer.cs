using System.Diagnostics;
using Discagem.Media.WebRtc;

namespace Discagem.Media.Audio;

/// <summary>An application whose sound goes into a screen share, as shown in the streamer's mixer.</summary>
/// <param name="Key">Stable identifier (the executable's name, in lower case), under which its volume is remembered.</param>
/// <param name="Name">Name to show.</param>
/// <param name="Volume">Gain applied to it: 0 (left out) to 1 (as played).</param>
/// <param name="IsPlaying">Whether it played anything audible in the last second.</param>
public sealed record StreamApplication(string Key, string Name, float Volume, bool IsPlaying);

/// <summary>
/// Mixes the sound of several applications into the 48 kHz stereo audio of a screen share, each at
/// its own volume. Captures add one input per playback stream (an application can have several) and
/// write interleaved stereo into it as it arrives; every 10 ms the mixer adds up what each input has
/// queued and hands the block on.
/// </summary>
/// <remarks>Thread-safe: inputs are written from capture threads, volumes are set from the UI thread.</remarks>
public sealed class ApplicationAudioMixer : IDisposable
{
    public const int Channels = 2;
    public const int BlockFrames = DiscagemAudio.SampleRate / 100;
    public const int BlockSamples = BlockFrames * Channels;

    /// <summary>Peak level above which an application counts as playing (about -50 dBFS).</summary>
    private const float PlayingThreshold = 0.003f;

    private static readonly long PlayingHoldTicks = Stopwatch.Frequency;

    private readonly AudioSink _sink;
    private readonly Func<string, float> _initialVolume;
    private readonly object _sync = new();
    private readonly Dictionary<long, Input> _inputs = new();
    private readonly Dictionary<string, Application> _applications = new(StringComparer.Ordinal);
    private readonly float[] _scratch = new float[BlockSamples];
    private Thread? _thread;
    private volatile bool _stopped;

    /// <param name="sink">Receives each mixed 10 ms block (on the mixer thread).</param>
    /// <param name="initialVolume">Volume of an application the first time it shows up, from its key.</param>
    public ApplicationAudioMixer(AudioSink sink, Func<string, float>? initialVolume = null)
    {
        _sink = sink;
        _initialVolume = initialVolume ?? (_ => 1f);
    }

    /// <summary>Raised (on any thread) when an application appears or disappears.</summary>
    public event Action? ApplicationsChanged;

    /// <summary>The applications with at least one input, in the order they appeared.</summary>
    public IReadOnlyList<StreamApplication> Applications
    {
        get
        {
            var now = Stopwatch.GetTimestamp();
            lock (_sync)
            {
                return _applications.Values
                    .Where(a => a.Inputs > 0)
                    .OrderBy(a => a.Order)
                    .Select(a => new StreamApplication(a.Key, a.Name, a.TargetGain, now - a.LastHeard < PlayingHoldTicks))
                    .ToList();
            }
        }
    }

    /// <summary>Starts mixing in real time on a thread of its own.</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(MixLoop) { IsBackground = true, Name = "Discagem stream audio mixer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Adds a playback stream of an application. Ids are the capture's own; reusing one replaces the input.</summary>
    public void AddInput(long id, string key, string name)
    {
        bool changed;
        lock (_sync)
        {
            RemoveInputLocked(id);
            if (!_applications.TryGetValue(key, out var application))
            {
                var volume = Math.Clamp(_initialVolume(key), 0f, 1f);
                application = new Application(key, name, _applications.Count, volume);
                _applications[key] = application;
            }

            changed = application.Inputs == 0;
            application.Inputs++;
            _inputs[id] = new Input(application);
        }

        if (changed)
        {
            ApplicationsChanged?.Invoke();
        }
    }

    public void RemoveInput(long id)
    {
        bool changed;
        lock (_sync)
        {
            changed = RemoveInputLocked(id);
        }

        if (changed)
        {
            ApplicationsChanged?.Invoke();
        }
    }

    /// <summary>Whether an input exists for this id.</summary>
    public bool HasInput(long id)
    {
        lock (_sync)
        {
            return _inputs.ContainsKey(id);
        }
    }

    /// <summary>Queues interleaved stereo samples that an input played.</summary>
    public void Write(long id, ReadOnlySpan<float> samples)
    {
        lock (_sync)
        {
            if (_inputs.TryGetValue(id, out var input))
            {
                input.Buffer.Write(samples);
            }
        }
    }

    /// <summary>Sets an application's volume (0 to 1). It changes smoothly over the next 10 ms block.</summary>
    public void SetVolume(string key, float volume)
    {
        lock (_sync)
        {
            if (_applications.TryGetValue(key, out var application))
            {
                application.TargetGain = Math.Clamp(volume, 0f, 1f);
            }
        }
    }

    /// <summary>Mixes one 10 ms block (<see cref="BlockSamples"/> interleaved samples) from what the inputs have queued.</summary>
    public void MixBlock(Span<float> block)
    {
        block.Clear();
        var now = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            foreach (var input in _inputs.Values)
            {
                var application = input.Application;
                var scratch = _scratch.AsSpan(0, block.Length);
                scratch.Clear();
                if (!input.Buffer.Read(scratch))
                {
                    continue;
                }

                var peak = 0f;
                foreach (var sample in scratch)
                {
                    peak = Math.Max(peak, Math.Abs(sample));
                }

                if (peak > PlayingThreshold)
                {
                    application.LastHeard = now;
                }

                // Ramp from the gain used for the previous block, so that moving a slider does not click.
                var from = application.Gain;
                var to = application.TargetGain;
                if (from == 0 && to == 0)
                {
                    continue;
                }

                var frames = block.Length / Channels;
                for (var frame = 0; frame < frames; frame++)
                {
                    var gain = from + (to - from) * (frame + 1) / frames;
                    var i = frame * Channels;
                    block[i] += scratch[i] * gain;
                    block[i + 1] += scratch[i + 1] * gain;
                }
            }

            foreach (var application in _applications.Values)
            {
                application.Gain = application.TargetGain;
            }
        }

        // Several loud applications can add up past full scale; keep the sum within range.
        for (var i = 0; i < block.Length; i++)
        {
            block[i] = Math.Clamp(block[i], -1f, 1f);
        }
    }

    private bool RemoveInputLocked(long id)
    {
        if (!_inputs.Remove(id, out var input))
        {
            return false;
        }

        input.Application.Inputs--;
        return input.Application.Inputs == 0;
    }

    /// <summary>Every 10 ms, mixes a block and hands it on.</summary>
    private void MixLoop()
    {
        var block = new float[BlockSamples];
        var clock = Stopwatch.StartNew();
        var next = 0.0;
        while (!_stopped)
        {
            var wait = next - clock.Elapsed.TotalMilliseconds;
            if (wait > 1)
            {
                Thread.Sleep((int)wait);
                continue;
            }

            // After a stall, catch up without bursting more than a few blocks.
            next = Math.Max(next + 10, clock.Elapsed.TotalMilliseconds - 30);
            MixBlock(block);
            _sink(block);
        }
    }

    public void Dispose()
    {
        _stopped = true;
        if (_thread is { } thread && Thread.CurrentThread != thread)
        {
            thread.Join(TimeSpan.FromSeconds(1));
        }
    }

    private sealed class Application(string key, string name, int order, float volume)
    {
        public string Key { get; } = key;

        public string Name { get; } = name;

        public int Order { get; } = order;

        public int Inputs { get; set; }

        public float TargetGain { get; set; } = volume;

        public float Gain { get; set; } = volume;

        public long LastHeard { get; set; } = long.MinValue / 2;
    }

    private sealed class Input(Application application)
    {
        public Application Application { get; } = application;

        public StereoJitterRing Buffer { get; } = new();
    }
}

/// <summary>
/// Interleaved stereo samples from one playback stream. Reading starts once 20 ms are queued, so
/// that the timing of capture callbacks does not cause gaps, and the queue is trimmed if it grows
/// past 200 ms.
/// </summary>
internal sealed class StereoJitterRing
{
    private const int Channels = ApplicationAudioMixer.Channels;
    private const int StartSamples = ApplicationAudioMixer.BlockSamples * 2;
    private const int MaxSamples = ApplicationAudioMixer.BlockSamples * 20;
    private const int TrimToSamples = ApplicationAudioMixer.BlockSamples * 4;

    private readonly float[] _buffer = new float[DiscagemAudio.SampleRate * Channels];
    private int _start;
    private int _count;
    private bool _playing;

    public int Count => _count;

    public void Write(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            if (_count == _buffer.Length)
            {
                _start = (_start + 1) % _buffer.Length;
                _count--;
            }

            _buffer[(_start + _count) % _buffer.Length] = sample;
            _count++;
        }
    }

    /// <summary>Copies up to <paramref name="block"/>'s length of queued samples into it; false when nothing was read.</summary>
    public bool Read(Span<float> block)
    {
        if (!_playing && _count < StartSamples)
        {
            return false;
        }

        _playing = true;
        if (_count > MaxSamples)
        {
            var drop = _count - TrimToSamples;
            drop -= drop % Channels;
            _start = (_start + drop) % _buffer.Length;
            _count -= drop;
        }

        var available = Math.Min(_count - _count % Channels, block.Length);
        for (var i = 0; i < available; i++)
        {
            block[i] = _buffer[(_start + i) % _buffer.Length];
        }

        _start = (_start + available) % _buffer.Length;
        _count -= available;
        if (_count == 0)
        {
            // Ran dry: the application paused or stopped. Buffer afresh when it resumes.
            _playing = false;
        }

        return available > 0;
    }
}
