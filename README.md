# Discagem

A self-hosted, Discord-style chat with screen sharing, written entirely in C#.
The server is ASP.NET Core and runs on a single private VPS; the desktop client is a
native Avalonia application for Windows (and Linux/X11).

![Chat](docs/screenshots/chat.png)

| Watching a screen share | Choosing what to share |
| --- | --- |
| ![Stream](docs/screenshots/stream.png) | ![Screen picker](docs/screenshots/screen-picker.png) |

## Features

**Text chat**
- Text channels (created, renamed and deleted by the administrator), with unread and mention badges.
- Messages grouped by author and day, Discord-like markdown (`**bold**`, `*italic*`, `__underline__`,
  `~~strike~~`, `` `code` ``, fenced code blocks, quotes), clickable links and `@mentions`.
- Edit (`↑` edits your last message) and delete; administrators can delete any message.
- Image previews and file attachments (button or drag and drop), up to 25 MB by default.
- Typing indicators, online/offline presence, history paging and automatic reconnection.

**Screen sharing**
- Share a whole monitor or a single application window, with live thumbnails in the picker.
- Quality presets from 720p30 to 1440p30 / source resolution; VP8 tuned for screen content.
- Any number of people can watch at the same time; the stream can be popped out or shown full screen.
- Live statistics (resolution, frame rate, bitrate) for both the streamer and viewers.

**Private by design**
- Accounts require an invite code; the first account becomes the administrator.
- Everything runs on your server: no third-party STUN/TURN or media services are involved.

## Architecture

```
 Desktop client (Avalonia)                            VPS
┌──────────────────────────┐   HTTPS + WSS    ┌─────────┐  HTTP   ┌──────────────────────────────┐
│ REST client              │ ───────────────► │  Caddy  │ ──────► │ ASP.NET Core                 │
│ SignalR client           │  (TLS, :443)     │ (:443)  │ (:5080) │  • REST: auth, uploads       │
│                          │                  └─────────┘         │  • SignalR hub: chat,        │
│ WebRTC peer (SIPSorcery) │                                      │    presence, signalling      │
│  capture → VP8 → RTP     │ ◄──────────── UDP 50000-50999 ─────► │  • SFU (SIPSorcery WebRTC)   │
│  RTP → VP8 → display     │      ICE · DTLS-SRTP · RTP/RTCP      │  • STUN responder (UDP 3478) │
└──────────────────────────┘                                      │  • SQLite + file storage     │
                                                                  └──────────────────────────────┘
```

| Concern | Protocol |
| --- | --- |
| Sign-in, profile, file upload/download | HTTPS (REST + JWT bearer tokens) |
| Messages, typing, presence, stream lifecycle | SignalR over WebSocket |
| Screen-share signalling | SDP offer/answer and trickled ICE candidates over the SignalR hub |
| Screen-share media | WebRTC: ICE, DTLS-SRTP, RTP/RTCP with NACK and PLI feedback |

### How screen sharing works

The server acts as an **SFU** (selective forwarding unit): the streamer uploads one video stream and
the server forwards it to every viewer. Because every client only talks to the server, which has a
public address, no TURN server or peer-to-peer NAT traversal is needed.

1. The streamer's client captures the screen (GDI on Windows, X11 on Linux), scales it, converts it to
   I420 and encodes it with libvpx (VP8 in real-time screen-content mode, CBR).
2. Frames are split into RTP packets and paced onto the network, so key frames do not leave as a burst.
3. The server forwards each packet to every viewer, keeps a short history, answers viewers' NACKs from
   that history, NACKs the streamer for packets lost on the uplink, and relays key frame requests (PLI).
4. Viewers reorder packets in a jitter buffer, request retransmissions of missing ones, decode VP8 and
   draw the picture. After unrecoverable loss they wait for (and request) the next key frame.

The RTP payload starts with a standard one-byte VP8 descriptor followed by a 16-bit *media sequence
number* that is preserved end to end. Retransmissions therefore travel with fresh RTP sequence numbers,
which keeps them clear of the SRTP replay window (see `src/Discagem.Media/Rtp/VideoPayload.cs`).
A simple loss-based controller lowers the bitrate when the streamer's uplink drops packets.

## Running the server on a VPS

Requirements: a Linux VPS with a public IP, Docker with the Compose plugin, and a DNS name pointing to
the server (for example `chat.example.com`).

1. **Open the firewall.** TCP `80` and `443` (Caddy/HTTPS), UDP `3478` (STUN) and UDP `50000-50999`
   (media). With `ufw`:
   ```bash
   sudo ufw allow 80/tcp && sudo ufw allow 443/tcp
   sudo ufw allow 3478/udp && sudo ufw allow 50000:50999/udp
   ```
   Cloud providers usually have an additional firewall/security group: open the same ports there.
2. **Configure.**
   ```bash
   git clone https://github.com/rodriguesarrais/discagem.git && cd discagem/deploy
   cp .env.example .env
   nano .env   # set DISCAGEM_DOMAIN and a long, random Discagem__InviteCode
   ```
   If the VPS sits behind 1:1 NAT (its network interface only has a private address, as on AWS,
   Google Cloud or Oracle Cloud), also set `Discagem__Media__PublicIpAddress` to the public IP.
3. **Start it.**
   ```bash
   docker compose up -d --build
   docker compose logs -f discagem
   ```
   Caddy obtains a Let's Encrypt certificate automatically. Data (SQLite database, uploads and the
   token signing key) lives in the `discagem-data` volume; back it up to keep your history.
