using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Discagem.Client.Services;

/// <summary>
/// Keeps one client running per user and profile. A second launch (from the Start menu, say, while
/// the first one sits in the notification area) asks the running client to show its window and exits.
/// </summary>
/// <remarks>
/// A named mutex tells whether a client is running; a named pipe, restricted to the current user,
/// carries the request to show the window. The pipe alone cannot be used for detection: on Unix a
/// second server silently takes the name over.
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    private const string ShowCommand = "show";

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    /// <summary>Raised on a background thread when another launch asks this client to show itself.</summary>
    public event Action? ActivationRequested;

    /// <summary>
    /// Claims the instance for this process. Returns null, after asking the running client to show
    /// its window, when another client already holds it.
    /// </summary>
    public static SingleInstance? TryClaim()
    {
        var name = InstanceName();

        // Unix scopes plain mutex names to the login session; "Global\" covers every terminal and launcher.
        var mutex = new Mutex(false, OperatingSystem.IsWindows() ? name : @"Global\" + name);
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
            return new SingleInstance(mutex, name);
        }

        mutex.Dispose();
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
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread; the operating system releases it when the process exits.
        }

        _mutex.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
