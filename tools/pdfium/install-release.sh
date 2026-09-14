#!/bin/bash
# Installs MegaPDF's patched PDFium release into the committed trees (#120):
# libs/pdfium/win-x64 (pdfium.dll) and libs/pdfium/android (arm64-v8a and x86_64
# libpdfium.so plus the public headers every platform's core build uses).
#
# macOS, Linux and iOS fetch at build time from the same libs/pdfium/RELEASE, and
# their fetch scripts insist the download's VERSION matches libs/pdfium/win-x64/VERSION,
# so this script also checks that every archive in the release carries one VERSION.
#
#   tools/pdfium/install-release.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
BASE="$(tr -d '[:space:]' < "$ROOT/libs/pdfium/RELEASE")"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

fetch() {
    local name=$1
    echo "fetching $name"
    curl -fsSL "$BASE/$name.tgz" -o "$TMP/$name.tgz"
    mkdir -p "$TMP/$name"
    tar xzf "$TMP/$name.tgz" -C "$TMP/$name"
}

for name in pdfium-win-x64 pdfium-android-arm64 pdfium-android-x64 pdfium-linux-x64 pdfium-mac-univ \
            pdfium-ios-device-arm64 pdfium-ios-simulator-arm64 pdfium-ios-simulator-x64; do
    fetch "$name"
done

# One build everywhere: identical VERSION files, carrying the MegaPDF patch series.
REFERENCE="$TMP/pdfium-linux-x64/VERSION"
grep -q '^MEGAPDF_PATCHES=' "$REFERENCE" || { echo "::error::release VERSION has no MEGAPDF_PATCHES line" >&2; exit 1; }
for dir in "$TMP"/pdfium-*/; do
    diff -q "$REFERENCE" "$dir/VERSION" >/dev/null || { echo "::error::VERSION differs in $(basename "$dir")" >&2; exit 1; }
done
echo "every archive carries:"; cat "$REFERENCE"

# Windows: the DLL, its VERSION and licences.
WIN="$ROOT/libs/pdfium/win-x64"
cp "$TMP/pdfium-win-x64/bin/pdfium.dll" "$WIN/pdfium.dll"
cp "$REFERENCE" "$WIN/VERSION"
cp "$TMP/pdfium-win-x64/LICENSE" "$WIN/LICENSE"
rm -rf "$WIN/licenses" && cp -R "$TMP/pdfium-win-x64/licenses" "$WIN/licenses"

# Android: both ABIs, the headers (shared by every platform's core build), VERSION, licences.
AND="$ROOT/libs/pdfium/android"
cp "$TMP/pdfium-android-arm64/lib/libpdfium.so" "$AND/lib/arm64-v8a/libpdfium.so"
cp "$TMP/pdfium-android-x64/lib/libpdfium.so" "$AND/lib/x86_64/libpdfium.so"
rm -rf "$AND/include" && cp -R "$TMP/pdfium-android-arm64/include" "$AND/include"
cp "$REFERENCE" "$AND/VERSION"
cp "$TMP/pdfium-android-arm64/LICENSE" "$AND/LICENSE"
rm -rf "$AND/licenses" && cp -R "$TMP/pdfium-android-arm64/licenses" "$AND/licenses"

# Fetched trees on this machine are now stale; the fetch scripts skip when present.
rm -rf "$ROOT/libs/pdfium/linux-x64" "$ROOT/libs/pdfium/mac-univ" "$ROOT/ios/Vendor/pdfium.xcframework"
echo "installed $(sed -n 's/^MEGAPDF_SERIES=//p' "$REFERENCE") into libs/pdfium/win-x64 and libs/pdfium/android"
