using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Discagem.Shared;

namespace Discagem.Client.Services;

/// <summary>
/// The server's custom emojis and stickers, by name, with their pictures. Messages are rendered with
/// the catalog of the signed-in server (<see cref="Current"/>); change it on the UI thread only.
/// </summary>
public sealed class EmojiCatalog(ApiClient api)
{
    /// <summary>Decoded width of stickers: their largest size on a 2x screen.</summary>
    private const int StickerDecodeWidth = 320;

    /// <summary>Decoded width of emojis: their jumbo size on a 2x screen.</summary>
    private const int EmojiDecodeWidth = 96;

    private readonly Dictionary<string, EmojiDto> _byName = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task<Bitmap?>> _images = new();

    /// <summary>The catalog messages are rendered with, or null when signed out.</summary>
    public static EmojiCatalog? Current { get; set; }

    /// <summary>Raised on the UI thread when emojis are added or removed, so messages render again.</summary>
    public static event Action? Changed;

    public IReadOnlyCollection<EmojiDto> All => _byName.Values;

    public EmojiDto? Find(string? name) => name is not null && _byName.TryGetValue(name, out var emoji) ? emoji : null;

    public void Reset(IEnumerable<EmojiDto> emojis)
    {
        _byName.Clear();
        foreach (var emoji in emojis)
        {
            _byName[emoji.Name] = emoji;
        }

        Changed?.Invoke();
    }

    public void Add(EmojiDto emoji)
    {
        _byName[emoji.Name] = emoji;
        Changed?.Invoke();
    }

    public void Remove(long id)
    {
        var emoji = _byName.Values.FirstOrDefault(e => e.Id == id);
        if (emoji is not null)
        {
            _byName.Remove(emoji.Name);
            _images.TryRemove(id, out _);
            Changed?.Invoke();
        }
    }

    /// <summary>Returns the picture, or null if it cannot be loaded right now (a later call tries again).</summary>
    public Task<Bitmap?> GetImageAsync(EmojiDto emoji) => _images.GetOrAdd(emoji.Id, _ => LoadAsync(emoji));

    private async Task<Bitmap?> LoadAsync(EmojiDto emoji)
    {
        try
        {
            var bytes = await api.DownloadEmojiAsync(emoji.Id);
            var width = emoji.Kind == EmojiKind.Sticker ? StickerDecodeWidth : EmojiDecodeWidth;
            return await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes);
                var bitmap = new Bitmap(stream);
                if (bitmap.PixelSize.Width <= width)
                {
                    return bitmap;
                }

                var height = (int)Math.Round(bitmap.PixelSize.Height * (width / (double)bitmap.PixelSize.Width));
                var scaled = bitmap.CreateScaledBitmap(new PixelSize(width, Math.Max(1, height)), BitmapInterpolationMode.HighQuality);
                bitmap.Dispose();
                return scaled;
            });
        }
        catch (Exception ex) when (ex is ApiException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            _images.TryRemove(emoji.Id, out _);
            return null;
        }
    }
}

/// <summary>Prepares pictures for upload as emojis and stickers.</summary>
public static class EmojiImages
{
    /// <summary>Largest side of an uploaded emoji, in pixels.</summary>
    public const int EmojiSize = 128;

    /// <summary>Largest side of an uploaded sticker, in pixels.</summary>
    public const int StickerSize = 320;

    /// <summary>
    /// Scales a picture to fit within a square of <paramref name="maxSide"/> pixels (never enlarging
    /// it), keeping its proportions and transparency, and encodes it as PNG. Throws for data that is
    /// not a supported image. Call on the UI thread.
    /// </summary>
    public static byte[] Prepare(Stream picture, int maxSide)
    {
        using var original = new Bitmap(picture);
        var width = original.PixelSize.Width;
        var height = original.PixelSize.Height;
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(width, height));
        var target = new PixelSize(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));

        using var output = new MemoryStream();
        if (target == original.PixelSize)
        {
            original.Save(output);
            return output.ToArray();
        }

        using var scaled = new RenderTargetBitmap(target, new Vector(96, 96));
        using (var context = scaled.CreateDrawingContext())
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
        {
            context.DrawImage(original, new Rect(original.Size), new Rect(0, 0, target.Width, target.Height));
        }

        scaled.Save(output);
        return output.ToArray();
    }
}
