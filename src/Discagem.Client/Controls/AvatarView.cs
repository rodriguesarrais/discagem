using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Discagem.Client.Controls;

public enum PresenceStatus
{
    None,
    Online,
    Offline,
    Live,
}

/// <summary>A round avatar with the user's initials and an optional presence dot.</summary>
public sealed class AvatarView : Control
{
    public static readonly StyledProperty<string?> DisplayNameProperty =
        AvaloniaProperty.Register<AvatarView, string?>(nameof(DisplayName));

    public static readonly StyledProperty<string?> ColorProperty =
        AvaloniaProperty.Register<AvatarView, string?>(nameof(Color));

    public static readonly StyledProperty<PresenceStatus> StatusProperty =
        AvaloniaProperty.Register<AvatarView, PresenceStatus>(nameof(Status));

    public static readonly StyledProperty<IBrush?> RingBrushProperty =
        AvaloniaProperty.Register<AvatarView, IBrush?>(nameof(RingBrush));

    private static readonly IBrush OnlineBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#3DBE73"));
    private static readonly IBrush OfflineBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#80848E"));
    private static readonly IBrush LiveBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#F04452"));
    private static readonly Typeface InitialsTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    static AvatarView()
    {
        AffectsRender<AvatarView>(DisplayNameProperty, ColorProperty, StatusProperty, RingBrushProperty);
    }

    public string? DisplayName
    {
        get => GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }

    public string? Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public PresenceStatus Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    /// <summary>Colour of the ring around the presence dot (usually the parent background).</summary>
    public IBrush? RingBrush
    {
        get => GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(40, 40);

    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var first = char.ConvertFromUtf32(char.ConvertToUtf32(words[0], 0)).ToUpperInvariant();
        if (words.Length == 1)
        {
            return first;
        }

        var last = words[^1];
        return first + char.ConvertFromUtf32(char.ConvertToUtf32(last, 0)).ToUpperInvariant();
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
        {
            return;
        }

        var radius = size / 2;
        var center = new Point(radius, radius);
        IBrush fill;
        try
        {
            fill = new SolidColorBrush(Avalonia.Media.Color.Parse(string.IsNullOrEmpty(Color) ? "#7C6CFF" : Color));
        }
        catch (FormatException)
        {
            fill = new SolidColorBrush(Colors.SlateBlue);
        }

        context.DrawEllipse(fill, null, center, radius, radius);

        var text = new FormattedText(
            Initials(DisplayName),
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            InitialsTypeface,
            Math.Max(8, size * (Initials(DisplayName).Length > 1 ? 0.36 : 0.42)),
            Brushes.White);
        context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));

        var statusBrush = Status switch
        {
            PresenceStatus.Online => OnlineBrush,
            PresenceStatus.Offline => OfflineBrush,
            PresenceStatus.Live => LiveBrush,
            _ => null,
        };

        if (statusBrush is null)
        {
            return;
        }

        var dotRadius = Math.Max(4, size * 0.16);
        var ring = Math.Max(2, size * 0.065);
        var dotCenter = new Point(size - dotRadius * 0.85, size - dotRadius * 0.85);
        if (RingBrush is not null)
        {
            context.DrawEllipse(RingBrush, null, dotCenter, dotRadius + ring, dotRadius + ring);
        }

        context.DrawEllipse(statusBrush, null, dotCenter, dotRadius, dotRadius);
    }
}
