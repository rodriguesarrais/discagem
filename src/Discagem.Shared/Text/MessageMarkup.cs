using System.Text;
using System.Text.RegularExpressions;

namespace Discagem.Shared.Text;

public enum SpanKind
{
    Text,
    Bold,
    Italic,
    Underline,
    Strikethrough,
    Code,
    Link,
    Mention,
}

/// <summary>A run of inline text with a single style.</summary>
public sealed record MarkupSpan(SpanKind Kind, string Text);

public abstract record MarkupBlock;

/// <summary>Regular text with inline formatting.</summary>
public sealed record ParagraphBlock(IReadOnlyList<MarkupSpan> Spans) : MarkupBlock;

/// <summary>A fenced code block.</summary>
public sealed record CodeBlock(string Code, string? Language) : MarkupBlock;

/// <summary>A line quoted with "&gt; ".</summary>
public sealed record QuoteBlock(IReadOnlyList<MarkupSpan> Spans) : MarkupBlock;

/// <summary>
/// A small, forgiving subset of Markdown as used in chat apps:
/// **bold**, *italic*, __underline__, ~~strike~~, `code`, ```code blocks```, &gt; quotes, links and @mentions.
/// </summary>
public static partial class MessageMarkup
{
    [GeneratedRegex(@"(`[^`\n]+`)|(\*\*[^*\n]+?\*\*)|((?<!\w)__[^_\n]+?__(?!\w))|(~~[^~\n]+?~~)|(\*[^*\s][^*\n]*?\*)|((?<!\w)_[^_\s][^_\n]*?_(?!\w))|(https?://[^\s<>""]+[^\s<>"".,;:!?)\]}'])|((?<![\w@])@[A-Za-z0-9_.]{2,24})")]
    private static partial Regex InlinePattern();

    public static IReadOnlyList<MarkupBlock> Parse(string? content)
    {
        var blocks = new List<MarkupBlock>();
        if (string.IsNullOrEmpty(content))
        {
            return blocks;
        }

        var text = content.Replace("\r\n", "\n");
        var index = 0;
        while (index < text.Length)
        {
            var fence = text.IndexOf("```", index, StringComparison.Ordinal);
            if (fence < 0)
            {
                AddTextBlocks(blocks, text[index..]);
                break;
            }

            var close = text.IndexOf("```", fence + 3, StringComparison.Ordinal);
            if (close < 0)
            {
                AddTextBlocks(blocks, text[index..]);
                break;
            }

            AddTextBlocks(blocks, text[index..fence]);
            var inner = text[(fence + 3)..close];
            string? language = null;
            var newline = inner.IndexOf('\n');
            if (newline > 0 && newline <= 20 && !inner[..newline].Contains(' '))
            {
                language = inner[..newline].Trim();
                inner = inner[(newline + 1)..];
            }
            else if (newline == 0)
            {
                inner = inner[1..];
            }

            blocks.Add(new CodeBlock(inner.TrimEnd('\n'), string.IsNullOrEmpty(language) ? null : language));
            index = close + 3;
            if (index < text.Length && text[index] == '\n')
            {
                index++;
            }
        }

        return blocks;
    }

    private static void AddTextBlocks(List<MarkupBlock> blocks, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        var paragraph = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("> ", StringComparison.Ordinal) || line == ">")
            {
                FlushParagraph(blocks, paragraph);
                blocks.Add(new QuoteBlock(ParseInline(line.Length > 2 ? line[2..] : "")));
            }
            else
            {
                if (paragraph.Length > 0)
                {
                    paragraph.Append('\n');
                }

                paragraph.Append(line);
            }
        }

        FlushParagraph(blocks, paragraph);
    }

    private static void FlushParagraph(List<MarkupBlock> blocks, StringBuilder paragraph)
    {
        var value = paragraph.ToString().Trim('\n');
        paragraph.Clear();
        if (value.Length > 0)
        {
            blocks.Add(new ParagraphBlock(ParseInline(value)));
        }
    }

    public static IReadOnlyList<MarkupSpan> ParseInline(string text)
    {
        var spans = new List<MarkupSpan>();
        var last = 0;
        foreach (Match match in InlinePattern().Matches(text))
        {
            if (match.Index > last)
            {
                spans.Add(new MarkupSpan(SpanKind.Text, text[last..match.Index]));
            }

            var value = match.Value;
            var span = match.Groups switch
            {
                var g when g[1].Success => new MarkupSpan(SpanKind.Code, value[1..^1]),
                var g when g[2].Success => new MarkupSpan(SpanKind.Bold, value[2..^2]),
                var g when g[3].Success => new MarkupSpan(SpanKind.Underline, value[2..^2]),
                var g when g[4].Success => new MarkupSpan(SpanKind.Strikethrough, value[2..^2]),
                var g when g[5].Success => new MarkupSpan(SpanKind.Italic, value[1..^1]),
                var g when g[6].Success => new MarkupSpan(SpanKind.Italic, value[1..^1]),
                var g when g[7].Success => new MarkupSpan(SpanKind.Link, value),
                _ => new MarkupSpan(SpanKind.Mention, value),
            };
            spans.Add(span);
            last = match.Index + match.Length;
        }

        if (last < text.Length)
        {
            spans.Add(new MarkupSpan(SpanKind.Text, text[last..]));
        }

        return spans;
    }

    /// <summary>True when the message mentions <paramref name="username"/> (case-insensitive).</summary>
    public static bool Mentions(string? content, string username)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(username))
        {
            return false;
        }

        foreach (var block in Parse(content))
        {
            var spans = block switch
            {
                ParagraphBlock p => p.Spans,
                QuoteBlock q => q.Spans,
                _ => [],
            };

            if (spans.Any(s => s.Kind == SpanKind.Mention &&
                               (string.Equals(s.Text[1..], username, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(s.Text, "@everyone", StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True for short messages that consist only of emoji (rendered larger, like other chat apps).</summary>
    public static bool IsEmojiOnly(string? content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 32)
        {
            return false;
        }

        var emoji = 0;
        foreach (var rune in content.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || rune.Value is 0x200D or 0xFE0F || rune.Value is >= 0x1F3FB and <= 0x1F3FF)
            {
                continue;
            }

            var isEmoji = rune.Value is >= 0x1F000 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF or >= 0x2B00 and <= 0x2BFF;
            if (!isEmoji)
            {
                return false;
            }

            emoji++;
        }

        return emoji is > 0 and <= 6;
    }
}
