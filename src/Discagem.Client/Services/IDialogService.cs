using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Discagem.Client.ViewModels;

namespace Discagem.Client.Services;

/// <summary>Window-level interactions requested by view models.</summary>
public interface IDialogService
{
    Task<ScreenShareSelection?> PickScreenAsync(ScreenPickerViewModel viewModel);

    Task ShowSettingsAsync(SettingsViewModel viewModel);

    Task<ChannelEditorViewModel?> EditChannelAsync(ChannelEditorViewModel viewModel);

    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger);

    Task<IReadOnlyList<IStorageFile>> PickFilesAsync();

    /// <summary>Asks for one image file (PNG, JPEG, GIF, WebP or BMP).</summary>
    Task<IStorageFile?> PickImageAsync(string title);

    /// <summary>Asks for one sound file (WAV, MP3 or Ogg Vorbis) for the soundboard.</summary>
    Task<IStorageFile?> PickSoundAsync();

    /// <summary>Asks for the name of a new emoji, sticker or sound. True when the user confirmed.</summary>
    Task<bool> ShowUploadExpressionAsync(UploadExpressionViewModel viewModel);

    Task<IStorageFile?> SaveFileAsync(string suggestedName);

    void ShowImage(Bitmap bitmap, string title);

    /// <summary>Flashes the taskbar entry / requests attention when the window is not focused.</summary>
    void RequestAttention();

    bool IsWindowActive { get; }
}
