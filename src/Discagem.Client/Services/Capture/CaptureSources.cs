using Discagem.Client.Resources;
using Discagem.Media.Pipeline;

namespace Discagem.Client.Services.Capture;

public enum CaptureKind
{
    Screen,
    Window,
}

/// <summary>Something the user can share: a monitor or an application window.</summary>
public sealed class CaptureSource
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required CaptureKind Kind { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>For windows: the process that owns it, whose audio is shared with it.</summary>
    public int? ProcessId { get; init; }

    public required Func<IFrameSource> Factory { get; init; }

    public IFrameSource Create() => Factory();
}

/// <summary>Enumerates capture sources for the current platform.</summary>
public static class CaptureSources
{
    public static bool IsSupported =>
        OperatingSystem.IsWindows() ||
        (OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")));

    public static bool SupportsWindows => OperatingSystem.IsWindows();

    public static string UnsupportedReason =>
        OperatingSystem.IsLinux()
            ? Strings.Picker_UnsupportedLinux
            : Strings.Picker_UnsupportedPlatform;

    public static IReadOnlyList<CaptureSource> GetScreens()
    {
        if (OperatingSystem.IsWindows())
        {
            return GdiCapture.EnumerateScreens();
        }

        if (OperatingSystem.IsLinux())
        {
            return X11Capture.EnumerateScreens();
        }

        return [];
    }

    public static IReadOnlyList<CaptureSource> GetWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return GdiCapture.EnumerateWindows();
        }

        return [];
    }
}
