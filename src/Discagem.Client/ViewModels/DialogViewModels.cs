using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Discagem.Client.Services;
using Discagem.Media.Pipeline;
using Discagem.Media.Vpx;
using Discagem.Shared;

namespace Discagem.Client.ViewModels;

/// <summary>Create or rename a channel.</summary>
public sealed partial class ChannelEditorViewModel(string heading, string confirmText, string name = "", string topic = "") : ObservableObject
{
    public string Heading { get; } = heading;

    public string ConfirmText { get; } = confirmText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm), nameof(Preview))]
    private string _name = name;

    [ObservableProperty]
    private string _topic = topic;

    public bool CanConfirm => !string.IsNullOrWhiteSpace(Name);

    /// <summary>Approximates the server-side normalisation so users see the final name.</summary>
    public string Preview
    {
        get
        {
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
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ApiClient _api;
    private readonly ClientSettings _settings;
    private readonly Action<AuthResponse> _tokenChanged;
    private readonly Func<string?> _hubConnectionId;

    public SettingsViewModel(
        ApiClient api,
        ClientSettings settings,
        MemberViewModel me,
        string serverName,
        Action<AuthResponse> tokenChanged,
        Func<string?> hubConnectionId)
    {
        _api = api;
        _settings = settings;
        _tokenChanged = tokenChanged;
        _hubConnectionId = hubConnectionId;
        Me = me;
        ServerName = serverName;
        _displayName = me.DisplayName;
        _avatarColor = me.AvatarColor;
        _defaultQuality = StreamQuality.Presets[Math.Clamp(settings.StreamQualityIndex, 0, StreamQuality.Presets.Length - 1)];
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
