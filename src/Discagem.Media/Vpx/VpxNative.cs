using System.Reflection;
using System.Runtime.InteropServices;

namespace Discagem.Media.Vpx;

/// <summary>
/// Minimal P/Invoke surface for libvpx (VP8 only).
/// </summary>
/// <remarks>
/// Only the stable prefix of the libvpx structures is accessed, and the opaque structures
/// (codec context, encoder configuration, image descriptor) are allocated as generously sized
/// unmanaged buffers. This keeps the binding compatible with the libvpx 1.9 build shipped for
/// Windows and with the 1.1x builds found on current Linux distributions.
/// </remarks>
internal static unsafe class VpxNative
{
    private const string LibraryName = "vpx";

    public const int VPX_CODEC_OK = 0;
    public const int VPX_CODEC_ABI_MISMATCH = 3;
    public const int VPX_CODEC_CX_FRAME_PKT = 0;
    public const int VPX_IMG_FMT_I420 = 0x102;
    public const int VPX_DL_REALTIME = 1;
    public const int VPX_EFLAG_FORCE_KF = 1;
    public const int VPX_RC_CBR = 1;
    public const int VPX_KF_AUTO = 1;
    public const uint VPX_ERROR_RESILIENT_DEFAULT = 1;

    public const int VP8E_SET_CPUUSED = 13;
    public const int VP8E_SET_NOISE_SENSITIVITY = 15;
    public const int VP8E_SET_STATIC_THRESHOLD = 17;
    public const int VP8E_SET_TOKEN_PARTITIONS = 18;
    public const int VP8E_SET_MAX_INTRA_BITRATE_PCT = 26;
    public const int VP8E_SET_SCREEN_CONTENT_MODE = 31;

    // Generous upper bounds for opaque structures (actual sizes on libvpx 1.14 x64: ctx 56, cfg 504, img 136).
    public const int CodecContextSize = 512;
    public const int EncoderConfigSize = 4096;
    public const int ImageSize = 512;

    // Offsets inside vpx_codec_enc_cfg_t. This prefix of the structure has been stable for many years.
    public static class EncCfg
    {
        public const int GThreads = 4;
        public const int GWidth = 12;
        public const int GHeight = 16;
        public const int GTimebaseNum = 28;
        public const int GTimebaseDen = 32;
        public const int GErrorResilient = 36;
        public const int GLagInFrames = 44;
        public const int RcDropframeThresh = 48;
        public const int RcResizeAllowed = 52;
        public const int RcEndUsage = 72;
        public const int RcTargetBitrate = 112;
        public const int RcMinQuantizer = 116;
        public const int RcMaxQuantizer = 120;
        public const int RcUndershootPct = 124;
        public const int RcOvershootPct = 128;
        public const int RcBufSz = 132;
        public const int RcBufInitialSz = 136;
        public const int RcBufOptimalSz = 140;
        public const int KfMode = 160;
        public const int KfMinDist = 164;
        public const int KfMaxDist = 168;
    }

    // Offsets inside vpx_image_t.
    public static class Img
    {
        public const int DisplayWidth = 24;
        public const int DisplayHeight = 28;
        public const int Planes = 48;
        public const int Strides = 80;
    }

    static VpxNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(VpxNative).Assembly, Resolve);
    }

    /// <summary>Forces the static constructor (and the import resolver) to run.</summary>
    public static void EnsureResolver()
    {
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName)
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in CandidateNames())
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle) ||
                NativeLibrary.TryLoad(candidate, out handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> CandidateNames()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return "vpxmd.dll";
            yield return "vpx.dll";
            yield return "libvpx.dll";
            yield return "libvpx-1.dll";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "libvpx.dylib";
            yield return "/opt/homebrew/lib/libvpx.dylib";
            yield return "/usr/local/lib/libvpx.dylib";
        }
        else
        {
            yield return "libvpx.so";
            yield return "libvpx.so.9";
            yield return "libvpx.so.8";
            yield return "libvpx.so.7";
            yield return "libvpx.so.6";
        }
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr vpx_codec_vp8_cx();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr vpx_codec_vp8_dx();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr vpx_codec_version_str();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr vpx_codec_err_to_string(int err);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr vpx_codec_error_detail(IntPtr ctx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vpx_codec_enc_config_default(IntPtr iface, IntPtr cfg, uint usage);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vpx_codec_enc_init_ver(IntPtr ctx, IntPtr iface, IntPtr cfg, CLong flags, int ver);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vpx_codec_enc_config_set(IntPtr ctx, IntPtr cfg);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vpx_codec_encode(IntPtr ctx, IntPtr img, long pts, CULong duration, CLong flags, CULong deadline);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr vpx_codec_get_cx_data(IntPtr ctx, ref IntPtr iter);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vpx_codec_dec_init_ver(IntPtr ctx, IntPtr iface, IntPtr cfg, CLong flags, int ver);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vpx_codec_decode(IntPtr ctx, byte* data, uint dataSize, IntPtr userPriv, CLong deadline);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr vpx_codec_get_frame(IntPtr ctx, ref IntPtr iter);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vpx_codec_destroy(IntPtr ctx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr vpx_img_wrap(IntPtr img, int fmt, uint width, uint height, uint strideAlign, IntPtr data);

    /// <summary>
    /// <c>vpx_codec_control_</c> is variadic in C. Integer variadic arguments are passed exactly like
    /// regular arguments on the supported x64 ABIs, so a fixed signature is used.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vpx_codec_control_(IntPtr ctx, int ctrlId, int value);

    public static string ErrorToString(int err) =>
        Marshal.PtrToStringAnsi(vpx_codec_err_to_string(err)) ?? $"error {err}";

    public static string Describe(IntPtr ctx, int err)
    {
        var detail = ctx == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(vpx_codec_error_detail(ctx));
        return string.IsNullOrEmpty(detail) ? ErrorToString(err) : $"{ErrorToString(err)} ({detail})";
    }

    public static IntPtr AllocZeroed(int size)
    {
        var ptr = (IntPtr)NativeMemory.AllocZeroed((nuint)size);
        return ptr;
    }

    public static void Free(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
        {
            NativeMemory.Free((void*)ptr);
        }
    }

    public static uint ReadU32(IntPtr basePtr, int offset) => *(uint*)((byte*)basePtr + offset);

    public static void WriteU32(IntPtr basePtr, int offset, uint value) => *(uint*)((byte*)basePtr + offset) = value;

    public static void WriteI32(IntPtr basePtr, int offset, int value) => *(int*)((byte*)basePtr + offset) = value;
}

/// <summary>Information about the libvpx build available at runtime.</summary>
public static class VpxLibrary
{
    private static readonly Lazy<string?> VersionLazy = new(() =>
    {
        try
        {
            VpxNative.EnsureResolver();
            return Marshal.PtrToStringAnsi(VpxNative.vpx_codec_version_str());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    });

    /// <summary>True when libvpx could be loaded.</summary>
    public static bool IsAvailable => VersionLazy.Value is not null;

    /// <summary>The libvpx version string (e.g. "v1.14.0"), or null if libvpx is not available.</summary>
    public static string? Version => VersionLazy.Value;
}
