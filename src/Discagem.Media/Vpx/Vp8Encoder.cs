using System.Runtime.InteropServices;
using Discagem.Media.Imaging;

namespace Discagem.Media.Vpx;

/// <summary>Settings for <see cref="Vp8Encoder"/>.</summary>
public sealed record Vp8EncoderOptions(
    int Width,
    int Height,
    int FrameRate,
    int BitrateKbps,
    bool ScreenContent = true,
    int? Threads = null);

/// <summary>An encoded VP8 frame.</summary>
public readonly record struct EncodedFrame(byte[] Data, bool IsKeyFrame);

/// <summary>
/// Real-time VP8 encoder backed by libvpx, tuned for screen content.
/// Callers write an I420 picture into <see cref="InputBuffer"/> and then call <see cref="Encode"/>.
/// </summary>
public sealed unsafe class Vp8Encoder : IDisposable
{
    private readonly object _sync = new();
    private IntPtr _ctx;
    private IntPtr _cfg;
    private IntPtr _img;
    private IntPtr _frameBuffer;
    private readonly int _frameBufferSize;
    private bool _forceKeyFrame = true;
    private long _frameIndex;
    private bool _disposed;

    public Vp8Encoder(Vp8EncoderOptions options)
    {
        if (options.Width < 16 || options.Height < 16 || options.Width % 2 != 0 || options.Height % 2 != 0)
        {
            throw new ArgumentException("Width and height must be even and at least 16 pixels.", nameof(options));
        }

        Options = options;
        VpxNative.EnsureResolver();

        _frameBufferSize = I420Layout.BufferSize(options.Width, options.Height);
        _frameBuffer = VpxNative.AllocZeroed(_frameBufferSize);
        _ctx = VpxNative.AllocZeroed(VpxNative.CodecContextSize);
        _cfg = VpxNative.AllocZeroed(VpxNative.EncoderConfigSize);
        _img = VpxNative.AllocZeroed(VpxNative.ImageSize);

        try
        {
            Initialise();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public Vp8EncoderOptions Options { get; private set; }

    public int Width => Options.Width;

    public int Height => Options.Height;

    /// <summary>The I420 input picture: Y plane, then U plane, then V plane, tightly packed.</summary>
    public Span<byte> InputBuffer => new((void*)_frameBuffer, _frameBufferSize);

    private void Initialise()
    {
        var iface = VpxNative.vpx_codec_vp8_cx();
        var err = VpxNative.vpx_codec_enc_config_default(iface, _cfg, 0);
        if (err != VpxNative.VPX_CODEC_OK)
        {
            throw new InvalidOperationException($"vpx_codec_enc_config_default failed: {VpxNative.ErrorToString(err)}");
        }

        // libvpx worker threads spin-wait on each other; on a busy machine more threads cost more
        // CPU than they save, so use at most half of the cores.
        var threads = Math.Clamp(Options.Threads ?? Environment.ProcessorCount / 2, 1, 4);
        VpxNative.WriteU32(_cfg, VpxNative.EncCfg.GThreads, (uint)threads);
        VpxNative.WriteU32(_cfg, VpxNative.EncCfg.GWidth, (uint)Options.Width);
        VpxNative.WriteU32(_cfg, VpxNative.EncCfg.GHeight, (uint)Options.Height);
        VpxNative.WriteI32(_cfg, VpxNative.EncCfg.GTimebaseNum, 1);
        VpxNative.WriteI32(_cfg, VpxNative.EncCfg.GTimebaseDen, 1000);
        VpxNative.WriteU32(_cfg, VpxNative.EncCfg.GErrorResilient, VpxNative.VPX_ERROR_RESILIENT_DEFAULT);
        VpxNative.WriteU32(_cfg, VpxNative.EncCfg.GLagInFrames, 0);
        VpxNative.WriteU32(_cfg, VpxNative.EncCfg.RcDropframeThresh, Options.ScreenContent ? 25u : 0u);
        VpxNative.WriteU32(_cfg, VpxNative.EncCfg.RcResizeAllowed, 0);
        VpxNative.WriteI32(_cfg, VpxNative.EncCfg.RcEndUsage, VpxNative.VPX_RC_CBR);
        ApplyBitrate(Options.BitrateKbps);
        VpxNative.WriteU32(_cfg, VpxNative.EncCfg.RcMinQuantizer, 2);
        VpxNative.WriteU32(_cfg, VpxNative.EncCfg.RcMaxQuantizer, 52);
        VpxNative.WriteU32(_cfg, VpxNative.EncCfg.RcUndershootPct, 100);
        VpxNative.WriteU32(_cfg, VpxNative.EncCfg.RcOvershootPct, 15);
        VpxNative.WriteU32(_cfg, VpxNative.EncCfg.RcBufSz, 1000);
        VpxNative.WriteU32(_cfg, VpxNative.EncCfg.RcBufInitialSz, 500);
        VpxNative.WriteU32(_cfg, VpxNative.EncCfg.RcBufOptimalSz, 600);
        VpxNative.WriteI32(_cfg, VpxNative.EncCfg.KfMode, VpxNative.VPX_KF_AUTO);
        VpxNative.WriteU32(_cfg, VpxNative.EncCfg.KfMinDist, 0);
        // Periodic key frames are a safety net only; receivers request key frames (PLI) when needed.
        VpxNative.WriteU32(_cfg, VpxNative.EncCfg.KfMaxDist, (uint)Math.Max(Options.FrameRate * 10, 30));

        InitWithAbiProbe(iface);

        var pixels = Options.Width * Options.Height;
        var cpuUsed = pixels >= 1920 * 1080 ? -10 : pixels >= 1280 * 720 ? -8 : -6;
        Control(VpxNative.VP8E_SET_CPUUSED, cpuUsed);
        Control(VpxNative.VP8E_SET_NOISE_SENSITIVITY, 0);
        Control(VpxNative.VP8E_SET_TOKEN_PARTITIONS, 0);
        Control(VpxNative.VP8E_SET_MAX_INTRA_BITRATE_PCT, 600);
        if (Options.ScreenContent)
        {
            Control(VpxNative.VP8E_SET_STATIC_THRESHOLD, 100);
            Control(VpxNative.VP8E_SET_SCREEN_CONTENT_MODE, 1);
        }

        var wrapped = VpxNative.vpx_img_wrap(_img, VpxNative.VPX_IMG_FMT_I420, (uint)Options.Width, (uint)Options.Height, 1, _frameBuffer);
        if (wrapped == IntPtr.Zero)
        {
            throw new InvalidOperationException("vpx_img_wrap failed.");
        }
    }

    private void InitWithAbiProbe(IntPtr iface)
    {
        // The encoder ABI version differs between libvpx releases (e.g. 23 for 1.9, 34 for 1.14).
        // A mismatching version is rejected without side effects, so probing is safe.
        var lastErr = 0;
        foreach (var abi in AbiCandidates())
        {
            lastErr = VpxNative.vpx_codec_enc_init_ver(_ctx, iface, _cfg, new CLong(0), abi);
            if (lastErr == VpxNative.VPX_CODEC_OK)
            {
                return;
            }

            if (lastErr != VpxNative.VPX_CODEC_ABI_MISMATCH)
            {
                break;
            }
        }

        throw new InvalidOperationException($"Could not initialise the VP8 encoder: {VpxNative.Describe(_ctx, lastErr)}");
    }

    internal static IEnumerable<int> AbiCandidates()
    {
        int[] known = [34, 23, 33, 32, 31, 30, 29, 28, 27, 26, 25, 24, 22, 21, 20];
        foreach (var abi in known)
        {
            yield return abi;
        }

        for (var abi = 35; abi <= 64; abi++)
        {
            yield return abi;
        }
    }

    private void Control(int id, int value)
    {
        var err = VpxNative.vpx_codec_control_(_ctx, id, value);
        if (err != VpxNative.VPX_CODEC_OK)
        {
            // Unsupported controls on older builds are not fatal.
            System.Diagnostics.Debug.WriteLine($"VP8 control {id}={value} failed: {VpxNative.ErrorToString(err)}");
        }
    }

    private void ApplyBitrate(int kbps)
    {
        VpxNative.WriteU32(_cfg, VpxNative.EncCfg.RcTargetBitrate, (uint)Math.Clamp(kbps, 100, 50_000));
    }

    /// <summary>Changes the target bitrate without restarting the encoder.</summary>
    public void SetBitrate(int kbps)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ApplyBitrate(kbps);
            var err = VpxNative.vpx_codec_enc_config_set(_ctx, _cfg);
            if (err != VpxNative.VPX_CODEC_OK)
            {
                throw new InvalidOperationException($"vpx_codec_enc_config_set failed: {VpxNative.Describe(_ctx, err)}");
            }

            Options = Options with { BitrateKbps = kbps };
        }
    }

