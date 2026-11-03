using Avalonia.Data.Converters;
using Avalonia.Media;
using Discagem.Client.Resources;

namespace Discagem.Client.Views;

/// <summary>Small value converters used by the views.</summary>
public static class Converters
{
    public static readonly IValueConverter WatchTooltip =
        new FuncValueConverter<bool, string>(isMine => isMine ? Strings.Shell_ThisIsYourStream : Strings.Shell_WatchStream);

    public static readonly IValueConverter MuteTooltip =
        new FuncValueConverter<bool, string>(off => off ? Strings.Voice_Unmute : Strings.Voice_Mute);

    public static readonly IValueConverter DeafenTooltip =
        new FuncValueConverter<bool, string>(deafened => deafened ? Strings.Voice_Undeafen : Strings.Voice_Deafen);

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

    /// <summary>Whether the selected index (of the settings pages, say) is the one given as the parameter.</summary>
    public static readonly IValueConverter IsIndex =
        new FuncValueConverter<int, string, bool>((index, parameter) =>
            int.TryParse(parameter, System.Globalization.CultureInfo.InvariantCulture, out var expected) && index == expected);

    public static readonly IValueConverter GroupMargin =
        new FuncValueConverter<bool, Avalonia.Thickness>(isGroupStart => isGroupStart ? new Avalonia.Thickness(0, 14, 0, 0) : new Avalonia.Thickness(0));
}
