using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Discagem.Client.Resources;
using Discagem.Media.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Discagem.Client.Services.Audio;

/// <summary>
/// Windows audio through WASAPI in shared mode. The microphone is opened in the communications
/// processing mode where available, which lets Windows apply its echo cancellation, noise
/// suppression and gain control. Conversion to and from 48 kHz mono (or stereo) happens here.
/// The audio of screen shares is captured with process loopback (Windows 10 2004 and later), which
/// can follow one application or leave Discagem's own sounds out.
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
            throw new AudioDeviceException(string.Format(Strings.Audio_WindowsAudioUnavailable, ex.Message), ex);
        }

        throw new AudioDeviceException(flow == DataFlow.Capture ? Strings.Audio_NoMicrophoneFound : Strings.Audio_NoSpeakersFound);
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
            throw new AudioDeviceException(string.Format(Strings.Audio_CouldNotOpenMicrophone, ex.Message), ex);
        }
    }

    public IAudioStream OpenOutput(string? deviceId, AudioSource source, int channels = 1)
    {
        var device = Resolve(DataFlow.Render, deviceId);
        try
        {
            return new OutputStream(device, source, channels);
        }
        catch (Exception ex) when (IsDeviceError(ex))
        {
            throw new AudioDeviceException(string.Format(Strings.Audio_CouldNotOpenSpeakers, ex.Message), ex);
        }
    }

    /// <summary>Process loopback capture, which can leave Discagem's own sounds out, needs Windows 10 version 2004 (build 19041).</summary>
    public bool SupportsSystemCapture => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    public bool SupportsApplicationCapture => SupportsSystemCapture;

    /// <remarks>
    /// Only process loopback is used: capturing a whole output device would also capture voice chat,
    /// which must never reach the stream. A window's application that cannot be captured on its own
    /// (one running as administrator, say) falls back to everything except Discagem.
    /// </remarks>
    public IAudioStream OpenSystemCapture(int? processId, AudioSink sink)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            throw new AudioDeviceException(Strings.Audio_SystemCaptureNeedsWindows2004);
        }

        Exception? applicationError = null;
        if (processId is { } pid)
        {
            try
            {
                return OpenProcessLoopback(pid, ProcessLoopbackMode.IncludeTargetProcessTree, Strings.Audio_SharedWindowApplication, sink);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                applicationError = ex;
                ClientLog.Warning($"Could not capture the sound of process {pid} alone: {ex}");
            }
        }

        try
        {
            var description = applicationError is null
                ? Strings.Audio_EverythingExceptDiscagem
                : Strings.Audio_EverythingExceptDiscagemFallback;
            return OpenProcessLoopback(Environment.ProcessId, ProcessLoopbackMode.ExcludeTargetProcessTree, description, sink);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ClientLog.Error($"Could not capture the computer's sound: {ex}");
            throw new AudioDeviceException(string.Format(Strings.Audio_CouldNotCaptureComputerSound, ex.Message), ex);
        }
    }

    [SupportedOSPlatform("windows10.0.19041")]
    private static LoopbackStream OpenProcessLoopback(int processId, ProcessLoopbackMode mode, string description, AudioSink sink)
    {
        // Process loopback has no device mix format: ask for 48 kHz float stereo and let Windows convert.
        var recorder = new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)processId, mode)
            .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))
            .WithBufferLength(20)
            .BuildAsync()
            .GetAwaiter()
            .GetResult();
        return new LoopbackStream(recorder, description, sink);
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
            _ => throw new AudioDeviceException(string.Format(Strings.Audio_UnsupportedDeviceFormat, format)),
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

    /// <summary>A loopback recorder delivering 48 kHz interleaved stereo.</summary>
    private sealed class LoopbackStream : IAudioStream
    {
        private readonly WasapiRecorder _recorder;
        private readonly PcmFormat _format;
        private readonly InterleavedResampler _resampler;
        private readonly AudioSink _sink;
        private float[] _stereo = [];
        private float[] _resampled = [];
        private int _disposed;

        public LoopbackStream(WasapiRecorder recorder, string description, AudioSink sink)
        {
            _recorder = recorder;
            _sink = sink;
            DeviceName = description;
            try
            {
                _format = ToPcmFormat(recorder.WaveFormat);
                _resampler = new InterleavedResampler(2, _format.SampleRate, Discagem.Media.WebRtc.DiscagemAudio.SampleRate);
                recorder.DataAvailable += OnDataAvailable;
                recorder.RecordingStopped += (_, e) =>
                {
                    if (e.Exception is not null && Volatile.Read(ref _disposed) == 0)
                    {
                        Failed?.Invoke(e.Exception.Message);
                    }
                };
                recorder.StartRecording();
            }
            catch
            {
                recorder.Dispose();
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
            if (_stereo.Length < frames * 2)
            {
                _stereo = new float[frames * 2];
            }

            var stereo = _stereo.AsSpan(0, frames * 2);
            if ((flags & AudioClientBufferFlags.Silent) != 0)
            {
                stereo.Clear();
            }
            else
            {
                PcmConversion.ToStereo(buffer, _format, stereo);
            }

            if (_resampler.IsPassThrough)
            {
                _sink(stereo);
                return;
            }

            var needed = _resampler.MaxOutputFor(stereo.Length);
            if (_resampled.Length < needed)
            {
                _resampled = new float[needed];
            }

            var written = _resampler.Process(stereo, _resampled);
            _sink(_resampled.AsSpan(0, written));
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

        public OutputStream(MMDevice device, AudioSource source, int channels)
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
                _player.Init(new Provider(mixFormat, ToPcmFormat(mixFormat), source, channels));
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

    /// <summary>Pulls 48 kHz mono or stereo audio and converts it to the device's mix format.</summary>
    private sealed class Provider(WaveFormat waveFormat, PcmFormat format, AudioSource source, int channels) : IWaveProvider
    {
        private readonly PlaybackResampler _resampler = new(format.SampleRate, source, channels);
        private float[] _samples = [];

        public WaveFormat WaveFormat { get; } = waveFormat;

        public int Read(Span<byte> buffer)
        {
            var frames = buffer.Length / format.BlockAlign;
            if (_samples.Length < frames * channels)
            {
                _samples = new float[frames * channels];
            }

            var samples = _samples.AsSpan(0, frames * channels);
            _resampler.Read(samples);
            if (channels == 2)
            {
                PcmConversion.FromStereo(samples, format, buffer);
            }
            else
            {
                PcmConversion.FromMono(samples, format, buffer);
            }

            return frames * format.BlockAlign;
        }
    }
}
