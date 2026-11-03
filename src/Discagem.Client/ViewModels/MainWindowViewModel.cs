using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using Discagem.Client.Resources;
using Discagem.Client.Services;

namespace Discagem.Client.ViewModels;

/// <summary>Shown while restoring a saved session.</summary>
public sealed class SplashViewModel(string message) : ObservableObject
{
    public string Message { get; } = message;
}

/// <summary>Root view model: switches between the login screen and the main shell.</summary>
public sealed partial class MainWindowViewModel(ClientSettings settings, IDialogService dialogs) : ObservableObject
{
    [ObservableProperty]
    private ObservableObject? _currentView;

    [ObservableProperty]
    private string _windowTitle = "Discagem";

    public async Task StartAsync()
    {
        var token = settings.Token;
        if (string.IsNullOrEmpty(token) || !ApiClient.TryNormalizeAddress(settings.ServerAddress, out var address))
        {
            ShowLogin();
            return;
        }

        CurrentView = new SplashViewModel(Strings.App_ConnectingToServer);
        var api = new ApiClient(address) { Token = token };
        try
        {
            var info = await api.GetServerInfoAsync();
            // Refreshing also validates the saved token.
            var auth = await api.RefreshTokenAsync();
            api.Token = auth.Token;
            settings.Token = auth.Token;
            settings.Save();
            await ShowShellAsync(new ClientSession(api, settings, info, auth.User));
        }
        catch (ApiException ex)
        {
            api.Dispose();
            if (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                settings.Token = null;
                settings.Save();
                ShowLogin(Strings.App_SessionExpired);
            }
            else
            {
                ShowLogin(ex.Message);
            }
        }
    }

    private void ShowLogin(string? error = null)
    {
        WindowTitle = "Discagem";
        CurrentView = new LoginViewModel(settings, ShowShellAsync, error);
    }

    private async Task ShowShellAsync(ClientSession session)
    {
        var shell = new ShellViewModel(session, dialogs);
        shell.LoggedOut += reason =>
        {
            _ = shell.DisposeAsync();
            ShowLogin(reason);
        };
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.WindowTitle))
            {
                WindowTitle = shell.WindowTitle;
            }
        };
        CurrentView = shell;
        WindowTitle = shell.WindowTitle;
        await shell.InitializeAsync();
    }

    public void Shutdown()
    {
        if (CurrentView is ShellViewModel shell)
        {
            // Give the media sessions a moment to close cleanly (off the UI thread to avoid deadlocks).
            Task.Run(() => shell.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(2));
        }
    }
}
