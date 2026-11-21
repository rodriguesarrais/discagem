using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Discagem.Shared;
using Discagem.Shared.Text;

namespace Discagem.Client.ViewModels;

/// <summary>Names a new emoji, sticker or soundboard sound before it is uploaded.</summary>
public sealed partial class UploadExpressionViewModel : ObservableObject
{
    private readonly bool _isEmojiName;
    private readonly Action? _preview;

    private UploadExpressionViewModel(string heading, string label, string name, int maxLength, bool isEmojiName, Bitmap? image, string details, Action? preview)
    {
        Heading = heading;
        Label = label;
        _name = name;
        MaxLength = maxLength;
        _isEmojiName = isEmojiName;
        Image = image;
        Details = details;
        _preview = preview;
    }

    public static UploadExpressionViewModel ForEmoji(EmojiKind kind, string suggestedName, Bitmap image) => kind == EmojiKind.Sticker
        ? new("Add a sticker", "STICKER NAME", suggestedName, Limits.MaxEmojiNameLength, isEmojiName: true, image,
            "Send it from the sticker tab of the emoji picker. Everyone on the server can use it.", preview: null)
        : new("Add an emoji", "EMOJI NAME", suggestedName, Limits.MaxEmojiNameLength, isEmojiName: true, image,
            "Use it in messages by writing its name between colons. Everyone on the server can use it.", preview: null);

    public static UploadExpressionViewModel ForSound(string suggestedName, string details, Action preview) =>
        new("Add a sound", "SOUND NAME", suggestedName, Limits.MaxSoundNameLength, isEmojiName: false, image: null, details, preview);

    public string Heading { get; }

    public string Label { get; }

    public int MaxLength { get; }

    public Bitmap? Image { get; }

    public bool HasImage => Image is not null;

    public string Details { get; }

    public bool CanPreview => _preview is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm), nameof(Preview))]
    private string _name;

    /// <summary>The name as it will be saved (emoji names are normalised).</summary>
    public string Preview => !_isEmojiName
        ? ""
        : EmojiNames.TryNormalize(Name, out var name)
            ? $"Written as :{name}:"
            : $"Use {Limits.MinEmojiNameLength} to {Limits.MaxEmojiNameLength} letters, digits or underscores.";

    public bool CanConfirm => _isEmojiName ? EmojiNames.TryNormalize(Name, out _) : !string.IsNullOrWhiteSpace(Name);

    [RelayCommand]
    private void PlayPreview() => _preview?.Invoke();
}