    /// <summary>Makes the next encoded frame a key frame.</summary>
    public void RequestKeyFrame() => _forceKeyFrame = true;

    /// <summary>
    /// Encodes the picture currently stored in <see cref="InputBuffer"/>.
    /// Returns null if the rate controller decided to drop the frame.
    /// </summary>
    public EncodedFrame? Encode(long timestampMs)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var flags = _forceKeyFrame ? VpxNative.VPX_EFLAG_FORCE_KF : 0;
            _forceKeyFrame = false;
            var duration = (uint)Math.Max(1, 1000 / Math.Max(1, Options.FrameRate));
            var err = VpxNative.vpx_codec_encode(_ctx, _img, timestampMs, new CULong(duration), new CLong(flags), new CULong(VpxNative.VPX_DL_REALTIME));
            if (err != VpxNative.VPX_CODEC_OK)
            {
                throw new InvalidOperationException($"vpx_codec_encode failed: {VpxNative.Describe(_ctx, err)}");
            }

            _frameIndex++;
            byte[]? output = null;
            var iter = IntPtr.Zero;
            IntPtr pkt;
            while ((pkt = VpxNative.vpx_codec_get_cx_data(_ctx, ref iter)) != IntPtr.Zero)
            {
                var kind = *(int*)pkt;
                if (kind != VpxNative.VPX_CODEC_CX_FRAME_PKT)
                {
                    continue;
                }

                var buf = *(IntPtr*)((byte*)pkt + 8);
                var size = (int)*(nuint*)((byte*)pkt + 16);
                if (output is null)
                {
                    output = new byte[size];
                    Marshal.Copy(buf, output, 0, size);
                }
                else
                {
                    var merged = new byte[output.Length + size];
                    output.CopyTo(merged, 0);
                    Marshal.Copy(buf, merged, output.Length, size);
                    output = merged;
                }
            }

