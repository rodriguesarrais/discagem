using System.Diagnostics;
using System.Runtime.InteropServices;
using Discagem.Media.Audio;
using Discagem.Media.WebRtc;

namespace Discagem.Client.Services.Audio;

/// <summary>
/// Captures what applications play through PulseAudio (or PipeWire's PulseAudio service) for a
/// screen share: one monitor stream per playback stream ("sink input"), mixed into 48 kHz stereo
/// by an <see cref="ApplicationAudioMixer"/>, where each application has its own volume.
/// Discagem's own playback streams (voice chat, watched streams, sounds) are left out, so the
/// people in a call never hear themselves through the stream. Playback streams that start later
/// are picked up as they appear.
/// </summary>
/// <remarks>
/// Uses the asynchronous libpulse API with a threaded main loop: PulseAudio callbacks run on its
/// thread with the loop locked, and the mixer's thread hands 10 ms blocks to the sink.
/// </remarks>
internal sealed unsafe class PulseApplicationCapture : IMixedAudioStream
{
    private const string Library = "libpulse.so.0";
    private const int Channels = 2;
    private const int BlockSamples = ApplicationAudioMixer.BlockSamples;

    // Layout of pa_sink_input_info and pa_sink_info on 64-bit Linux (stable libpulse ABI).
    private const int SinkInputIndexOffset = 0;
    private const int SinkInputSinkOffset = 24;
    private const int SinkInputProplistOffset = 344;
    private const int SinkMonitorSourceNameOffset = 312;

    private const int SubscriptionMaskSinkInput = 0x0004;
    private const int SubscriptionFacilitySinkInput = 0x0002;
    private const int SubscriptionFacilityMask = 0x000F;
    private const int SubscriptionTypeMask = 0x0030;
    private const int SubscriptionTypeRemove = 0x0020;
    private const int ContextReady = 4;
    private const int ContextFailed = 5;
    private const int ContextTerminated = 6;
    private const int StreamFailed = 3;
    private const int StreamTerminated = 4;
    private const int StreamDontMove = 0x0200;
    private const int StreamAdjustLatency = 0x2000;
    private const int SampleFormatFloat32Le = 5;

    private readonly int? _onlyProcessId;
    private readonly int _ownProcessId = Environment.ProcessId;
    private readonly IntPtr _mainloop;
    private readonly IntPtr _context;
    private readonly Dictionary<uint, Input> _inputs = new();
    private readonly Dictionary<uint, (string Key, string Name)> _identities = new();
    private readonly object _inputsLock = new();
    private volatile bool _stopped;

    // Callbacks handed to native code; kept in fields so that they live as long as this object.
    private readonly ContextNotifyCallback _onContextState;
    private readonly SubscribeCallback _onSubscription;
    private readonly SinkInputInfoCallback _onSinkInput;
    private readonly SinkInfoCallback _onSink;
    private readonly StreamRequestCallback _onRead;
    private readonly StreamNotifyCallback _onStreamState;

    public PulseApplicationCapture(int? onlyProcessId, AudioSink sink, Func<string, float>? initialVolume)
    {
        Mixer = new ApplicationAudioMixer(sink, initialVolume);
        _onlyProcessId = onlyProcessId;
        _onContextState = OnContextState;
        _onSubscription = OnSubscription;
        _onSinkInput = OnSinkInput;
        _onSink = OnSink;
        _onRead = OnRead;
        _onStreamState = OnStreamState;

        _mainloop = pa_threaded_mainloop_new();
        if (_mainloop == IntPtr.Zero)
        {
            throw new AudioDeviceException("Could not start the PulseAudio main loop.");
        }

        _context = pa_context_new(pa_threaded_mainloop_get_api(_mainloop), "Discagem");
        pa_context_set_state_callback(_context, _onContextState, IntPtr.Zero);
        if (pa_context_connect(_context, null, 0, IntPtr.Zero) < 0 || pa_threaded_mainloop_start(_mainloop) < 0)
        {
            var error = Describe(pa_context_errno(_context));
            Release();
            throw new AudioDeviceException($"Could not connect to PulseAudio: {error}.");
        }

        pa_threaded_mainloop_lock(_mainloop);
        try
        {
            int state;
            while ((state = pa_context_get_state(_context)) is not (ContextReady or ContextFailed or ContextTerminated))
            {
                pa_threaded_mainloop_wait(_mainloop);
            }

            if (state != ContextReady)
            {
                throw new AudioDeviceException($"Could not connect to PulseAudio: {Describe(pa_context_errno(_context))}.");
            }

            // Watch for playback streams coming and going, then pick up the ones already playing.
            pa_context_set_subscribe_callback(_context, _onSubscription, IntPtr.Zero);
            Unref(pa_context_subscribe(_context, SubscriptionMaskSinkInput, IntPtr.Zero, IntPtr.Zero));
            Unref(pa_context_get_sink_input_info_list(_context, _onSinkInput, IntPtr.Zero));
        }
        catch
        {
            pa_threaded_mainloop_unlock(_mainloop);
            Release();
            throw;
        }

        pa_threaded_mainloop_unlock(_mainloop);
        Mixer.Start();
    }

