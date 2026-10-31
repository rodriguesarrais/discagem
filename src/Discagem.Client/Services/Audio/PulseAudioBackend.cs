using System.Runtime.InteropServices;
using Discagem.Media.Audio;
using Discagem.Media.WebRtc;

namespace Discagem.Client.Services.Audio;

/// <summary>
/// Linux audio through the PulseAudio "simple" API, which PipeWire also provides. Streams are
/// 48 kHz mono or stereo float, so the sound server does any conversion; each stream runs on its own
/// thread moving 10 ms blocks. The audio of screen shares is captured application by application,
/// without Discagem's own sounds (see <see cref="PulseApplicationCapture"/>).
/// </summary>
internal sealed class PulseAudioBackend : IAudioBackend
{
    private const string SimpleLibrary = "libpulse-simple.so.0";
    private const string PulseLibrary = "libpulse.so.0";
    private const int BlockSamples = DiscagemAudio.SampleRate / 100;

    private static readonly Lazy<bool> Available = new(() =>
        NativeLibrary.TryLoad(SimpleLibrary, out _) && NativeLibrary.TryLoad(PulseLibrary, out _));

    public static bool IsAvailable => OperatingSystem.IsLinux() && Available.Value;

    public string Name => "PulseAudio";

    public IReadOnlyList<AudioDeviceInfo> InputDevices() => [AudioDeviceInfo.Default];

    public IReadOnlyList<AudioDeviceInfo> OutputDevices() => [AudioDeviceInfo.Default];

    public IAudioStream OpenInput(string? deviceId, AudioSink sink) =>
        new PulseStream(Native.Direction.Record, null, 1, "Voice input", sink, null);

    public IAudioStream OpenOutput(string? deviceId, AudioSource source, int channels = 1) =>
        new PulseStream(Native.Direction.Playback, null, channels, channels == 1 ? "Voice output" : "Stream audio", null, source);

    public bool SupportsSystemCapture => true;

    public bool SupportsApplicationCapture => true;

    /// <summary>Captures every application's playback except Discagem's own (see <see cref="PulseApplicationCapture"/>).</summary>
    public IAudioStream OpenSystemCapture(int? processId, AudioSink sink) => new PulseApplicationCapture(processId, sink);

    private sealed unsafe class PulseStream : IAudioStream
    {
        private readonly IntPtr _handle;
        private readonly Thread _thread;
        private readonly int _channels;
        private volatile bool _stopped;

        public PulseStream(Native.Direction direction, string? device, int channels, string streamName, AudioSink? sink, AudioSource? source)
        {
            _channels = channels;
            var spec = new Native.SampleSpec { Format = Native.SampleFormatFloat32Le, Rate = DiscagemAudio.SampleRate, Channels = (byte)channels };
            var blockBytes = (uint)(BlockSamples * channels * sizeof(float));
            var attributes = new Native.BufferAttributes
            {
                MaxLength = uint.MaxValue,
                // Playback: keep about 40 ms queued; capture: deliver 10 ms fragments.
                TargetLength = direction == Native.Direction.Playback ? blockBytes * 4 : uint.MaxValue,
                PreBuffer = uint.MaxValue,
                MinimumRequest = direction == Native.Direction.Playback ? blockBytes : uint.MaxValue,
                FragmentSize = direction == Native.Direction.Record ? blockBytes : uint.MaxValue,
            };

            _handle = Native.pa_simple_new(
                null,
                "Discagem",
                direction,
                device,
                streamName,
                ref spec,
                IntPtr.Zero,
                ref attributes,
                out var error);
            if (_handle == IntPtr.Zero)
            {
                var what = device is not null ? "the computer's audio" : direction == Native.Direction.Record ? "the microphone" : "the speakers";
                throw new AudioDeviceException($"Could not open {what}: {Native.Describe(error)}.");
            }

            _thread = new Thread(() => Run(direction, sink, source))
            {
                IsBackground = true,
                Name = $"Discagem PulseAudio {direction}",
                Priority = ThreadPriority.Highest,
            };
            _thread.Start();
        }

        public string DeviceName { get; init; } = "System default";

        public event Action<string>? Failed;

        private void Run(Native.Direction direction, AudioSink? sink, AudioSource? source)
        {
            var buffer = new float[BlockSamples * _channels];
            var bytes = (nuint)(buffer.Length * sizeof(float));
            fixed (float* data = buffer)
            {
                while (!_stopped)
                {
                    int result;
                    int error;
                    if (direction == Native.Direction.Record)
                    {
                        result = Native.pa_simple_read(_handle, data, bytes, out error);
                        if (result >= 0)
                        {
                            sink!(buffer);
                        }
                    }
                    else
                    {
                        source!(buffer);
                        result = Native.pa_simple_write(_handle, data, bytes, out error);
                    }

                    if (result < 0)
                    {
                        if (!_stopped)
                        {
                            Failed?.Invoke(Native.Describe(error));
                        }

                        return;
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_stopped)
            {
                return;
            }

            // Reads and writes return within a block, so the thread notices promptly.
            _stopped = true;
            if (Thread.CurrentThread != _thread && !_thread.Join(TimeSpan.FromSeconds(1)))
            {
                // Still blocked in the sound server: leak the handle rather than free it under the thread.
                return;
            }

            Native.pa_simple_free(_handle);
        }
    }

    private static unsafe class Native
    {
        public const int SampleFormatFloat32Le = 5;

        public enum Direction
        {
            Playback = 1,
            Record = 2,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SampleSpec
        {
            public int Format;
            public uint Rate;
            public byte Channels;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BufferAttributes
        {
            public uint MaxLength;
            public uint TargetLength;
            public uint PreBuffer;
            public uint MinimumRequest;
            public uint FragmentSize;
        }

        [DllImport(SimpleLibrary)]
        public static extern IntPtr pa_simple_new(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? server,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            Direction direction,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? device,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string streamName,
            ref SampleSpec spec,
            IntPtr channelMap,
            ref BufferAttributes attributes,
            out int error);

        [DllImport(SimpleLibrary)]
        public static extern int pa_simple_read(IntPtr stream, float* data, nuint bytes, out int error);

        [DllImport(SimpleLibrary)]
        public static extern int pa_simple_write(IntPtr stream, float* data, nuint bytes, out int error);

        [DllImport(SimpleLibrary)]
        public static extern void pa_simple_free(IntPtr stream);

        [DllImport(PulseLibrary)]
        private static extern IntPtr pa_strerror(int error);

        public static string Describe(int error) => Marshal.PtrToStringUTF8(pa_strerror(error)) ?? $"error {error}";
    }
}
