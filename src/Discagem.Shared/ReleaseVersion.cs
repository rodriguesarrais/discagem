using System.Globalization;

namespace Discagem.Shared;

/// <summary>
/// A release version as used for tags and builds: <c>major.minor.patch</c> with an optional
/// pre-release suffix (<c>0.3.0-beta.1</c>), ordered as Semantic Versioning 2.0 specifies.
/// A leading <c>v</c> and any build metadata (<c>+abc123</c>) are ignored.
/// </summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch, string PreRelease = "") : IComparable<ReleaseVersion>
{
    public bool IsPreRelease => PreRelease.Length > 0;

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus];
        }

        var dash = value.IndexOf('-');
        var preRelease = dash >= 0 ? value[(dash + 1)..] : "";
        var parts = (dash >= 0 ? value[..dash] : value).Split('.');
        if (parts.Length != 3 || (dash >= 0 && preRelease.Length == 0))
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new ReleaseVersion(numbers[0], numbers[1], numbers[2], preRelease);
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result == 0)
        {
            result = Minor.CompareTo(other.Minor);
        }

        if (result == 0)
        {
            result = Patch.CompareTo(other.Patch);
        }

        return result != 0 ? result : ComparePreRelease(PreRelease ?? "", other.PreRelease ?? "");
    }

    /// <summary>A release outranks its pre-releases; identifiers compare numerically when both are numbers.</summary>
    private static int ComparePreRelease(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return b.Length.CompareTo(a.Length);
        }

        var left = a.Split('.');
        var right = b.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftIsNumber = long.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightIsNumber = long.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
            var result = (leftIsNumber, rightIsNumber) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(left[i], right[i]),
            };
            if (result != 0)
            {
                return Math.Sign(result);
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => IsPreRelease ? $"{Major}.{Minor}.{Patch}-{PreRelease}" : $"{Major}.{Minor}.{Patch}";
}
