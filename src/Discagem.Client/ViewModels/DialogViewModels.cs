using System.Reflection;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Discagem.Client.Resources;
using Discagem.Client.Services;
using Discagem.Client.Services.Audio;
using Discagem.Media.Pipeline;
using Discagem.Media.Vpx;
using Discagem.Shared;

namespace Discagem.Client.ViewModels;

/// <summary>Create or rename a text or voice channel.</summary>
public sealed partial class ChannelEditorViewModel : ObservableObject
{
    private ChannelEditorViewModel(string heading, string confirmText, bool canChooseKind, ChannelKind kind, string name, string topic)
    {
        Heading = heading;
        ConfirmText = confirmText;
        CanChooseKind = canChooseKind;
        _kind = kind;
        _name = name;
        _topic = topic;
    }

    public static ChannelEditorViewModel ForCreate(ChannelKind kind) =>
        new(Strings.ChannelVm_CreateHeading, Strings.ChannelVm_CreateConfirm, canChooseKind: true, kind, "", "");

    public static ChannelEditorViewModel ForEdit(ChannelKind kind, string name, string topic) =>
        new(Strings.ChannelVm_EditHeading, Strings.ChannelVm_EditConfirm, canChooseKind: false, kind, name, topic);

    public string Heading { get; }

    public string ConfirmText { get; }

    /// <summary>The kind can only be chosen when creating a channel.</summary>
    public bool CanChooseKind { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsText), nameof(IsVoice), nameof(Preview), nameof(Watermark))]
    private ChannelKind _kind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm), nameof(Preview))]
    private string _name;

    [ObservableProperty]
    private string _topic;

    public bool IsText
    {
        get => Kind == ChannelKind.Text;
        set
        {
            if (value)
            {
                Kind = ChannelKind.Text;
            }
        }
    }

    public bool IsVoice
    {
        get => Kind == ChannelKind.Voice;
        set
        {
            if (value)
            {
                Kind = ChannelKind.Voice;
            }
        }
    }

    public string Watermark => IsVoice ? Strings.ChannelVm_VoiceWatermark : Strings.ChannelVm_TextWatermark;

    public bool CanConfirm => !string.IsNullOrWhiteSpace(Name);

    /// <summary>Approximates the server-side normalisation so users see the final name.</summary>
    public string Preview
    {
        get
        {
            if (IsVoice)
            {
                return string.Join(' ', Name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            var chars = Name.Trim().ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : char.IsWhiteSpace(c) || c == '-' ? '-' : '\0')
                .Where(c => c != '\0')
                .ToArray();
            var value = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
            return value.Length == 0 ? "" : "#" + value;
        }
    }
}


