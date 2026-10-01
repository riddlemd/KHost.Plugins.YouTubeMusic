#!/bin/bash
# Builds KHost's YouTube Music helper, "YouTube Music.app": universal (arm64 + x86_64), ad-hoc signed.
#
#   build.sh <output-folder> [--bundle-id <id>] [--name <display name>] [--version <x.y.z>]
#   build.sh --prebuilt     rebuild helpers/macos/prebuilt/ and its sources.sha256 stamp
#
# Needs macOS with the Xcode command line tools (xcrun swiftc, lipo, codesign). The plugin's own
# build runs this on a Mac; a build anywhere else ships helpers/macos/prebuilt/ instead, and refuses
# to when that copy's stamp no longer matches these sources.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
BUNDLE_ID="com.khost.youtube-music-helper"
NAME="YouTube Music"
VERSION="1.0.0"
OUT=""
PREBUILT=0

while [ $# -gt 0 ]; do
  case "$1" in
    --bundle-id) BUNDLE_ID="$2"; shift 2 ;;
    --name) NAME="$2"; shift 2 ;;
    --version) VERSION="$2"; shift 2 ;;
    --prebuilt) PREBUILT=1; shift ;;
    -*) echo "build.sh: unknown option $1" >&2; exit 2 ;;
    *) OUT="$1"; shift ;;
  esac
done

if [ "$PREBUILT" = 1 ]; then
  OUT="$HERE/prebuilt"
fi

if [ -z "$OUT" ]; then
  echo "usage: build.sh <output-folder> [--bundle-id <id>] [--name <name>] [--version <x.y.z>] | --prebuilt" >&2
  exit 2
fi

if [ "$(uname -s)" != "Darwin" ]; then
  echo "build.sh: the helper builds only on macOS" >&2
  exit 1
fi

SDK="$(xcrun --sdk macosx --show-sdk-path)"
APP="$OUT/$NAME.app"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/ytm-helper.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

for ARCH in arm64 x86_64; do
  xcrun --sdk macosx swiftc -sdk "$SDK" -swift-version 5 -O -whole-module-optimization \
    -target "$ARCH-apple-macos13.0" \
    -o "$WORK/helper-$ARCH" "$HERE/main.swift"
done

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
xcrun lipo -create -output "$APP/Contents/MacOS/khost-youtube-music-helper" "$WORK/helper-arm64" "$WORK/helper-x86_64"
cp "$HERE/youtube-music-page.js" "$APP/Contents/Resources/youtube-music-page.js"
sed -e "s/@BUNDLE_ID@/$BUNDLE_ID/g" -e "s/@NAME@/$NAME/g" -e "s/@VERSION@/$VERSION/g" \
  "$HERE/Info.plist.in" > "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"

codesign --sign - --force --timestamp=none "$APP"
codesign --verify --strict "$APP"

if [ "$PREBUILT" = 1 ]; then
  # Read by the plugin's build on Windows and Linux, which cannot rebuild the helper and refuses to
  # ship it once these sources have moved on from it.
  (cd "$HERE" && shasum -a 256 main.swift youtube-music-page.js Info.plist.in build.sh | awk '{print $1 "  " $2}') > "$OUT/sources.sha256"
fi

echo "built $APP"
