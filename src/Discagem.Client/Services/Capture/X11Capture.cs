using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Discagem.Media.Pipeline;

namespace Discagem.Client.Services.Capture;

/// <summary>Screen capture for X11 sessions using XGetSubImage (and XRandR to list monitors).</summary>
[SupportedOSPlatform("linux")]
internal static unsafe class X11Capture
{
    public static IReadOnlyList<CaptureSource> EnumerateScreens()
    {
        var display = Native.XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            var screen = Native.XDefaultScreen(display);
            var rootWidth = Native.XDisplayWidth(display, screen);
            var rootHeight = Native.XDisplayHeight(display, screen);
            var monitors = GetMonitors(display);

            var sources = new List<CaptureSource>();
            if (monitors.Count > 1)
            {
                for (var i = 0; i < monitors.Count; i++)
                {
                    var m = monitors[i];
                    var label = $"Screen {i + 1}{(m.Primary ? " (primary)" : "")}";
                    sources.Add(new CaptureSource
                    {
                        Id = $"x11:{m.X},{m.Y},{m.Width},{m.Height}",
                        Name = label,
                        Kind = CaptureKind.Screen,
                        Width = m.Width,
                        Height = m.Height,
                        Factory = () => new X11ScreenSource(label, m.X, m.Y, m.Width, m.Height),
                    });
                }
            }

            sources.Add(new CaptureSource
            {
                Id = "x11:root",
                Name = monitors.Count > 1 ? "All screens" : "Entire screen",
                Kind = CaptureKind.Screen,
                Width = rootWidth,
                Height = rootHeight,
                Factory = () => new X11ScreenSource("Entire screen", 0, 0, rootWidth, rootHeight),
            });
            return sources;
        }
        finally
        {
            Native.XCloseDisplay(display);
        }
    }

    private readonly record struct Monitor(int X, int Y, int Width, int Height, bool Primary);

    private static List<Monitor> GetMonitors(IntPtr display)
    {
        var result = new List<Monitor>();
        try
        {
            var root = Native.XDefaultRootWindow(display);
            var monitors = Native.XRRGetMonitors(display, root, 1, out var count);
            if (monitors == IntPtr.Zero)
            {
                return result;
            }

            // XRRMonitorInfo: Atom name; Bool primary; Bool automatic; int noutput; int x, y, width, height, mwidth, mheight; RROutput* outputs.
            const int size = 56;
            for (var i = 0; i < count; i++)
            {
                var p = (byte*)monitors + i * size;
                var primary = *(int*)(p + 8) != 0;
                var x = *(int*)(p + 20);
                var y = *(int*)(p + 24);
                var width = *(int*)(p + 28);
                var height = *(int*)(p + 32);
                if (width > 0 && height > 0)
                {
                    result.Add(new Monitor(x, y, width, height, primary));
                }
            }

            Native.XRRFreeMonitors(monitors);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // XRandR is optional; fall back to the whole root window.
        }

        return result.OrderByDescending(m => m.Primary).ThenBy(m => m.X).ToList();
    }

    internal static class Native
    {
        private const string LibX11 = "libX11.so.6";
        private const string LibXrandr = "libXrandr.so.2";

        public const int ZPixmap = 2;

        [DllImport(LibX11)]
        public static extern IntPtr XOpenDisplay(IntPtr name);

        [DllImport(LibX11)]
        public static extern int XCloseDisplay(IntPtr display);

        [DllImport(LibX11)]
        public static extern int XDefaultScreen(IntPtr display);

        [DllImport(LibX11)]
        public static extern IntPtr XDefaultRootWindow(IntPtr display);

        [DllImport(LibX11)]
        public static extern IntPtr XDefaultVisual(IntPtr display, int screen);

        [DllImport(LibX11)]
        public static extern int XDefaultDepth(IntPtr display, int screen);

        [DllImport(LibX11)]
        public static extern int XDisplayWidth(IntPtr display, int screen);

        [DllImport(LibX11)]
        public static extern int XDisplayHeight(IntPtr display, int screen);

        [DllImport(LibX11)]
        public static extern IntPtr XCreateImage(IntPtr display, IntPtr visual, uint depth, int format, int offset, IntPtr data, uint width, uint height, int bitmapPad, int bytesPerLine);

        [DllImport(LibX11)]
        public static extern IntPtr XGetSubImage(IntPtr display, IntPtr drawable, int x, int y, uint width, uint height, nuint planeMask, int format, IntPtr destImage, int destX, int destY);

        [DllImport(LibX11)]
        public static extern int XFree(IntPtr data);

        [DllImport(LibXrandr)]
        public static extern IntPtr XRRGetMonitors(IntPtr display, IntPtr window, int getActive, out int count);

        [DllImport(LibXrandr)]
        public static extern void XRRFreeMonitors(IntPtr monitors);
    }
}

