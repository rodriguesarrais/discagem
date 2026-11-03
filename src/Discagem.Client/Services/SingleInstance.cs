using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Discagem.Client.Services;

/// <summary>
/// Keeps one client running per user and profile. A second launch (from the Start menu, say, while
/// the first one sits in the notification area) asks the running client to show its window and exits.
/// </summary>
/// <remarks>
/// <para>
/// The claim is a named mutex on Windows and, elsewhere, an exclusive lock on a file next to the
/// settings (which the system releases when the process ends, even after a crash). .NET keeps named
/// mutexes under the home folder on Linux, which a Flatpak sandbox does not let it write. A named
/// pipe, restricted to the current user, carries the request to show the window; it cannot be used
/// for detection, because on Unix a second server silently takes the name over.
/// </para>
/// <para>
/// When the claim cannot be made at all (an unwritable settings folder, for instance), the client
/// runs anyway: running twice is better than not running.
/// </para>
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    private const string ShowCommand = "show";

    private readonly IDisposable? _claim;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(IDisposable? claim, string pipeName)
    {
        _claim = claim;
        _pipeName = pipeName;
    }

    /// <summary>Raised on a background thread when another launch asks this client to show itself.</summary>
    public event Action? ActivationRequested;

    /// <summary>
    /// Claims the instance for this process. Returns null, after asking the running client to show
    /// its window, when another client already holds it (still, after <paramref name="wait"/>: a
    /// restart waits for the client it replaces to finish quitting).
    /// </summary>
    public static SingleInstance? TryClaim(TimeSpan wait = default)
    {
        var name = InstanceName();
        var deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        IDisposable? claim;
        try
        {
            while ((claim = OperatingSystem.IsWindows() ? ClaimMutex(name) : ClaimLockFile()) is null
                   && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(200);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            ClientLog.Error($"Cannot tell whether Discagem is already running ({ex.Message}); starting anyway.");
            return new SingleInstance(null, name);
        }

        if (claim is not null)
        {
            return new SingleInstance(claim, name);
        }

        AskRunningInstanceToShow(name);
        return null;
    }

    /// <summary>One name per user and profile, so that test profiles can still run side by side.</summary>
    private static string InstanceName()
    {
        var profile = Environment.GetEnvironmentVariable("DISCAGEM_PROFILE");
        var key = $"{Environment.UserName}|{(string.IsNullOrWhiteSpace(profile) ? "" : profile)}";
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return "Discagem-" + hash;
    }

    /// <summary>Windows: a named mutex, or null when another process owns it.</summary>
    private static IDisposable? ClaimMutex(string name)
    {
        var mutex = new Mutex(false, name);
        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // The previous client crashed; the mutex is ours now.
            owned = true;
        }

        if (owned)
        {
            return new OwnedMutex(mutex);
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// Elsewhere: an exclusive lock (flock) on a file in the settings folder, which is per user and
    /// profile, or null when another process holds it.
    /// </summary>
    private static FileStream? ClaimLockFile()
    {
        Directory.CreateDirectory(ClientSettings.Directory);
        try
        {
            return new FileStream(Path.Combine(ClientSettings.Directory, "instance.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (IsHeldByAnotherProcess(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Whether opening the lock file failed because another process holds the lock: .NET reports
    /// the errno of flock (EWOULDBLOCK: 11 on Linux, 35 on macOS) as the HRESULT on Unix.
    /// </summary>
    private static bool IsHeldByAnotherProcess(IOException ex) =>
        ex.HResult is 11 or 35 or unchecked((int)0x80070020);

    private static void AskRunningInstanceToShow(string pipeName)
    {
        if (OperatingSystem.IsWindows())
        {
            // Lets the running client bring its window to the front (only the foreground process may grant this).
            _ = AllowSetForegroundWindow(-1);
        }

        // The running client may still be starting up; give it a few seconds to open the pipe.
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(500);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(ShowCommand);
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>Starts answering other launches.</summary>
    public void Listen() => _ = Task.Run(ListenLoopAsync);

    private async Task ListenLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(_stop.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2), _stop.Token);
                if (line == ShowCommand)
                {
                    ActivationRequested?.Invoke();
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
            {
                // A client that went away mid-request; keep listening.
                await Task.Delay(100);
            }
        }
    }

    /// <summary>Releases the instance. Must be called on the thread that claimed it.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        _claim?.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>A mutex this process owns: released, then closed.</summary>
    private sealed class OwnedMutex(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned by this thread; the operating system releases it when the process exits.
            }

            mutex.Dispose();
        }
    }
}
