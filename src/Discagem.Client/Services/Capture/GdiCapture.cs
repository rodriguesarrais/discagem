using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Discagem.Client.Resources;
using Discagem.Media.Pipeline;

namespace Discagem.Client.Services.Capture;

/// <summary>Windows screen and window capture using GDI (works on every Windows 10/11 machine).</summary>
[SupportedOSPlatform("windows")]
internal static class GdiCapture
{
    public static IReadOnlyList<CaptureSource> EnumerateScreens()
    {
        var monitors = new List<(Native.RECT Bounds, string Device, bool Primary)>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(monitor, ref info))
            {
                monitors.Add((info.rcMonitor, info.szDevice, (info.dwFlags & 1) != 0));
            }

            return true;
        }, IntPtr.Zero);

        var ordered = monitors.OrderByDescending(m => m.Primary).ThenBy(m => m.Bounds.Left).ToList();
        var sources = new List<CaptureSource>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var bounds = ordered[i].Bounds;
            var label = ordered.Count == 1
                ? Strings.Picker_EntireScreen
                : string.Format(ordered[i].Primary ? Strings.Picker_ScreenNumberPrimary : Strings.Picker_ScreenNumber, i + 1);
            sources.Add(new CaptureSource
            {
                Id = "screen:" + ordered[i].Device,
                Name = label,
                Kind = CaptureKind.Screen,
                Width = bounds.Width,
                Height = bounds.Height,
                Factory = () => new GdiScreenSource(label, bounds.Left, bounds.Top, bounds.Width, bounds.Height),
            });
        }

        return sources;
    }

    public static IReadOnlyList<CaptureSource> EnumerateWindows()
    {
        var ownProcess = (uint)Environment.ProcessId;
        var shell = Native.GetShellWindow();
        var sources = new List<CaptureSource>();

        Native.EnumWindows((hwnd, _) =>
        {
            if (hwnd == shell || !Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero)
            {
                return true;
            }

            var exStyle = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0 && (exStyle & Native.WS_EX_APPWINDOW) == 0)
            {
                return true;
            }

            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            Native.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == ownProcess)
            {
                return true;
            }

            var length = Native.GetWindowTextLength(hwnd);
            if (length == 0)
            {
                return true;
            }

            var title = new StringBuilder(length + 1);
            Native.GetWindowText(hwnd, title, title.Capacity);
            var bounds = GdiWindowSource.GetVisibleBounds(hwnd);
            if (bounds.Width < 64 || bounds.Height < 64)
            {
                return true;
            }

            var name = title.ToString();
            var handle = hwnd;
            sources.Add(new CaptureSource
            {
                Id = "window:" + hwnd.ToInt64(),
                Name = name,
                Kind = CaptureKind.Window,
                Width = bounds.Width,
                Height = bounds.Height,
                ProcessId = (int)processId,
                Factory = () => new GdiWindowSource(name, handle),
            });
            return true;
        }, IntPtr.Zero);

        return sources;
    }

    /// <summary>A memory DC with a top-down 32-bit DIB section, reallocated when the size changes.</summary>
    internal sealed class Surface : IDisposable
    {
        private IntPtr _memoryDc;
        private IntPtr _bitmap;
        private IntPtr _previous;

        public IntPtr Bits { get; private set; }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public IntPtr DeviceContext => _memoryDc;

        public void Ensure(IntPtr referenceDc, int width, int height)
        {
            if (_memoryDc != IntPtr.Zero && width == Width && height == Height)
            {
                return;
            }

            Release();
            _memoryDc = Native.CreateCompatibleDC(referenceDc);
            var info = new Native.BITMAPINFO
            {
                bmiHeader = new Native.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                },
            };
            _bitmap = Native.CreateDIBSection(referenceDc, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero)
            {
                Release();
                throw new InvalidOperationException(Strings.Stream_CaptureSurfaceFailed);
            }

            _previous = Native.SelectObject(_memoryDc, _bitmap);
            Bits = bits;
            Width = width;
            Height = height;
        }

        private void Release()
        {
            if (_memoryDc != IntPtr.Zero)
            {
                if (_previous != IntPtr.Zero)
                {
                    Native.SelectObject(_memoryDc, _previous);
                }

                Native.DeleteDC(_memoryDc);
            }

            if (_bitmap != IntPtr.Zero)
            {
                Native.DeleteObject(_bitmap);
            }

            _memoryDc = _bitmap = _previous = IntPtr.Zero;
            Bits = IntPtr.Zero;
            Width = Height = 0;
        }

        public void Dispose() => Release();
    }

    /// <summary>Draws the mouse pointer onto a surface whose top-left corner is at (originX, originY) on screen.</summary>
    internal static void DrawCursor(IntPtr dc, int originX, int originY)
    {
        var cursor = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
        if (!Native.GetCursorInfo(ref cursor) || (cursor.flags & Native.CURSOR_SHOWING) == 0 || cursor.hCursor == IntPtr.Zero)
        {
            return;
        }

        if (!Native.GetIconInfo(cursor.hCursor, out var icon))
        {
            return;
        }

        try
        {
            var x = cursor.ptScreenPos.X - originX - icon.xHotspot;
            var y = cursor.ptScreenPos.Y - originY - icon.yHotspot;
            Native.DrawIconEx(dc, x, y, cursor.hCursor, 0, 0, 0, IntPtr.Zero, Native.DI_NORMAL);
        }
        finally
        {
            if (icon.hbmMask != IntPtr.Zero)
            {
                Native.DeleteObject(icon.hbmMask);
            }

            if (icon.hbmColor != IntPtr.Zero)
            {
                Native.DeleteObject(icon.hbmColor);
            }
        }
    }

    internal static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const long WS_EX_TOOLWINDOW = 0x00000080;
        public const long WS_EX_APPWINDOW = 0x00040000;
        public const uint GW_OWNER = 4;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        public const int DWMWA_CLOAKED = 14;
        public const uint SRCCOPY = 0x00CC0020;
        public const uint CAPTUREBLT = 0x40000000;
        public const uint PW_RENDERFULLCONTENT = 0x00000002;
        public const int CURSOR_SHOWING = 0x00000001;
        public const uint DI_NORMAL = 0x0003;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;

            public readonly int Width => Right - Left;

            public readonly int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hwnd, uint command);

        [DllImport("user32.dll")]
        public static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
        public static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
        public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("user32.dll")]
        public static extern bool GetCursorInfo(ref CURSORINFO info);

        [DllImport("user32.dll")]
        public static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);

        [DllImport("user32.dll")]
        public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint rop);

        [DllImport("gdi32.dll")]
        public static extern bool GdiFlush();

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    }
}