/// <summary>Captures a region of the X11 root window into a reusable buffer.</summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class X11ScreenSource : IFrameSource
{
    // XImage field offsets on 64-bit Linux.
    private const int XImageBytesPerLine = 44;
    private const int XImageBitsPerPixel = 48;

    private readonly int _x;
    private readonly int _y;
    private readonly int _width;
    private readonly int _height;
    private IntPtr _display;
    private IntPtr _root;
    private IntPtr _image;
    private byte* _buffer;
    private int _stride;

    public X11ScreenSource(string name, int x, int y, int width, int height)
    {
        Name = name;
        _x = x;
        _y = y;
        _width = width & ~1;
        _height = height & ~1;
    }

    public string Name { get; }

    public bool TryCapture(out CapturedFrame frame)
    {
        frame = default;
        if (_display == IntPtr.Zero && !Open())
        {
            return false;
        }

        var result = X11Capture.Native.XGetSubImage(_display, _root, _x, _y, (uint)_width, (uint)_height, nuint.MaxValue, X11Capture.Native.ZPixmap, _image, 0, 0);
        if (result == IntPtr.Zero)
        {
            return false;
        }

        frame = new CapturedFrame((IntPtr)_buffer, _width, _height, _stride);
        return true;
    }

    private bool Open()
    {
        // A private connection, used only from the capture thread.
        _display = X11Capture.Native.XOpenDisplay(IntPtr.Zero);
        if (_display == IntPtr.Zero)
        {
            return false;
        }

        var screen = X11Capture.Native.XDefaultScreen(_display);
        _root = X11Capture.Native.XDefaultRootWindow(_display);
        var visual = X11Capture.Native.XDefaultVisual(_display, screen);
        var depth = X11Capture.Native.XDefaultDepth(_display, screen);
        if (depth < 24)
        {
            return false;
        }

        _stride = _width * 4;
        _buffer = (byte*)NativeMemory.AllocZeroed((nuint)(_stride * _height));
        _image = X11Capture.Native.XCreateImage(_display, visual, (uint)depth, X11Capture.Native.ZPixmap, 0, (IntPtr)_buffer, (uint)_width, (uint)_height, 32, _stride);
        if (_image == IntPtr.Zero || *(int*)((byte*)_image + XImageBitsPerPixel) != 32)
        {
            return false;
        }

        _stride = *(int*)((byte*)_image + XImageBytesPerLine);
        return true;
    }

    public void Dispose()
    {
        if (_image != IntPtr.Zero)
        {
            // The pixel buffer is ours; only free the XImage header.
            X11Capture.Native.XFree(_image);
            _image = IntPtr.Zero;
        }

        if (_buffer != null)
        {
            NativeMemory.Free(_buffer);
            _buffer = null;
        }

        if (_display != IntPtr.Zero)
        {
            X11Capture.Native.XCloseDisplay(_display);
            _display = IntPtr.Zero;
        }
    }
}
