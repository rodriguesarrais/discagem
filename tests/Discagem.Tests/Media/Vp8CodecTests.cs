using Discagem.Media.Imaging;
using Discagem.Media.Vpx;

namespace Discagem.Tests.Media;

public sealed unsafe class Vp8CodecTests
{
    private static BgraFrame CreatePattern(int width, int height, int shift)
    {
        var frame = new BgraFrame(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * frame.Stride + x * 4;
                // Smooth gradients plus a block pattern, roughly like UI content.
                var block = ((x + shift) / 32 + y / 32) % 2 == 0;
                frame.Data[i + 0] = (byte)(block ? 230 : (x * 255 / width));
                frame.Data[i + 1] = (byte)(block ? 230 : (y * 255 / height));
                frame.Data[i + 2] = (byte)(block ? 40 : 128);
                frame.Data[i + 3] = 255;
            }
        }

        return frame;
    }

    private static double MeanAbsoluteError(BgraFrame a, BgraFrame b)
    {
        long sum = 0;
        for (var i = 0; i < a.Data.Length; i += 4)
        {
            sum += Math.Abs(a.Data[i] - b.Data[i]) + Math.Abs(a.Data[i + 1] - b.Data[i + 1]) + Math.Abs(a.Data[i + 2] - b.Data[i + 2]);
        }

        return sum / (a.Data.Length / 4.0 * 3);
    }

    [Fact]
    public void Libvpx_IsAvailable()
    {
        Assert.True(VpxLibrary.IsAvailable, "libvpx must be installed to run the media tests (e.g. apt install libvpx9).");
        Assert.StartsWith("v1.", VpxLibrary.Version);
    }

    [Fact]
    public void EncodeDecode_RoundTripsPicture()
    {
        const int width = 640, height = 360;
        using var encoder = new Vp8Encoder(new Vp8EncoderOptions(width, height, 30, 2500));
        using var decoder = new Vp8Decoder();
        BgraFrame? decoded = null;

        for (var n = 0; n < 5; n++)
        {
            var source = CreatePattern(width, height, n * 4);
            fixed (byte* src = source.Data)
            fixed (byte* dst = encoder.InputBuffer)
            {
                PixelConverter.BgraToI420(src, source.Stride, width, height, dst);
            }

            var encoded = encoder.Encode(n * 33);
            Assert.NotNull(encoded);
            if (n == 0)
            {
                Assert.True(encoded.Value.IsKeyFrame);
                Assert.True(Vp8Bitstream.TryGetKeyFrameSize(encoded.Value.Data, out var w, out var h));
                Assert.Equal((width, height), (w, h));
            }

            Assert.True(decoder.Decode(encoded.Value.Data, ref decoded));
            Assert.NotNull(decoded);
            Assert.Equal(width, decoded.Width);
            Assert.Equal(height, decoded.Height);

            if (n == 4)
            {
                var error = MeanAbsoluteError(source, decoded);
                Assert.True(error < 12, $"Mean absolute error too high: {error:F2}");
            }
        }
    }

    [Fact]
    public void RequestKeyFrame_ProducesKeyFrame()
    {
        const int width = 320, height = 240;
        using var encoder = new Vp8Encoder(new Vp8EncoderOptions(width, height, 30, 800));
        var source = CreatePattern(width, height, 0);
        fixed (byte* src = source.Data)
        fixed (byte* dst = encoder.InputBuffer)
        {
            PixelConverter.BgraToI420(src, source.Stride, width, height, dst);
        }

        Assert.True(encoder.Encode(0)!.Value.IsKeyFrame);
        Assert.False(encoder.Encode(33)!.Value.IsKeyFrame);
        encoder.RequestKeyFrame();
        Assert.True(encoder.Encode(66)!.Value.IsKeyFrame);
        encoder.SetBitrate(400);
        Assert.NotNull(encoder.Encode(99));
    }

    [Fact]
    public void Decoder_RejectsGarbage()
    {
        using var decoder = new Vp8Decoder();
        BgraFrame? frame = null;
        Assert.False(decoder.Decode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, ref frame));
    }

    [Fact]
    public void Scaler_HalvesAndFits()
    {
        var source = CreatePattern(1920, 1080, 0);
        var (w, h) = BgraScaler.FitWithin(1920, 1080, 720);
        Assert.Equal((1280, 720), (w, h));
        Assert.Equal((1920, 1080), BgraScaler.FitWithin(1920, 1080, 1080));
        Assert.Equal((2560, 1440), BgraScaler.FitWithin(2560, 1440, 0));

        var target = new BgraFrame(w, h);
        using var scaler = new BgraScaler();
        fixed (byte* src = source.Data)
        fixed (byte* dst = target.Data)
        {
            scaler.Scale(src, 1920, 1080, source.Stride, dst, w, h, target.Stride);
        }

        // Top-left pixel lies inside a solid block, so it must keep its colour.
        Assert.Equal(230, target.Data[0]);
        Assert.Equal(230, target.Data[1]);
        Assert.Equal(40, target.Data[2]);
        Assert.Equal(255, target.Data[3]);
    }
}
