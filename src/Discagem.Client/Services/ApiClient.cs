using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Discagem.Shared;

namespace Discagem.Client.Services;

/// <summary>An error returned by the server (or a network failure), with a user-presentable message.</summary>
public sealed class ApiException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>Typed client for the server's REST API.</summary>
public sealed class ApiClient : IDisposable
{
    private readonly HttpClient _http;
    private string? _token;

    public ApiClient(Uri baseAddress)
    {
        BaseAddress = baseAddress;
        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Discagem-Desktop/0.1");
    }

    public Uri BaseAddress { get; }

    public string? Token
    {
        get => _token;
        set
        {
            _token = value;
            _http.DefaultRequestHeaders.Authorization = value is null ? null : new AuthenticationHeaderValue("Bearer", value);
        }
    }

    /// <summary>Accepts "chat.example.com", "https://chat.example.com" or "http://localhost:5080".</summary>
    public static bool TryNormalizeAddress(string? input, out Uri address)
    {
        address = null!;
        var value = (input ?? "").Trim().TrimEnd('/');
        if (value.Length == 0)
        {
            return false;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            var isLocal = value.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                          value.StartsWith("127.", StringComparison.Ordinal) ||
                          value.StartsWith("192.168.", StringComparison.Ordinal) ||
                          value.StartsWith("10.", StringComparison.Ordinal);
            value = (isLocal ? "http://" : "https://") + value;
        }

        if (!Uri.TryCreate(value + "/", UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        address = uri;
        return true;
    }

    public Task<ServerInfoDto> GetServerInfoAsync(CancellationToken cancellationToken = default) =>
        SendAsync<ServerInfoDto>(HttpMethod.Get, "api/server", null, cancellationToken);

    public Task<AuthResponse> LoginAsync(string username, string password) =>
        SendAsync<AuthResponse>(HttpMethod.Post, "api/auth/login", new LoginRequest(username, password));

    public Task<AuthResponse> RegisterAsync(string username, string displayName, string password, string? inviteCode) =>
        SendAsync<AuthResponse>(HttpMethod.Post, "api/auth/register", new RegisterRequest(username, displayName, password, inviteCode));

    public Task<UserDto> GetMeAsync() => SendAsync<UserDto>(HttpMethod.Get, "api/me", null);

    public Task<AuthResponse> RefreshTokenAsync() => SendAsync<AuthResponse>(HttpMethod.Post, "api/me/token", null);

    public Task<UserDto> UpdateProfileAsync(string? displayName, string? avatarColor) =>
        SendAsync<UserDto>(HttpMethod.Patch, "api/me", new UpdateProfileRequest(displayName, avatarColor));

    public Task<AuthResponse> ChangePasswordAsync(string currentPassword, string newPassword, string? keepConnectionId) =>
        SendAsync<AuthResponse>(HttpMethod.Post, "api/me/password", new ChangePasswordRequest(currentPassword, newPassword, keepConnectionId));

    public async Task<AttachmentDto> UploadAsync(Stream content, string fileName, string? contentType, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType);
        form.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/attachments") { Content = form };
        return await SendCoreAsync<AttachmentDto>(request, cancellationToken, TimeSpan.FromMinutes(10));
    }

    /// <summary>Replaces the profile photo (a prepared PNG, see <see cref="AvatarImages"/>).</summary>
    public async Task<UserDto> UploadAvatarAsync(byte[] png, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "avatar.png");
        using var request = new HttpRequestMessage(HttpMethod.Put, "api/me/avatar") { Content = form };
        return await SendCoreAsync<UserDto>(request, cancellationToken, TimeSpan.FromMinutes(2));
    }

    public Task<UserDto> RemoveAvatarAsync() => SendAsync<UserDto>(HttpMethod.Delete, "api/me/avatar", null);

