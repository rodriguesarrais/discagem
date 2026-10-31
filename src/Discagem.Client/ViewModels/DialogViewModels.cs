using System.Reflection;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
        new("Create channel", "Create channel", canChooseKind: true, kind, "", "");

    public static ChannelEditorViewModel ForEdit(ChannelKind kind, string name, string topic) =>
        new("Edit channel", "Save changes", canChooseKind: false, kind, name, topic);

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

    public string Watermark => IsVoice ? "Hangout" : "new-channel";

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

/// <summary>Account and app settings.</summary>
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

    public SettingsViewModel(
        ApiClient api,
        ClientSettings settings,
        MemberViewModel me,
        string serverName,
        Action<AuthResponse> tokenChanged,
        Func<string?> hubConnectionId,
        VoiceService voice)
    {
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

        // Show the microphone level live (in a call, or while testing), as a guide for the sensitivity slider.
        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) =>
        {
            var level = _voice.InputLevelDb;
            InputLevelPercent = Math.Clamp((level - MeterFloorDb) / -MeterFloorDb * 100, 0, 100);
            IsAboveThreshold = level >= ThresholdDb;
        });
        _meterTimer.Start();
    }

    public IReadOnlyList<AudioDeviceInfo> InputDevices { get; }

    public IReadOnlyList<AudioDeviceInfo> OutputDevices { get; }

    public string AudioSystem => $"Audio: {_voice.Audio.Name}";

    public string EchoAdvice => OperatingSystem.IsWindows()
        ? "Windows echo cancellation and noise suppression are used where your microphone supports them."
        : "Use headphones to keep others from hearing themselves through your microphone.";

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

    public string MicrophoneTestText => IsTestingMicrophone ? "Stop test" : "Test microphone";

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

    public MemberViewModel Me { get; }

    public string ServerName { get; }

    public string ServerAddress => _api.BaseAddress.ToString().TrimEnd('/');

    public IReadOnlyList<string> Palette { get; } =
    [
        "#7C6CFF", "#5865F2", "#3498DB", "#1ABC9C", "#3BA55C",
        "#FAA61A", "#E67E22", "#ED4245", "#E91E63", "#EB459E", "#9B59B6", "#607D8B",
    ];

    public IReadOnlyList<StreamQuality> Qualities => StreamQuality.Presets;

    /// <summary>Whether closing the window keeps Discagem running in the notification area.</summary>
    [ObservableProperty]
    private bool _closeToTray;

    public string TrayHint => OperatingSystem.IsWindows()
        ? "Open it again from its icon in the notification area (you may find it under the ^ arrow), or from the Start menu. Quit it from the icon's menu."
        : "Your desktop must show tray icons (on GNOME, the AppIndicator extension). Launching Discagem again brings the window back.";

    partial void OnCloseToTrayChanged(bool value)
    {
        _settings.CloseToTray = value;
        _settings.Save();
    }

    public string AppVersion =>
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    public string CodecInfo => VpxLibrary.IsAvailable ? $"VP8 via libvpx {VpxLibrary.Version}" : "libvpx not found — screen sharing unavailable";

    /// <summary>Raised when the user chooses to sign out.</summary>
    public event Action? LogoutRequested;

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
            ProfileMessage = "Profile saved.";
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

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        PasswordMessage = null;
        if (NewPassword != ConfirmPassword)
        {
            PasswordError = true;
            PasswordMessage = "The new passwords do not match.";
            return;
        }

        if (NewPassword.Length < Limits.MinPasswordLength)
        {
            PasswordError = true;
            PasswordMessage = $"Use at least {Limits.MinPasswordLength} characters.";
            return;
        }

        IsBusy = true;
        try
        {
            var auth = await _api.ChangePasswordAsync(CurrentPassword, NewPassword, _hubConnectionId());
            _tokenChanged(auth);
            CurrentPassword = NewPassword = ConfirmPassword = "";
            PasswordError = false;
            PasswordMessage = "Password changed. Other devices have been signed out.";
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
