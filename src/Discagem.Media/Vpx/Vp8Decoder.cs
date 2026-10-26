using System.Runtime.InteropServices;
using Discagem.Media.Imaging;

namespace Discagem.Media.Vpx;

/// <summary>VP8 decoder backed by libvpx that produces BGRA pictures.</summary>
public sealed unsafe class Vp8Decoder : IDisposable
{
    private readonly object _sync = new();
    private IntPtr _ctx;
    private IntPtr _cfg;
    private bool _disposed;

    public Vp8Decoder(int threads = 4)
    {
        VpxNative.EnsureResolver();
        _ctx = VpxNative.AllocZeroed(VpxNative.CodecContextSize);
        _cfg = VpxNative.AllocZeroed(64);
        VpxNative.WriteU32(_cfg, 0, (uint)Math.Clamp(threads, 1, 8));

        var iface = VpxNative.vpx_codec_vp8_dx();
        var lastErr = 0;
        var ok = false;
        foreach (var abi in AbiCandidates())
        {
            lastErr = VpxNative.vpx_codec_dec_init_ver(_ctx, iface, _cfg, new CLong(0), abi);
            if (lastErr == VpxNative.VPX_CODEC_OK)
            {
                ok = true;
                break;
            }

            if (lastErr != VpxNative.VPX_CODEC_ABI_MISMATCH)
            {
                break;
            }
        }

        if (!ok)
        {
            var message = VpxNative.Describe(_ctx, lastErr);
            VpxNative.Free(_ctx);
            VpxNative.Free(_cfg);
            _ctx = _cfg = IntPtr.Zero;
            throw new InvalidOperationException($"Could not initialise the VP8 decoder: {message}");
        }
    }

    private static IEnumerable<int> AbiCandidates()
    {
        yield return 12;
        for (var abi = 1; abi <= 32; abi++)
        {
            if (abi != 12)
            {
                yield return abi;
            }
        }
    }

    /// <summary>
    /// Decodes a frame and converts the result into <paramref name="target"/>, reallocating it when the
    /// picture size changes. Returns false if the frame could not be decoded (the caller should then
    /// request a key frame).
    /// </summary>
    public bool Decode(ReadOnlySpan<byte> frame, ref BgraFrame? target)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (frame.IsEmpty)
            {
                return false;
            }

            int err;
            fixed (byte* data = frame)
            {
                err = VpxNative.vpx_codec_decode(_ctx, data, (uint)frame.Length, IntPtr.Zero, new CLong(0));
            }

            if (err != VpxNative.VPX_CODEC_OK)
            {
                return false;
            }

            var produced = false;
            var iter = IntPtr.Zero;
            IntPtr img;
            while ((img = VpxNative.vpx_codec_get_frame(_ctx, ref iter)) != IntPtr.Zero)
            {
                var width = (int)VpxNative.ReadU32(img, VpxNative.Img.DisplayWidth);
                var height = (int)VpxNative.ReadU32(img, VpxNative.Img.DisplayHeight);
                if (width <= 0 || height <= 0)
                {
                    continue;
                }

                var planes = (IntPtr*)((byte*)img + VpxNative.Img.Planes);
                var strides = (int*)((byte*)img + VpxNative.Img.Strides);

                if (target is null || target.Width != width || target.Height != height)
                {
                    target = new BgraFrame(width, height);
                }

                PixelConverter.I420ToBgra(
                    (byte*)planes[0], strides[0],
                    (byte*)planes[1], strides[1],
                    (byte*)planes[2], strides[2],
                    width, height,
                    target);
                produced = true;
            }

            return produced;
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
                VpxNative.vpx_codec_destroy(_ctx);
            }

            VpxNative.Free(_ctx);
            VpxNative.Free(_cfg);
            _ctx = _cfg = IntPtr.Zero;
        }
    }
}
