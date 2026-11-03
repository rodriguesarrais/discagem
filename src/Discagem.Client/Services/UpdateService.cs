using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Discagem.Client.Resources;
using Discagem.Shared;

namespace Discagem.Client.Services;

/// <summary>
/// A newer release, with its Windows installer when it has one. The installer is verified against
/// <paramref name="Sha256"/> when given, otherwise against the release's SHA256SUMS.
/// </summary>
public sealed record AvailableUpdate(ReleaseVersion Version, string PageUrl, string? InstallerUrl, string? InstallerName, string? ChecksumsUrl,
    string? Sha256 = null);

/// <summary>
/// Looks for a newer version and, for clients installed with the Windows installer, downloads the
/// new installer, verifies its checksum and runs it silently; the installer restarts Discagem when
/// it is done.
/// </summary>
/// <remarks>
/// <para>
/// The server the client is signed in to comes first: its image carries the client of its own
/// release (see <c>/api/client/latest</c>), and servers update themselves, so this works without
/// access to GitHub (the repository is private). Updates are then trusted as much as that server.
/// When the server offers no client (an older server, or one built from source), the project's
/// GitHub releases are checked instead.
/// </para>
/// <para>
/// The repository comes from the <c>UpdateRepository</c> assembly metadata (see the project file).
/// <c>DISCAGEM_UPDATE_FEED</c> points the check at another URL returning the same JSON as GitHub's
/// "latest release" endpoint, instead of the server and GitHub, which is how updates are tested.
/// </para>
/// </remarks>
public sealed class UpdateService : IDisposable
{
    private static readonly string DownloadDirectory = Path.Combine(Path.GetTempPath(), "Discagem-update");

    private readonly HttpClient _http;
    private readonly Uri? _server;

    /// <param name="server">The address of the server the client is signed in to.</param>
    public UpdateService(Uri? server = null)
    {
        _server = server;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Discagem", CurrentVersion.ToString()));
    }

    public static ReleaseVersion CurrentVersion { get; } =
        ReleaseVersion.TryParse(typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, out var version)
            ? version
            : new ReleaseVersion(0, 0, 0);

    /// <summary>
    /// Whether updates can be installed from within the app: only for clients installed with the
    /// Windows installer (which puts its uninstaller next to the executable). Others get a link.
    /// </summary>
    public static bool CanInstall { get; } =
        OperatingSystem.IsWindows() && File.Exists(Path.Combine(AppContext.BaseDirectory, "Uninstall.exe"));

    private static string? FeedOverride =>
        Environment.GetEnvironmentVariable("DISCAGEM_UPDATE_FEED") is { Length: > 0 } feed && !string.IsNullOrWhiteSpace(feed) ? feed : null;

    private static string FeedUrl
    {
        get
        {
            if (FeedOverride is { } feed)
            {
                return feed;
            }

            var repository = typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value ?? "rodriguesarrais/discagem";
            return $"https://api.github.com/repos/{repository}/releases/latest";
        }
    }

    /// <summary>Returns the latest release if it is newer than this client, or null.</summary>
    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        CleanUpOldDownloads();

        if (_server is not null && FeedOverride is null)
        {
            var (offered, update) = await CheckServerAsync(_server, cancellationToken);
            if (offered)
            {
                return update;
            }
        }

