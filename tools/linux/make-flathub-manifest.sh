#!/usr/bin/env bash
# The Flathub manifest, filled in from a release tarball (#254 A3).
#
#     tools/linux/make-flathub-manifest.sh <tarball> [--local] [out.yml]
#
#   <tarball>  megapdf-linux-x64-<version>.tar.gz, from tools/linux/make-release-tarball.sh
#   --local    point the source at the file on disk instead of at its release URL, so
#              the manifest can be built before the tarball is published anywhere
#   out.yml    default artifacts/release/ca.electricrv.MegaPDF.yml
#
# The template is tools/linux/flatpak/flathub/ca.electricrv.MegaPDF.yml.in and is the
# thing to review; only the `sources:` block is filled in here. --local writes the same
# manifest with `path:` in place of `url:`, which is what lets the tag build prove the
# manifest against the artefact it has just made rather than against the one it hopes to
# publish — the two differ in one line, and that line is written by this script.
#
# NOTHING HERE PUBLISHES OR SUBMITS. The output is a file.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
TEMPLATE="$ROOT/tools/linux/flatpak/flathub/ca.electricrv.MegaPDF.yml.in"

TARBALL=
LOCAL=
OUT=
for arg in "$@"; do
    case "$arg" in
        --local) LOCAL=1 ;;
        *) if [ -z "$TARBALL" ]; then TARBALL=$arg; else OUT=$arg; fi ;;
    esac
done
[ -n "$TARBALL" ] || { sed -n '2,17p' "$0"; exit 2; }
[ -f "$TARBALL" ] || { echo "::error::no tarball at $TARBALL" >&2; exit 1; }
OUT="${OUT:-$ROOT/artifacts/release/ca.electricrv.MegaPDF.yml}"

BASE="$(basename "$TARBALL")"
VERSION="$(echo "$BASE" | sed -E 's/^megapdf-linux-x64-(.+)\.tar\.gz$/\1/')"
[ "$VERSION" != "$BASE" ] || {
    echo "::error::$BASE is not named megapdf-linux-x64-<version>.tar.gz" >&2; exit 1; }
SHA="$(sha256sum "$TARBALL" | cut -d' ' -f1)"

# The tag that carries this tarball: linux-v<version>, the Linux series of the
# per-platform tags this repository uses (ios-v*, android-v*). Not a bare v<version>,
# which v1.3.0 … v1.6.2 used for the retired Windows sideload builds.
URL="https://github.com/SlyWombat/MegaPDF/releases/download/linux-v$VERSION/$BASE"

mkdir -p "$(dirname "$OUT")"
if [ -n "$LOCAL" ]; then
    SOURCE="      # A dry run: the same archive, before it has been published anywhere.
      - type: archive
        path: $(cd "$(dirname "$TARBALL")" && pwd)/$BASE
        sha256: $SHA"
else
    SOURCE="      - type: archive
        url: $URL
        sha256: $SHA"
fi

awk -v src="$SOURCE" '{ if ($0 == "@SOURCE@") print src; else print }' "$TEMPLATE" > "$OUT"

echo "manifest: $OUT"
echo "version:  $VERSION"
if [ -n "$LOCAL" ]; then echo "source:   $TARBALL (local, not published)"; else echo "source:   $URL"; fi
echo "sha256:   $SHA"
