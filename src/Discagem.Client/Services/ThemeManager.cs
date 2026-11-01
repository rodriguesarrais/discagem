using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
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
