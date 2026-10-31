using System.Runtime.InteropServices;
using Avalonia;
using Discagem.Client.Services;

namespace Discagem.Client;

internal static class Program
{
    /// <summary>This process's claim to be the running client (null only while designing or testing).</summary>
    public static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        // A client is already running (perhaps in the notification area): it shows its window instead.
        using var instance = SingleInstance.TryClaim();
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
