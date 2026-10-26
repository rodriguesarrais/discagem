using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media.TextFormatting;
using Avalonia.Layout;
using Avalonia.Media;
using Discagem.Shared.Text;

namespace Discagem.Client.Controls;

/// <summary>Renders chat markup (see <see cref="MessageMarkup"/>) as selectable text blocks.</summary>
public sealed class MessageContentView : StackPanel
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MessageContentView, string?>(nameof(Text));

    public static readonly StyledProperty<bool> IsEditedProperty =
        AvaloniaProperty.Register<MessageContentView, bool>(nameof(IsEdited));

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, DejaVu Sans Mono, Menlo, monospace");

    static MessageContentView()
    {
        TextProperty.Changed.AddClassHandler<MessageContentView>((view, _) => view.Rebuild());
        IsEditedProperty.Changed.AddClassHandler<MessageContentView>((view, _) => view.Rebuild());
    }

    public MessageContentView()
    {
        Spacing = 4;
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsEdited
    {
        get => GetValue(IsEditedProperty);
        set => SetValue(IsEditedProperty, value);
    }

    private IBrush Resource(string key, string fallback) =>
        this.TryFindResource(key, out var value) && value is IBrush brush ? brush : new SolidColorBrush(Color.Parse(fallback));

    private void Rebuild()
    {
        Children.Clear();
        var text = Text;
        if (string.IsNullOrEmpty(text))
        {
            if (IsEdited)
            {
                Children.Add(CreateParagraph([], isQuote: false, emojiOnly: false));
            }

            return;
        }

        var blocks = MessageMarkup.Parse(text);
        var emojiOnly = MessageMarkup.IsEmojiOnly(text);
        for (var i = 0; i < blocks.Count; i++)
        {
            var isLast = i == blocks.Count - 1;
            Control control = blocks[i] switch
            {
                CodeBlock code => CreateCodeBlock(code),
                QuoteBlock quote => CreateQuote(quote.Spans, isLast),
                ParagraphBlock paragraph => CreateParagraph(paragraph.Spans, isQuote: false, emojiOnly, isLast),
                _ => new Control(),
            };
            Children.Add(control);
        }

        if (IsEdited && blocks.Count > 0 && blocks[^1] is CodeBlock)
        {
            Children.Add(CreateParagraph([], isQuote: false, emojiOnly: false));
        }
    }

    private LinkTextBlock CreateParagraph(IReadOnlyList<MarkupSpan> spans, bool isQuote, bool emojiOnly, bool isLast = true)
    {
        var block = new LinkTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = emojiOnly ? 34 : 14.5,
            LineHeight = emojiOnly ? 42 : 21,
            Foreground = Resource("TextPrimaryBrush", "#ECEDF1"),
        };

        var inlines = block.Inlines ??= [];
        var offset = 0;
        foreach (var span in spans)
        {
            var run = CreateRun(span);
            if (span.Kind == SpanKind.Link)
            {
                block.AddLink(offset, span.Text.Length, span.Text, run);
            }

            inlines.Add(run);
            offset += span.Text.Length;
        }

        if (IsEdited && isLast)
        {
            inlines.Add(new Run(" (edited)")
            {
                FontSize = 11,
                Foreground = Resource("TextMutedBrush", "#868A96"),
            });
        }

        return block;
    }

    private Control CreateQuote(IReadOnlyList<MarkupSpan> spans, bool isLast)
    {
        var text = CreateParagraph(spans, isQuote: true, emojiOnly: false, isLast);
        text.Foreground = Resource("TextSecondaryBrush", "#B7BAC4");
        return new Border
        {
            BorderBrush = Resource("TextMutedBrush", "#868A96"),
            BorderThickness = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(10, 0, 0, 0),
            Child = text,
        };
    }

    private Run CreateRun(MarkupSpan span) => span.Kind switch
    {
        SpanKind.Bold => new Run(span.Text) { FontWeight = FontWeight.Bold },
        SpanKind.Italic => new Run(span.Text) { FontStyle = FontStyle.Italic },
        SpanKind.Underline => new Run(span.Text) { TextDecorations = TextDecorations.Underline },
        SpanKind.Strikethrough => new Run(span.Text) { TextDecorations = TextDecorations.Strikethrough },
        SpanKind.Code => new Run(span.Text)
        {
            FontFamily = MonoFont,
            FontSize = 13,
            Background = Resource("RailBrush", "#17181C"),
        },
        SpanKind.Mention => new Run(span.Text)
        {
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#C9C3FF")),
            Background = Resource("MentionBrush", "#337C6CFF"),
        },
        SpanKind.Link => new Run(span.Text) { Foreground = Resource("LinkBrush", "#7FB4FF") },
        _ => new Run(span.Text),
    };

    private Control CreateCodeBlock(CodeBlock code)
    {
        return new Border
        {
            Background = Resource("RailBrush", "#17181C"),
            BorderBrush = Resource("DividerBrush", "#34363E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10),
            Margin = new Thickness(0, 2, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxWidth = 900,
            Child = new SelectableTextBlock
            {
                Text = code.Code,
                FontFamily = MonoFont,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Resource("TextPrimaryBrush", "#ECEDF1"),
            },
        };
    }
}

/// <summary>A selectable text block whose link runs open in the browser when clicked.</summary>
public sealed class LinkTextBlock : SelectableTextBlock
{
    private readonly List<(int Start, int Length, string Url, Run Run)> _links = [];
    private Run? _hovered;

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public void AddLink(int start, int length, string url, Run run) => _links.Add((start, length, url, run));

    private (string Url, Run Run)? LinkAt(Point point)
    {
        if (_links.Count == 0)
        {
            return null;
        }

        var position = point - new Point(Padding.Left, Padding.Top);
        var hit = TextLayout.HitTestPoint(position);
        if (!hit.IsInside)
        {
            return null;
        }

        foreach (var link in _links)
        {
            if (hit.TextPosition >= link.Start && hit.TextPosition < link.Start + link.Length)
            {
                return (link.Url, link.Run);
            }
        }

        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var link = LinkAt(e.GetPosition(this));
        if (link?.Run == _hovered)
        {
            return;
        }

        if (_hovered is not null)
        {
            _hovered.TextDecorations = null;
        }

        _hovered = link?.Run;
        if (_hovered is not null)
        {
            _hovered.TextDecorations = Avalonia.Media.TextDecorations.Underline;
        }

        Cursor = _hovered is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hovered is not null)
        {
            _hovered.TextDecorations = null;
            _hovered = null;
            Cursor = Cursor.Default;
        }
    }

    protected override async void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left || SelectionStart != SelectionEnd)
        {
            return;
        }

        var link = LinkAt(e.GetPosition(this));
        if (link is null || !Uri.TryCreate(link.Value.Url, UriKind.Absolute, out var uri))
        {
            return;
        }

        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is not null)
        {
            await launcher.LaunchUriAsync(uri);
        }
    }
}
