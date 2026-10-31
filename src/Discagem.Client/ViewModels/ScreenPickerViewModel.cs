using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Discagem.Client.Services.Capture;
using Discagem.Media.Imaging;
using Discagem.Media.Pipeline;

namespace Discagem.Client.ViewModels;

/// <summary>What the user chose to share.</summary>
public sealed record ScreenShareSelection(CaptureSource Source, StreamQuality Quality, string Title, bool ShareAudio);

/// <summary>A selectable screen or window with a live thumbnail.</summary>
public sealed partial class CaptureTileViewModel(CaptureSource source) : ObservableObject
{
    public CaptureSource Source { get; } = source;

    public string Name => Source.Name;

    public string SizeLabel => Source.Width > 0 ? $"{Source.Width} × {Source.Height}" : "";

    public bool IsScreen => Source.Kind == CaptureKind.Screen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnail;

    public bool HasThumbnail => Thumbnail is not null;
}

public sealed partial class ScreenPickerViewModel : ObservableObject
{
    private readonly bool _supportsApplicationAudio;

    public ScreenPickerViewModel(string defaultTitle, int qualityIndex, bool canShareAudio = false, bool supportsApplicationAudio = false, bool shareAudio = true)
    {
        Title = defaultTitle;
        Quality = StreamQuality.Presets[Math.Clamp(qualityIndex, 0, StreamQuality.Presets.Length - 1)];
        CanShareAudio = canShareAudio;
        _supportsApplicationAudio = supportsApplicationAudio;
        _shareAudio = canShareAudio && shareAudio;
    }

    public bool CanShareAudio { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioHint))]
    private bool _shareAudio;

    /// <summary>What viewers will hear with the current choice.</summary>
    public string AudioHint => !ShareAudio
        ? "Viewers will not hear anything."
        : !_supportsApplicationAudio
            ? "Viewers hear everything your computer plays, Discagem's voice chat included. Turn this off if they are in your voice channel."
            : Selected?.Source.Kind == CaptureKind.Window
                ? "Viewers hear this window's application only."
                : "Viewers hear everything your computer plays, except Discagem.";

    public ObservableCollection<CaptureTileViewModel> Screens { get; } = [];

    public ObservableCollection<CaptureTileViewModel> Windows { get; } = [];

    public IReadOnlyList<StreamQuality> Qualities => StreamQuality.Presets;

    public bool IsSupported => CaptureSources.IsSupported;

    public bool SupportsWindows => CaptureSources.SupportsWindows;

    public string UnsupportedReason => CaptureSources.UnsupportedReason;

    [ObservableProperty]
    private StreamQuality _quality;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selected), nameof(CanGoLive), nameof(AudioHint))]
    private CaptureTileViewModel? _selectedScreen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selected), nameof(CanGoLive), nameof(AudioHint))]
    private CaptureTileViewModel? _selectedWindow;

    [ObservableProperty]
    private bool _isLoading = true;

    public CaptureTileViewModel? Selected => SelectedWindow ?? SelectedScreen;

    public bool CanGoLive => Selected is not null;

    public int QualityIndex => Array.IndexOf(StreamQuality.Presets, Quality);

    partial void OnSelectedScreenChanged(CaptureTileViewModel? value)
    {
        if (value is not null)
        {
            SelectedWindow = null;
        }
    }

    partial void OnSelectedWindowChanged(CaptureTileViewModel? value)
    {
        if (value is not null)
        {
            SelectedScreen = null;
        }
    }

    public ScreenShareSelection? BuildSelection() =>
        Selected is { } tile ? new ScreenShareSelection(tile.Source, Quality, Title.Trim(), CanShareAudio && ShareAudio) : null;

    public async Task LoadAsync()
    {
        if (!IsSupported)
        {
            IsLoading = false;
            return;
        }

        var (screens, windows) = await Task.Run(() => (CaptureSources.GetScreens(), CaptureSources.GetWindows()));
        foreach (var screen in screens)
        {
            Screens.Add(new CaptureTileViewModel(screen));
        }

        foreach (var window in windows)
        {
            Windows.Add(new CaptureTileViewModel(window));
        }

        SelectedScreen = Screens.FirstOrDefault();
        IsLoading = false;

        // Thumbnails are captured one at a time in the background.
        var tiles = Screens.Concat(Windows).ToList();
        await Task.Run(() =>
        {
            foreach (var tile in tiles)
            {
                var thumbnail = CaptureThumbnail(tile.Source);
                if (thumbnail is not null)
                {
                    Dispatcher.UIThread.Post(() => tile.Thumbnail = thumbnail);
                }
            }
        });
    }

    private static unsafe Bitmap? CaptureThumbnail(CaptureSource source)
    {
        try
        {
            using var frameSource = source.Create();
            if (!frameSource.TryCapture(out var frame) || frame.Width < 16 || frame.Height < 16)
            {
                return null;
            }

            const int maxWidth = 320, maxHeight = 180;
            var scale = Math.Min(maxWidth / (double)frame.Width, maxHeight / (double)frame.Height);
            var width = Math.Max(2, (int)(frame.Width * scale));
            var height = Math.Max(2, (int)(frame.Height * scale));
            var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using var locked = bitmap.Lock();
            using var scaler = new BgraScaler();
            scaler.Scale((byte*)frame.Data, frame.Width, frame.Height, frame.Stride, (byte*)locked.Address, width, height, locked.RowBytes);
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
