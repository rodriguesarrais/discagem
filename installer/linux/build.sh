#!/usr/bin/env bash
# Builds the Linux packages of the desktop client from one self-contained build (no .NET needed on
# the computer that installs them):
#
#   installer/linux/build.sh <version> <output folder> [deb] [flatpak]
#
#   deb      discagem_<version>_amd64.deb for Debian 11+, Ubuntu 20.04+ and derivatives (needs dpkg-deb)
#   flatpak  Discagem-<version>-x86_64.flatpak for any distribution with Flatpak (needs flatpak and
#            flatpak-builder with the Flathub remote, from which the freedesktop runtime is installed)
#
# Without a format, both are built. FLATPAK_BUILDER_ARGS adds options to flatpak-builder (for
# example --disable-rofiles-fuse where FUSE is not available).
set -euo pipefail

VERSION=${1:?Usage: build.sh <version> <output folder> [deb] [flatpak]}
OUT=${2:?Usage: build.sh <version> <output folder> [deb] [flatpak]}
shift 2
FORMATS=("$@")
if [[ ${#FORMATS[@]} -eq 0 ]]; then
  FORMATS=(deb flatpak)
fi

APP_ID=io.github.rodriguesarrais.Discagem
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../.." && pwd)
mkdir -p "$OUT"
OUT=$(cd "$OUT" && pwd)
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

echo "Publishing the client $VERSION"
dotnet publish "$ROOT/src/Discagem.Client" -c Release -r linux-x64 --self-contained \
  -p:DebugType=none -p:Version="$VERSION" -o "$WORK/app"
# LTTng tracing support: optional, and it would pull in liblttng-ust.
rm -f "$WORK/app/libcoreclrtraceptprovider.so"

# Desktop integration, shared by both packages: launcher entry, software centre metadata, icons.
share=$WORK/share
install -Dm644 "$HERE/$APP_ID.desktop" "$share/applications/$APP_ID.desktop"
mkdir -p "$share/metainfo"
sed -e "s/@VERSION@/$VERSION/" -e "s/@DATE@/$(date -u +%Y-%m-%d)/" "$HERE/$APP_ID.metainfo.xml" \
  > "$share/metainfo/$APP_ID.metainfo.xml"
mkdir -p "$share/icons"
cp -r "$HERE/icons/hicolor" "$share/icons/"

build_deb() {
  # Debian sorts "~" before anything, so 1.0.0~beta.1 comes before 1.0.0.
  local version=${VERSION/-/\~}
  local root=$WORK/deb
  mkdir -p "$root/DEBIAN" "$root/usr/lib" "$root/usr/bin" "$root/usr/share"
  cp -a "$WORK/app" "$root/usr/lib/discagem"
  ln -s ../lib/discagem/Discagem "$root/usr/bin/discagem"
  cp -a "$share/." "$root/usr/share/"
  find "$root" -type d -exec chmod 755 {} +
  find "$root" -type f -exec chmod go-w {} +

  local size
  size=$(du -sk --exclude=DEBIAN "$root" | cut -f1)
  cat > "$root/DEBIAN/control" <<EOF
Package: discagem
Version: $version
Architecture: amd64
Maintainer: rodriguesarrais <134543499+rodriguesarrais@users.noreply.github.com>
Installed-Size: $size
Depends: libc6 (>= 2.27), libgcc-s1, libstdc++6, zlib1g,
 libicu78 | libicu77 | libicu76 | libicu75 | libicu74 | libicu73 | libicu72 | libicu71 | libicu70 | libicu69 | libicu68 | libicu67 | libicu66,
 libssl3t64 | libssl3 | libssl1.1, libfontconfig1,
 libx11-6, libice6, libsm6, libxcursor1, libxext6, libxfixes3, libxi6, libxrandr2,
 libpulse0, libvpx12 | libvpx11 | libvpx10 | libvpx9 | libvpx8 | libvpx7 | libvpx6
Recommends: libgl1, libgtk-3-0, pipewire-pulse | pulseaudio
Section: net
Priority: optional
Homepage: https://github.com/rodriguesarrais/discagem
Description: Self-hosted text chat, voice channels and screen sharing
 Discagem is the desktop client of a chat server you run yourself, for a group
 of friends or a small team: text channels with images and files, voice
 channels, and screen sharing with sound. Nothing goes through third-party
 services.
EOF
  local file="$OUT/discagem_${version}_amd64.deb"
  dpkg-deb --root-owner-group -Zxz --build "$root" "$file"
  echo "Built $file"
}

build_flatpak() {
  local dir=$WORK/flatpak
  mkdir -p "$dir"
  cp "$HERE/flatpak/$APP_ID.yml" "$dir/"
  cp -a "$WORK/app" "$dir/app"
  cp -a "$share" "$dir/share"
  # flatpak-builder keeps its cache (.flatpak-builder) in the current directory.
  (
    cd "$WORK"
    # shellcheck disable=SC2086
    flatpak-builder --user --install-deps-from=flathub --force-clean --repo="$WORK/repo" \
      ${FLATPAK_BUILDER_ARGS:-} "$WORK/build" "$dir/$APP_ID.yml"
  )
  local file="$OUT/Discagem-$VERSION-x86_64.flatpak"
  flatpak build-bundle --runtime-repo=https://dl.flathub.org/repo/flathub.flatpakrepo \
    "$WORK/repo" "$file" "$APP_ID"
  echo "Built $file"
}

for format in "${FORMATS[@]}"; do
  case "$format" in
    deb) build_deb ;;
    flatpak) build_flatpak ;;
    *) echo "Unknown format: $format (expected deb or flatpak)" >&2; exit 1 ;;
  esac
done