    public ApplicationAudioMixer Mixer { get; }

    public string DeviceName => _onlyProcessId is null ? "Applications' sound (without Discagem)" : "Application sound";

    public event Action<string>? Failed;

    /// <summary>Applications currently captured (for diagnostics).</summary>
    public int CapturedStreams
    {
        get
        {
            lock (_inputsLock)
            {
                return _inputs.Values.Count(i => !i.IsDead);
            }
        }
    }

    // ---- PulseAudio callbacks (main loop thread, loop locked) ------------------------------------

    private void OnContextState(IntPtr context, IntPtr userdata)
    {
        var state = pa_context_get_state(context);
        if (state is ContextFailed or ContextTerminated && !_stopped)
        {
            Failed?.Invoke($"PulseAudio connection lost ({Describe(pa_context_errno(context))})");
        }

        pa_threaded_mainloop_signal(_mainloop, 0);
    }

    private void OnSubscription(IntPtr context, int type, uint index, IntPtr userdata)
    {
        if ((type & SubscriptionFacilityMask) != SubscriptionFacilitySinkInput || _stopped)
        {
            return;
        }

        if ((type & SubscriptionTypeMask) == SubscriptionTypeRemove)
        {
            RemoveInput(index);
        }
        else
        {
            // New stream, or one that changed (for example moved to another output).
            Unref(pa_context_get_sink_input_info(context, index, _onSinkInput, IntPtr.Zero));
        }
    }

    private void OnSinkInput(IntPtr context, IntPtr info, int eol, IntPtr userdata)
    {
        if (eol != 0 || info == IntPtr.Zero || _stopped)
        {
            return;
        }

        var index = *(uint*)(info + SinkInputIndexOffset);
        var sinkIndex = *(uint*)(info + SinkInputSinkOffset);
        var properties = *(IntPtr*)(info + SinkInputProplistOffset);
        var processId = int.TryParse(Property(properties, "application.process.id"), out var pid) ? pid : (int?)null;
        var binary = Property(properties, "application.process.binary");
        var appName = Property(properties, "application.name");

        var wanted = _onlyProcessId is { } only ? processId == only : processId != _ownProcessId;
        if (!wanted)
        {
            return;
        }

        lock (_inputsLock)
        {
            if (_inputs.TryGetValue(index, out var existing) && existing.SinkIndex == sinkIndex && !existing.IsDead)
            {
                return;
            }
        }

        RemoveInput(index);
        lock (_inputsLock)
        {
            _identities[index] = ApplicationIdentity(binary, appName);
        }

        // The monitor stream must be connected to the monitor of the output the stream plays on.
        Unref(pa_context_get_sink_info_by_index(context, sinkIndex, _onSink, (IntPtr)(((long)sinkIndex << 32) | index)));
    }

    private void OnSink(IntPtr context, IntPtr info, int eol, IntPtr userdata)
    {
        if (eol != 0 || info == IntPtr.Zero || _stopped)
        {
            return;
        }

        var index = (uint)((long)userdata & 0xFFFFFFFF);
        var sinkIndex = (uint)((long)userdata >> 32);
        var monitor = Marshal.PtrToStringUTF8(*(IntPtr*)(info + SinkMonitorSourceNameOffset));
        if (string.IsNullOrEmpty(monitor))
        {
            return;
        }

        var spec = new SampleSpec { Format = SampleFormatFloat32Le, Rate = DiscagemAudio.SampleRate, Channels = Channels };
        var stream = pa_stream_new(context, "Screen share audio", ref spec, IntPtr.Zero);
        if (stream == IntPtr.Zero)
        {
            return;
        }

        var key = (IntPtr)index;
        pa_stream_set_monitor_stream(stream, index);
        pa_stream_set_read_callback(stream, _onRead, key);
        pa_stream_set_state_callback(stream, _onStreamState, key);
        var attributes = new BufferAttributes
        {
            MaxLength = uint.MaxValue,
            TargetLength = uint.MaxValue,
            PreBuffer = uint.MaxValue,
            MinimumRequest = uint.MaxValue,
            FragmentSize = BlockSamples * sizeof(float),
        };

        if (pa_stream_connect_record(stream, monitor, ref attributes, StreamDontMove | StreamAdjustLatency) < 0)
        {
            pa_stream_unref(stream);
            return;
        }

        (string Key, string Name) identity;
        lock (_inputsLock)
        {
            identity = _identities.GetValueOrDefault(index, ("unknown", "Unknown application"));
            _inputs[index] = new Input(index, sinkIndex, stream);
        }

        Mixer.AddInput(index, identity.Key, identity.Name);
    }

