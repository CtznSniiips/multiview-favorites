#!/usr/bin/env bash
# Builds Emby.MultiviewFavorites.dll.
#
# The plugin compiles against Emby's own SDK assemblies (MediaBrowser.Common /
# Controller / Model). Point EMBY_SDK_DIR at an Emby install's "system" folder,
# or let this script pull them from the official Emby server .deb on GitHub.
#
#   ./build.sh                                   # downloads Emby 4.9.5.0 SDK DLLs into ./sdk
#   EMBY_SDK_DIR=/opt/emby-server/system ./build.sh
#   EMBY_VERSION=4.8.11.0 ./build.sh             # compile against a different server version
set -euo pipefail
cd "$(dirname "$0")"

EMBY_VERSION="${EMBY_VERSION:-4.9.5.0}"
VERSION="${VERSION:-}"            # plugin version, e.g. 1.2.0 (optional)
SDK_DIR="${EMBY_SDK_DIR:-$PWD/sdk}"

if [[ ! -f "$SDK_DIR/MediaBrowser.Controller.dll" ]]; then
  echo "Fetching Emby $EMBY_VERSION SDK assemblies..."
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT
  curl -fL -o "$tmp/emby.deb" \
    "https://github.com/MediaBrowser/Emby.Releases/releases/download/${EMBY_VERSION}/emby-server-deb_${EMBY_VERSION}_amd64.deb"
  (cd "$tmp" && ar x emby.deb && mkdir x && tar -xf data.tar.* -C x)
  mkdir -p "$SDK_DIR"
  cp "$tmp"/x/opt/emby-server/system/MediaBrowser.{Common,Controller,Model}.dll "$SDK_DIR/"
fi

props=(-p:EmbySdkDir="$SDK_DIR")
if [[ -n "$VERSION" ]]; then
  if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
    echo "VERSION must look like 1.2.3 or 1.2.3.4 (got '$VERSION')" >&2
    exit 1
  fi
  # Emby shows the assembly version; it must be 4-part.
  asm="$VERSION"
  [[ "$asm" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] && asm="$asm.0"
  props+=(-p:Version="$VERSION" -p:AssemblyVersion="$asm" -p:FileVersion="$asm")
fi

dotnet build src/Emby.MultiviewFavorites/Emby.MultiviewFavorites.csproj -c Release "${props[@]}"

mkdir -p dist
cp src/Emby.MultiviewFavorites/bin/Release/netstandard2.1/Emby.MultiviewFavorites.dll dist/
echo
echo "Built dist/Emby.MultiviewFavorites.dll"
echo "Copy it into Emby's plugins folder (e.g. /var/lib/emby/plugins) and restart Emby."
