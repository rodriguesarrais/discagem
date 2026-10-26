# Discagem server image. Build from the repository root:
#   docker build -f deploy/Dockerfile -t discagem-server .
#
# The SDK stage always runs on the build machine's architecture and cross-compiles for the target,
# so multi-architecture images (linux/amd64, linux/arm64) build quickly without emulation.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG VERSION=0.1.0
WORKDIR /source

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Discagem.Shared/Discagem.Shared.csproj src/Discagem.Shared/
COPY src/Discagem.Media/Discagem.Media.csproj src/Discagem.Media/
COPY src/Discagem.Server/Discagem.Server.csproj src/Discagem.Server/
RUN dotnet restore src/Discagem.Server/Discagem.Server.csproj ${TARGETARCH:+-a $TARGETARCH}

COPY src/Discagem.Shared/ src/Discagem.Shared/
COPY src/Discagem.Media/ src/Discagem.Media/
COPY src/Discagem.Server/ src/Discagem.Server/
RUN dotnet publish src/Discagem.Server/Discagem.Server.csproj -c Release ${TARGETARCH:+-a $TARGETARCH} -o /app --no-restore \
    -p:DebugType=none -p:Version=$VERSION \
    && mkdir /data

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Created in the build stage so that no command has to run on the target architecture.
COPY --from=build --chown=$APP_UID:$APP_UID /data /data

ENV Urls=http://127.0.0.1:5080 \
    Discagem__DataDirectory=/data

VOLUME ["/data"]
USER $APP_UID

# 5080/tcp: HTTP API and SignalR (put a TLS reverse proxy in front)
# 3478/udp: built-in STUN responder
# 50000-50999/udp: WebRTC media
EXPOSE 5080/tcp 3478/udp 50000-50999/udp

ENTRYPOINT ["./Discagem.Server"]
