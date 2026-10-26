using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Discagem.Shared;

namespace Discagem.Tests.Server;

public sealed class AuthTests : IDisposable
{
    private readonly DiscagemFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task FirstUserIsAdmin_LaterUsersAreNot()
    {
        var (_, first) = await _factory.RegisterAsync("alice", "Alice");
        var (_, second) = await _factory.RegisterAsync("bob");

        Assert.True(first.User.IsAdmin);
        Assert.Equal("Alice", first.User.DisplayName);
        Assert.False(second.User.IsAdmin);
        Assert.Matches("^#[0-9A-F]{6}$", second.User.AvatarColor);
    }

    [Fact]
    public async Task Register_RequiresInviteCode()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest("mallory", "Mallory", "password123", "nope"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Register_RejectsDuplicatesAndBadInput()
    {
        await _factory.RegisterAsync("carol");
        var client = _factory.CreateClient();

        var duplicate = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest("CAROL", "x", "password123", DiscagemFactory.InviteCode));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var shortPassword = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest("dave", "Dave", "short", DiscagemFactory.InviteCode));
        Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);

        var badName = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest("bad name!", "x", "password123", DiscagemFactory.InviteCode));
        Assert.Equal(HttpStatusCode.BadRequest, badName.StatusCode);
    }

    [Fact]
    public async Task Login_IsCaseInsensitiveAndRejectsWrongPassword()
    {
        await _factory.RegisterAsync("erin");
        var client = _factory.CreateClient();

        var ok = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("ERIN", "password123"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var wrong = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("erin", "password124"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var unknown = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("nobody", "password123"));
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoints_RequireToken()
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/me")).StatusCode);

        var (client, auth) = await _factory.RegisterAsync("frank");
        var me = await client.GetFromJsonAsync<UserDto>("/api/me");
        Assert.Equal(auth.User.Id, me!.Id);
    }

    [Fact]
    public async Task PasswordChange_InvalidatesOldTokens()
    {
        var (client, auth) = await _factory.RegisterAsync("grace");
        var response = await client.PostAsJsonAsync("/api/me/password", new ChangePasswordRequest("password123", "newpassword456"));
        response.EnsureSuccessStatusCode();
        var refreshed = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);
        Assert.NotEqual(auth.Token, refreshed.Token);
    }

    [Fact]
    public async Task UpdateProfile_ValidatesColour()
    {
        var (client, _) = await _factory.RegisterAsync("heidi");
        var bad = await client.PatchAsJsonAsync("/api/me", new UpdateProfileRequest(null, "red"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var good = await client.PatchAsJsonAsync("/api/me", new UpdateProfileRequest("Heidi K.", "#12ab34"));
        var user = await good.Content.ReadFromJsonAsync<UserDto>();
        Assert.Equal("Heidi K.", user!.DisplayName);
        Assert.Equal("#12AB34", user.AvatarColor);
    }

    [Fact]
    public async Task ServerInfo_IsPublic()
    {
        var info = await _factory.CreateClient().GetFromJsonAsync<ServerInfoDto>("/api/server");
        Assert.NotNull(info);
        Assert.True(info.RegistrationRequiresInvite);
        Assert.Equal(0, info.StunPort);
    }
}
