using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Discagem.Server.Services;
using Discagem.Shared;

namespace Discagem.Tests.Server;

public sealed class ClientDownloadTests : IDisposable
{
    private readonly DiscagemFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private byte[] AddPackage(string name, int size = 4096)
    {
        Directory.CreateDirectory(_factory.ClientDownloadsDirectory);
        var bytes = RandomNumberGenerator.GetBytes(size);
        File.WriteAllBytes(Path.Combine(_factory.ClientDownloadsDirectory, name), bytes);
        return bytes;
    }

    [Fact]
    public async Task WithoutPackages_NothingIsOffered()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/client/latest")).StatusCode);
        var page = await client.GetStringAsync("/download");
        Assert.Contains("does not offer the app for download", page);
    }

    [Fact]
    public async Task OffersTheNewestPackagesWithTheirChecksums()
    {
        var installer = AddPackage("Discagem-0.9.0-win-x64-setup.exe");
        AddPackage("discagem_0.9.0_amd64.deb");
        AddPackage("Discagem-0.9.0-x86_64.flatpak");
        AddPackage("Discagem-0.8.0-win-x64-setup.exe");
        AddPackage("notes.txt");
        var client = _factory.CreateClient();

        var release = await client.GetFromJsonAsync<ClientReleaseDto>("/api/client/latest");

        Assert.NotNull(release);
        Assert.Equal("0.9.0", release.Version);
        Assert.Equal(
            [ClientPlatforms.Deb, ClientPlatforms.Flatpak, ClientPlatforms.WindowsInstaller],
            release.Downloads.Select(d => d.Platform).Order(StringComparer.Ordinal));
        var windows = release.Downloads.Single(d => d.Platform == ClientPlatforms.WindowsInstaller);
        Assert.Equal("Discagem-0.9.0-win-x64-setup.exe", windows.FileName);
        Assert.Equal(installer.Length, windows.Size);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(installer)), windows.Sha256);
        Assert.Equal("downloads/Discagem-0.9.0-win-x64-setup.exe", windows.Url);

        var downloaded = await client.GetByteArrayAsync("/" + windows.Url);
        Assert.Equal(installer, downloaded);
    }

    [Theory]
    [InlineData("Discagem-0.8.0-win-x64-setup.exe")] // an older version than the one on offer
    [InlineData("notes.txt")]
    [InlineData("..%2Fdiscagem.db")]
    [InlineData("missing.exe")]
    public async Task ServesOnlyThePackagesOnOffer(string fileName)
    {
        AddPackage("Discagem-0.9.0-win-x64-setup.exe");
        AddPackage("Discagem-0.8.0-win-x64-setup.exe");
        AddPackage("notes.txt");
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/downloads/" + fileName);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DownloadPage_ListsThePackagesAndTheServerAddress()
    {
        AddPackage("Discagem-1.2.3-win-x64-setup.exe");
        AddPackage("Discagem-1.2.3-x86_64.flatpak");
        var client = _factory.CreateClient();

        var page = await client.GetStringAsync("/download");

        Assert.Contains("version 1.2.3", page);
        Assert.Contains("href=\"downloads/Discagem-1.2.3-win-x64-setup.exe\"", page);
        Assert.Contains("flatpak install --user Discagem-1.2.3-x86_64.flatpak", page);
        Assert.DoesNotContain("apt install", page);
        Assert.Contains($"<code>{client.BaseAddress!.Authority}</code>", page);
    }

    [Theory]
    [InlineData("Discagem-0.5.0-win-x64-setup.exe", "0.5.0", ClientPlatforms.WindowsInstaller)]
    [InlineData("discagem_1.0.0~beta.1_amd64.deb", "1.0.0-beta.1", ClientPlatforms.Deb)]
    [InlineData("Discagem-2.0.0-rc.2-x86_64.flatpak", "2.0.0-rc.2", ClientPlatforms.Flatpak)]
    public void RecognisesReleasePackageNames(string fileName, string version, string platform)
    {
        var package = ClientDownloads.Recognize(fileName);

        Assert.NotNull(package);
        Assert.Equal(version, package.Value.Version.ToString());
        Assert.Equal(platform, package.Value.Platform);
    }

    [Theory]
    [InlineData("Discagem-0.5.0-linux-x64.tar.gz")]
    [InlineData("Discagem-0.5.0-win-x64.zip")]
    [InlineData("discagem-server-0.5.0-linux-x64.tar.gz")]
    [InlineData("SHA256SUMS")]
    public void IgnoresOtherFiles(string fileName) => Assert.Null(ClientDownloads.Recognize(fileName));
}
