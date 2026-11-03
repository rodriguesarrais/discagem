using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Discagem.Client.Resources;
using Discagem.Client.Services;
using Discagem.Client.ViewModels;
using Discagem.Client.Views.Dialogs;

namespace Discagem.Client.Views;

/// <summary>Shows dialogs and pickers on behalf of view models.</summary>
public sealed class DialogService(Window owner) : IDialogService
{
    public bool IsWindowActive => owner.IsActive;

    public async Task<ScreenShareSelection?> PickScreenAsync(ScreenPickerViewModel viewModel)
    {
        var dialog = new ScreenPickerDialog { DataContext = viewModel };
        _ = viewModel.LoadAsync();
        var accepted = await dialog.ShowDialog<bool>(owner);
        return accepted ? viewModel.BuildSelection() : null;
    }

    public async Task ShowSettingsAsync(SettingsViewModel viewModel)
    {
        var dialog = new SettingsDialog { DataContext = viewModel };
        viewModel.LogoutRequested += dialog.Close;
        await dialog.ShowDialog(owner);
    }

    public async Task<ChannelEditorViewModel?> EditChannelAsync(ChannelEditorViewModel viewModel)
    {
        var dialog = new ChannelEditorDialog { DataContext = viewModel };
        var accepted = await dialog.ShowDialog<bool>(owner);
        return accepted && viewModel.CanConfirm ? viewModel : null;
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger) =>
        new ConfirmDialog(title, message, confirmText, danger).ShowDialog<bool>(owner);

    public async Task<IReadOnlyList<IStorageFile>> PickFilesAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Dialog_UploadFilesTitle,
            AllowMultiple = true,
        });
        return files;
    }

    public async Task<IStorageFile?> PickImageAsync(string title)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.Dialog_ImagesFileType)
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp"],
                    MimeTypes = ["image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp"],
                },
            ],
        });
        return files.FirstOrDefault();
    }

    public Task<IStorageFile?> SaveFileAsync(string suggestedName) =>
        owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.Dialog_SaveFileTitle,
            SuggestedFileName = suggestedName,
            ShowOverwritePrompt = true,
        });

    public void ShowImage(Bitmap bitmap, string title) => new ImageViewerWindow(bitmap, title).Show(owner);

    public void RequestAttention()
    {
        if (owner.IsActive || !OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero)
        {
            var info = new FlashInfo
            {
                cbSize = (uint)Marshal.SizeOf<FlashInfo>(),
                hwnd = handle,
                dwFlags = FlashTray | FlashUntilForeground,
                uCount = 3,
            };
            FlashWindowEx(ref info);
        }
    }

    private const uint FlashTray = 0x00000002;
    private const uint FlashUntilForeground = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
