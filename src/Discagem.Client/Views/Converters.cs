using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Discagem.Client.Views;

/// <summary>Small value converters used by the views.</summary>
public static class Converters
{
    private static readonly IBrush Primary = new SolidColorBrush(Color.Parse("#ECEDF1"));

    public static readonly IValueConverter BoolToSemiBold =
        new FuncValueConverter<bool, FontWeight>(value => value ? FontWeight.SemiBold : FontWeight.Medium);

    /// <summary>Unread channels use the primary text colour; others keep the inherited item colour.</summary>
    public static readonly IValueConverter UnreadForeground =
        new FuncValueConverter<bool, object?>(value => value ? Primary : Avalonia.AvaloniaProperty.UnsetValue);

    public static readonly IValueConverter WatchTooltip =
        new FuncValueConverter<bool, string>(isMine => isMine ? "This is your stream" : "Watch stream");

    public static readonly IValueConverter MuteTooltip =
        new FuncValueConverter<bool, string>(off => off ? "Unmute" : "Mute");

    public static readonly IValueConverter DeafenTooltip =
        new FuncValueConverter<bool, string>(deafened => deafened ? "Undeafen" : "Deafen");

    public static readonly IValueConverter ColorToBrush =
        new FuncValueConverter<string, IBrush>(value =>
        {
            try
            {
                return new SolidColorBrush(Color.Parse(value ?? "#7C6CFF"));
            }
            catch (FormatException)
            {
                return Brushes.Gray;
            }
        });

    public static readonly IValueConverter StatusForeground =
        new FuncValueConverter<bool, IBrush>(isError => isError ? new SolidColorBrush(Color.Parse("#FF8A8F")) : new SolidColorBrush(Color.Parse("#3DBE73")));

    public static readonly IValueConverter BannerBackground =
        new FuncValueConverter<bool, IBrush>(isError => isError ? new SolidColorBrush(Color.Parse("#C93A3F")) : new SolidColorBrush(Color.Parse("#4A44A8")));

    public static readonly IValueConverter GroupMargin =
        new FuncValueConverter<bool, Avalonia.Thickness>(isGroupStart => isGroupStart ? new Avalonia.Thickness(0, 14, 0, 0) : new Avalonia.Thickness(0));
}
