#!/usr/bin/env bash
# Fetches the pinned PDFium macOS universal prebuilt (bblanchon/pdfium-binaries)
# for ADR-002's spike. Unlike ios/scripts/fetch-pdfium.sh, which hardcodes the
# release tag, this reads the pin from libs/pdfium/win-x64/VERSION and then
# *verifies* the downloaded slice reports the same build — which makes SDD §6.1's
# "all platforms pin the same PDFium major" machine-enforced rather than a comment.
#
# The dylib is fetched, never committed: 14.6 MB in git to answer a decision
# question isn't worth it, and the iOS tree already establishes
# fetch-and-gitignore as the accepted pattern.
#
# Usage: tools/fetch-pdfium-mac.sh [dest-dir]
#   dest-dir defaults to libs/pdfium/mac-univ, which is where
#   MegaPDF.Core.csproj looks for it (and which .gitignore excludes)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PIN="$ROOT/libs/pdfium/win-x64/VERSION"
DEST="${1:-$ROOT/libs/pdfium/mac-univ}"

[ -f "$PIN" ] || { echo "::error::pin file not found: $PIN" >&2; exit 1; }

BUILD="$(grep -E '^BUILD=' "$PIN" | cut -d= -f2 | tr -d '[:space:]')"
MAJOR="$(grep -E '^MAJOR=' "$PIN" | cut -d= -f2 | tr -d '[:space:]')"
[ -n "$BUILD" ] || { echo "::error::could not read BUILD= from $PIN" >&2; exit 1; }
echo "pinned PDFium: ${MAJOR}.x build ${BUILD} (from libs/pdfium/win-x64/VERSION)"

if [ -f "$DEST/lib/libpdfium.dylib" ]; then
    echo "already present — skipping fetch: $DEST/lib/libpdfium.dylib"
    exit 0
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# mac-univ is a genuine 2-slice fat binary (x86_64 + arm64), so one download
# covers Apple Silicon and Intel. Verified 2026-08-27 against build 7934.
URL="https://github.com/bblanchon/pdfium-binaries/releases/download/chromium%2F${BUILD}/pdfium-mac-univ.tgz"
echo "fetching $URL"
curl -fsSL "$URL" -o "$TMP/mac.tgz"
mkdir -p "$TMP/x"
tar xzf "$TMP/mac.tgz" -C "$TMP/x"

# The doctrine check. A silent drift here would put macOS on different
# heuristics from Windows/Android/iOS, which SDD §6.2 treats as a breaking change.
if ! diff -q "$PIN" "$TMP/x/VERSION" >/dev/null 2>&1; then
    echo "::error::PDFium version mismatch — the macOS slice is not the pinned build."
    echo "--- expected (libs/pdfium/win-x64/VERSION):"; cat "$PIN"
    echo "--- got (pdfium-mac-univ):"; cat "$TMP/x/VERSION"
    exit 1
fi
echo "version check passed — macOS slice matches the Windows pin exactly"

[ -f "$TMP/x/lib/libpdfium.dylib" ] || { echo "::error::lib/libpdfium.dylib missing from tarball" >&2; exit 1; }

mkdir -p "$DEST"
cp -R "$TMP/x/lib" "$DEST/"
cp "$TMP/x/VERSION" "$DEST/VERSION"
cp "$TMP/x/LICENSE" "$DEST/LICENSE" 2>/dev/null || true
cp -R "$TMP/x/licenses" "$DEST/" 2>/dev/null || true

# Note for whoever vendors this for the shipping app: the macOS licenses/ set is
# byte-identical in filenames to libs/pdfium/win-x64/licenses/ (checked
# 2026-08-27), so THIRD-PARTY-NOTICES would not change — but re-run
# tools/gen_third_party_notices.py and commit the result anyway, because the
# `notices` CI job hard-fails on drift.
echo "done: $DEST/lib/libpdfium.dylib"
