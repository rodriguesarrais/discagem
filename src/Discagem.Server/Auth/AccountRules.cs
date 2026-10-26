using System.Text.RegularExpressions;
using Discagem.Shared;

namespace Discagem.Server.Auth;

public static partial class AccountRules
{
    public static readonly string[] AvatarPalette =
    [
        "#5865F2", "#3BA55C", "#FAA61A", "#ED4245", "#EB459E",
        "#9B59B6", "#1ABC9C", "#E67E22", "#3498DB", "#E91E63",
    ];

    [GeneratedRegex("^[A-Za-z0-9_.]+$")]
    private static partial Regex UsernamePattern();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorPattern();

    public static string? ValidateUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return "Username is required.";
        }

        if (username.Length < Limits.MinUsernameLength || username.Length > Limits.MaxUsernameLength)
        {
            return $"Username must be between {Limits.MinUsernameLength} and {Limits.MaxUsernameLength} characters.";
        }

        return UsernamePattern().IsMatch(username) ? null : "Username may only contain letters, digits, '_' and '.'.";
    }

    public static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < Limits.MinPasswordLength)
        {
            return $"Password must be at least {Limits.MinPasswordLength} characters.";
        }

        return password.Length > 256 ? "Password is too long." : null;
    }

    public static string? NormalizeDisplayName(string? displayName, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(displayName) ? fallback : displayName.Trim();
        value = new string(value.Where(c => !char.IsControl(c)).ToArray());
        if (value.Length == 0)
        {
            return null;
        }

        return value.Length > Limits.MaxDisplayNameLength ? value[..Limits.MaxDisplayNameLength] : value;
    }

    public static bool IsValidColor(string? color) => color is not null && ColorPattern().IsMatch(color);

    public static string RandomAvatarColor() => AvatarPalette[Random.Shared.Next(AvatarPalette.Length)];

    public static string NormalizeUsername(string username) => username.Trim().ToUpperInvariant();
}
