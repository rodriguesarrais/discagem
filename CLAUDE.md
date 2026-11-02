# Discagem: notes for Claude Code

Self-hosted chat with voice channels and screen sharing, in C# on .NET 10: an ASP.NET Core server
(`src/Discagem.Server`), an Avalonia desktop client (`src/Discagem.Client`), a media library
(`src/Discagem.Media`: VP8, Opus, RTP), shared contracts (`src/Discagem.Shared`) and the tests
(`tests/Discagem.Tests`). The README describes the architecture, deployment and releases.

## Working rules

- Everything written into the repository (code, comments, commit messages, pull requests, docs)
  is in formal English.
- Branches, commit messages and pull request titles follow [CONTRIBUTING.md](CONTRIBUTING.md):
  `<type>/<short-description>` branches and Conventional Commits. A workflow checks every pull
  request.
- Work on a branch cut from `main` and open a pull request; never push to `main` directly.
- Run `dotnet test` before pushing. `VoiceChannelTests.Voice_IsForwardedToEveryoneElse` can fail
  under heavy load; re-run it alone before treating it as a real failure.
- Schema changes need an EF Core migration (see the README's Development section).
- The repository is public. Never commit server addresses, hosting account details, e-mail
  addresses, keys or tokens. Personal notes belong in `CLAUDE.local.md`, which is gitignored.

## Running locally (Windows, PowerShell)

```powershell
# Server on http://localhost:5080, invite code "dev"
$env:ASPNETCORE_ENVIRONMENT = "Development"; dotnet run --project src/Discagem.Server

# Client, with its own settings so that it runs next to an installed Discagem
$env:DISCAGEM_PROFILE = "dev"; dotnet run --project src/Discagem.Client

dotnet test
```

## Releases

A release is a version tag (`git tag v0.5.2; git push origin v0.5.2`) or the *Release* workflow
run from the Actions tab. It builds the clients, installers and the server image; servers running
the updater in `deploy/updater` pick up the image by themselves, and clients then update from
their server. Only release when asked to.
