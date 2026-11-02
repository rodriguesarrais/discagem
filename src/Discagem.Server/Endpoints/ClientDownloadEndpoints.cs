using System.Text;
using System.Text.Encodings.Web;
using Discagem.Server.Options;
using Discagem.Server.Services;
using Discagem.Shared;
using Microsoft.Extensions.Options;

namespace Discagem.Server.Endpoints;

/// <summary>
/// The desktop client, served by the server that carries it: a download page for people joining,
/// the files themselves, and the release that installed clients update to. None of it needs an
/// account (the invite code still guards registration).
/// </summary>
public static class ClientDownloadEndpoints
{
    public static void MapClientDownloadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/client/latest", (ClientDownloads downloads) =>
            downloads.Release is { } release ? Results.Ok(release) : Results.NotFound());

        app.MapGet("/downloads/{fileName}", (string fileName, ClientDownloads downloads) =>
            downloads.PathOf(fileName) is { } path
                ? Results.File(path, "application/octet-stream", fileName, enableRangeProcessing: true)
                : Results.NotFound());

        app.MapGet("/download", (HttpContext context, ClientDownloads downloads, IOptions<DiscagemOptions> options) =>
            Results.Content(DownloadPage(downloads.Release, options.Value.ServerName, context.Request.Host.Value ?? ""),
                "text/html; charset=utf-8"));
    }

    private static string DownloadPage(ClientReleaseDto? release, string serverName, string host)
    {
        var html = HtmlEncoder.Default;
        var page = new StringBuilder();
        page.Append("""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Download Discagem</title>
            <style>
              :root { color-scheme: dark; }
              body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #17181c;
                     color: #ecedf1; font: 15px/1.5 system-ui, "Segoe UI", sans-serif; }
              main { width: min(560px, calc(100% - 32px)); padding: 32px 0; }
              h1 { margin: 0; font-size: 28px; }
              .sub { margin: 4px 0 24px; color: #b7bac4; }
              section { background: #1f2025; border-radius: 12px; padding: 18px 20px; margin-bottom: 12px; }
              h2 { margin: 0 0 4px; font-size: 16px; }
              p { margin: 6px 0 0; color: #b7bac4; }
              a.button { display: inline-block; margin-top: 10px; padding: 9px 16px; border-radius: 6px;
                         background: #7c6cff; color: #fff; font-weight: 600; text-decoration: none; }
              a.button:hover { background: #6b5bf0; }
              code { background: #26272d; padding: 2px 6px; border-radius: 4px; font-size: 13px; overflow-wrap: anywhere; }
              .size { color: #868a96; font-size: 13px; margin-left: 8px; }
            </style>
            </head>
            <body>
            <main>
            <h1>Discagem</h1>
            """);

        if (release is null)
        {
            page.Append($"""
                <p class="sub">Desktop app for {html.Encode(serverName)}</p>
                <section><p>This server does not offer the app for download. Ask its owner for the installer.</p></section>
                """);
        }
        else
        {
            page.Append($"""<p class="sub">Desktop app for {html.Encode(serverName)} · version {html.Encode(release.Version)}</p>""");
            foreach (var download in release.Downloads)
            {
                var file = html.Encode(download.FileName);
                var link = $"""<a class="button" href="{html.Encode(download.Url)}">Download</a><span class="size">{download.Size / (1024.0 * 1024):0.#} MB</span>""";
                page.Append(download.Platform switch
                {
                    ClientPlatforms.WindowsInstaller => $"""
                        <section><h2>Windows 10 and 11</h2>
                        <p>Run the installer (no administrator rights needed). The app keeps itself up to date.</p>{link}</section>
                        """,
                    ClientPlatforms.Deb => $"""
                        <section><h2>Debian, Ubuntu and derivatives</h2>
                        <p>Install it with <code>sudo apt install ./{file}</code></p>{link}</section>
                        """,
                    ClientPlatforms.Flatpak => $"""
                        <section><h2>Other Linux distributions (Flatpak)</h2>
                        <p>Install it with <code>flatpak install --user {file}</code></p>{link}</section>
                        """,
                    _ => "",
                });
            }
        }

        page.Append($"""
            <p>Then open Discagem, choose <b>Register</b> (with the invite code you were given) or <b>Log in</b>, and enter
            the server address <code>{html.Encode(host)}</code>.</p>
            </main>
            </body>
            </html>
            """);
        return page.ToString();
    }
}
