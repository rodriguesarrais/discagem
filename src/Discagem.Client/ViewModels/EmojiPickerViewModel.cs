using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Discagem.Client.Services;
using Discagem.Shared;
using Discagem.Shared.Text;

namespace Discagem.Client.ViewModels;

/// <summary>
/// The composer's emoji picker: standard emoji, the server's custom emojis (inserted as
/// <c>:name:</c>) and its stickers (sent as a message of their own).
/// </summary>
public sealed partial class EmojiPickerViewModel : ObservableObject
{
    private readonly ApiClient _api;
    private readonly EmojiCatalog _catalog;
    private readonly IDialogService _dialogs;
    private readonly Func<MemberViewModel> _me;
    private readonly Func<string, Task> _send;
    private readonly Action<string> _showError;

    public EmojiPickerViewModel(
        ApiClient api,
        EmojiCatalog catalog,
        IDialogService dialogs,
        Func<MemberViewModel> me,
        Func<string, Task> send,
        Action<string> showError)
    {
        _api = api;
        _catalog = catalog;
        _dialogs = dialogs;
        _me = me;
        _send = send;
        _showError = showError;
        Emojis.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoEmojis));
        Stickers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoStickers));
    }

    /// <summary>Common standard emoji, inserted as they are.</summary>
    public IReadOnlyList<string> StandardEmojis { get; } =
    [
        "😀", "😂", "🤣", "😊", "😍", "😘", "😎", "🤔", "😐", "🙄", "😴", "😭",
        "😡", "🥺", "😳", "🤯", "🥳", "😇", "🤡", "💀", "👀", "🙈", "👍", "👎",
        "👏", "🙏", "💪", "🤝", "👋", "✌️", "🤞", "👌", "🔥", "✨", "🎉", "💯",
        "❤️", "💔", "💙", "💚", "💛", "💜", "⭐", "☀️", "🍕", "🍺", "☕", "🎮",
        "🎧", "🎵", "⚽", "🏆", "🚀", "💻", "✅", "❌", "⚠️", "❓", "❗", "😢",
    ];

    public ObservableCollection<EmojiItemViewModel> Emojis { get; } = [];

    public ObservableCollection<EmojiItemViewModel> Stickers { get; } = [];

    public bool HasNoEmojis => Emojis.Count == 0;

    public bool HasNoStickers => Stickers.Count == 0;

    public void Reset(IReadOnlyList<EmojiDto> emojis)
    {
        _catalog.Reset(emojis);
        Emojis.Clear();
        Stickers.Clear();
        foreach (var emoji in emojis)
        {
            Add(emoji);
        }
    }

    public void Add(EmojiDto emoji)
    {
        _catalog.Add(emoji);
        var list = emoji.Kind == EmojiKind.Sticker ? Stickers : Emojis;
        if (list.Any(e => e.Id == emoji.Id))
        {
            return;
        }

        var item = new EmojiItemViewModel(emoji, _catalog, CanDelete(emoji));
        var index = 0;
        while (index < list.Count && string.CompareOrdinal(list[index].Name, emoji.Name) < 0)
        {
            index++;
        }

        list.Insert(index, item);
    }

    public void Remove(long id)
    {
        _catalog.Remove(id);
        foreach (var list in new[] { Emojis, Stickers })
        {
            if (list.FirstOrDefault(e => e.Id == id) is { } item)
            {
                list.Remove(item);
            }
        }
    }

    /// <summary>The picker opened: load the pictures it shows.</summary>
    public void OnOpened()
    {
        foreach (var item in Emojis.Concat(Stickers))
        {
            item.EnsureLoaded();
        }
    }

    private bool CanDelete(EmojiDto emoji) => emoji.UploaderId == _me().Id || _me().IsAdmin;

    [RelayCommand]
    private Task AddEmojiAsync() => AddAsync(EmojiKind.Emoji);

    [RelayCommand]
    private Task AddStickerAsync() => AddAsync(EmojiKind.Sticker);

    private async Task AddAsync(EmojiKind kind)
    {
        var sticker = kind == EmojiKind.Sticker;
        var file = await _dialogs.PickImageAsync(sticker ? "Choose a picture for the sticker" : "Choose a picture for the emoji");
        if (file is null)
        {
            return;
        }

        byte[] png;
        try
        {
            await using var stream = await file.OpenReadAsync();
            png = EmojiImages.Prepare(stream, sticker ? EmojiImages.StickerSize : EmojiImages.EmojiSize);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            _showError("That file is not a picture Discagem can read.");
            return;
        }

        var max = sticker ? Limits.MaxStickerBytes : Limits.MaxEmojiBytes;
        if (png.Length > max)
        {
            _showError($"That picture is too detailed: even scaled down it is larger than {max / 1024} KB.");
            return;
        }

        using var preview = new Bitmap(new MemoryStream(png));
        var dialog = UploadExpressionViewModel.ForEmoji(kind, EmojiNames.Suggest(file.Name), preview);
        if (!await _dialogs.ShowUploadExpressionAsync(dialog) || !EmojiNames.TryNormalize(dialog.Name, out var name))
        {
            return;
        }

        try
        {
            Add(await _api.UploadEmojiAsync(name, kind, png));
        }
        catch (ApiException ex)
        {
            _showError($"Could not add :{name}:. {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(EmojiItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var noun = item.Kind == EmojiKind.Sticker ? "sticker" : "emoji";
        if (!await _dialogs.ConfirmAsync($"Remove {item.Token}", $"Remove the {noun} {item.Token} for everyone? Messages that use it will show its name instead.", "Remove", danger: true))
        {
            return;
        }

        try
        {
            await _api.DeleteEmojiAsync(item.Id);
            Remove(item.Id);
        }
        catch (ApiException ex)
        {
            _showError($"Could not remove {item.Token}. {ex.Message}");
        }
    }

    [RelayCommand]
    private Task SendStickerAsync(EmojiItemViewModel? item) => item is null ? Task.CompletedTask : _send(item.Token);
}

/// <summary>A custom emoji or sticker in the picker.</summary>
public sealed partial class EmojiItemViewModel(EmojiDto emoji, EmojiCatalog catalog, bool canDelete) : ObservableObject
{
    private bool _loadRequested;

    public EmojiDto Emoji { get; } = emoji;

    public long Id => Emoji.Id;

    public string Name => Emoji.Name;

    public EmojiKind Kind => Emoji.Kind;

    /// <summary>How messages refer to it.</summary>
    public string Token => $":{Emoji.Name}:";

    public bool CanDelete { get; } = canDelete;

    [ObservableProperty]
    private Bitmap? _image;

    public async void EnsureLoaded()
    {
        if (_loadRequested)
        {
            return;
        }

        _loadRequested = true;
        Image = await catalog.GetImageAsync(Emoji);
        _loadRequested = Image is not null;
    }
}