    /// <summary>The key and name the mixer shows for a playback stream, from its application's properties.</summary>
    internal static (string Key, string Name) ApplicationIdentity(string? binary, string? name)
    {
        var key = !string.IsNullOrWhiteSpace(binary) ? binary.Trim()
            : !string.IsNullOrWhiteSpace(name) ? name.Trim()
            : "unknown";
        var shown = !string.IsNullOrWhiteSpace(name) ? name.Trim()
            : key == "unknown" ? "Unknown application" : key;
        return (key.ToLowerInvariant(), shown);
    }

    private void OnRead(IntPtr stream, nuint bytes, IntPtr userdata)
    {
        var index = (uint)(long)userdata;
        while (pa_stream_peek(stream, out var data, out var length) == 0 && length > 0)
        {
            if (data != IntPtr.Zero)
            {
                Mixer.Write(index, new ReadOnlySpan<float>((void*)data, (int)(length / sizeof(float))));
            }

            pa_stream_drop(stream);
        }
    }

    private void OnStreamState(IntPtr stream, IntPtr userdata)
    {
        // A stream that moved to another output, or whose application stopped, ends here; the
        // subscription events that follow clean it up or create a new one.
        if (pa_stream_get_state(stream) is StreamFailed or StreamTerminated)
        {
            lock (_inputsLock)
            {
                if (_inputs.TryGetValue((uint)(long)userdata, out var input) && input.Stream == stream)
                {
                    input.IsDead = true;
                }
            }
        }
    }

    /// <summary>Disconnects the monitor stream of a playback stream. Call with the main loop locked.</summary>
    private void RemoveInput(uint index)
    {
        Input? input;
        lock (_inputsLock)
        {
            if (!_inputs.Remove(index, out input))
            {
                return;
            }
        }

        Mixer.RemoveInput(index);

        pa_stream_set_read_callback(input.Stream, null, IntPtr.Zero);
        pa_stream_set_state_callback(input.Stream, null, IntPtr.Zero);
        pa_stream_disconnect(input.Stream);
        pa_stream_unref(input.Stream);
    }

    // ---- Lifetime -------------------------------------------------------------------------------

    public void Dispose()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        Mixer.Dispose();

        pa_threaded_mainloop_lock(_mainloop);
        List<uint> indexes;
        lock (_inputsLock)
        {
            indexes = _inputs.Keys.ToList();
        }

        foreach (var index in indexes)
        {
            RemoveInput(index);
        }

