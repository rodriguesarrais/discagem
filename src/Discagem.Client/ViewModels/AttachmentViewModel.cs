using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Discagem.Client.Services;
using Discagem.Shared;

namespace Discagem.Client.ViewModels;

/// <summary>A file attached to a message. Images are downloaded and shown inline.</summary>
public sealed partial class AttachmentViewModel : ObservableObject
{
    private readonly ImageCache _images;
    private readonly ApiClient _api;
    private readonly IDialogService _dialogs;
    private bool _loadRequested;

    public AttachmentViewModel(AttachmentDto attachment, ImageCache images, ApiClient api, IDialogService dialogs)
    {
        Attachment = attachment;
        _images = images;
        _api = api;
        _dialogs = dialogs;
    }

    public AttachmentDto Attachment { get; }

    public string FileName => Attachment.FileName;

    public bool IsImage => Attachment.IsImage;

    public bool IsFile => !Attachment.IsImage;

    public string SizeLabel => FormatSize(Attachment.Size);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private Bitmap? _image;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _error;

    public bool HasImage => Image is not null;

    /// <summary>Starts loading the inline preview (once).</summary>
    public async void EnsureLoaded()
    {
        if (!IsImage || _loadRequested)
        {
            return;
        }

        _loadRequested = true;
        IsLoading = true;
        try
        {
            Image = await _images.GetAsync(Attachment);
        }
        catch (Exception ex)
        {
            Error = ex is ApiException ? ex.Message : "Could not load image.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Open()
    {
        if (Image is not null)
        {
            _dialogs.ShowImage(Image, FileName);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var file = await _dialogs.SaveFileAsync(FileName);
        if (file is null)
        {
            return;
        }

        try
        {
            IsLoading = true;
            var bytes = await _api.DownloadAttachmentAsync(Attachment);
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}

/// <summary>A file selected in the composer, uploaded when the message is sent.</summary>
public sealed partial class PendingAttachmentViewModel(string fileName, long size, Func<Task<Stream>> open, string? contentType) : ObservableObject
{
    public string FileName { get; } = fileName;

    public long Size { get; } = size;

    public string? ContentType { get; } = contentType;

    public string SizeLabel => AttachmentViewModel.FormatSize(Size);

    public Func<Task<Stream>> Open { get; } = open;

    [ObservableProperty]
    private bool _isUploading;

    public long? UploadedId { get; set; }
}
