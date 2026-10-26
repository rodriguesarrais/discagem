using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Discagem.Media.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Discagem.Client.Services.Audio;

/// <summary>
/// Windows audio through WASAPI in shared mode. The microphone is opened in the communications
/// processing mode where available, which lets Windows apply its echo cancellation, noise
/// suppression and gain control. Conversion to and from 48 kHz mono happens here.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WasapiAudioBackend : IAudioBackend
{
    /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT.</summary>
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    public string Name => "Windows (WASAPI)";

    public IReadOnlyList<AudioDeviceInfo> InputDevices() => List(DataFlow.Capture);

    public IReadOnlyList<AudioDeviceInfo> OutputDevices() => List(DataFlow.Render);

    private static List<AudioDeviceInfo> List(DataFlow flow)
    {
        var devices = new List<AudioDeviceInfo> { AudioDeviceInfo.Default };
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    devices.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
                }
            }
        }
        catch (COMException)
        {
            // The audio service is unavailable; only the default entry is offered.
        }

        return devices;
    }

    /// <summary>Finds the chosen device, falling back to the default one when it is gone.</summary>
    /// <remarks>The device is handed to NAudio, which keeps using it for the lifetime of the stream.</remarks>
    private static MMDevice Resolve(DataFlow flow, string? deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!string.IsNullOrEmpty(deviceId))
            {
                try
                {
                    var device = enumerator.GetDevice(deviceId);
                    if (device.State == DeviceState.Active)
                    {
                        return device;
                    }
                }
                catch (COMException)
                {
                    // Unplugged since it was chosen.
                }
            }

            if (enumerator.TryGetDefaultAudioEndpoint(flow, Role.Multimedia, out var fallback))
            {
                return fallback;
            }
        }
        catch (COMException ex)
        {
            throw new AudioDeviceException($"The Windows audio service is unavailable ({ex.Message}).", ex);
        }

        throw new AudioDeviceException(flow == DataFlow.Capture ? "No microphone was found." : "No speakers or headphones were found.");
    }

    public IAudioStream OpenInput(string? deviceId, AudioSink sink)
    {
        var device = Resolve(DataFlow.Capture, deviceId);
        try
        {
            // Windows' communications pipeline is not available on every device; fall back to raw capture.
            try
            {
                return new InputStream(device, sink, communications: true);
            }
            catch (Exception ex) when (IsDeviceError(ex))
            {
                return new InputStream(device, sink, communications: false);
            }
        }
        catch (Exception ex) when (IsDeviceError(ex))
        {
            throw new AudioDeviceException($"Could not open the microphone ({ex.Message}).", ex);
        }
    }

    public IAudioStream OpenOutput(string? deviceId, AudioSource source)
    {
        var device = Resolve(DataFlow.Render, deviceId);
        try
        {
            return new OutputStream(device, source);
        }
        catch (Exception ex) when (IsDeviceError(ex))
        {
            throw new AudioDeviceException($"Could not open the speakers ({ex.Message}).", ex);
        }
    }

    private static bool IsDeviceError(Exception ex) =>
        ex is COMException or ArgumentException or InvalidOperationException or NotSupportedException or AudioDeviceException;

    private static PcmFormat ToPcmFormat(WaveFormat format)
    {
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                      (format is WaveFormatExtensible extensible && extensible.SubFormat == FloatSubFormat);
        var type = (isFloat, format.BitsPerSample) switch
        {
            (true, 32) => PcmSampleType.Float32,
            (false, 16) => PcmSampleType.Int16,
            (false, 24) => PcmSampleType.Int24,
            (false, 32) => PcmSampleType.Int32,
            _ => throw new AudioDeviceException($"Unsupported device format: {format}."),
        };
        return new PcmFormat(format.SampleRate, format.Channels, type);
    }

    private sealed class InputStream : IAudioStream
    {
        private readonly WasapiRecorder _recorder;
        private readonly PcmFormat _format;
        private readonly CaptureResampler _resampler;
        private float[] _mono = [];
        private int _disposed;

        public InputStream(MMDevice device, AudioSink sink, bool communications)
        {
            var builder = new WasapiRecorderBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithEventSync()
                .WithBufferLength(20)
                .WithMmcssThreadPriority("Audio");
            if (communications)
            {
                builder = builder.WithCommunicationsMode();
            }

            DeviceName = device.FriendlyName;
            _recorder = builder.Build();
            try
            {
                _format = ToPcmFormat(_recorder.WaveFormat);
                _resampler = new CaptureResampler(_format.SampleRate, sink);
                _recorder.DataAvailable += OnDataAvailable;
                _recorder.RecordingStopped += (_, e) =>
                {
                    if (e.Exception is not null && Volatile.Read(ref _disposed) == 0)
                    {
                        Failed?.Invoke(e.Exception.Message);
                    }
                };
                _recorder.StartRecording();
            }
            catch
            {
                _recorder.Dispose();
                throw;
            }
        }

        public string DeviceName { get; }

        public event Action<string>? Failed;

        private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
        {
            if (buffer.IsEmpty || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            var frames = buffer.Length / _format.BlockAlign;
            if (_mono.Length < frames)
            {
                _mono = new float[frames];
            }

            var mono = _mono.AsSpan(0, frames);
            if ((flags & AudioClientBufferFlags.Silent) != 0)
            {
                mono.Clear();
            }
            else
            {
                PcmConversion.ToMono(buffer, _format, mono);
            }

            _resampler.Write(mono);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _recorder.StopRecording();
            }
            catch (Exception ex) when (IsDeviceError(ex))
            {
                // The device is already gone.
            }

            _recorder.Dispose();
        }
    }

    private sealed class OutputStream : IAudioStream
    {
        private readonly WasapiPlayer _player;
        private int _disposed;

        public OutputStream(MMDevice device, AudioSource source)
        {
            DeviceName = device.FriendlyName;
            _player = new WasapiPlayerBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithEventSync()
                .WithLatency(40)
                .WithMmcssThreadPriority("Audio")
                .Build();
            try
            {
                var mixFormat = _player.DeviceMixFormat;
                _player.PlaybackStopped += (_, e) =>
                {
                    if (e.Exception is not null && Volatile.Read(ref _disposed) == 0)
                    {
                        Failed?.Invoke(e.Exception.Message);
                    }
                };
                _player.Init(new Provider(mixFormat, ToPcmFormat(mixFormat), source));
                _player.Play();
            }
            catch
            {
                _player.Dispose();
                throw;
            }
        }

        public string DeviceName { get; }

        public event Action<string>? Failed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _player.Stop();
            }
            catch (Exception ex) when (IsDeviceError(ex))
            {
                // The device is already gone.
            }

            _player.Dispose();
        }
    }

    /// <summary>Pulls 48 kHz mono audio and converts it to the device's mix format.</summary>
    private sealed class Provider(WaveFormat waveFormat, PcmFormat format, AudioSource source) : IWaveProvider
    {
        private readonly PlaybackResampler _resampler = new(format.SampleRate, source);
        private float[] _mono = [];

        public WaveFormat WaveFormat { get; } = waveFormat;

        public int Read(Span<byte> buffer)
        {
            var frames = buffer.Length / format.BlockAlign;
            if (_mono.Length < frames)
            {
                _mono = new float[frames];
            }

            var mono = _mono.AsSpan(0, frames);
            _resampler.Read(mono);
            PcmConversion.FromMono(mono, format, buffer);
            return frames * format.BlockAlign;
        }
    }
}
