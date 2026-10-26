using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Discagem.Client.Services;
using Discagem.Shared;

namespace Discagem.Client.ViewModels;

/// <summary>Everything the main shell needs after a successful sign-in.</summary>
public sealed record ClientSession(ApiClient Api, ClientSettings Settings, ServerInfoDto Server, UserDto User);

public sealed partial class LoginViewModel : ObservableObject
{
    private readonly ClientSettings _settings;
    private readonly Func<ClientSession, Task> _onAuthenticated;
    private CancellationTokenSource? _probe;

    public LoginViewModel(ClientSettings settings, Func<ClientSession, Task> onAuthenticated, string? error = null)
    {
        _settings = settings;
        _onAuthenticated = onAuthenticated;
        _serverAddress = settings.ServerAddress;
        _username = settings.Username;
        _errorMessage = error;
        if (!string.IsNullOrWhiteSpace(_serverAddress))
        {
            _ = ProbeServerAsync(_serverAddress);
        }
    }

    [ObservableProperty]
    private string _serverAddress;

    [ObservableProperty]
    private string _username;

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private string _inviteCode = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading), nameof(Subheading), nameof(SubmitText), nameof(ToggleText), nameof(TogglePrompt), nameof(ShowInviteCode))]
    private bool _isRegisterMode;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerStatus))]
    private string? _serverStatus;

    [ObservableProperty]
    private bool _serverReachable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInviteCode))]
    private bool _inviteRequired = true;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasServerStatus => !string.IsNullOrEmpty(ServerStatus);

    public bool ShowInviteCode => IsRegisterMode && InviteRequired;

    public string Heading => IsRegisterMode ? "Create an account" : "Welcome back!";

    public string Subheading => IsRegisterMode ? "Join your friends on your private server." : "We're so excited to see you again!";

    public string SubmitText => IsRegisterMode ? "Create account" : "Log in";

    public string TogglePrompt => IsRegisterMode ? "Already have an account?" : "Need an account?";

    public string ToggleText => IsRegisterMode ? "Log in" : "Register";

    partial void OnServerAddressChanged(string value) => _ = ProbeServerAsync(value);

    private async Task ProbeServerAsync(string address)
    {
        _probe?.Cancel();
        var cts = _probe = new CancellationTokenSource();
        ServerStatus = null;
        ServerReachable = false;
        if (!ApiClient.TryNormalizeAddress(address, out var uri))
        {
            return;
        }

        try
        {
            await Task.Delay(500, cts.Token);
            using var api = new ApiClient(uri);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            var info = await api.GetServerInfoAsync(timeout.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            ServerReachable = true;
            InviteRequired = info.RegistrationRequiresInvite;
            ServerStatus = info.RegistrationOpen ? $"{info.Name} · v{info.Version}" : $"{info.Name} · registration closed";
        }
        catch (OperationCanceledException)
        {
        }
        catch (ApiException)
        {
            if (!cts.IsCancellationRequested)
            {
                ServerStatus = "Server not reachable";
            }
        }
    }

    [RelayCommand]
    private void ToggleMode()
    {
        IsRegisterMode = !IsRegisterMode;
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        ErrorMessage = null;
        if (!ApiClient.TryNormalizeAddress(ServerAddress, out var address))
        {
            ErrorMessage = "Enter the address of your Discagem server, e.g. chat.example.com.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
        {
            ErrorMessage = "Enter your username and password.";
            return;
        }

        IsBusy = true;
        var api = new ApiClient(address);
        try
        {
            var auth = IsRegisterMode
                ? await api.RegisterAsync(Username.Trim(), string.IsNullOrWhiteSpace(DisplayName) ? Username.Trim() : DisplayName.Trim(), Password, InviteCode.Trim())
                : await api.LoginAsync(Username.Trim(), Password);
            api.Token = auth.Token;
            var info = await api.GetServerInfoAsync();

            _settings.ServerAddress = address.ToString().TrimEnd('/');
            _settings.Username = auth.User.Username;
            _settings.Token = auth.Token;
            _settings.Save();
            Password = "";
            await _onAuthenticated(new ClientSession(api, _settings, info, auth.User));
        }
        catch (ApiException ex)
        {
            api.Dispose();
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
