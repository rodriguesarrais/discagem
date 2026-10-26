using System.Runtime.InteropServices;

namespace Discagem.Media.Imaging;

/// <summary>
/// Downscales BGRA pictures. Large reductions use repeated 2x2 box filtering (sharp text, no aliasing)
/// followed by a bilinear pass to reach the exact target size. Intermediate buffers are reused.
/// </summary>
public sealed unsafe class BgraScaler : IDisposable
{
    private byte* _bufferA;
    private byte* _bufferB;
    private nuint _bufferSize;
    private int[] _xIndex = [];
    private int[] _xFrac = [];
    private int _cachedSrcWidth;
    private int _cachedDstWidth;

    /// <summary>
    /// Scales <paramref name="src"/> into <paramref name="dst"/> (which must hold dstHeight * dstStride bytes).
    /// </summary>
    public void Scale(byte* src, int srcWidth, int srcHeight, int srcStride, byte* dst, int dstWidth, int dstHeight, int dstStride)
    {
        if (srcWidth == dstWidth && srcHeight == dstHeight)
        {
            for (var y = 0; y < srcHeight; y++)
            {
                Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * dstStride, dstStride, (long)dstWidth * 4);
            }

            return;
        }

        var curr = src;
        int currWidth = srcWidth, currHeight = srcHeight, currStride = srcStride;
        var useA = true;

        while (currWidth >= dstWidth * 2 && currHeight >= dstHeight * 2)
        {
            var halfWidth = currWidth / 2;
            var halfHeight = currHeight / 2;
            EnsureBuffers((nuint)halfWidth * (nuint)halfHeight * 4);
            var target = useA ? _bufferA : _bufferB;
            HalveBox(curr, currWidth, currHeight, currStride, target, halfWidth, halfHeight);
            curr = target;
            currWidth = halfWidth;
            currHeight = halfHeight;
            currStride = halfWidth * 4;
            useA = !useA;
        }

        if (currWidth == dstWidth && currHeight == dstHeight)
        {
            for (var y = 0; y < dstHeight; y++)
            {
                Buffer.MemoryCopy(curr + (long)y * currStride, dst + (long)y * dstStride, dstStride, (long)dstWidth * 4);
            }

            return;
        }

