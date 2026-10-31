using System.Reflection;
using System.Threading.RateLimiting;
using Discagem.Server.Auth;
using Discagem.Server.Data;
using Discagem.Server.Endpoints;
using Discagem.Server.Hubs;
using Discagem.Server.Media;
using Discagem.Server.Options;
using Discagem.Server.Services;
using Discagem.Shared;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DiscagemOptions>(builder.Configuration.GetSection(DiscagemOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<AppDbContext>((services, o) =>
    o.UseSqlite($"Data Source={DataPaths.Database(services.GetRequiredService<IOptions<DiscagemOptions>>().Value)}"));
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddSingleton<SigningKeyProvider>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddSingleton<PeerConnectionFactory>();
builder.Services.AddSingleton<SfuService>();
builder.Services.AddSingleton<VoiceService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SfuService>());
builder.Services.AddHostedService<StunResponder>();
builder.Services.AddHostedService<AttachmentCleanupService>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services
    .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<SigningKeyProvider, IOptions<DiscagemOptions>>((jwt, keys, options) =>
    {
        jwt.MapInboundClaims = false;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Value.Jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = TokenService.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = keys.Key,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = DiscagemClaims.Name,
        };
        jwt.Events = new JwtBearerEvents
        {
            // Browsers and SignalR clients cannot always set headers on WebSocket requests.
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments(HubPaths.Chat))
                {
                    context.Token = token;
                }

                return Task.CompletedTask;
            },
            // Reject tokens of deleted users and tokens issued before a password change.
            OnTokenValidated = async context =>
            {
                var principal = context.Principal!;
                var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                var userId = principal.GetUserId();
                var tokenVersion = principal.FindFirst(DiscagemClaims.TokenVersion)?.Value;
                var user = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.TokenVersion, u.IsAdmin }).SingleOrDefaultAsync();
                if (user is null || user.TokenVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) != tokenVersion || user.IsAdmin != principal.IsAdmin())
                {
                    context.Fail("The token is no longer valid.");
                }
            },
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 128 * 1024;
    o.KeepAliveInterval = TimeSpan.FromSeconds(15);
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(40);
});
builder.Services.AddSingleton<IUserIdProvider, SubjectUserIdProvider>();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.AuthRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
});

// Discagem is meant to run behind a reverse proxy on the same host (e.g. Caddy) that terminates TLS.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

builder.Services.AddOptions<KestrelServerOptions>()
    .Configure<IOptions<DiscagemOptions>>((kestrel, options) => kestrel.Limits.MaxRequestBodySize = options.Value.MaxUploadBytes + 1024 * 1024);
builder.Services.AddOptions<FormOptions>()
    .Configure<IOptions<DiscagemOptions>>((form, options) => form.MultipartBodyLengthLimit = options.Value.MaxUploadBytes + 1024 * 1024);

var app = builder.Build();

// The media library's logger is process-wide. In-memory test hosts come and go (and dispose their
// logger factories), so they leave it alone.
if (!app.Environment.IsEnvironment("Testing"))
{
    SIPSorcery.LogFactory.Set(app.Services.GetRequiredService<ILoggerFactory>());
}

await DatabaseInitializer.InitializeAsync(app.Services);

app.UseForwardedHeaders();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

app.MapGet("/", () => Results.Text($"Discagem server {version}"));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/server", async (IOptions<DiscagemOptions> options, AppDbContext db) =>
{
    var o = options.Value;
    var requiresInvite = !string.IsNullOrEmpty(o.InviteCode);
    var open = requiresInvite || !await db.Users.AnyAsync();
    var iceServers = o.Media.IceServers
        .Where(s => !string.IsNullOrWhiteSpace(s.Urls))
        .Select(s => new IceServerDto(s.Urls, s.Username, s.Credential))
        .ToList();
    return new ServerInfoDto(o.ServerName, version, requiresInvite, open, o.Media.StunPort, iceServers);
});

app.MapAuthEndpoints();
app.MapAttachmentEndpoints();
app.MapAvatarEndpoints();
app.MapHub<ChatHub>(HubPaths.Chat, o => o.CloseOnAuthenticationExpiration = true);

app.Run();

/// <summary>Entry point; partial so integration tests can reference it.</summary>
public partial class Program;
