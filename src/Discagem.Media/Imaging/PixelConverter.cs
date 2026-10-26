namespace Discagem.Media.Imaging;

/// <summary>
/// Colour space conversions between BGRA and I420 (BT.601, limited range), parallelised by rows.
/// </summary>
public static unsafe class PixelConverter
{
    private static readonly int[] YTable = new int[256];
    private static readonly int[] RvTable = new int[256];
    private static readonly int[] GuTable = new int[256];
    private static readonly int[] GvTable = new int[256];
    private static readonly int[] BuTable = new int[256];
    private const int ClampOffset = 512;
    private static readonly byte[] ClampTable = new byte[1536];

    static PixelConverter()
    {
        for (var i = 0; i < 256; i++)
        {
            YTable[i] = 298 * (i - 16);
            RvTable[i] = 409 * (i - 128);
            GuTable[i] = 100 * (i - 128);
            GvTable[i] = 208 * (i - 128);
            BuTable[i] = 516 * (i - 128);
        }

        for (var i = 0; i < ClampTable.Length; i++)
        {
            ClampTable[i] = (byte)Math.Clamp(i - ClampOffset, 0, 255);
        }
    }

    private static int ChunkCount(int rows) => Math.Clamp(Math.Min(Environment.ProcessorCount, rows / 32), 1, 16);

    /// <summary>
    /// Converts a BGRA picture with even dimensions into a tightly packed I420 buffer.
    /// </summary>
    public static void BgraToI420(byte* src, int srcStride, int width, int height, byte* dst)
    {
        if ((width & 1) != 0 || (height & 1) != 0)
        {
            throw new ArgumentException("BGRA to I420 conversion requires even dimensions.");
        }

        var chromaWidth = width / 2;
        var yPlane = (nint)dst;
        var uPlane = yPlane + width * height;
        var vPlane = uPlane + chromaWidth * (height / 2);
        var srcBase = (nint)src;
        var pairRows = height / 2;
        var chunks = ChunkCount(height);

        Parallel.For(0, chunks, chunk =>
        {
            var start = pairRows * chunk / chunks;
            var end = pairRows * (chunk + 1) / chunks;
            for (var pair = start; pair < end; pair++)
            {
                var y = pair * 2;
                var row0 = (byte*)srcBase + (long)y * srcStride;
                var row1 = row0 + srcStride;
                var y0 = (byte*)yPlane + (long)y * width;
                var y1 = y0 + width;
                var u = (byte*)uPlane + (long)pair * chromaWidth;
                var v = (byte*)vPlane + (long)pair * chromaWidth;

                for (var x = 0; x < width; x += 2)
                {
                    var p00 = row0 + x * 4;
                    var p01 = p00 + 4;
                    var p10 = row1 + x * 4;
                    var p11 = p10 + 4;

                    int b00 = p00[0], g00 = p00[1], r00 = p00[2];
                    int b01 = p01[0], g01 = p01[1], r01 = p01[2];
                    int b10 = p10[0], g10 = p10[1], r10 = p10[2];
                    int b11 = p11[0], g11 = p11[1], r11 = p11[2];

                    y0[x] = (byte)(((66 * r00 + 129 * g00 + 25 * b00 + 128) >> 8) + 16);
                    y0[x + 1] = (byte)(((66 * r01 + 129 * g01 + 25 * b01 + 128) >> 8) + 16);
                    y1[x] = (byte)(((66 * r10 + 129 * g10 + 25 * b10 + 128) >> 8) + 16);
                    y1[x + 1] = (byte)(((66 * r11 + 129 * g11 + 25 * b11 + 128) >> 8) + 16);

                    var r = (r00 + r01 + r10 + r11 + 2) >> 2;
                    var g = (g00 + g01 + g10 + g11 + 2) >> 2;
                    var b = (b00 + b01 + b10 + b11 + 2) >> 2;
                    var cx = x >> 1;
                    u[cx] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                    v[cx] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                }
            }
        });
    }

    /// <summary>Converts planar I420 into a BGRA frame (alpha is set to 255).</summary>
    public static void I420ToBgra(
        byte* yPlane, int yStride,
        byte* uPlane, int uStride,
        byte* vPlane, int vStride,
        int width, int height,
        BgraFrame target)
    {
        if (target.Width != width || target.Height != height)
        {
            throw new ArgumentException("Target frame size does not match the source picture.", nameof(target));
        }

        fixed (byte* dst = target.Data)
        {
            var dstBase = (nint)dst;
            var dstStride = target.Stride;
            var yBase = (nint)yPlane;
            var uBase = (nint)uPlane;
            var vBase = (nint)vPlane;
            var chunks = ChunkCount(height);

            Parallel.For(0, chunks, chunk =>
            {
                var start = height * chunk / chunks;
                var end = height * (chunk + 1) / chunks;
                fixed (int* yt = YTable, rv = RvTable, gu = GuTable, gv = GvTable, bu = BuTable)
                fixed (byte* clamp = ClampTable)
                {
                    var c = clamp + ClampOffset;
                    for (var row = start; row < end; row++)
                    {
                        var ySrc = (byte*)yBase + (long)row * yStride;
                        var uSrc = (byte*)uBase + (long)(row >> 1) * uStride;
                        var vSrc = (byte*)vBase + (long)(row >> 1) * vStride;
                        var outRow = (byte*)dstBase + (long)row * dstStride;

                        for (var x = 0; x < width; x++)
                        {
                            var luma = yt[ySrc[x]];
                            var uu = uSrc[x >> 1];
                            var vv = vSrc[x >> 1];
                            var o = outRow + x * 4;
                            o[0] = c[(luma + bu[uu] + 128) >> 8];
                            o[1] = c[(luma - gu[uu] - gv[vv] + 128) >> 8];
                            o[2] = c[(luma + rv[vv] + 128) >> 8];
                            o[3] = 255;
                        }
                    }
                }
            });
        }
    }
}