/// <summary>Account and app settings, in pages chosen from a list on the left.</summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>Lowest level shown on the input meter and sensitivity slider, in dBFS.</summary>
    public const double MeterFloorDb = -80;

    private readonly ApiClient _api;
    private readonly ClientSettings _settings;
    private readonly Action<AuthResponse> _tokenChanged;
    private readonly Func<string?> _hubConnectionId;
    private readonly VoiceService _voice;
    private readonly DispatcherTimer _meterTimer;
    private readonly IDialogService _dialogs;
    private readonly Func<Task<string>> _checkForUpdates;

    public SettingsViewModel(
        ApiClient api,
        ClientSettings settings,
        MemberViewModel me,
        string serverName,
        Action<AuthResponse> tokenChanged,
        Func<string?> hubConnectionId,
        VoiceService voice,
        IDialogService dialogs,
        Func<Task<string>> checkForUpdates)
    {
        _dialogs = dialogs;
        _checkForUpdates = checkForUpdates;
        _checkForUpdatesAutomatically = settings.CheckForUpdates;
        _api = api;
        _settings = settings;
        _tokenChanged = tokenChanged;
        _hubConnectionId = hubConnectionId;
        _voice = voice;
        Me = me;
        ServerName = serverName;
        _displayName = me.DisplayName;
        _avatarColor = me.AvatarColor;
        _defaultQuality = StreamQuality.Presets[Math.Clamp(settings.StreamQualityIndex, 0, StreamQuality.Presets.Length - 1)];

        InputDevices = voice.Audio.InputDevices();
        OutputDevices = voice.Audio.OutputDevices();
        _inputDevice = InputDevices.FirstOrDefault(d => d.Id == settings.VoiceInputDevice) ?? InputDevices[0];
        _outputDevice = OutputDevices.FirstOrDefault(d => d.Id == settings.VoiceOutputDevice) ?? OutputDevices[0];
        _thresholdDb = settings.VoiceThresholdDb;
        _outputVolumePercent = settings.VoiceOutputVolume * 100;
        _closeToTray = settings.CloseToTrayEnabled;
        _minimizeToTray = settings.MinimizeToTray;
        _startWithSystem = Autostart.IsEnabled;
        _startMinimized = settings.StartMinimized;
        _selectedTheme = Themes.FirstOrDefault(t => t.Theme == settings.ThemeValue) ?? Themes[0];
        _messageSounds = settings.MessageSounds;
        _messageSoundMentionsOnly = settings.MessageSoundMentionsOnly;
        _silenceSoundsWhileDeafened = settings.SilenceSoundsWhileDeafened;
        _notificationVolumePercent = settings.NotificationVolume * 100;
        _flashTaskbar = settings.FlashTaskbar;
        _callSounds = settings.CallSounds;
        _selectedLanguage = Languages.FirstOrDefault(l => l.Code == settings.Language) ?? Languages[0];

        // Show the microphone level live (in a call, or while testing), as a guide for the sensitivity slider.
        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) =>
        {
            var level = _voice.InputLevelDb;
            InputLevelPercent = Math.Clamp((level - MeterFloorDb) / -MeterFloorDb * 100, 0, 100);
            IsAboveThreshold = level >= ThresholdDb;
        });
        _meterTimer.Start();
    }

    // ---- Pages ----

    /// <summary>The pages in the list on the left, in the order of the panels in the dialog.</summary>
    public IReadOnlyList<string> Pages { get; } =
    [
        Strings.Settings_PageAccount,
        Strings.Settings_PageAppearance,
        Strings.Settings_PageNotifications,
        Strings.Settings_PageVoice,
        Strings.Settings_PageStreaming,
        Strings.Settings_PageWindow,
        Strings.Settings_PageLanguage,
        Strings.Settings_PageAbout,
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageTitle))]
    private int _selectedPageIndex;

    public string PageTitle => Pages[Math.Clamp(SelectedPageIndex, 0, Pages.Count - 1)];

    // ---- Voice & audio ----

    public IReadOnlyList<AudioDeviceInfo> InputDevices { get; }

    public IReadOnlyList<AudioDeviceInfo> OutputDevices { get; }

    public string AudioSystem => string.Format(Strings.Settings_AudioSystem, _voice.Audio.Name);

    public string EchoAdvice => OperatingSystem.IsWindows() ? Strings.Settings_EchoAdviceWindows : Strings.Settings_EchoAdviceOther;

    [ObservableProperty]
    private AudioDeviceInfo _inputDevice;

    [ObservableProperty]
    private AudioDeviceInfo _outputDevice;

    /// <summary>Input sensitivity in dBFS: the microphone transmits above this level.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThresholdText))]
    private double _thresholdDb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputVolumeText))]
    private double _outputVolumePercent;

    [ObservableProperty]
    private double _inputLevelPercent;

    [ObservableProperty]
    private bool _isAboveThreshold;

    public string ThresholdText => $"{ThresholdDb:0} dB";

    /// <summary>In a call the meter shows the live microphone; otherwise it can be tested here.</summary>
    public bool CanTestMicrophone => !_voice.IsInCall;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MicrophoneTestText))]
    private bool _isTestingMicrophone;

    public string MicrophoneTestText => IsTestingMicrophone ? Strings.Settings_StopMicTest : Strings.Settings_StartMicTest;

    [RelayCommand]
    private void ToggleMicrophoneTest()
    {
        if (IsTestingMicrophone)
        {
            _voice.StopMicrophoneTest();
        }
        else
        {
            _voice.StartMicrophoneTest();
        }

        IsTestingMicrophone = !IsTestingMicrophone;
    }

    public string OutputVolumeText => $"{OutputVolumePercent:0} %";

    partial void OnInputDeviceChanged(AudioDeviceInfo value)
    {
        _settings.VoiceInputDevice = value.Id;
        _settings.Save();
        _voice.ApplyAudioSettings(devicesChanged: true);
    }

    partial void OnOutputDeviceChanged(AudioDeviceInfo value)
    {
        _settings.VoiceOutputDevice = value.Id;
        _settings.Save();
        _voice.ApplyAudioSettings(devicesChanged: true);
    }

    // Slider values are applied live and saved when the dialog closes.
    partial void OnThresholdDbChanged(double value)
    {
        _settings.VoiceThresholdDb = (float)value;
        _voice.ApplyAudioSettings(devicesChanged: false);
    }

    partial void OnOutputVolumePercentChanged(double value)
    {
        _settings.VoiceOutputVolume = (float)(value / 100);
        _voice.ApplyAudioSettings(devicesChanged: false);
    }

    public void Dispose()
    {
        _meterTimer.Stop();
        _voice.StopMicrophoneTest();
        _settings.Save();
    }

    // ---- Account ----

    public MemberViewModel Me { get; }

    public string ServerName { get; }

    public string ServerAddress => _api.BaseAddress.ToString().TrimEnd('/');

    public IReadOnlyList<string> Palette { get; } =
    [
        "#7C6CFF", "#5865F2", "#3498DB", "#1ABC9C", "#3BA55C",
        "#FAA61A", "#E67E22", "#ED4245", "#E91E63", "#EB459E", "#9B59B6", "#607D8B",
    ];

    public IReadOnlyList<StreamQuality> Qualities => StreamQuality.Presets;

    // ---- Window & start-up ----

    /// <summary>Whether closing the window keeps Discagem running in the notification area.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloseQuits))]
    private bool _closeToTray;

    /// <summary>The other choice for the close button (two radio buttons share one setting).</summary>
    public bool CloseQuits
    {
        get => !CloseToTray;
        set
        {
            if (value)
            {
                CloseToTray = false;
            }
        }
    }

    public string TrayHint => OperatingSystem.IsWindows() ? Strings.Settings_TrayHintWindows : Strings.Settings_TrayHintOther;

    partial void OnCloseToTrayChanged(bool value)
    {
        _settings.CloseToTray = value;
        _settings.Save();
    }

    /// <summary>Whether minimizing the window puts it in the notification area instead of the taskbar.</summary>
    [ObservableProperty]
    private bool _minimizeToTray;

    partial void OnMinimizeToTrayChanged(bool value)
    {
        _settings.MinimizeToTray = value;
        _settings.Save();
    }

    public bool CanStartWithSystem => Autostart.IsSupported;

    public string StartWithSystemText => OperatingSystem.IsWindows() ? Strings.Settings_StartWithWindows : Strings.Settings_StartWithSession;

    public string StartWithSystemHint => Autostart.IsSupported ? Strings.Settings_StartWithSystemHint : Strings.Settings_StartWithSystemUnsupported;

    /// <summary>Whether the system starts Discagem as the user signs in (read from the system's own entry).</summary>
    [ObservableProperty]
    private bool _startWithSystem;

    [ObservableProperty]
    private string? _startWithSystemError;

    partial void OnStartWithSystemChanged(bool value)
    {
        if (Autostart.IsEnabled == value)
        {
            return;
        }

        if (Autostart.SetEnabled(value))
        {
            StartWithSystemError = null;
            return;
        }

        StartWithSystemError = Strings.Settings_StartWithSystemFailed;
        Dispatcher.UIThread.Post(() => StartWithSystem = !value);
    }

    /// <summary>Whether Discagem started with the computer stays in the notification area.</summary>
    [ObservableProperty]
    private bool _startMinimized;

    partial void OnStartMinimizedChanged(bool value)
    {
        _settings.StartMinimized = value;
        _settings.Save();
    }

    // ---- Appearance ----

    public IReadOnlyList<ThemeOption> Themes => ThemeOption.All;

    /// <summary>The theme; picking one applies it at once.</summary>
    [ObservableProperty]
    private ThemeOption _selectedTheme;

    partial void OnSelectedThemeChanged(ThemeOption value)
    {
        if (value is null)
        {
            return;
        }

        _settings.ThemeValue = value.Theme;
        _settings.Save();
        ThemeManager.Apply(value.Theme);
    }

    // ---- Notifications ----

    [ObservableProperty]
    private bool _messageSounds;

    partial void OnMessageSoundsChanged(bool value)
    {
        _settings.MessageSounds = value;
        _settings.Save();
        if (value)
        {
            _voice.PlayNotification(ThemeManager.Sounds.Message);
        }
    }

    [ObservableProperty]
    private bool _messageSoundMentionsOnly;

    partial void OnMessageSoundMentionsOnlyChanged(bool value)
    {
        _settings.MessageSoundMentionsOnly = value;
        _settings.Save();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeafenedNotice))]
    private bool _silenceSoundsWhileDeafened;

    partial void OnSilenceSoundsWhileDeafenedChanged(bool value)
    {
        _settings.SilenceSoundsWhileDeafened = value;
        _settings.Save();
    }

    /// <summary>Says why no sound plays right now, when that is the case.</summary>
    public string? DeafenedNotice => _voice.IsDeafened && SilenceSoundsWhileDeafened ? Strings.Settings_DeafenedNotice : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotificationVolumeText))]
    private double _notificationVolumePercent;

    public string NotificationVolumeText => $"{NotificationVolumePercent:0} %";

    partial void OnNotificationVolumePercentChanged(double value) => _settings.NotificationVolume = (float)(value / 100);

    [ObservableProperty]
    private bool _flashTaskbar;

    partial void OnFlashTaskbarChanged(bool value)
    {
        _settings.FlashTaskbar = value;
        _settings.Save();
    }

    [ObservableProperty]
    private bool _callSounds;

    partial void OnCallSoundsChanged(bool value)
    {
        _settings.CallSounds = value;
        _settings.Save();
    }

    /// <summary>Plays the message sound at the chosen volume, also while deafened.</summary>
    [RelayCommand]
    private void TestSound() => _voice.PlayNotification(ThemeManager.Sounds.Message, evenWhileDeafened: true);

    // ---- Language ----

    public IReadOnlyList<LanguageOption> Languages { get; } = Localization.Options;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsRestart))]
    private LanguageOption _selectedLanguage;

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (value is null)
        {
            return;
        }

        _settings.Language = value.Code;
        _settings.Save();
    }

    /// <summary>The language is applied at start-up, so a different one waits for a restart.</summary>
    public bool NeedsRestart => SelectedLanguage is { } language && Localization.Resolve(language.Code) != Localization.Current;

    public bool CanRestart => App.CanRestart;

    [RelayCommand]
    private void Restart()
    {
        _settings.Save();
        App.Restart();
    }

    // ---- About & updates ----

    [ObservableProperty]
    private bool _checkForUpdatesAutomatically;

    [ObservableProperty]
    private string _updateStatus = "";

    [ObservableProperty]
    private bool _isCheckingForUpdates;

    partial void OnCheckForUpdatesAutomaticallyChanged(bool value)
    {
        _settings.CheckForUpdates = value;
        _settings.Save();
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingForUpdates = true;
        UpdateStatus = Strings.Settings_CheckingForUpdates;
        try
        {
            UpdateStatus = await _checkForUpdates();
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    /// <summary>Where the log that helps diagnose problems is kept.</summary>
    public string LogFolder => ClientSettings.Directory;

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LogFolder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            // No file manager to open it with; the path is shown next to the button.
        }
    }

    public string AppVersion =>
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    public string AppVersionText => string.Format(Strings.Settings_AppVersion, AppVersion);

    public string CodecInfo => VpxLibrary.IsAvailable
        ? string.Format(Strings.Settings_CodecInfo, VpxLibrary.Version)
        : Strings.Settings_CodecMissing;

    /// <summary>Raised when the user chooses to sign out.</summary>
    public event Action? LogoutRequested;

    // ---- Profile and password ----

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private string _avatarColor;

    [ObservableProperty]
    private string _currentPassword = "";

    [ObservableProperty]
    private string _newPassword = "";

    [ObservableProperty]
    private string _confirmPassword = "";

    [ObservableProperty]
    private string? _profileMessage;

    [ObservableProperty]
    private bool _profileError;

    [ObservableProperty]
    private string? _passwordMessage;

    [ObservableProperty]
    private bool _passwordError;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private StreamQuality _defaultQuality;

    partial void OnDefaultQualityChanged(StreamQuality value)
    {
        _settings.StreamQualityIndex = Array.IndexOf(StreamQuality.Presets, value);
        _settings.Save();
    }

    [RelayCommand]
    private void PickColor(string color) => AvatarColor = color;

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        IsBusy = true;
        ProfileMessage = null;
        try
        {
            var updated = await _api.UpdateProfileAsync(DisplayName.Trim(), AvatarColor);
            Me.Update(updated);
            ProfileError = false;
            ProfileMessage = Strings.Settings_ProfileSaved;
        }
        catch (ApiException ex)
        {
            ProfileError = true;
            ProfileMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [ObservableProperty]
    private string? _photoMessage;

    [ObservableProperty]
    private bool _photoError;

    [RelayCommand]
    private async Task ChangePhotoAsync()
    {
        var file = await _dialogs.PickImageAsync(Strings.Settings_ChoosePhotoTitle);
        if (file is null)
        {
            return;
        }

        IsBusy = true;
        PhotoMessage = null;
        try
        {
            byte[] png;
            await using (var stream = await file.OpenReadAsync())
            {
                png = AvatarImages.Prepare(stream);
            }

            Me.Update(await _api.UploadAvatarAsync(png));
            PhotoError = false;
            PhotoMessage = Strings.Settings_PhotoUpdated;
        }
        catch (Exception ex) when (ex is ApiException or IOException or UnauthorizedAccessException)
        {
            PhotoError = true;
            PhotoMessage = ex.Message;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // Avalonia could not decode the file.
            PhotoError = true;
            PhotoMessage = Strings.Settings_PhotoUnreadable;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemovePhotoAsync()
    {
        IsBusy = true;
        PhotoMessage = null;
        try
        {
            Me.Update(await _api.RemoveAvatarAsync());
            PhotoError = false;
            PhotoMessage = Strings.Settings_PhotoRemoved;
        }
        catch (ApiException ex)
        {
            PhotoError = true;
            PhotoMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        PasswordMessage = null;
        if (NewPassword != ConfirmPassword)
        {
            PasswordError = true;
            PasswordMessage = Strings.Settings_PasswordsDoNotMatch;
            return;
        }

        if (NewPassword.Length < Limits.MinPasswordLength)
        {
            PasswordError = true;
            PasswordMessage = string.Format(Strings.Settings_PasswordTooShort, Limits.MinPasswordLength);
            return;
        }

        IsBusy = true;
        try
        {
            var auth = await _api.ChangePasswordAsync(CurrentPassword, NewPassword, _hubConnectionId());
            _tokenChanged(auth);
            CurrentPassword = NewPassword = ConfirmPassword = "";
            PasswordError = false;
            PasswordMessage = Strings.Settings_PasswordChanged;
        }
        catch (ApiException ex)
        {
            PasswordError = true;
            PasswordMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Logout() => LogoutRequested?.Invoke();
}