        pa_context_disconnect(_context);
        pa_threaded_mainloop_unlock(_mainloop);
        Release();
    }

    private void Release()
    {
        pa_threaded_mainloop_stop(_mainloop);
        if (_context != IntPtr.Zero)
        {
            pa_context_unref(_context);
        }

        pa_threaded_mainloop_free(_mainloop);
    }

    private static void Unref(IntPtr operation)
    {
        if (operation != IntPtr.Zero)
        {
            pa_operation_unref(operation);
        }
    }

    private static string? Property(IntPtr properties, string key) =>
        properties == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pa_proplist_gets(properties, key));

    private static string Describe(int error) => Marshal.PtrToStringUTF8(pa_strerror(error)) ?? $"error {error}";

    /// <summary>One application's playback stream and the audio received from it.</summary>
    private sealed class Input(uint index, uint sinkIndex, IntPtr stream)
    {
        public uint Index { get; } = index;

        public uint SinkIndex { get; } = sinkIndex;

        public IntPtr Stream { get; } = stream;

        public bool IsDead { get; set; }
    }

    // ---- libpulse -------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferAttributes
    {
        public uint MaxLength;
        public uint TargetLength;
        public uint PreBuffer;
        public uint MinimumRequest;
        public uint FragmentSize;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ContextNotifyCallback(IntPtr context, IntPtr userdata);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SubscribeCallback(IntPtr context, int type, uint index, IntPtr userdata);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SinkInputInfoCallback(IntPtr context, IntPtr info, int eol, IntPtr userdata);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SinkInfoCallback(IntPtr context, IntPtr info, int eol, IntPtr userdata);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StreamRequestCallback(IntPtr stream, nuint bytes, IntPtr userdata);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StreamNotifyCallback(IntPtr stream, IntPtr userdata);

    [DllImport(Library)] private static extern IntPtr pa_threaded_mainloop_new();
    [DllImport(Library)] private static extern IntPtr pa_threaded_mainloop_get_api(IntPtr mainloop);
    [DllImport(Library)] private static extern int pa_threaded_mainloop_start(IntPtr mainloop);
    [DllImport(Library)] private static extern void pa_threaded_mainloop_stop(IntPtr mainloop);
    [DllImport(Library)] private static extern void pa_threaded_mainloop_free(IntPtr mainloop);
    [DllImport(Library)] private static extern void pa_threaded_mainloop_lock(IntPtr mainloop);
    [DllImport(Library)] private static extern void pa_threaded_mainloop_unlock(IntPtr mainloop);
    [DllImport(Library)] private static extern void pa_threaded_mainloop_wait(IntPtr mainloop);
    [DllImport(Library)] private static extern void pa_threaded_mainloop_signal(IntPtr mainloop, int waitForAccept);

    [DllImport(Library)] private static extern IntPtr pa_context_new(IntPtr api, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Library)] private static extern void pa_context_set_state_callback(IntPtr context, ContextNotifyCallback? callback, IntPtr userdata);
    [DllImport(Library)] private static extern int pa_context_connect(IntPtr context, [MarshalAs(UnmanagedType.LPUTF8Str)] string? server, int flags, IntPtr spawnApi);
    [DllImport(Library)] private static extern int pa_context_get_state(IntPtr context);
    [DllImport(Library)] private static extern int pa_context_errno(IntPtr context);
    [DllImport(Library)] private static extern void pa_context_disconnect(IntPtr context);
    [DllImport(Library)] private static extern void pa_context_unref(IntPtr context);
    [DllImport(Library)] private static extern void pa_context_set_subscribe_callback(IntPtr context, SubscribeCallback? callback, IntPtr userdata);
    [DllImport(Library)] private static extern IntPtr pa_context_subscribe(IntPtr context, int mask, IntPtr callback, IntPtr userdata);
    [DllImport(Library)] private static extern IntPtr pa_context_get_sink_input_info_list(IntPtr context, SinkInputInfoCallback callback, IntPtr userdata);
    [DllImport(Library)] private static extern IntPtr pa_context_get_sink_input_info(IntPtr context, uint index, SinkInputInfoCallback callback, IntPtr userdata);
    [DllImport(Library)] private static extern IntPtr pa_context_get_sink_info_by_index(IntPtr context, uint index, SinkInfoCallback callback, IntPtr userdata);
    [DllImport(Library)] private static extern void pa_operation_unref(IntPtr operation);
    [DllImport(Library)] private static extern IntPtr pa_proplist_gets(IntPtr proplist, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);
    [DllImport(Library)] private static extern IntPtr pa_strerror(int error);

    [DllImport(Library)] private static extern IntPtr pa_stream_new(IntPtr context, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, ref SampleSpec spec, IntPtr channelMap);
    [DllImport(Library)] private static extern int pa_stream_set_monitor_stream(IntPtr stream, uint sinkInputIndex);
    [DllImport(Library)] private static extern void pa_stream_set_read_callback(IntPtr stream, StreamRequestCallback? callback, IntPtr userdata);
    [DllImport(Library)] private static extern void pa_stream_set_state_callback(IntPtr stream, StreamNotifyCallback? callback, IntPtr userdata);
    [DllImport(Library)] private static extern int pa_stream_connect_record(IntPtr stream, [MarshalAs(UnmanagedType.LPUTF8Str)] string device, ref BufferAttributes attributes, int flags);
    [DllImport(Library)] private static extern int pa_stream_get_state(IntPtr stream);
    [DllImport(Library)] private static extern int pa_stream_peek(IntPtr stream, out IntPtr data, out nuint bytes);
    [DllImport(Library)] private static extern int pa_stream_drop(IntPtr stream);
    [DllImport(Library)] private static extern int pa_stream_disconnect(IntPtr stream);
    [DllImport(Library)] private static extern void pa_stream_unref(IntPtr stream);
}
