namespace Discagem.Media.Imaging;

/// <summary>A 32-bit BGRA picture stored in a managed buffer with a tight stride.</summary>
public sealed class BgraFrame
{
    public BgraFrame(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        Stride = width * 4;
        Data = new byte[Stride * height];
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public byte[] Data { get; }
}

/// <summary>Layout helpers for tightly packed I420 buffers.</summary>
public static class I420Layout
{
    public static int BufferSize(int width, int height)
    {
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;
        return width * height + 2 * chromaWidth * chromaHeight;
    }
}
