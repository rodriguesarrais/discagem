using System.Text;

namespace Discagem.Shared.Text;

/// <summary>Names of custom emojis and stickers, written <c>:name:</c> in messages.</summary>
public static class EmojiNames
{
    /// <summary>
    /// Turns a suggested name into a valid one: lowercase letters, digits and underscores (spaces and
    /// dashes become underscores, accented letters lose their accents, anything else is dropped), between
    /// <see cref="Limits.MinEmojiNameLength"/> and <see cref="Limits.MaxEmojiNameLength"/> characters.
    /// </summary>
    public static bool TryNormalize(string? input, out string name)
    {
        var builder = new StringBuilder();
        // Decomposed, an accented letter is its base letter followed by marks, which are then dropped.
        foreach (var c in (input ?? "").Trim().Trim(':').ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(c);
            }
            else if ((c is '_' or '-' || char.IsWhiteSpace(c)) && builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        name = builder.ToString().TrimEnd('_');
        if (name.Length > Limits.MaxEmojiNameLength)
        {
            name = name[..Limits.MaxEmojiNameLength].TrimEnd('_');
        }

        return name.Length >= Limits.MinEmojiNameLength;
    }

    /// <summary>Suggests a name from a file name ("Party Parrot.png" becomes "party_parrot").</summary>
    public static string Suggest(string fileName)
    {
        return TryNormalize(Path.GetFileNameWithoutExtension(fileName), out var name) ? name : "";
    }
}