/// <summary>Captures a rectangle of the virtual desktop (one monitor).</summary>
[SupportedOSPlatform("windows")]
internal sealed class GdiScreenSource(string name, int x, int y, int width, int height) : IFrameSource
{
    private readonly GdiCapture.Surface _surface = new();

    public string Name { get; } = name;

    public bool TryCapture(out CapturedFrame frame)
    {
        frame = default;
        var screenDc = GdiCapture.Native.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            _surface.Ensure(screenDc, width, height);
            if (!GdiCapture.Native.BitBlt(_surface.DeviceContext, 0, 0, width, height, screenDc, x, y, GdiCapture.Native.SRCCOPY | GdiCapture.Native.CAPTUREBLT))
            {
                return false;
            }

            GdiCapture.DrawCursor(_surface.DeviceContext, x, y);
            GdiCapture.Native.GdiFlush();
            frame = new CapturedFrame(_surface.Bits, width, height, width * 4);
            return true;
        }
        finally
        {
            GdiCapture.Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public void Dispose() => _surface.Dispose();
}

/// <summary>Captures a single top-level window, including when it is partially covered.</summary>
[SupportedOSPlatform("windows")]
internal sealed class GdiWindowSource(string name, IntPtr hwnd) : IFrameSource
{
    private readonly GdiCapture.Surface _surface = new();

    public string Name { get; } = name;

    /// <summary>The window bounds without the invisible resize borders Windows 10/11 adds.</summary>
    public static GdiCapture.Native.RECT GetVisibleBounds(IntPtr hwnd)
    {
        if (GdiCapture.Native.DwmGetWindowAttribute(hwnd, GdiCapture.Native.DWMWA_EXTENDED_FRAME_BOUNDS, out GdiCapture.Native.RECT bounds, Marshal.SizeOf<GdiCapture.Native.RECT>()) == 0)
        {
            return bounds;
        }

        GdiCapture.Native.GetWindowRect(hwnd, out bounds);
        return bounds;
    }

    public bool TryCapture(out CapturedFrame frame)
    {
        frame = default;
        if (!GdiCapture.Native.IsWindow(hwnd) || GdiCapture.Native.IsIconic(hwnd) || !GdiCapture.Native.GetWindowRect(hwnd, out var windowRect))
        {
            return false;
        }

        var visible = GetVisibleBounds(hwnd);
        var width = windowRect.Width;
        var height = windowRect.Height;
        if (width < 16 || height < 16)
        {
            return false;
        }

        var screenDc = GdiCapture.Native.GetDC(IntPtr.Zero);
        try
        {
            _surface.Ensure(screenDc, width, height);
        }
        finally
        {
            GdiCapture.Native.ReleaseDC(IntPtr.Zero, screenDc);
        }

        if (!GdiCapture.Native.PrintWindow(hwnd, _surface.DeviceContext, GdiCapture.Native.PW_RENDERFULLCONTENT))
        {
            return false;
        }

        GdiCapture.DrawCursor(_surface.DeviceContext, windowRect.Left, windowRect.Top);
        GdiCapture.Native.GdiFlush();

        // Crop away the transparent resize borders.
        var offsetX = Math.Clamp(visible.Left - windowRect.Left, 0, width - 16);
        var offsetY = Math.Clamp(visible.Top - windowRect.Top, 0, height - 16);
        var cropWidth = Math.Clamp(visible.Width, 16, width - offsetX);
        var cropHeight = Math.Clamp(visible.Height, 16, height - offsetY);
        var stride = width * 4;
        frame = new CapturedFrame(_surface.Bits + offsetY * stride + offsetX * 4, cropWidth, cropHeight, stride);
        Debug.Assert(cropWidth > 0 && cropHeight > 0);
        return true;
    }

    public void Dispose() => _surface.Dispose();
}
