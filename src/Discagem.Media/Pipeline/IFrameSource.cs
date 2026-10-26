namespace Discagem.Media.Pipeline;

/// <summary>A BGRA picture owned by a frame source; valid until the next capture call.</summary>
public readonly record struct CapturedFrame(IntPtr Data, int Width, int Height, int Stride);

/// <summary>Something that can be captured repeatedly (a monitor, a window, a test pattern).</summary>
public interface IFrameSource : IDisposable
{
    string Name { get; }

    /// <summary>
    /// Captures the current picture. Returns false when nothing can be captured right now
    /// (e.g. the window is minimised).
    /// </summary>
    bool TryCapture(out CapturedFrame frame);
}

/// <summary>Resolution and frame rate requested for a screen share.</summary>
public sealed record StreamQuality(string Label, int MaxHeight, int FrameRate)
{
    public static readonly StreamQuality[] Presets =
    [
        new("720p · 30 FPS", 720, 30),
        new("720p · 60 FPS", 720, 60),
        new("1080p · 30 FPS", 1080, 30),
        new("1080p · 60 FPS", 1080, 60),
        new("1440p · 30 FPS", 1440, 30),
        new("Source · 30 FPS", 0, 30),
    ];

    public static StreamQuality Default => Presets[2];

    /// <summary>Target bitrate for screen content at the given resolution.</summary>
    public int BitrateKbpsFor(int width, int height)
    {
        // Frame rates above 30 cost less than linearly, since consecutive frames are more similar.
        var effectiveFps = FrameRate <= 30 ? FrameRate : 30 + (FrameRate - 30) * 0.5;
        var kbps = width * (double)height * effectiveFps * 0.075 / 1000.0;
        return (int)Math.Clamp(kbps, 1200, 20000);
    }

    public override string ToString() => Label;
}
