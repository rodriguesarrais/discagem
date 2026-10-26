using System.Security.Claims;
using System.Security.Cryptography;
using Discagem.Server.Data;
using Discagem.Server.Options;
using Discagem.Shared;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Discagem.Server.Auth;

public static class DiscagemClaims
{
    public const string Subject = "sub";
    public const string Name = "name";
    public const string TokenVersion = "tv";
    public const string Admin = "adm";

    public static long GetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirst(Subject)?.Value;
        return long.TryParse(value, out var id) ? id : throw new InvalidOperationException("The principal has no user id.");
    }

    public static bool IsAdmin(this ClaimsPrincipal principal) => principal.HasClaim(Admin, "1");
}

/// <summary>Provides the HMAC key used to sign access tokens.</summary>
public sealed class SigningKeyProvider
{
    public SigningKeyProvider(IOptions<DiscagemOptions> options, ILogger<SigningKeyProvider> logger)
    {
        var configured = options.Value.Jwt.Key;
        byte[] keyBytes;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (configured.Length < 32)
            {
                throw new InvalidOperationException("Discagem:Jwt:Key must be at least 32 characters long.");
            }

            keyBytes = System.Text.Encoding.UTF8.GetBytes(configured);
        }
        else
        {
            var path = Path.Combine(DataPaths.Root(options.Value), "jwt.key");
            if (File.Exists(path))
            {
                keyBytes = Convert.FromBase64String(File.ReadAllText(path).Trim());
            }
            else
            {
                keyBytes = RandomNumberGenerator.GetBytes(64);
                File.WriteAllText(path, Convert.ToBase64String(keyBytes));
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                logger.LogInformation("Generated a new token signing key at {Path}", path);
            }
        }

        Key = new SymmetricSecurityKey(keyBytes);
    }

    public SymmetricSecurityKey Key { get; }
}

public sealed class TokenService(SigningKeyProvider keys, IOptions<DiscagemOptions> options, TimeProvider time)
{
    public const string Audience = "discagem-client";

    public AuthResponse Issue(User user)
    {
        var jwt = options.Value.Jwt;
        var now = time.GetUtcNow();
        var expires = now.AddDays(Math.Max(1, jwt.LifetimeDays));

        var claims = new List<Claim>
        {
            new(DiscagemClaims.Subject, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(DiscagemClaims.Name, user.Username),
            new(DiscagemClaims.TokenVersion, user.TokenVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        if (user.IsAdmin)
        {
            claims.Add(new Claim(DiscagemClaims.Admin, "1"));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = jwt.Issuer,
            Audience = Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(keys.Key, SecurityAlgorithms.HmacSha256),
        };

        var token = new JsonWebTokenHandler().CreateToken(descriptor);
        return new AuthResponse(token, expires, user.ToDto());
    }
}
