using Avalonia;
using Avalonia.Media;
using Discagem.Client.Services;

namespace Discagem.Client.ViewModels;

/// <summary>A theme in the settings, with the colours of its little preview.</summary>
public sealed class ThemeOption
{
    private ThemeOption(AppTheme theme, string name, string description, IBrush window, IBrush sidebar, IBrush header, IBrush text, IBrush accent, IBrush border)
    {
        Theme = theme;
        Name = name;
        Description = description;
        Window = window;
        Sidebar = sidebar;
        Header = header;
        Text = text;
        Accent = accent;
        Border = border;
    }

    public AppTheme Theme { get; }

    public string Name { get; }

    public string Description { get; }

    public IBrush Window { get; }

    public IBrush Sidebar { get; }

    public IBrush Header { get; }

    public IBrush Text { get; }

    public IBrush Accent { get; }

    public IBrush Border { get; }

    public static IReadOnlyList<ThemeOption> All { get; } =
    [
        new(AppTheme.Discagem, "Discagem", "The original dark look.",
            Solid("#26272D"), Solid("#1F2025"), Solid("#30323A"), Solid("#ECEDF1"), Solid("#7C6CFF"), Solid("#17181C")),
        new(AppTheme.Aero, "Aero", "Light and glossy, in the style of Windows 7. Messenger sounds.",
            Solid("#FFFFFF"), Vertical("#DCEAF8", "#B9D3EF"), Vertical("#8EBDEB", "#E4F0FB"), Solid("#1C1C1C"), Glossy("#7DBBF5", "#2E7FD6"), Solid("#9DBEE2")),
        new(AppTheme.AeroDark, "Aero Dark", "Aero on dark glass. Messenger sounds.",
            Solid("#0F1B2B"), Vertical("#172942", "#111F33"), Vertical("#2C5486", "#0E1F36"), Solid("#E6EEF8"), Glossy("#6FB3F2", "#2470C4"), Solid("#06101C")),
    ];

    private static IBrush Solid(string color) => new SolidColorBrush(Color.Parse(color));

    private static IBrush Vertical(string top, string bottom) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse(top), 0), new GradientStop(Color.Parse(bottom), 1) },
    };

    private static IBrush Glossy(string light, string dark) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse(light), 0),
            new GradientStop(Color.Parse(light), 0.45),
            new GradientStop(Color.Parse(dark), 0.55),
            new GradientStop(Color.Parse(dark), 1),
        },
    };
}
