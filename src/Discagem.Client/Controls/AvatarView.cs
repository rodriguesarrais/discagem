using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;

namespace Discagem.Client.Controls;

public enum PresenceStatus
{
    None,
    Online,
    Offline,
    Live,
}

public enum AvatarShape
{
    /// <summary>A circle with a presence dot.</summary>
    Circle,

    /// <summary>A rounded square in a glossy frame coloured by presence (the Aero themes).</summary>
    Framed,
}

/// <summary>An avatar (the profile photo, or the user's initials) showing presence.</summary>
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

    public static readonly StyledProperty<bool> IsSpeakingProperty =
        AvaloniaProperty.Register<AvatarView, bool>(nameof(IsSpeaking));

    public static readonly StyledProperty<IImage?> ImageProperty =
        AvaloniaProperty.Register<AvatarView, IImage?>(nameof(Image));

    public static readonly StyledProperty<AvatarShape> ShapeProperty =
        AvaloniaProperty.Register<AvatarView, AvatarShape>(nameof(Shape));

    public static readonly StyledProperty<IBrush> OnlineBrushProperty =
        AvaloniaProperty.Register<AvatarView, IBrush>(nameof(OnlineBrush), new ImmutableSolidColorBrush(Avalonia.Media.Color.Parse("#3DBE73")));

    public static readonly StyledProperty<IBrush> OfflineBrushProperty =
        AvaloniaProperty.Register<AvatarView, IBrush>(nameof(OfflineBrush), new ImmutableSolidColorBrush(Avalonia.Media.Color.Parse("#80848E")));

    public static readonly StyledProperty<IBrush> LiveBrushProperty =
        AvaloniaProperty.Register<AvatarView, IBrush>(nameof(LiveBrush), new ImmutableSolidColorBrush(Avalonia.Media.Color.Parse("#F04452")));

    /// <summary>Colour of the frame of a <see cref="AvatarShape.Framed"/> avatar without a presence.</summary>
    public static readonly StyledProperty<IBrush> FrameBrushProperty =
        AvaloniaProperty.Register<AvatarView, IBrush>(nameof(FrameBrush), new ImmutableSolidColorBrush(Avalonia.Media.Color.Parse("#8FB2D6")));

    private static readonly Typeface InitialsTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    static AvatarView()
    {
        AffectsRender<AvatarView>(DisplayNameProperty, ColorProperty, StatusProperty, RingBrushProperty, IsSpeakingProperty, ImageProperty,
            ShapeProperty, OnlineBrushProperty, OfflineBrushProperty, LiveBrushProperty, FrameBrushProperty);
    }

    public AvatarShape Shape
    {
        get => GetValue(ShapeProperty);
        set => SetValue(ShapeProperty, value);
    }

    public IBrush OnlineBrush
    {
        get => GetValue(OnlineBrushProperty);
        set => SetValue(OnlineBrushProperty, value);
    }

    public IBrush OfflineBrush
    {
        get => GetValue(OfflineBrushProperty);
        set => SetValue(OfflineBrushProperty, value);
    }

    public IBrush LiveBrush
    {
        get => GetValue(LiveBrushProperty);
        set => SetValue(LiveBrushProperty, value);
    }

    public IBrush FrameBrush
    {
        get => GetValue(FrameBrushProperty);
        set => SetValue(FrameBrushProperty, value);
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

    /// <summary>Draws a green ring around the avatar while the person is heard in voice.</summary>
    public bool IsSpeaking
    {
        get => GetValue(IsSpeakingProperty);
        set => SetValue(IsSpeakingProperty, value);
    }

    /// <summary>The profile photo; the initials are drawn while there is none.</summary>
    public IImage? Image
    {
        get => GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
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

        if (Shape == AvatarShape.Framed)
        {
            RenderFramed(context, size);
            return;
        }

        var radius = size / 2;
        var center = new Point(radius, radius);

        // While speaking, shrink the avatar a little and draw the ring in the freed space.
        var avatarRadius = IsSpeaking ? radius - 3 : radius;
        if (IsSpeaking)
        {
            context.DrawEllipse(null, new Pen(OnlineBrush, 2), center, radius - 1, radius - 1);
        }

        var target = new Rect(center.X - avatarRadius, center.Y - avatarRadius, avatarRadius * 2, avatarRadius * 2);
        using (context.PushGeometryClip(new EllipseGeometry(target)))
        {
            DrawPicture(context, target);
        }

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

    /// <summary>The photo, or the initials on the avatar colour, filling <paramref name="target"/> (clipped by the caller).</summary>
    private void DrawPicture(DrawingContext context, Rect target)
    {
        if (Image is { } image && image.Size.Width > 0 && image.Size.Height > 0)
        {
            var side = Math.Min(image.Size.Width, image.Size.Height);
            var source = new Rect((image.Size.Width - side) / 2, (image.Size.Height - side) / 2, side, side);
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            {
                context.DrawImage(image, source, target);
            }

            return;
        }

        IBrush fill;
        try
        {
            fill = new SolidColorBrush(Avalonia.Media.Color.Parse(string.IsNullOrEmpty(Color) ? "#7C6CFF" : Color));
        }
        catch (FormatException)
        {
            fill = new SolidColorBrush(Colors.SlateBlue);
        }

        context.DrawRectangle(fill, null, target);
        var initials = Initials(DisplayName);
        var text = new FormattedText(
            initials,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            InitialsTypeface,
            Math.Max(8, Math.Min(target.Width, target.Height) * (initials.Length > 1 ? 0.36 : 0.42)),
            Brushes.White);
        context.DrawText(text, new Point(target.Center.X - text.Width / 2, target.Center.Y - text.Height / 2));
    }

    /// <summary>
    /// A rounded square picture inside a glossy frame whose colour tells the presence (green when
    /// online or speaking, grey when offline, red when live).
    /// </summary>
    private void RenderFramed(DrawingContext context, double size)
    {
        var frameBrush = IsSpeaking ? OnlineBrush : Status switch
        {
            PresenceStatus.Online => OnlineBrush,
            PresenceStatus.Offline => OfflineBrush,
            PresenceStatus.Live => LiveBrush,
            _ => FrameBrush,
        };
        var color = frameBrush is ISolidColorBrush solid ? solid.Color : Avalonia.Media.Color.Parse("#8FB2D6");

        var thickness = Math.Max(2, Math.Round(size * (IsSpeaking ? 0.12 : 0.09)));
        var radius = Math.Max(3, size * 0.22);
        var outer = new Rect(0.5, 0.5, size - 1, size - 1);

        // The frame: a vertical gradient with a bright upper half, like glass catching the light.
        var frame = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Mix(color, Colors.White, 0.62), 0),
                new GradientStop(Mix(color, Colors.White, 0.22), 0.48),
                new GradientStop(color, 0.52),
                new GradientStop(Mix(color, Colors.Black, 0.12), 1),
            },
        };
        var edge = new Pen(new SolidColorBrush(Mix(color, Colors.Black, 0.38)), 1);
        context.DrawRectangle(frame, edge, outer, radius, radius);

        var inner = new Rect(thickness, thickness, size - thickness * 2, size - thickness * 2);
        var innerRadius = Math.Max(1.5, radius - thickness * 0.75);
        using (context.PushGeometryClip(new RectangleGeometry(inner, innerRadius, innerRadius)))
        {
            DrawPicture(context, inner);
        }

        // A thin light line between the frame and the picture, and a dark line inside it.
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Avalonia.Media.Color.FromArgb(200, 255, 255, 255)), 1),
            inner.Inflate(0.5), innerRadius + 0.5, innerRadius + 0.5);
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Avalonia.Media.Color.FromArgb(70, 0, 0, 0)), 1),
            inner.Deflate(0.5), innerRadius, innerRadius);
    }

    private static Color Mix(Color from, Color to, double amount) => Avalonia.Media.Color.FromArgb(
        from.A,
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));
}
