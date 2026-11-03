using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Discagem.Client.Services;

/// <summary>
/// Starts Discagem when the user signs in to the computer: a value under the Run key on Windows,
/// an XDG autostart entry elsewhere. The system's entry is the source of truth, so turning it off
/// in Windows' Startup apps shows here too. Discagem started this way gets
/// <see cref="Argument"/>, so that it can stay out of the way (see
/// <see cref="ClientSettings.StartMinimized"/>).
/// </summary>
public static class Autostart
{
    public const string Argument = "--autostart";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DesktopId = "io.github.rodriguesarrais.Discagem";

    /// <summary>
    /// Whether the entry can be written: not inside a Flatpak sandbox, which cannot write the host's
    /// autostart folder (and would need the background portal).
    /// </summary>
    public static bool IsSupported =>
        Environment.ProcessPath is not null
        && (OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("FLATPAK_ID") is null));

    /// <summary>The entry's name: one per profile, so that test profiles do not replace each other.</summary>
    private static string Name
    {
        get
        {
            var profile = Environment.GetEnvironmentVariable("DISCAGEM_PROFILE");
            return string.IsNullOrWhiteSpace(profile) ? "Discagem" : $"Discagem-{profile}";
        }
    }

    public static bool IsEnabled
    {
        get
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    return ReadRunValue() is not null;
                }

                return IsSupported && File.Exists(DesktopFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return false;
            }
        }
    }

    /// <summary>Adds or removes the entry; returns false (after logging why) when that failed.</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                WriteRunValue(enabled);
            }
            else if (enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DesktopFile)!);
                File.WriteAllText(DesktopFile, DesktopEntry());
            }
            else
            {
                File.Delete(DesktopFile);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ClientLog.Error($"Could not {(enabled ? "add" : "remove")} the start-up entry: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Points an existing entry at this executable, which moves when Discagem is reinstalled
    /// elsewhere or a profile runs from another build.
    /// </summary>
    public static void Refresh()
    {
        if (IsSupported && IsEnabled)
        {
            SetEnabled(true);
        }
    }

    private static string Command => $"\"{Environment.ProcessPath}\" {Argument}";

    [SupportedOSPlatform("windows")]
    private static string? ReadRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Name) as string;
    }

    [SupportedOSPlatform("windows")]
    private static void WriteRunValue(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(Name, Command);
        }
        else
        {
            key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }

    private static string DesktopFile
    {
        get
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(config))
            {
                config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }

            var file = Name == "Discagem" ? DesktopId : $"{DesktopId}.{Name}";
            return Path.Combine(config, "autostart", file + ".desktop");
        }
    }

    private static string DesktopEntry()
    {
        var profile = Environment.GetEnvironmentVariable("DISCAGEM_PROFILE");
        var exec = string.IsNullOrWhiteSpace(profile) ? Command : $"env DISCAGEM_PROFILE={profile} {Command}";
        return string.Join('\n',
            "[Desktop Entry]",
            "Type=Application",
            "Name=Discagem",
            $"Exec={exec}",
            $"Icon={DesktopId}",
            "Terminal=false",
            "X-GNOME-Autostart-enabled=true",
            "");
    }
}
