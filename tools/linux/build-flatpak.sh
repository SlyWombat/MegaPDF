#!/usr/bin/env bash
# Builds the MegaPDF Flatpak from a tree that tools/build-linux-app.sh has already
# produced (#158), and leaves a single-file bundle beside the repository it exported.
#
#     tools/build-linux-app.sh linux-x64 artifacts/linux
#     tools/linux/build-flatpak.sh [tree-dir] [out-dir]
#
#   tree-dir  the unpacked app, default artifacts/linux/MegaPDF
#   out-dir   default artifacts/flatpak; holds build/, repo/ and the .flatpak bundle
#
# Needs flatpak, flatpak-builder, appstreamcli and desktop-file-validate, plus the
# org.freedesktop Platform and Sdk the manifest names. In a container it also needs
# user namespaces, which bubblewrap uses and Docker does not grant by default —
# tools/Linux-Packaging.md has the exact flags.
#
# NOTHING HERE PUBLISHES. It exports to a local ostree repository and makes a bundle.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
TREE="${1:-$ROOT/artifacts/linux/MegaPDF}"
OUT="${2:-$ROOT/artifacts/flatpak}"
SRC="$ROOT/tools/linux/flatpak"

MANIFEST="$(ls "$SRC"/*.yml | head -1)"
APP_ID="$(basename "$MANIFEST" .yml)"
CONTEXT="$OUT/context"

[ -d "$TREE/bin" ] || { echo "::error::no app tree at $TREE — run tools/build-linux-app.sh first" >&2; exit 1; }

echo "building $APP_ID from $TREE"

# --- the build context ----------------------------------------------------------
# Assembled rather than referenced in place: the manifest's sources are relative to
# the manifest, and the tree is built wherever the caller asked for it.
rm -rf "$CONTEXT"
mkdir -p "$CONTEXT"
cp -a "$TREE" "$CONTEXT/tree"
cp "$MANIFEST" "$SRC/megapdf.sh" "$SRC/$APP_ID.metainfo.xml" "$CONTEXT/"

# The desktop entry is the one the unpacked tree ships, renamed and re-keyed rather
# than duplicated: two copies of it would drift, and the one thing that must differ
# is the icon name, which Flatpak requires to be the app ID.
sed -e "s/^Icon=.*/Icon=$APP_ID/" \
    "$ROOT/tools/linux/megapdf.desktop" > "$CONTEXT/$APP_ID.desktop"

# --- what a store reads ---------------------------------------------------------
# Validated before the build rather than after: a metainfo file that fails here is
# a listing that fails at submission, and the build takes minutes.
desktop-file-validate "$CONTEXT/$APP_ID.desktop"
echo "  desktop entry valid"

# --no-net because the screenshot URLs are checked by fetching them otherwise, which
# a build machine should not depend on. The runbook says who checks them and when.
appstreamcli validate --no-net --explain "$CONTEXT/$APP_ID.metainfo.xml"
echo "  metainfo valid"

# flatpak-builder runs `appstreamcli compose` from the build machine, not from inside
# the sandbox, and that reads the scalable icon through gdk-pixbuf. Without the SVG
# loader it stops with "Unrecognized image file format" naming a file that is a
# perfectly good SVG, and the build fails at the very last step. Said here, before the
# minutes are spent, because the message it fails with does not name the package.
if ! find /usr/lib /usr/lib64 -name 'libpixbufloader*svg*' -print -quit 2>/dev/null | grep -q .; then
    echo "::warning::no gdk-pixbuf SVG loader on this machine — install librsvg2-common," \
         "or appstreamcli compose will refuse the scalable icon at the end of the build" >&2
fi

# The notices must be in the tree before it is wrapped: a package without them cannot
# ship (#194), and finding that out after the build wastes the build.
[ -s "$TREE/share/doc/MegaPDF/THIRD-PARTY-NOTICES.txt" ] \
    || { echo "::error::the tree has no THIRD-PARTY-NOTICES.txt — build-linux-app.sh should have refused (#194)" >&2; exit 1; }

# --- build ----------------------------------------------------------------------
# --disable-rofiles-fuse: rofiles-fuse needs /dev/fuse, which a container does not
# have unless it is given one. The cost is that the build copies where it would have
# hard-linked, on a tree of ninety megabytes.
rm -rf "$OUT/build" "$OUT/repo" "$OUT/.flatpak-builder"
# --state-dir inside the output directory: flatpak-builder puts its cache beside the
# working directory by default and refuses to run when that is on another filesystem,
# which it is whenever the output is on a mount of its own.
flatpak-builder --force-clean --disable-rofiles-fuse \
    --state-dir="$OUT/.flatpak-builder" \
    --repo="$OUT/repo" "$OUT/build" "$CONTEXT/$(basename "$MANIFEST")"

BUNDLE="$OUT/$APP_ID.flatpak"
rm -f "$BUNDLE"
flatpak build-bundle "$OUT/repo" "$BUNDLE" "$APP_ID"

echo
echo "built: $BUNDLE"
du -h "$BUNDLE" | cut -f1 | sed 's/^/  bundle  /'
du -sh "$OUT/build/files" | cut -f1 | sed 's/^/  files   /'
echo
echo "install it with:  flatpak install --user $BUNDLE"
echo "run it with:      flatpak run $APP_ID"
