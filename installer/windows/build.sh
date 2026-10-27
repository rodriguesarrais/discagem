#!/usr/bin/env bash
# Builds the Windows installer: publishes the client for win-x64 and packs it with NSIS.
#
# Usage: installer/windows/build.sh <version> [output directory]
#
# Needs the .NET 10 SDK and makensis (NSIS 3; "apt install nsis" on Debian/Ubuntu). Writes
# Discagem-<version>-win-x64-setup.exe to the output directory (default: dist). Set PROJECT_URL to
# change the link shown by the installer and in "Installed apps".
set -euo pipefail

version="${1:?usage: build.sh <version> [output directory]}"
output_dir="${2:-dist}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

if ! [[ "$version" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)(-[0-9A-Za-z.-]+)?$ ]]; then
  echo "error: \"$version\" is not a valid version (expected something like 1.2.3 or 1.2.3-beta.1)" >&2
  exit 1
fi
numeric="${BASH_REMATCH[1]}.${BASH_REMATCH[2]}.${BASH_REMATCH[3]}.0"

staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT

# A regular (not single-file) publish: installed apps start faster when nothing has to be extracted.
dotnet publish "$root/src/Discagem.Client" -c Release -r win-x64 --self-contained \
  -p:DebugType=none -p:Version="$version" -o "$staging/app"

mkdir -p "$output_dir"
output="$(cd "$output_dir" && pwd)/Discagem-$version-win-x64-setup.exe"
defines=(
  "-DVERSION=$version"
  "-DVERSION_NUMERIC=$numeric"
  "-DSOURCE_DIR=$staging/app"
  "-DOUTPUT_FILE=$output"
)
if [[ -n "${PROJECT_URL:-}" ]]; then
  defines+=("-DPROJECT_URL=$PROJECT_URL")
fi
makensis -V2 "${defines[@]}" "$root/installer/windows/discagem.nsi"

echo "Wrote $output"
