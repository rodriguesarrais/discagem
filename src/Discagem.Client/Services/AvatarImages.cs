using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Discagem.Client.Services;

/// <summary>Prepares photos for upload as profile pictures.</summary>
public static class AvatarImages
{
    /// <summary>Side of the uploaded picture, in pixels: sharp at the largest size avatars are shown, on high-DPI screens.</summary>
    public const int Size = 256;

    /// <summary>
    /// Crops a photo to its centre square, scales it to <see cref="Size"/> pixels and encodes it as PNG.
    /// Throws for data that is not a supported image. Call on the UI thread.
    /// </summary>
    public static byte[] Prepare(Stream photo)
    {
        using var original = new Bitmap(photo);
        var side = Math.Min(original.Size.Width, original.Size.Height);
        var source = new Rect((original.Size.Width - side) / 2, (original.Size.Height - side) / 2, side, side);

        using var target = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
        {
            context.DrawImage(original, source, new Rect(0, 0, Size, Size));
        }

        using var output = new MemoryStream();
        target.Save(output);
        return output.ToArray();
    }
}

/// <summary>Downloads and decodes profile photos, keyed by user and photo version.</summary>
public sealed class AvatarCache(ApiClient api)
{
    /// <summary>Decoded width: enough for the largest avatar (80 px) on a 2x screen.</summary>
    private const int DecodeWidth = 160;

    private readonly ConcurrentDictionary<(long UserId, int Version), Task<Bitmap?>> _entries = new();

    /// <summary>Returns the photo, or null if it cannot be loaded right now (a later call tries again).</summary>
    public Task<Bitmap?> GetAsync(long userId, int version) => _entries.GetOrAdd((userId, version), LoadAsync);

    private async Task<Bitmap?> LoadAsync((long UserId, int Version) key)
    {
        try
        {
            var bytes = await api.DownloadAvatarAsync(key.UserId, key.Version);
            return await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes);
                return Bitmap.DecodeToWidth(stream, DecodeWidth, BitmapInterpolationMode.HighQuality);
            });
        }
        catch (Exception ex) when (ex is ApiException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            _entries.TryRemove(key, out _);
            return null;
        }
    }
}

/// <summary>Downloads and decodes room icons, keyed by room and icon version.</summary>
public sealed class RoomIconCache(ApiClient api)
{
    /// <summary>Decoded width: the room tile (48 px) on a 2x screen.</summary>
    private const int DecodeWidth = 128;

    private readonly ConcurrentDictionary<(long RoomId, int Version), Task<Bitmap?>> _entries = new();

    /// <summary>Returns the icon, or null if it cannot be loaded right now (a later call tries again).</summary>
    public Task<Bitmap?> GetAsync(long roomId, int version) => _entries.GetOrAdd((roomId, version), LoadAsync);

    private async Task<Bitmap?> LoadAsync((long RoomId, int Version) key)
    {
        try
        {
            var bytes = await api.DownloadRoomIconAsync(key.RoomId, key.Version);
            return await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes);
                return Bitmap.DecodeToWidth(stream, DecodeWidth, BitmapInterpolationMode.HighQuality);
            });
        }
        catch (Exception ex) when (ex is ApiException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            _entries.TryRemove(key, out _);
            return null;
        }
    }
}
