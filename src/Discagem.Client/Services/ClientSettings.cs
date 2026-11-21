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

    /// <summary>Volume of soundboard sounds (1 = as uploaded).</summary>
    public float SoundboardVolume { get; set; } = 0.5f;

    public bool VoiceMuted { get; set; }

    public bool VoiceDeafened { get; set; }

    /// <summary>Per-user playback volume (user id to gain; 1 = 100 %).</summary>
    public Dictionary<long, float> UserVolumes { get; set; } = [];

    /// <summary>Whether to send the computer's sound (or the shared window's) with a screen share.</summary>
    public bool StreamShareAudio { get; set; } = true;

    /// <summary>Accent colour (#RRGGBB) used instead of the theme's, or null.</summary>
    public string? AccentColor { get; set; }

    /// <summary>Size of message text, in points.</summary>
    public double MessageFontSize { get; set; } = AppearanceOptions.DefaultFontSize;

    /// <summary>Smaller avatars and less space between messages.</summary>
    public bool CompactMessages { get; set; }

    /// <summary>Scale of the whole window (1 = 100 %).</summary>
    public double Zoom { get; set; } = 1.0;

    public AppearanceOptions Appearance => new(AccentColor, MessageFontSize, CompactMessages, Zoom);

    /// <summary>The room shown last.</summary>
    public long? LastRoomId { get; set; }

    /// <summary>The channel shown last in each room (room id to channel id).</summary>
    public Dictionary<long, long> LastChannelByRoom { get; set; } = [];

    /// <summary>The user's order of the room rail (room ids); rooms not listed go at the end.</summary>
    public List<long> RoomOrder { get; set; } = [];

    /// <summary>Volume of each streamer's stream audio (user id to gain; 1 = 100 %).</summary>
    public Dictionary<long, float> StreamVolumes { get; set; } = [];

    /// <summary>
    /// Volume of each application in the sound of your own streams (application key, the executable's
    /// name in lower case, to gain; 1 = 100 %, 0 = left out).
    /// </summary>
    public Dictionary<string, float> StreamApplicationVolumes { get; set; } = [];

    /// <summary>
    /// Whether closing the window keeps Discagem running in the notification area (null: the
    /// platform default, on for Windows; Linux desktops do not all show tray icons).
    /// </summary>
    public bool? CloseToTray { get; set; }

    [JsonIgnore]
    public bool CloseToTrayEnabled => CloseToTray ?? OperatingSystem.IsWindows();

    /// <summary>Whether to look for new releases at start-up and every few hours.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// The theme's name (see <see cref="AppTheme"/>). Kept as text so that a name this version does
    /// not know, written by a newer one, falls back to the default instead of failing to load.
    /// </summary>
    public string Theme { get; set; } = nameof(AppTheme.Discagem);

    [JsonIgnore]
    public AppTheme ThemeValue
    {
        get => Enum.TryParse<AppTheme>(Theme, ignoreCase: true, out var theme) && Enum.IsDefined(theme) ? theme : AppTheme.Discagem;
        set => Theme = value.ToString();
    }

    /// <summary>Play a sound when a message arrives that is not in view.</summary>
    public bool MessageSounds { get; set; } = true;

    /// <summary>Play the message sound for @mentions even when message sounds are off.</summary>
    public bool MentionSounds { get; set; } = true;

    /// <summary>Sounds when people join or leave the call.</summary>
    public bool CallSounds { get; set; } = true;

    /// <summary>Flash the taskbar entry when a message arrives in the background.</summary>
    public bool TaskbarAlerts { get; set; } = true;

    /// <summary>Notification level of each room (room id to level); rooms not listed get every message.</summary>
    public Dictionary<long, NotificationLevel> RoomNotifications { get; set; } = [];

    /// <summary>Notification level of channels that do not follow their room's (channel id to level).</summary>
    public Dictionary<long, NotificationLevel> ChannelNotifications { get; set; } = [];

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
            // Create: without it the path is empty when the folder (~/.config) does not exist yet,
            // and the settings would end up in the current directory.
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
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

/// <summary>What a room or channel notifies about.</summary>
public enum NotificationLevel
{
    /// <summary>For a channel: as its room. For a room: every message.</summary>
    Default = 0,

    /// <summary>Every message marks the channel unread, plays the sound and flashes the taskbar.</summary>
    All = 1,

    /// <summary>Messages mark the channel unread, but only @mentions play the sound and flash the taskbar.</summary>
    Mentions = 2,

    /// <summary>Nothing: no unread mark, no sound, no alert, even for @mentions.</summary>
    Nothing = 3,
}
