using System.Diagnostics;
using Discagem.Client.Resources;
using Discagem.Media.Audio;
using Discagem.Media.WebRtc;

namespace Discagem.Client.Services.Audio;

/// <summary>A microphone or speaker device. An empty id means "the system default".</summary>
public sealed record AudioDeviceInfo(string Id, string Name)
{
    public static AudioDeviceInfo Default { get; } = new("", Strings.Audio_SystemDefault);

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

/// <summary>
/// The platform's audio system. Streams exchange 48 kHz float samples: mono for voice, interleaved
/// stereo for the audio of screen shares.
/// </summary>
public interface IAudioBackend
{
    string Name { get; }

    IReadOnlyList<AudioDeviceInfo> InputDevices();

    IReadOnlyList<AudioDeviceInfo> OutputDevices();

    IAudioStream OpenInput(string? deviceId, AudioSink sink);

    /// <summary>Opens a playback stream pulling mono (<c>channels: 1</c>) or interleaved stereo (<c>channels: 2</c>) samples.</summary>
    IAudioStream OpenOutput(string? deviceId, AudioSource source, int channels = 1);

    /// <summary>Whether <see cref="OpenSystemCapture"/> is available.</summary>
    bool SupportsSystemCapture { get; }

    /// <summary>Whether system capture can be limited to one application and leave Discagem's own sounds out.</summary>
    bool SupportsApplicationCapture { get; }

    /// <summary>
    /// Captures what the computer plays, as interleaved stereo, to send along with a screen share:
    /// only the given process and its children when <paramref name="processId"/> is set, otherwise
    /// everything except Discagem itself (where <see cref="SupportsApplicationCapture"/>; elsewhere
    /// everything the default output plays).
    /// </summary>
    IAudioStream OpenSystemCapture(int? processId, AudioSink sink);
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
    public string Name => Strings.Audio_NoBackend;

    public IReadOnlyList<AudioDeviceInfo> InputDevices() => [AudioDeviceInfo.Default];

    public IReadOnlyList<AudioDeviceInfo> OutputDevices() => [AudioDeviceInfo.Default];

    public IAudioStream OpenInput(string? deviceId, AudioSink sink) =>
        throw new AudioDeviceException(Strings.Audio_NoAudioSystem);

    public IAudioStream OpenOutput(string? deviceId, AudioSource source, int channels = 1) =>
        throw new AudioDeviceException(Strings.Audio_NoAudioSystem);

    public bool SupportsSystemCapture => false;

    public bool SupportsApplicationCapture => false;

    public IAudioStream OpenSystemCapture(int? processId, AudioSink sink) =>
        throw new AudioDeviceException(Strings.Audio_NoAudioSystem);
}

/// <summary>
/// Diagnostics, used to verify voice and stream audio end to end on machines without audio hardware:
/// <list type="bullet">
/// <item><c>DISCAGEM_TEST_TONE=440</c> replaces the microphone with a tone.</item>
/// <item><c>DISCAGEM_TEST_RECORD=/path/file.f32</c> records what voice playback would play (raw 48 kHz mono float).</item>
/// <item><c>DISCAGEM_TEST_STREAM_TONE=440,1000</c> replaces the audio captured for screen shares with a tone on each channel.</item>
/// <item><c>DISCAGEM_TEST_STREAM_RECORD=/path/file.f32</c> records the audio of watched streams (raw 48 kHz stereo float).</item>
/// </list>
/// </summary>
internal sealed class SyntheticAudioBackend(
    IAudioBackend inner,
    double? toneHz,
    string? recordPath,
    (double Left, double Right)? streamTones,
    string? streamRecordPath) : IAudioBackend
{
    public static IAudioBackend FromEnvironment(IAudioBackend inner)
    {
        static double? Frequency(string? text) =>
            double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var hz) && hz > 0 ? hz : null;

        var tone = Frequency(Environment.GetEnvironmentVariable("DISCAGEM_TEST_TONE"));
        var record = Environment.GetEnvironmentVariable("DISCAGEM_TEST_RECORD");
        var streamToneParts = (Environment.GetEnvironmentVariable("DISCAGEM_TEST_STREAM_TONE") ?? "").Split(',');
        (double, double)? streamTones = Frequency(streamToneParts[0]) is { } left
            ? (left, Frequency(streamToneParts.ElementAtOrDefault(1)) ?? left)
            : null;
        var streamRecord = Environment.GetEnvironmentVariable("DISCAGEM_TEST_STREAM_RECORD");
        return tone is null && string.IsNullOrEmpty(record) && streamTones is null && string.IsNullOrEmpty(streamRecord)
            ? inner
            : new SyntheticAudioBackend(inner, tone, record, streamTones, streamRecord);
    }

    public string Name => $"{inner.Name} (synthetic)";

    public IReadOnlyList<AudioDeviceInfo> InputDevices() => toneHz is null ? inner.InputDevices() : [AudioDeviceInfo.Default];

    public IReadOnlyList<AudioDeviceInfo> OutputDevices() => string.IsNullOrEmpty(recordPath) ? inner.OutputDevices() : [AudioDeviceInfo.Default];

    public bool SupportsSystemCapture => streamTones is not null || inner.SupportsSystemCapture;

    public bool SupportsApplicationCapture => streamTones is not null || inner.SupportsApplicationCapture;

    public IAudioStream OpenInput(string? deviceId, AudioSink sink)
    {
        if (toneHz is not { } hz)
        {
            return inner.OpenInput(deviceId, sink);
        }

        var tone = new Tone(hz);
        return new ClockedStream($"{hz} Hz tone", 1, buffer => tone.Fill(buffer, 1, 0), null, sink);
    }

    public IAudioStream OpenOutput(string? deviceId, AudioSource source, int channels = 1)
    {
        var path = channels == 1 ? recordPath : streamRecordPath;
        if (string.IsNullOrEmpty(path))
        {
            return inner.OpenOutput(deviceId, source, channels);
        }

        var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        return new ClockedStream("Recorder", channels, source, file, null);
    }

    public IAudioStream OpenSystemCapture(int? processId, AudioSink sink)
    {
        if (streamTones is not var (leftHz, rightHz))
        {
            return inner.OpenSystemCapture(processId, sink);
        }

        var left = new Tone(leftHz);
        var right = new Tone(rightHz);
        return new ClockedStream($"{leftHz}/{rightHz} Hz tones", 2, buffer =>
        {
            left.Fill(buffer, 2, 0);
            right.Fill(buffer, 2, 1);
        }, null, sink);
    }

    /// <summary>A sine wave written to one channel of an interleaved buffer.</summary>
    private sealed class Tone(double hz)
    {
        private long _phase;

        public void Fill(Span<float> buffer, int channels, int channel)
        {
            for (var i = channel; i < buffer.Length; i += channels)
            {
                buffer[i] = (float)(Math.Sin(2 * Math.PI * hz * _phase++ / DiscagemAudio.SampleRate) * 0.3);
            }
        }
    }

    /// <summary>Moves 10 ms blocks in real time, like a sound card would.</summary>
    private sealed class ClockedStream : IAudioStream
    {
        private readonly Thread _thread;
        private volatile bool _stopped;

        public ClockedStream(string name, int channels, AudioSource source, Stream? record, AudioSink? sink)
        {
            DeviceName = name;
            _thread = new Thread(() =>
            {
                var buffer = new float[DiscagemAudio.SampleRate / 100 * channels];
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
