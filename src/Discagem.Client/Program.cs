using System.Runtime.InteropServices;
using Avalonia;
using Discagem.Client.Services;

namespace Discagem.Client;

internal static class Program
{
    /// <summary>Passed by <see cref="App.Restart"/> to the client that replaces it.</summary>
    public const string RestartArgument = "--restart";

    /// <summary>This process's claim to be the running client (null only while designing or testing).</summary>
    public static SingleInstance? Instance { get; private set; }

    /// <summary>Whether the system started Discagem as the user signed in (see <see cref="Autostart"/>).</summary>
    public static bool StartedAtLogin { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        StartedAtLogin = args.Contains(Autostart.Argument);

        // A client is already running (perhaps in the notification area): it shows its window instead.
        // After a restart, the previous client may need a moment to finish quitting.
        var wait = args.Contains(RestartArgument) ? TimeSpan.FromSeconds(15) : TimeSpan.Zero;
        using var instance = SingleInstance.TryClaim(wait);
        if (instance is null)
        {
            return;
        }

        Instance = instance;
        if (OperatingSystem.IsWindows())
        {
            // 1 ms timer resolution keeps frame capture and packet pacing on schedule.
            _ = TimeBeginPeriod(1);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);
}