            if (output is null || output.Length == 0)
            {
                return null;
            }

            return new EncodedFrame(output, Vp8Bitstream.IsKeyFrame(output));
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_ctx != IntPtr.Zero)
            {
                // Destroying a context that failed to initialise is harmless (it returns an error).
                VpxNative.vpx_codec_destroy(_ctx);
            }

            VpxNative.Free(_ctx);
            VpxNative.Free(_cfg);
            VpxNative.Free(_img);
            VpxNative.Free(_frameBuffer);
            _ctx = _cfg = _img = _frameBuffer = IntPtr.Zero;
        }
    }
}

/// <summary>Helpers for inspecting raw VP8 bitstreams.</summary>
public static class Vp8Bitstream
{
    /// <summary>Bit 0 of the VP8 frame tag is 0 for key frames (RFC 6386, section 9.1).</summary>
    public static bool IsKeyFrame(ReadOnlySpan<byte> frame) => frame.Length > 0 && (frame[0] & 0x01) == 0;

    /// <summary>Reads the picture size from a key frame header, if present.</summary>
    public static bool TryGetKeyFrameSize(ReadOnlySpan<byte> frame, out int width, out int height)
    {
        width = height = 0;
        if (frame.Length < 10 || !IsKeyFrame(frame) || frame[3] != 0x9d || frame[4] != 0x01 || frame[5] != 0x2a)
        {
            return false;
        }

        width = (frame[6] | (frame[7] << 8)) & 0x3fff;
        height = (frame[8] | (frame[9] << 8)) & 0x3fff;
        return true;
    }
}
