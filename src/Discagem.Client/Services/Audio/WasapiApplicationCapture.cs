using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Discagem.Media.Audio;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Discagem.Client.Services.Audio;

/// <summary>
/// Captures the sound of a screen share on Windows application by application, each with process
/// loopback in its include mode, and mixes them in an <see cref="ApplicationAudioMixer"/> where each
/// has its own volume.
/// </summary>
/// <remarks>
/// <para>
/// For a whole screen, the applications are the processes with an audio session on any output
/// device, found again every two seconds so that applications that start later are picked up.
/// Discagem is never one of them. A process whose parent is already captured is skipped, because the
/// parent's capture includes its children.
/// </para>
/// <para>
/// Programs that replay other programs' sound (headset software such as SteelSeries Sonar, or
/// virtual mixers such as Voicemeeter) play Discagem's voice chat too. They show up in the mixer like
/// any other application, starting muted when they are recognised, so that the people in the call
/// do not hear themselves through the stream.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.19041")]
internal sealed class WasapiApplicationCapture : IMixedAudioStream
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);

    private readonly int _ownProcessId = Environment.ProcessId;
    private readonly Dictionary<int, IAudioStream> _captures = new();
    private readonly HashSet<int> _failed = new();
    private readonly HashSet<int> _broken = new();
    private readonly object _sync = new();
    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread? _scanner;
    private int _disposed;

    private WasapiApplicationCapture(AudioSink sink, Func<string, float>? initialVolume, string description)
    {
        Mixer = new ApplicationAudioMixer(sink, initialVolume);
        DeviceName = description;
    }

    private WasapiApplicationCapture(AudioSink sink, Func<string, float>? initialVolume, string description, bool scan)
        : this(sink, initialVolume, description)
    {
        if (!scan)
        {
            return;
        }

        // The first scan runs here, so that an audio system that cannot be queried fails the capture.
        Scan();
        _scanner = new Thread(ScanLoop) { IsBackground = true, Name = "Discagem stream audio applications" };
        _scanner.Start();
        Mixer.Start();
    }

    public ApplicationAudioMixer Mixer { get; }

    public string DeviceName { get; }

    public event Action<string>? Failed;

    /// <summary>Captures one process (the shared window's) and its children.</summary>
    public static WasapiApplicationCapture ForProcess(int processId, AudioSink sink, Func<string, float>? initialVolume)
    {
        var (key, name) = Identify(processId);
        var capture = new WasapiApplicationCapture(sink, initialVolume, name);
        try
        {
            var stream = WasapiAudioBackend.OpenProcessLoopback(processId, name, samples => capture.Mixer.Write(processId, samples));
            stream.Failed += reason => capture.Failed?.Invoke(reason);
            capture._captures[processId] = stream;
            capture.Mixer.AddInput(processId, key, name);
            capture.Mixer.Start();
            return capture;
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    /// <summary>Captures every application with an audio session, except Discagem.</summary>
    public static WasapiApplicationCapture ForAllApplications(AudioSink sink, Func<string, float>? initialVolume, string description)
    {
        var capture = new WasapiApplicationCapture(sink, initialVolume, description, scan: true);
        return capture;
    }

    private void ScanLoop()
    {
        while (!_stop.Wait(ScanInterval))
        {
            try
            {
                Scan();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Devices come and go; the next scan tries again.
                ClientLog.Warning($"Could not list the applications playing sound: {ex.Message}");
            }
        }
    }

    /// <summary>Starts capturing applications that began playing and stops those that went away.</summary>
    private void Scan()
    {
        var wanted = ApplicationProcesses();
        List<int> gone;
        lock (_sync)
        {
            if (_disposed != 0)
            {
                return;
            }

            gone = _captures.Keys.Where(pid => !wanted.Contains(pid) || _broken.Contains(pid)).ToList();
            _failed.IntersectWith(wanted);
        }

        foreach (var pid in gone)
        {
            Stop(pid);
        }

        foreach (var pid in wanted)
        {
            lock (_sync)
            {
                if (_disposed != 0 || _captures.ContainsKey(pid) || _failed.Contains(pid))
                {
                    continue;
                }
            }

            Start(pid);
        }
    }

    private void Start(int processId)
    {
        var (key, name) = Identify(processId);
        IAudioStream stream;
        try
        {
            stream = WasapiAudioBackend.OpenProcessLoopback(processId, name, samples => Mixer.Write(processId, samples));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Typically a process running as administrator. Not retried until it goes away.
            lock (_sync)
            {
                _failed.Add(processId);
            }

            ClientLog.Warning($"Could not capture the sound of {name} (process {processId}): {ex.Message}");
            return;
        }

        stream.Failed += reason =>
        {
            ClientLog.Warning($"The sound capture of {name} (process {processId}) stopped: {reason}");
            lock (_sync)
            {
                _broken.Add(processId);
            }
        };

        lock (_sync)
        {
            if (_disposed != 0)
            {
                stream.Dispose();
                return;
            }

            _captures[processId] = stream;
        }

        Mixer.AddInput(processId, key, name);
        ClientLog.Info($"Stream audio: capturing {name} (process {processId}, {key}).");
    }

    private void Stop(int processId)
    {
        IAudioStream? stream;
        lock (_sync)
        {
            _captures.Remove(processId, out stream);
            _broken.Remove(processId);
        }

        Mixer.RemoveInput(processId);
        stream?.Dispose();
    }

    /// <summary>
    /// The processes to capture: those with an audio session on an output device, except Discagem
    /// and its children, and except processes whose parent is captured already.
    /// </summary>
    private HashSet<int> ApplicationProcesses()
    {
        var withSessions = new HashSet<int>();
        using (var enumerator = new MMDeviceEnumerator())
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    try
                    {
                        AddSessionProcesses(device, withSessions);
                    }
                    catch (Exception ex) when (WasapiAudioBackend.IsDeviceError(ex))
                    {
                        // A device that went away or does not report sessions: the others still count.
                        ClientLog.Warning($"Could not list the audio sessions of {device.FriendlyName}: {ex.Message}");
                    }
                }
            }
        }

        var parents = ProcessParents();
        var result = new HashSet<int>();
        foreach (var pid in withSessions)
        {
            var ancestors = Ancestors(pid, parents);
            if (pid == _ownProcessId || ancestors.Contains(_ownProcessId) || ancestors.Any(withSessions.Contains))
            {
                continue;
            }

            result.Add(pid);
        }

        return result;
    }

    private static void AddSessionProcesses(MMDevice device, HashSet<int> processes)
    {
        using var manager = device.AudioSessionManager;
        var sessions = manager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            try
            {
                using var session = sessions[i];
                if (session.IsSystemSoundsSession || session.State == AudioSessionState.AudioSessionStateExpired)
                {
                    continue;
                }

                var pid = (int)session.GetProcessID;
                if (pid > 0)
                {
                    processes.Add(pid);
                }
            }
            catch (Exception ex) when (WasapiAudioBackend.IsDeviceError(ex))
            {
                // A session that ended while being read.
            }
        }
    }

    private static List<int> Ancestors(int pid, Dictionary<int, int> parents)
    {
        var ancestors = new List<int>();
        var current = pid;
        // Parent ids can be stale (the parent exited and its id was reused), so stop on cycles.
        while (ancestors.Count < 32 && parents.TryGetValue(current, out var parent) && parent > 0 && parent != current && !ancestors.Contains(parent))
        {
            ancestors.Add(parent);
            current = parent;
        }

        return ancestors;
    }

    /// <summary>A stable key (the executable's name, lower case) and a name to show for a process.</summary>
    private static (string Key, string Name) Identify(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var key = process.ProcessName.ToLowerInvariant();
            var name = process.ProcessName;
            try
            {
                var description = process.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(description))
                {
                    name = description.Trim();
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Another user's or an elevated process: the executable's name will do.
            }

            return (key, name);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ($"process-{processId}", $"Process {processId}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Set();
        if (_scanner is { } scanner && Thread.CurrentThread != scanner)
        {
            scanner.Join(TimeSpan.FromSeconds(3));
        }

        List<IAudioStream> streams;
        lock (_sync)
        {
            streams = _captures.Values.ToList();
            _captures.Clear();
        }

        Mixer.Dispose();
        foreach (var stream in streams)
        {
            stream.Dispose();
        }
    }

    // ---- Process tree (Tool Help) ---------------------------------------------------------------

    private const uint SnapProcess = 0x00000002;

    /// <summary>Parent process id of every running process.</summary>
    private static Dictionary<int, int> ProcessParents()
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return parents;
        }

        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                }
                while (Process32NextW(snapshot, ref entry));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return parents;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
