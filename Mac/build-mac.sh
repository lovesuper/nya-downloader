#!/bin/bash
# Builds NyaDownloader.app for Apple Silicon and Intel Macs and zips each one for a GitHub release:
#   publish/mac/NyaDownloader-mac-arm64.zip
#   publish/mac/NyaDownloader-mac-x64.zip
# The zip names are what the app looks for when it updates itself (AppUpdate.AssetName).
#
# Needs the .NET 10 SDK and macOS (codesign, ditto). The app is signed ad hoc, which is enough to run it.
set -euo pipefail

cd "$(dirname "$0")"
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' ../VideoDownloader.csproj | head -1)
OUT=../publish/mac

rm -rf "$OUT"
mkdir -p "$OUT"

for ARCH in arm64 x64; do
    echo "== NyaDownloader $VERSION for $ARCH"
    BIN="$OUT/bin-$ARCH"
    dotnet publish NyaDownloader.Mac.csproj -c Release -r "osx-$ARCH" --self-contained true -o "$BIN"

    APP="$OUT/$ARCH/NyaDownloader.app"
    mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
    cp -R "$BIN/." "$APP/Contents/MacOS/"
    sed "s/@VERSION@/$VERSION/g" Info.plist > "$APP/Contents/Info.plist"
    cp NyaDownloader.icns "$APP/Contents/Resources/"

    codesign --force --deep --sign - "$APP"
    codesign --verify --deep --strict "$APP"

    ditto -c -k --keepParent "$APP" "$OUT/NyaDownloader-mac-$ARCH.zip"
    rm -rf "$BIN"
    echo "   $OUT/NyaDownloader-mac-$ARCH.zip"
done
