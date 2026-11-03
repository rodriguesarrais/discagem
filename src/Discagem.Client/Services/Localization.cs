using System.Globalization;
using Discagem.Client.Resources;

namespace Discagem.Client.Services;

/// <summary>A language the interface can be shown in ("" follows the system).</summary>
public sealed record LanguageOption(string Code, string Name);

/// <summary>
/// Chooses the interface language. The text lives in Resources/Strings.resx (English) and its
/// translations (Strings.pt-BR.resx); the language is applied once at start-up, before any window
/// is built, so changing it takes effect at the next start.
/// </summary>
public static class Localization
{
    private static readonly string[] Supported = ["en", "pt-BR"];

    /// <summary>The system's language, kept before <see cref="Apply"/> replaces the current one.</summary>
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;

    /// <summary>The choices for the settings, each named in its own language.</summary>
    public static IReadOnlyList<LanguageOption> Options =>
    [
        new("", Strings.Settings_LanguageSystem),
        new("en", "English"),
        new("pt-BR", "Português (Brasil)"),
    ];

    /// <summary>The language in use since start-up.</summary>
    public static string Current { get; private set; } = "en";

    /// <summary>The culture a setting stands for ("" and unknown names follow the system).</summary>
    public static string Resolve(string language) =>
        Supported.FirstOrDefault(s => string.Equals(s, language, StringComparison.OrdinalIgnoreCase))
        ?? FromSystem(SystemCulture);

    public static void Apply(string language)
    {
        var explicitChoice = Supported.Contains(language, StringComparer.OrdinalIgnoreCase);
        var ui = CultureInfo.GetCultureInfo(Resolve(language));
        Current = ui.Name;

        // An explicit choice also sets dates and numbers; following the system keeps its formats
        // (Portuguese text with the formats of Portugal, say).
        if (explicitChoice)
        {
            CultureInfo.DefaultThreadCurrentCulture = ui;
            CultureInfo.CurrentCulture = ui;
        }

        CultureInfo.DefaultThreadCurrentUICulture = ui;
        CultureInfo.CurrentUICulture = ui;
        Strings.Culture = ui;
    }

    /// <summary>Portuguese of any country gets the Brazilian translation; everything else English.</summary>
    private static string FromSystem(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName == "pt" ? "pt-BR" : "en";
}
