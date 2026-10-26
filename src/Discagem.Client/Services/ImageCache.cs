using Avalonia;
using Avalonia.Media.Imaging;
using Discagem.Shared;

namespace Discagem.Client.Services;

/// <summary>Downloads and decodes image attachments, keeping recently used ones in memory.</summary>
public sealed class ImageCache(ApiClient api)
{
    private const int MaxEntries = 120;
    private const int MaxWidth = 1600;

    private readonly Dictionary<long, Task<Bitmap>> _entries = new();
    private readonly LinkedList<long> _order = new();
    private readonly object _sync = new();

    public Task<Bitmap> GetAsync(AttachmentDto attachment)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(attachment.Id, out var existing) && !existing.IsFaulted)
            {
                _order.Remove(attachment.Id);
                _order.AddFirst(attachment.Id);
                return existing;
            }

            var task = LoadAsync(attachment);
            _entries[attachment.Id] = task;
            _order.Remove(attachment.Id);
            _order.AddFirst(attachment.Id);
            while (_order.Count > MaxEntries)
            {
                var oldest = _order.Last!.Value;
                _order.RemoveLast();
                _entries.Remove(oldest);
            }

            return task;
        }
    }

    private async Task<Bitmap> LoadAsync(AttachmentDto attachment)
    {
        var bytes = await api.DownloadAttachmentAsync(attachment);
        return await Task.Run(() =>
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);
            if (bitmap.PixelSize.Width <= MaxWidth)
            {
                return bitmap;
            }

            var height = (int)Math.Round(bitmap.PixelSize.Height * (MaxWidth / (double)bitmap.PixelSize.Width));
            var scaled = bitmap.CreateScaledBitmap(new PixelSize(MaxWidth, Math.Max(1, height)), BitmapInterpolationMode.HighQuality);
            bitmap.Dispose();
            return scaled;
        });
    }
}
