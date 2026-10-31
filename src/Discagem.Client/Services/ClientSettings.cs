using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Discagem.Client.Services;

/// <summary>User preferences and the remembered session, stored as JSON in the user's profile.</summary>
public sealed class ClientSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string ServerAddress { get; set; } = "";

    public string Username { get; set; } = "";

    /// <summary>Access token protected with DPAPI on Windows (base64 elsewhere, with a user-only file).</summary>
    public string? ProtectedToken { get; set; }

    public long? LastChannelId { get; set; }

    public int StreamQualityIndex { get; set; } = 2;

    public bool ShowMembers { get; set; } = true;

    /// <summary>Microphone device id ("" for the system default).</summary>
    public string VoiceInputDevice { get; set; } = "";

    /// <summary>Speaker device id ("" for the system default).</summary>
    public string VoiceOutputDevice { get; set; } = "";

    /// <summary>Input sensitivity: the microphone transmits above this level (dBFS).</summary>
    public float VoiceThresholdDb { get; set; } = -50f;

    /// <summary>Playback volume, 1 = 100 %.</summary>
    public float VoiceOutputVolume { get; set; } = 1f;

    public bool VoiceMuted { get; set; }

    public bool VoiceDeafened { get; set; }

    /// <summary>Per-user playback volume (user id to gain; 1 = 100 %).</summary>
    public Dictionary<long, float> UserVolumes { get; set; } = [];

    /// <summary>Whether to send the computer's sound (or the shared window's) with a screen share.</summary>
    public bool StreamShareAudio { get; set; } = true;

    /// <summary>Volume of each streamer's stream audio (user id to gain; 1 = 100 %).</summary>
    public Dictionary<long, float> StreamVolumes { get; set; } = [];

    /// <summary>
    /// Whether closing the window keeps Discagem running in the notification area (null: the
    /// platform default, on for Windows; Linux desktops do not all show tray icons).
    /// </summary>
    public bool? CloseToTray { get; set; }

    [JsonIgnore]
    public bool CloseToTrayEnabled => CloseToTray ?? OperatingSystem.IsWindows();

    /// <summary>Whether to look for new releases at start-up and every few hours.</summary>
    public bool CheckForUpdates { get; set; } = true;

    public double WindowWidth { get; set; } = 1280;

    public double WindowHeight { get; set; } = 800;

    public bool WindowMaximized { get; set; }

    [JsonIgnore]
    public string? Token
    {
        get => Unprotect(ProtectedToken);
        set => ProtectedToken = Protect(value);
    }

    /// <summary>
    /// Settings directory. DISCAGEM_PROFILE selects an alternative profile, which makes it possible
    /// to run several clients side by side on one machine.
    /// </summary>
    public static string Directory
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var profile = Environment.GetEnvironmentVariable("DISCAGEM_PROFILE");
            var name = string.IsNullOrWhiteSpace(profile) ? "Discagem" : $"Discagem-{profile}";
            return Path.Combine(root, name);
        }
    }

    private static string FilePath => Path.Combine(Directory, "settings.json");

    public static ClientSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new ClientSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings: start fresh.
        }

        return new ClientSettings();
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not save settings: {ex.Message}");
        }
    }

    private static string? Protect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        if (OperatingSystem.IsWindows())
        {
            bytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return "dpapi:" + Convert.ToBase64String(bytes);
        }

        return "plain:" + Convert.ToBase64String(bytes);
    }

    private static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            if (value.StartsWith("dpapi:", StringComparison.Ordinal) && OperatingSystem.IsWindows())
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value[6..]), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }

            if (value.StartsWith("plain:", StringComparison.Ordinal))
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value[6..]));
            }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Token from another machine or user: ignore it.
        }

        return null;
    }

    private static readonly byte[] Entropy = "Discagem.Token.v1"u8.ToArray();
}
