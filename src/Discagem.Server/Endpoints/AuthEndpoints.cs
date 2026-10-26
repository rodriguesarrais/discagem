using System.Security.Cryptography;
using System.Text;
using Discagem.Server.Auth;
using Discagem.Server.Data;
using Discagem.Server.Hubs;
using Discagem.Server.Options;
using Discagem.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Discagem.Server.Endpoints;

public static class AuthEndpoints
{
    public const string AuthRateLimitPolicy = "auth";

    // Serialises registrations so that only one account can become the initial administrator.
    private static readonly SemaphoreSlim RegistrationLock = new(1, 1);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").RequireRateLimiting(AuthRateLimitPolicy);
        auth.MapPost("/register", Register);
        auth.MapPost("/login", Login);

        var me = app.MapGroup("/api/me").RequireAuthorization();
        me.MapGet("/", GetMe);
        me.MapPatch("/", UpdateProfile);
        me.MapPost("/password", ChangePassword).RequireRateLimiting(AuthRateLimitPolicy);
        me.MapPost("/token", RefreshToken);
    }

    private static async Task<IResult> Register(
        RegisterRequest request,
        AppDbContext db,
        TokenService tokens,
        IPasswordHasher<User> hasher,
        IOptions<DiscagemOptions> options,
        IHubContext<ChatHub, IChatClient> hub,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        var error = AccountRules.ValidateUsername(request.Username) ?? AccountRules.ValidatePassword(request.Password);
        if (error is not null)
        {
            return Results.BadRequest(new ApiError(error));
        }

        await RegistrationLock.WaitAsync();
        try
        {
            return await RegisterLocked(request, db, tokens, hasher, options, hub, time, loggerFactory);
        }
        finally
        {
            RegistrationLock.Release();
        }
    }

    private static async Task<IResult> RegisterLocked(
        RegisterRequest request,
        AppDbContext db,
        TokenService tokens,
        IPasswordHasher<User> hasher,
        IOptions<DiscagemOptions> options,
        IHubContext<ChatHub, IChatClient> hub,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        var anyUser = await db.Users.AnyAsync();
        var inviteCode = options.Value.InviteCode;
        if (!string.IsNullOrEmpty(inviteCode))
        {
            if (!FixedTimeEquals(inviteCode, request.InviteCode ?? ""))
            {
                return Results.Json(new ApiError("Invalid invite code."), statusCode: StatusCodes.Status403Forbidden);
            }
        }
        else if (anyUser)
        {
            return Results.Json(new ApiError("Registration is closed on this server."), statusCode: StatusCodes.Status403Forbidden);
        }

        var username = request.Username.Trim();
        var normalized = AccountRules.NormalizeUsername(username);
        if (await db.Users.AnyAsync(u => u.NormalizedUsername == normalized))
        {
            return Results.Conflict(new ApiError("That username is already taken."));
        }

        var displayName = AccountRules.NormalizeDisplayName(request.DisplayName, username) ?? username;
        var user = new User
        {
            Username = username,
            NormalizedUsername = normalized,
            DisplayName = displayName,
            AvatarColor = AccountRules.RandomAvatarColor(),
            IsAdmin = !anyUser,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        user.PasswordHash = hasher.HashPassword(user, request.Password);
        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return Results.Conflict(new ApiError("That username is already taken."));
        }

        loggerFactory.CreateLogger("Discagem.Auth").LogInformation("Registered user {Username} (admin: {IsAdmin})", user.Username, user.IsAdmin);
        await hub.Clients.All.UserCreated(user.ToDto());
        return Results.Ok(tokens.Issue(user));
    }

    private static async Task<IResult> Login(
        LoginRequest request,
        AppDbContext db,
        TokenService tokens,
        IPasswordHasher<User> hasher)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return Results.BadRequest(new ApiError("Username and password are required."));
        }

        var normalized = AccountRules.NormalizeUsername(request.Username);
        var user = await db.Users.SingleOrDefaultAsync(u => u.NormalizedUsername == normalized);
        if (user is null)
        {
            // Spend comparable time to avoid revealing which usernames exist.
            hasher.HashPassword(new User { Username = "x", NormalizedUsername = "X", DisplayName = "x", AvatarColor = "#000000" }, request.Password);
            return Results.Json(new ApiError("Invalid username or password."), statusCode: StatusCodes.Status401Unauthorized);
        }

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            return Results.Json(new ApiError("Invalid username or password."), statusCode: StatusCodes.Status401Unauthorized);
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, request.Password);
            await db.SaveChangesAsync();
        }

        return Results.Ok(tokens.Issue(user));
    }

    private static async Task<IResult> GetMe(HttpContext context, AppDbContext db)
    {
        var user = await db.Users.FindAsync(context.User.GetUserId());
        return user is null ? Results.Unauthorized() : Results.Ok(user.ToDto());
    }

    private static async Task<IResult> RefreshToken(HttpContext context, AppDbContext db, TokenService tokens)
    {
        var user = await db.Users.FindAsync(context.User.GetUserId());
        return user is null ? Results.Unauthorized() : Results.Ok(tokens.Issue(user));
    }

    private static async Task<IResult> UpdateProfile(
        UpdateProfileRequest request,
        HttpContext context,
        AppDbContext db,
        IHubContext<ChatHub, IChatClient> hub)
    {
        var user = await db.Users.FindAsync(context.User.GetUserId());
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (request.DisplayName is not null)
        {
            var displayName = AccountRules.NormalizeDisplayName(request.DisplayName, "");
            if (displayName is null)
            {
                return Results.BadRequest(new ApiError("Display name cannot be empty."));
            }

            user.DisplayName = displayName;
        }

        if (request.AvatarColor is not null)
        {
            if (!AccountRules.IsValidColor(request.AvatarColor))
            {
                return Results.BadRequest(new ApiError("Avatar colour must be in #RRGGBB format."));
            }

            user.AvatarColor = request.AvatarColor.ToUpperInvariant();
        }

        await db.SaveChangesAsync();
        var dto = user.ToDto();
        await hub.Clients.All.UserUpdated(dto);
        return Results.Ok(dto);
    }

    private static async Task<IResult> ChangePassword(
        ChangePasswordRequest request,
        HttpContext context,
        AppDbContext db,
        IPasswordHasher<User> hasher,
        TokenService tokens)
    {
        var user = await db.Users.FindAsync(context.User.GetUserId());
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword ?? "") == PasswordVerificationResult.Failed)
        {
            return Results.BadRequest(new ApiError("The current password is incorrect."));
        }

        var error = AccountRules.ValidatePassword(request.NewPassword);
        if (error is not null)
        {
            return Results.BadRequest(new ApiError(error));
        }

        user.PasswordHash = hasher.HashPassword(user, request.NewPassword);
        user.TokenVersion++;
        await db.SaveChangesAsync();
        return Results.Ok(tokens.Issue(user));
    }

    private static bool FixedTimeEquals(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(actual)));
}
