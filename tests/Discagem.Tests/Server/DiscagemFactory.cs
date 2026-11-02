using System.Net.Http.Headers;
using System.Net.Http.Json;
using Discagem.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;

namespace Discagem.Tests.Server;

/// <summary>Hosts the server in-memory with an isolated data directory.</summary>
public sealed class DiscagemFactory : WebApplicationFactory<Program>
{
    public const string InviteCode = "test-invite";
    private static int _portBlock;

    public DiscagemFactory()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "discagem-tests", Guid.NewGuid().ToString("N"));
        // Separate RTP port ranges keep parallel test classes from competing for ports.
        var block = Interlocked.Increment(ref _portBlock);
        RtpPortStart = 52000 + block * 200;
    }

    public string DataDirectory { get; }

    /// <summary>Folder of client packages the server offers (absent unless a test creates it).</summary>
    public string ClientDownloadsDirectory => Path.Combine(DataDirectory, "client");

    public int RtpPortStart { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Discagem:DataDirectory"] = DataDirectory,
                ["Discagem:ClientDownloadsDirectory"] = ClientDownloadsDirectory,
                ["Discagem:InviteCode"] = InviteCode,
                ["Discagem:Media:StunPort"] = "0",
                ["Discagem:Media:RtpPortStart"] = RtpPortStart.ToString(),
                ["Discagem:Media:RtpPortEnd"] = (RtpPortStart + 199).ToString(),
                ["Logging:LogLevel:Default"] = "Warning",
            });
        });
    }

    public async Task<(HttpClient Client, AuthResponse Auth)> RegisterAsync(string username, string? displayName = null)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(username, displayName ?? username, "password123", InviteCode));
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return (client, auth);
    }

    public async Task<HubConnection> ConnectHubAsync(string token)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, HubPaths.Chat.TrimStart('/')), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    /// <summary>
    /// Connects over WebSockets, where (as with the real client) the token is only checked when the
    /// connection opens, so server-side revocation can be observed.
    /// </summary>
    public async Task<HubConnection> ConnectHubOverWebSocketsAsync(string token)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, HubPaths.Chat.TrimStart('/')), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                options.Transports = HttpTransportType.WebSockets;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var uri = new UriBuilder(context.Uri) { Scheme = "ws" };
                    uri.Query = uri.Query.TrimStart('?') + "&access_token=" + Uri.EscapeDataString(token);
                    return await Server.CreateWebSocketClient().ConnectAsync(uri.Uri, cancellationToken);
                };
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
