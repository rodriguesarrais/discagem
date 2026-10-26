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

    Task<IStorageFile?> SaveFileAsync(string suggestedName);

    void ShowImage(Bitmap bitmap, string title);

    /// <summary>Flashes the taskbar entry / requests attention when the window is not focused.</summary>
    void RequestAttention();

    bool IsWindowActive { get; }
}