4. **Create the administrator.** Open the client, choose *Register*, enter your domain, the invite code
   and your details. The first account is the administrator; share the invite code with your friends.

### Without Docker

```bash
dotnet publish src/Discagem.Server -c Release -r linux-x64 --self-contained -o /opt/discagem
sudo useradd --system --home /var/lib/discagem --create-home discagem
echo 'Discagem__InviteCode=change-me' | sudo tee /etc/discagem.env
sudo cp deploy/discagem.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now discagem
```

Then put a TLS reverse proxy in front of `127.0.0.1:5080` (the `deploy/Caddyfile` works with a
native Caddy install as well).

### Server configuration

Settings live in `src/Discagem.Server/appsettings.json` and can be overridden with environment
variables (`Section__Key`, as in `.env.example`).

| Setting | Default | Description |
| --- | --- | --- |
| `Urls` | `http://127.0.0.1:5080` | HTTP listen address (keep it on loopback behind the proxy). |
| `Discagem:ServerName` | `Discagem` | Name shown in the client. |
| `Discagem:InviteCode` | *(empty)* | Required to register. When empty only the first account can be created. |
| `Discagem:DataDirectory` | `data` | Database, uploads and generated signing key. |
| `Discagem:MaxUploadBytes` | `26214400` | Maximum attachment size. |
| `Discagem:Jwt:Key` | *(generated)* | Token signing key (32+ characters). Generated and stored in the data directory when empty. |
| `Discagem:Jwt:LifetimeDays` | `30` | Session lifetime; clients refresh their token on start-up. |
| `Discagem:Media:PublicIpAddress` | *(empty)* | Public IP to announce when behind 1:1 NAT. |
| `Discagem:Media:BindAddress` | *(empty)* | Local address for media sockets. |
| `Discagem:Media:RtpPortStart` / `RtpPortEnd` | `50000` / `50999` | UDP range for media; each stream or viewer uses one port. |
| `Discagem:Media:StunPort` | `3478` | Built-in STUN responder (`0` disables it). |
| `Discagem:Media:IceServers` | `[]` | Extra ICE servers handed to clients (e.g. a TURN server). |
| `Discagem:Media:MaxViewersPerStream` | `25` | Viewer limit per stream. |

## Installing the client

### Windows

Download the `Discagem-win-x64` artifact from the latest CI run, or build it yourself with the
.NET 10 SDK:

```powershell
dotnet publish src/Discagem.Client -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o publish/win-x64
```

Keep `Discagem.exe` and `vpxmd.dll` (the VP8 codec) together. Screen sharing needs the
[Microsoft Visual C++ Redistributable (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe), which most
machines already have; the client tells you if it is missing.

### Linux

Install libvpx (`sudo apt install libvpx9` on Ubuntu 24.04, `libvpx7` on older releases) and run
`dotnet run --project src/Discagem.Client`. Screen sharing requires an X11 session; Wayland capture is
not implemented yet.

Settings and the saved session are stored in `%APPDATA%\Discagem` (Windows, with the session token
encrypted by DPAPI) or `~/.config/Discagem` (Linux). Set `DISCAGEM_PROFILE=name` to run several
clients side by side with separate profiles.

## Development

Requirements: .NET 10 SDK and libvpx (for the media tests and the client on Linux).

```bash
# Server on http://localhost:5080 (invite code "dev", see appsettings.Development.json)
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Discagem.Server

# Client
dotnet run --project src/Discagem.Client

# Tests: unit tests plus integration tests that stream video through the SFU over real UDP sockets
dotnet test
```

Database schema changes use EF Core migrations
(`dotnet ef migrations add <Name> --project src/Discagem.Server --output-dir Data/Migrations`);
they are applied automatically on start-up.

### Project layout

| Path | Contents |
| --- | --- |
| `src/Discagem.Shared` | DTOs, hub contracts and the chat markup parser shared by server and client. |
| `src/Discagem.Media` | libvpx VP8 binding, colour conversion and scaling, RTP payload format, pacer, jitter buffer, NACK tracking, WebRTC publisher/viewer and the capture/encode/decode pipelines. |
| `src/Discagem.Server` | ASP.NET Core host: auth, attachments, SignalR hub, SFU, STUN responder, EF Core (SQLite). |
| `src/Discagem.Client` | Avalonia desktop app (MVVM with CommunityToolkit.Mvvm), screen capture for Windows and X11. |
| `tests/Discagem.Tests` | xUnit tests for media, markup, REST, hub and end-to-end streaming. |
| `deploy/` | Dockerfile, Compose file with Caddy, environment template and systemd unit. |

## Security notes

- Registration requires the invite code; login and registration are rate limited per IP.
- Passwords are hashed with ASP.NET Core Identity's PBKDF2 hasher. Changing the password revokes
  existing sessions.
- Run the server behind HTTPS (the Compose setup does this with Caddy). Media is always encrypted with
  DTLS-SRTP.
- Attachments are only served to signed-in users; only common image types are served inline.

## Limitations and ideas

- No voice chat yet; the SFU and signalling are designed so audio tracks can be added later.
- Screen sharing does not include system audio.
- Capture uses GDI on Windows; DXGI Desktop Duplication and hardware H.264 encoding would lower CPU usage.
- Networks that block all outbound UDP cannot receive streams (there is no TCP/TURN fallback).
- Wayland and macOS capture are not implemented.
