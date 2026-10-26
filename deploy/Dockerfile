# Discagem server image. Build from the repository root:
#   docker build -f deploy/Dockerfile -t discagem-server .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Discagem.Shared/Discagem.Shared.csproj src/Discagem.Shared/
COPY src/Discagem.Media/Discagem.Media.csproj src/Discagem.Media/
COPY src/Discagem.Server/Discagem.Server.csproj src/Discagem.Server/
RUN dotnet restore src/Discagem.Server/Discagem.Server.csproj

COPY src/Discagem.Shared/ src/Discagem.Shared/
COPY src/Discagem.Media/ src/Discagem.Media/
COPY src/Discagem.Server/ src/Discagem.Server/
RUN dotnet publish src/Discagem.Server/Discagem.Server.csproj -c Release -o /app --no-restore -p:DebugType=none

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENV Urls=http://127.0.0.1:5080 \
    Discagem__DataDirectory=/data

RUN mkdir -p /data && chown "$APP_UID" /data
VOLUME ["/data"]
USER $APP_UID

# 5080/tcp: HTTP API and SignalR (put a TLS reverse proxy in front)
# 3478/udp: built-in STUN responder
# 50000-50999/udp: WebRTC media
EXPOSE 5080/tcp 3478/udp 50000-50999/udp

ENTRYPOINT ["./Discagem.Server"]