    public async Task<byte[]> DownloadAvatarAsync(long userId, int version, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync($"api/users/{userId}/avatar?v={version}", cancellationToken);
            await EnsureSuccessAsync(response);
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException("Could not reach the server.", ex.StatusCode, ex);
        }
    }

    public async Task<byte[]> DownloadAttachmentAsync(AttachmentDto attachment, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync($"api/attachments/{attachment.Id}/{Uri.EscapeDataString(attachment.FileName)}", cancellationToken);
            await EnsureSuccessAsync(response);
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException("Could not reach the server.", ex.StatusCode, ex);
        }
    }

    /// <summary>Adds a custom emoji or sticker (a prepared PNG, see <see cref="EmojiImages"/>).</summary>
    public async Task<EmojiDto> UploadEmojiAsync(string name, EmojiKind kind, byte[] png, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(name), "name");
        form.Add(new StringContent(kind.ToString()), "kind");
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "emoji.png");
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/emojis") { Content = form };
        return await SendCoreAsync<EmojiDto>(request, cancellationToken, TimeSpan.FromMinutes(2));
    }

    public Task<byte[]> DownloadEmojiAsync(long id, CancellationToken cancellationToken = default) =>
        DownloadAsync($"api/emojis/{id}/image", cancellationToken);

    public Task DeleteEmojiAsync(long id) => SendWithoutResultAsync(HttpMethod.Delete, $"api/emojis/{id}");

    /// <summary>Adds a soundboard sound (a prepared WAV file, see <see cref="Discagem.Media.Audio.SoundClips"/>).</summary>
    public async Task<SoundDto> UploadSoundAsync(string name, byte[] wav, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(name), "name");
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "sound.wav");
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/sounds") { Content = form };
        return await SendCoreAsync<SoundDto>(request, cancellationToken, TimeSpan.FromMinutes(2));
    }

    public Task<byte[]> DownloadSoundAsync(long id, CancellationToken cancellationToken = default) =>
        DownloadAsync($"api/sounds/{id}/audio", cancellationToken);

    public Task DeleteSoundAsync(long id) => SendWithoutResultAsync(HttpMethod.Delete, $"api/sounds/{id}");

    private async Task<byte[]> DownloadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(path, cancellationToken);
            await EnsureSuccessAsync(response);
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException("Could not reach the server.", ex.StatusCode, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's timeout, not the caller's cancellation: callers only expect ApiException.
            throw new ApiException("The server did not respond in time.", null, ex);
        }
    }

    private async Task SendWithoutResultAsync(HttpMethod method, string path)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            using var response = await _http.SendAsync(request);
            await EnsureSuccessAsync(response);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException($"Could not reach the server ({ex.Message.TrimEnd('.')}).", ex.StatusCode, ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new ApiException("The server did not respond in time.", null, ex);
        }
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        return await SendCoreAsync<T>(request, cancellationToken);
    }

    private async Task<T> SendCoreAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is not null)
        {
            timeoutSource.CancelAfter(timeout.Value);
        }

        try
        {
            using var response = await _http.SendAsync(request, timeoutSource.Token);
            await EnsureSuccessAsync(response);
            return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: timeoutSource.Token))
                ?? throw new ApiException("The server sent an empty response.");
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException($"Could not reach the server ({ex.Message.TrimEnd('.')}).", ex.StatusCode, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ApiException("The server did not respond in time.", null, ex);
        }
        catch (JsonException ex)
        {
            throw new ApiException("The server sent an unexpected response. Is this a Discagem server?", null, ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? message = null;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiError>();
            message = error?.Error;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
        }

        message ??= response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Your session has expired. Please sign in again.",
            HttpStatusCode.TooManyRequests => "Too many attempts. Please wait a minute and try again.",
            HttpStatusCode.RequestEntityTooLarge => "That file is too large.",
            HttpStatusCode.NotFound => "Not found. Check the server address.",
            _ => $"The server returned an error ({(int)response.StatusCode}).",
        };
        throw new ApiException(message, response.StatusCode);
    }

    public void Dispose() => _http.Dispose();
}
