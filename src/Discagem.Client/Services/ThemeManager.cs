using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Discagem.Media.Audio;

namespace Discagem.Client.Services;

/// <summary>The looks the client can wear.</summary>
public enum AppTheme
{
    /// <summary>The original dark look.</summary>
    Discagem,

    /// <summary>Glossy, light Windows 7 / Messenger look.</summary>
    Aero,

    /// <summary>The Aero look on dark glass.</summary>
    AeroDark,
}

/// <summary>The notification sounds of a theme (48 kHz mono samples).</summary>
public sealed record ThemeSounds(float[] Join, float[] Leave, float[] Message);

/// <summary>
/// Appearance options on top of the theme: an accent colour (null for the theme's), the size of message
/// text, compact messages and a zoom factor for the whole window.
/// </summary>
public sealed record AppearanceOptions(string? AccentColor = null, double MessageFontSize = AppearanceOptions.DefaultFontSize, bool CompactMessages = false, double Zoom = 1.0)
{
    public const double DefaultFontSize = 14.5;

    public static AppearanceOptions Default { get; } = new();
}

/// <summary>
/// Switches themes while the app runs: replaces the palette (views use DynamicResource), adds or
/// removes the theme's extra styles, picks the matching light or dark variant of the built-in
/// controls and swaps the notification sounds.
/// </summary>
public static class ThemeManager
{
    private static readonly Dictionary<AppTheme, IResourceDictionary> Palettes = [];
    private static readonly Lazy<ThemeSounds> DefaultSounds = new(() => new ThemeSounds(VoiceCues.Join, VoiceCues.Leave, VoiceCues.Message));
    private static readonly Lazy<ThemeSounds> MessengerSounds = new(LoadMessengerSounds);
    private static IStyle? _aeroStyles;

    public static AppTheme Current { get; private set; }

    /// <summary>Raised on the UI thread after a theme was applied.</summary>
    public static event Action? Changed;

    public static ThemeSounds Sounds => Current == AppTheme.Discagem ? DefaultSounds.Value : MessengerSounds.Value;

    public static bool IsAero(AppTheme theme) => theme is AppTheme.Aero or AppTheme.AeroDark;

    /// <summary>The appearance options in effect (see <see cref="ApplyAppearance"/>).</summary>
    public static AppearanceOptions Appearance { get; private set; } = AppearanceOptions.Default;

    private static readonly ResourceDictionary Overrides = new();

    /// <summary>
    /// Applies appearance options on top of the theme: the accent colour replaces the theme's in
    /// buttons, highlights and links. Must run on the UI thread, after the application was initialised.
    /// </summary>
    public static void ApplyAppearance(AppearanceOptions options)
    {
        var app = Application.Current ?? throw new InvalidOperationException("The application is not running.");
        options = options with
        {
            MessageFontSize = Math.Clamp(options.MessageFontSize, 12, 20),
            Zoom = Math.Clamp(options.Zoom, 0.8, 1.5),
        };

        Overrides.Clear();
        Overrides["MessageFontSize"] = options.MessageFontSize;
        if (options.AccentColor is { } accent && Color.TryParse(accent, out var color))
        {
            Overrides["AccentBrush"] = new SolidColorBrush(color);
            Overrides["SystemControlHighlightAccentBrush"] = new SolidColorBrush(color);
            Overrides["PrimaryButtonBrush"] = new SolidColorBrush(color);
            Overrides["PrimaryButtonHoverBrush"] = new SolidColorBrush(Shade(color, 0.88));
            Overrides["PrimaryButtonPressedBrush"] = new SolidColorBrush(Shade(color, 0.76));
            Overrides["LinkBrush"] = new SolidColorBrush(color);
        }

        // Merged dictionaries added later take precedence over the palette.
        var dictionaries = app.Resources.MergedDictionaries;
        dictionaries.Remove(Overrides);
        dictionaries.Add(Overrides);

        Appearance = options;
        Changed?.Invoke();

        static Color Shade(Color color, double factor) =>
            Color.FromRgb((byte)(color.R * factor), (byte)(color.G * factor), (byte)(color.B * factor));
    }

    /// <summary>Applies a theme. Must run on the UI thread, after the application was initialised.</summary>
    public static void Apply(AppTheme theme)
    {
        if (!Enum.IsDefined(theme))
        {
            theme = AppTheme.Discagem;
        }

        var app = Application.Current ?? throw new InvalidOperationException("The application is not running.");

        // App.axaml lists the palette first among the merged dictionaries.
        var dictionaries = app.Resources.MergedDictionaries;
        var palette = Palette(theme);
        if (dictionaries.Count == 0)
        {
            dictionaries.Add(palette);
        }
        else if (!ReferenceEquals(dictionaries[0], palette))
        {
            dictionaries[0] = palette;
        }

        _aeroStyles ??= (IStyle)AvaloniaXamlLoader.Load(new Uri("avares://Discagem/Styles/Aero.axaml"));
        var hasAero = app.Styles.Contains(_aeroStyles);
        if (IsAero(theme) && !hasAero)
        {
            app.Styles.Add(_aeroStyles);
        }
        else if (!IsAero(theme) && hasAero)
        {
            app.Styles.Remove(_aeroStyles);
        }

        app.RequestedThemeVariant = theme == AppTheme.Aero ? ThemeVariant.Light : ThemeVariant.Dark;

        var changed = Current != theme;
        Current = theme;
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private static IResourceDictionary Palette(AppTheme theme)
    {
        if (!Palettes.TryGetValue(theme, out var palette))
        {
            var file = theme switch
            {
                AppTheme.Aero => "AeroPalette.axaml",
                AppTheme.AeroDark => "AeroDarkPalette.axaml",
                _ => "Palette.axaml",
            };
            palette = (IResourceDictionary)AvaloniaXamlLoader.Load(new Uri($"avares://Discagem/Styles/{file}"));
            Palettes[theme] = palette;
        }

        return palette;
    }

    private static ThemeSounds LoadMessengerSounds()
    {
        try
        {
            return new ThemeSounds(Load("join.wav"), Load("leave.wav"), Load("message.wav"));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            ClientLog.Error($"Could not load the theme's sounds: {ex.Message}");
            return DefaultSounds.Value;
        }

        static float[] Load(string name)
        {
            using var stream = AssetLoader.Open(new Uri($"avares://Discagem/Assets/Sounds/{name}"));
            return WavAudio.ReadMono(stream);
        }
    }
}