        Bilinear(curr, currWidth, currHeight, currStride, dst, dstWidth, dstHeight, dstStride);
    }

    private void EnsureBuffers(nuint size)
    {
        if (_bufferSize >= size)
        {
            return;
        }

        FreeBuffers();
        _bufferA = (byte*)NativeMemory.Alloc(size);
        _bufferB = (byte*)NativeMemory.Alloc(size);
        _bufferSize = size;
    }

    private static void HalveBox(byte* src, int srcWidth, int srcHeight, int srcStride, byte* dst, int dstWidth, int dstHeight)
    {
        var srcBase = (nint)src;
        var dstBase = (nint)dst;
        var dstStride = dstWidth * 4;
        var chunks = Math.Clamp(Math.Min(Environment.ProcessorCount, dstHeight / 16), 1, 16);

        Parallel.For(0, chunks, chunk =>
        {
            var start = dstHeight * chunk / chunks;
            var end = dstHeight * (chunk + 1) / chunks;
            for (var y = start; y < end; y++)
            {
                var r0 = (byte*)srcBase + (long)(y * 2) * srcStride;
                var r1 = r0 + srcStride;
                var o = (byte*)dstBase + (long)y * dstStride;
                for (var x = 0; x < dstWidth; x++)
                {
                    var a = r0 + x * 8;
                    var b = r1 + x * 8;
                    o[0] = (byte)((a[0] + a[4] + b[0] + b[4] + 2) >> 2);
                    o[1] = (byte)((a[1] + a[5] + b[1] + b[5] + 2) >> 2);
                    o[2] = (byte)((a[2] + a[6] + b[2] + b[6] + 2) >> 2);
                    o[3] = 255;
                    o += 4;
                }
            }
        });
    }

    private void Bilinear(byte* src, int srcWidth, int srcHeight, int srcStride, byte* dst, int dstWidth, int dstHeight, int dstStride)
    {
        if (_cachedSrcWidth != srcWidth || _cachedDstWidth != dstWidth)
        {
            _xIndex = new int[dstWidth];
            _xFrac = new int[dstWidth];
            for (var x = 0; x < dstWidth; x++)
            {
                var sx = (x + 0.5) * srcWidth / dstWidth - 0.5;
                if (sx < 0)
                {
                    sx = 0;
                }

                var ix = (int)sx;
                if (ix >= srcWidth - 1)
                {
                    ix = Math.Max(0, srcWidth - 2);
                    sx = srcWidth - 1;
                }

                _xIndex[x] = ix;
                _xFrac[x] = (int)((sx - ix) * 256);
            }

            _cachedSrcWidth = srcWidth;
            _cachedDstWidth = dstWidth;
        }

        var xIndex = _xIndex;
        var xFrac = _xFrac;
        var srcBase = (nint)src;
        var dstBase = (nint)dst;
        var lastSrcColumn = srcWidth - 1;
        var chunks = Math.Clamp(Math.Min(Environment.ProcessorCount, dstHeight / 16), 1, 16);

        Parallel.For(0, chunks, chunk =>
        {
            var start = dstHeight * chunk / chunks;
            var end = dstHeight * (chunk + 1) / chunks;
            for (var y = start; y < end; y++)
            {
                var sy = (y + 0.5) * srcHeight / dstHeight - 0.5;
                if (sy < 0)
                {
                    sy = 0;
                }

                var iy = (int)sy;
                if (iy >= srcHeight - 1)
                {
                    iy = Math.Max(0, srcHeight - 2);
                    sy = srcHeight - 1;
                }

                var fy = (int)((sy - iy) * 256);
                var r0 = (byte*)srcBase + (long)iy * srcStride;
                var r1 = srcHeight > 1 ? r0 + srcStride : r0;
                var o = (byte*)dstBase + (long)y * dstStride;

                for (var x = 0; x < dstWidth; x++)
                {
                    var ix = xIndex[x];
                    var fx = xFrac[x];
                    var ix1 = ix < lastSrcColumn ? ix + 1 : ix;
                    var a = r0 + ix * 4;
                    var b = r0 + ix1 * 4;
                    var c = r1 + ix * 4;
                    var d = r1 + ix1 * 4;
                    for (var ch = 0; ch < 3; ch++)
                    {
                        var top = (a[ch] << 8) + (b[ch] - a[ch]) * fx;
                        var bottom = (c[ch] << 8) + (d[ch] - c[ch]) * fx;
                        var value = ((top << 8) + (bottom - top) * fy + 32768) >> 16;
                        o[ch] = (byte)value;
                    }

                    o[3] = 255;
                    o += 4;
                }
            }
        });
    }

    private void FreeBuffers()
    {
        if (_bufferA != null)
        {
            NativeMemory.Free(_bufferA);
            _bufferA = null;
        }

        if (_bufferB != null)
        {
            NativeMemory.Free(_bufferB);
            _bufferB = null;
        }

        _bufferSize = 0;
    }

    public void Dispose() => FreeBuffers();

    /// <summary>
    /// Computes an even-sized output resolution that fits inside <paramref name="maxHeight"/> lines
    /// (and a proportional width), never upscaling.
    /// </summary>
    public static (int Width, int Height) FitWithin(int srcWidth, int srcHeight, int maxHeight)
    {
        if (maxHeight <= 0 || srcHeight <= maxHeight)
        {
            return (srcWidth & ~1, srcHeight & ~1);
        }

        var scale = (double)maxHeight / srcHeight;
        var width = (int)Math.Round(srcWidth * scale);
        return (Math.Max(16, width & ~1), Math.Max(16, maxHeight & ~1));
    }
}
