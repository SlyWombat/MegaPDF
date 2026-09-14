#!/usr/bin/env bash
# Fetches the pinned PDFium linux-x64 prebuilt (MegaPDF's patched build, libs/pdfium/RELEASE) for the
# shared engine core's test target (#104, ADR-003). No app ships from Linux; this
# exists so `core/` can be built and tested on the cheapest CI runner, with
# AddressSanitizer. Same doctrine as tools/fetch-pdfium-mac.sh: the build is read
# from libs/pdfium/win-x64/VERSION and the download is verified against it.
#
# Usage: tools/fetch-pdfium-linux.sh [dest-dir]
#   dest-dir defaults to libs/pdfium/linux-x64 (gitignored), where
#   core/CMakeLists.txt looks for lib/libpdfium.so.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PIN="$ROOT/libs/pdfium/win-x64/VERSION"
DEST="${1:-$ROOT/libs/pdfium/linux-x64}"

[ -f "$PIN" ] || { echo "::error::pin file not found: $PIN" >&2; exit 1; }
BUILD="$(grep -E '^BUILD=' "$PIN" | cut -d= -f2 | tr -d '[:space:]')"
[ -n "$BUILD" ] || { echo "::error::could not read BUILD= from $PIN" >&2; exit 1; }

if [ -f "$DEST/lib/libpdfium.so" ]; then
    echo "already present — skipping fetch: $DEST/lib/libpdfium.so"
    exit 0
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
URL="$(tr -d '[:space:]' < "$ROOT/libs/pdfium/RELEASE")/pdfium-linux-x64.tgz"
echo "fetching $URL"
curl -fsSL "$URL" -o "$TMP/linux.tgz"
mkdir -p "$TMP/x"
tar xzf "$TMP/linux.tgz" -C "$TMP/x"

if ! diff -q "$PIN" "$TMP/x/VERSION" >/dev/null 2>&1; then
    echo "::error::PDFium version mismatch — the linux-x64 build is not the pinned one."
    echo "--- expected:"; cat "$PIN"; echo "--- got:"; cat "$TMP/x/VERSION"
    exit 1
fi
[ -f "$TMP/x/lib/libpdfium.so" ] || { echo "::error::lib/libpdfium.so missing from tarball" >&2; exit 1; }

mkdir -p "$DEST"
cp -R "$TMP/x/lib" "$DEST/"
cp "$TMP/x/VERSION" "$DEST/VERSION"
cp "$TMP/x/LICENSE" "$DEST/LICENSE" 2>/dev/null || true
echo "done: $DEST/lib/libpdfium.so"