        return await CheckGitHubAsync(cancellationToken);
    }

    /// <summary>
    /// Asks the server for the client of its release. <c>Offered</c> is false when it has none to
    /// offer (or cannot be asked), and GitHub should be checked instead.
    /// </summary>
    private async Task<(bool Offered, AvailableUpdate? Update)> CheckServerAsync(Uri server, CancellationToken cancellationToken)
    {
        ClientReleaseDto? release;
        try
        {
            using var response = await _http.GetAsync(new Uri(server, "api/client/latest"), cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (false, null);
            }

            response.EnsureSuccessStatusCode();
            release = await response.Content.ReadFromJsonAsync<ClientReleaseDto>(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            ClientLog.Error($"Could not ask the server for updates ({ex.Message}); trying GitHub.");
            return (false, null);
        }

        if (release is null || !ReleaseVersion.TryParse(release.Version, out var version))
        {
            return (false, null);
        }

        if (version <= CurrentVersion)
        {
            return (true, null);
        }

        // Only download from the server itself, whatever the answer says.
        var installer = release.Downloads.FirstOrDefault(d => d.Platform == ClientPlatforms.WindowsInstaller);
        string? installerUrl = null;
        if (installer is not null && Uri.TryCreate(server, installer.Url, out var url) &&
            url.Scheme == server.Scheme && url.Host == server.Host && url.Port == server.Port)
        {
            installerUrl = url.ToString();
        }

        return (true, new AvailableUpdate(version, new Uri(server, "download").ToString(), installerUrl,
            installerUrl is null ? null : installer!.FileName, null, installerUrl is null ? null : installer!.Sha256));
    }

    private async Task<AvailableUpdate?> CheckGitHubAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, FeedUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var release = document.RootElement;

        if (Flag(release, "draft") || Flag(release, "prerelease") ||
            !ReleaseVersion.TryParse(Text(release, "tag_name"), out var version) ||
            version <= CurrentVersion)
        {
            return null;
        }

        string? installerName = null, installerUrl = null, checksumsUrl = null;
        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = Text(asset, "name");
                var url = Text(asset, "browser_download_url");
                if (string.Equals(name, $"Discagem-{version}-win-x64-setup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    (installerName, installerUrl) = (name, url);
                }
                else if (name == "SHA256SUMS")
                {
                    checksumsUrl = url;
                }
            }
        }

        var page = Text(release, "html_url") ?? "https://github.com/rodriguesarrais/discagem/releases/latest";
        return new AvailableUpdate(version, page, installerUrl, installerName, checksumsUrl);
    }

    /// <summary>Downloads and verifies the update's installer. Returns its path.</summary>
    public async Task<string> DownloadInstallerAsync(AvailableUpdate update, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        if (update.InstallerUrl is null || update.InstallerName is null || (update.Sha256 is null && update.ChecksumsUrl is null))
        {
            throw new InvalidOperationException(Strings.Update_NoInstaller);
        }

        var expected = update.Sha256;
        if (expected is null)
        {
            var checksums = await _http.GetStringAsync(update.ChecksumsUrl, cancellationToken);
            expected = ExpectedHash(checksums, update.InstallerName)
                ?? throw new InvalidOperationException(Strings.Update_ChecksumMissing);
        }

        Directory.CreateDirectory(DownloadDirectory);
        var path = Path.Combine(DownloadDirectory, update.InstallerName);
        using var response = await _http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                received += read;
                if (total > 0)
                {
                    progress?.Report((double)received / total.Value);
                }
            }
        }

        if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidOperationException(Strings.Update_InstallerCorrupt);
        }

        return path;
    }

    /// <summary>
    /// Runs the installer silently. It waits for this client to exit, replaces it and starts the
    /// new version, so the caller should quit right after.
    /// </summary>
    public static void LaunchInstaller(string path) =>
        Process.Start(new ProcessStartInfo(path, "/S /RUN") { UseShellExecute = true });

    /// <summary>Finds a file's hash in <c>sha256sum</c> output ("hash  name" or "hash *name").</summary>
    internal static string? ExpectedHash(string checksums, string fileName)
    {
        foreach (var line in checksums.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == fileName && parts[0].Length == 64)
            {
                return parts[0];
            }
        }

        return null;
    }

    private static void CleanUpOldDownloads()
    {
        try
        {
            if (!Directory.Exists(DownloadDirectory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(DownloadDirectory))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An installer that is still running; try again next time.
        }
    }

    public static void OpenInBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public void Dispose() => _http.Dispose();
}
