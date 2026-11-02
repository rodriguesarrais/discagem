using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Discagem.Server.Options;
using Discagem.Shared;
using Microsoft.Extensions.Options;

namespace Discagem.Server.Services;

/// <summary>
/// The desktop client's packages found in <see cref="DiscagemOptions.ClientDownloadsDirectory"/>:
/// the release's Windows installer, .deb and Flatpak, which the server offers for download and
/// clients update from. The folder is read (and the files hashed) once, on first use.
/// </summary>
public sealed partial class ClientDownloads(IOptions<DiscagemOptions> options, ILogger<ClientDownloads> logger)
{
    private readonly object _sync = new();
    private Catalog? _catalog;

    /// <summary>The client release on offer, or null when the folder holds none.</summary>
    public ClientReleaseDto? Release => Load().Release;

    /// <summary>Full path of a file on offer, or null when there is no such download.</summary>
    public string? PathOf(string fileName) => Load().Files.GetValueOrDefault(fileName);

    private Catalog Load()
    {
        lock (_sync)
        {
            return _catalog ??= Scan();
        }
    }

    private Catalog Scan()
    {
        var directory = options.Value.ClientDownloadsDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return Catalog.Empty;
        }

        if (!Path.IsPathRooted(directory))
        {
            directory = Path.Combine(AppContext.BaseDirectory, directory);
        }

        if (!Directory.Exists(directory))
        {
            return Catalog.Empty;
        }

        // Group the recognised packages by version and offer the newest.
        var found = new List<(ReleaseVersion Version, string Platform, string Path)>();
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            if (Recognize(Path.GetFileName(path)) is { } package)
            {
                found.Add((package.Version, package.Platform, path));
            }
        }

        if (found.Count == 0)
        {
            return Catalog.Empty;
        }

        var newest = found.Max(f => f.Version);
        var downloads = new List<ClientDownloadDto>();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        // Windows first: most people use it.
        string[] order = [ClientPlatforms.WindowsInstaller, ClientPlatforms.Deb, ClientPlatforms.Flatpak];
        foreach (var (_, platform, path) in found.Where(f => f.Version == newest).OrderBy(f => Array.IndexOf(order, f.Platform)))
        {
            var name = Path.GetFileName(path);
            string hash;
            using (var stream = File.OpenRead(path))
            {
                hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            }

            downloads.Add(new ClientDownloadDto(platform, name, new FileInfo(path).Length, hash, "downloads/" + Uri.EscapeDataString(name)));
            files[name] = path;
        }

        logger.LogInformation("Offering Discagem {Version} for download: {Files}", newest, string.Join(", ", files.Keys));
        return new Catalog(new ClientReleaseDto(newest.ToString(), downloads), files);
    }

    /// <summary>Recognises the release workflow's package names.</summary>
    internal static (ReleaseVersion Version, string Platform)? Recognize(string fileName)
    {
        (Regex Pattern, string Platform)[] patterns =
        [
            (WindowsInstallerName(), ClientPlatforms.WindowsInstaller),
            (DebName(), ClientPlatforms.Deb),
            (FlatpakName(), ClientPlatforms.Flatpak),
        ];

        foreach (var (pattern, platform) in patterns)
        {
            var match = pattern.Match(fileName);
            // Debian writes pre-release suffixes with "~" (0.5.0~beta.1).
            if (match.Success && ReleaseVersion.TryParse(match.Groups["version"].Value.Replace('~', '-'), out var version))
            {
                return (version, platform);
            }
        }

        return null;
    }

    [GeneratedRegex(@"^Discagem-(?<version>[0-9][0-9A-Za-z.-]*)-win-x64-setup\.exe$")]
    private static partial Regex WindowsInstallerName();

    [GeneratedRegex(@"^discagem_(?<version>[0-9][0-9A-Za-z.~]*)_amd64\.deb$")]
    private static partial Regex DebName();

    [GeneratedRegex(@"^Discagem-(?<version>[0-9][0-9A-Za-z.-]*)-x86_64\.flatpak$")]
    private static partial Regex FlatpakName();

    private sealed record Catalog(ClientReleaseDto? Release, IReadOnlyDictionary<string, string> Files)
    {
        public static Catalog Empty { get; } = new(null, new Dictionary<string, string>());
    }
}
