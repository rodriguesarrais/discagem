using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Discagem.Client.Views;

/// <summary>Small value converters used by the views.</summary>
public static class Converters
{
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

    public static readonly IValueConverter GroupMargin =
        new FuncValueConverter<bool, Avalonia.Thickness>(isGroupStart => isGroupStart ? new Avalonia.Thickness(0, 14, 0, 0) : new Avalonia.Thickness(0));
}
