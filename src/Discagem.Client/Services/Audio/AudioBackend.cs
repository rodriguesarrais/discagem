using System.Diagnostics;
using Discagem.Media.Audio;
using Discagem.Media.WebRtc;

namespace Discagem.Client.Services.Audio;

/// <summary>A microphone or speaker device. An empty id means "the system default".</summary>
public sealed record AudioDeviceInfo(string Id, string Name)
{
    public static AudioDeviceInfo Default { get; } = new("", "System default");

    public override string ToString() => Name;
}

public sealed class AudioDeviceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>An open capture or playback stream.</summary>
public interface IAudioStream : IDisposable
{
    string DeviceName { get; }

    /// <summary>Raised (on an audio thread) when the device stops working, for example because it was unplugged.</summary>
    event Action<string>? Failed;
}

/// <summary>The platform's audio system. Streams exchange 48 kHz mono float samples.</summary>
public interface IAudioBackend
{
    string Name { get; }

    IReadOnlyList<AudioDeviceInfo> InputDevices();

    IReadOnlyList<AudioDeviceInfo> OutputDevices();

    IAudioStream OpenInput(string? deviceId, AudioSink sink);

    IAudioStream OpenOutput(string? deviceId, AudioSource source);
}

public static class AudioBackends
{
    /// <summary>
    /// Picks the audio system for this platform: WASAPI on Windows, PulseAudio (or PipeWire's
    /// PulseAudio service) on Linux.
    /// </summary>
    public static IAudioBackend Create()
    {
        IAudioBackend backend = OperatingSystem.IsWindows()
            ? new WasapiAudioBackend()
            : PulseAudioBackend.IsAvailable
                ? new PulseAudioBackend()
                : new UnavailableAudioBackend();

        return SyntheticAudioBackend.FromEnvironment(backend);
    }
}

/// <summary>No usable audio system: voice works without a microphone or speakers.</summary>
internal sealed class UnavailableAudioBackend : IAudioBackend
{
    public string Name => "None";

    public IReadOnlyList<AudioDeviceInfo> InputDevices() => [AudioDeviceInfo.Default];

    public IReadOnlyList<AudioDeviceInfo> OutputDevices() => [AudioDeviceInfo.Default];

    public IAudioStream OpenInput(string? deviceId, AudioSink sink) =>
        throw new AudioDeviceException("No audio system was found (PulseAudio or PipeWire is required on Linux).");

    public IAudioStream OpenOutput(string? deviceId, AudioSource source) =>
        throw new AudioDeviceException("No audio system was found (PulseAudio or PipeWire is required on Linux).");
}

/// <summary>
/// Diagnostics: <c>DISCAGEM_TEST_TONE=440</c> replaces the microphone with a tone and
/// <c>DISCAGEM_TEST_RECORD=/path/file.f32</c> records what would be played (raw 48 kHz mono float)
/// instead of using the speakers. Used to verify voice end to end on machines without audio hardware.
/// </summary>
internal sealed class SyntheticAudioBackend(IAudioBackend inner, double? toneHz, string? recordPath) : IAudioBackend
{
    private long _phase;

    public static IAudioBackend FromEnvironment(IAudioBackend inner)
    {
        var tone = double.TryParse(Environment.GetEnvironmentVariable("DISCAGEM_TEST_TONE"), System.Globalization.CultureInfo.InvariantCulture, out var hz) && hz > 0
            ? hz
            : (double?)null;
        var record = Environment.GetEnvironmentVariable("DISCAGEM_TEST_RECORD");
        return tone is null && string.IsNullOrEmpty(record) ? inner : new SyntheticAudioBackend(inner, tone, record);
    }

    public string Name => $"{inner.Name} (synthetic)";

    public IReadOnlyList<AudioDeviceInfo> InputDevices() => toneHz is null ? inner.InputDevices() : [AudioDeviceInfo.Default];

    public IReadOnlyList<AudioDeviceInfo> OutputDevices() => string.IsNullOrEmpty(recordPath) ? inner.OutputDevices() : [AudioDeviceInfo.Default];

    public IAudioStream OpenInput(string? deviceId, AudioSink sink) =>
        toneHz is { } hz ? new ClockedStream($"{hz} Hz tone", buffer => Tone(buffer, hz), null, sink) : inner.OpenInput(deviceId, sink);

    public IAudioStream OpenOutput(string? deviceId, AudioSource source)
    {
        if (string.IsNullOrEmpty(recordPath))
        {
            return inner.OpenOutput(deviceId, source);
        }

        var file = new FileStream(recordPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        return new ClockedStream("Recorder", source, file, null);
    }

    private void Tone(Span<float> buffer, double hz)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (float)(Math.Sin(2 * Math.PI * hz * _phase++ / DiscagemAudio.SampleRate) * 0.3);
        }
    }

    /// <summary>Moves 10 ms blocks in real time, like a sound card would.</summary>
    private sealed class ClockedStream : IAudioStream
    {
        private readonly Thread _thread;
        private volatile bool _stopped;

        public ClockedStream(string name, AudioSource source, Stream? record, AudioSink? sink)
        {
            DeviceName = name;
            _thread = new Thread(() =>
            {
                var buffer = new float[DiscagemAudio.SampleRate / 100];
                var bytes = new byte[buffer.Length * sizeof(float)];
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

                    next += 10;
                    source(buffer);
                    sink?.Invoke(buffer);
                    if (record is not null)
                    {
                        System.Runtime.InteropServices.MemoryMarshal.AsBytes(buffer.AsSpan()).CopyTo(bytes);
                        record.Write(bytes);
                    }
                }

                record?.Dispose();
            })
            { IsBackground = true, Name = $"Discagem synthetic audio ({name})" };
            _thread.Start();
        }

        public string DeviceName { get; }

        public event Action<string>? Failed
        {
            add { }
            remove { }
        }

        public void Dispose()
        {
            _stopped = true;
            if (Thread.CurrentThread != _thread)
            {
                _thread.Join(TimeSpan.FromSeconds(1));
            }
        }
    }
}
