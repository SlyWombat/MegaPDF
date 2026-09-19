#!/usr/bin/env bash
# Builds the MegaPDF snap from a tree that tools/build-linux-app.sh has already
# produced (#158), the same way build-flatpak.sh wraps it.
#
#     tools/build-linux-app.sh linux-x64 artifacts/linux
#     sudo tools/linux/build-snap.sh [tree-dir] [out-dir]
#
#   tree-dir  the unpacked app, default artifacts/linux/MegaPDF
#   out-dir   default artifacts/snap; holds context/ and megapdf_<version>_amd64.snap
#
# Runs `snapcraft pack --destructive-mode`, which builds on this machine rather than
# in an LXD container: the machine must therefore be Ubuntu 24.04, the release core24
# is built from, and snapcraft must be installed (`sudo snap install snapcraft
# --classic`). Root, because the gnome extension installs its SDK snap through snapd.
# A GitHub ubuntu-24.04 runner is exactly that machine, which is where CI builds it
# (.github/workflows/snap.yml).
#
# NOTHING HERE UPLOADS. It writes a .snap file.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
TREE="${1:-$ROOT/artifacts/linux/MegaPDF}"
OUT="${2:-$ROOT/artifacts/snap}"
SRC="$ROOT/tools/linux/snap"
METAINFO="$ROOT/tools/linux/flatpak/ca.electricrv.MegaPDF.metainfo.xml"
CONTEXT="$OUT/context"

[ -d "$TREE/bin" ] || { echo "::error::no app tree at $TREE — run tools/build-linux-app.sh first" >&2; exit 1; }
[ -s "$TREE/VERSION" ] || { echo "::error::$TREE/VERSION is missing" >&2; exit 1; }
VERSION="$(cat "$TREE/VERSION")"
command -v snapcraft >/dev/null || { echo "::error::snapcraft is not installed (sudo snap install snapcraft --classic)" >&2; exit 1; }
. /etc/os-release
if [ "${VERSION_ID:-}" != "24.04" ]; then
    echo "::error::--destructive-mode builds core24 snaps on Ubuntu 24.04 only; this is ${PRETTY_NAME:-unknown}" >&2
    exit 1
fi

echo "building the megapdf snap $VERSION from $TREE"

# --- the build context ----------------------------------------------------------
# Assembled rather than built in place, like the Flatpak's: snapcraft treats its project
# directory as the source of everything, and the tree is wherever the caller built it.

rm -rf "$CONTEXT"
mkdir -p "$CONTEXT/snap/gui" "$CONTEXT/payload/lib"
cp -a "$TREE/bin" "$CONTEXT/payload/lib/megapdf"

# The notices beside the binary, where package-check.sh looks for them (#194).
[ -s "$CONTEXT/payload/lib/megapdf/THIRD-PARTY-NOTICES.txt" ] \
    || cp "$TREE/share/doc/MegaPDF/THIRD-PARTY-NOTICES.txt" "$CONTEXT/payload/lib/megapdf/" \
    || { echo "::error::the tree has no THIRD-PARTY-NOTICES.txt (#194)" >&2; exit 1; }
cp "$ROOT/LICENSE" "$CONTEXT/payload/lib/megapdf/LICENSE"

# The desktop entry is the one every other Linux package ships, with one change: a
# snap's icon is a path under the snap, not a theme name. Exec already says `megapdf
# %f`, which is this snap's command.
sed -e 's|^Icon=.*|Icon=${SNAP}/meta/gui/megapdf.png|' \
    "$ROOT/tools/linux/megapdf.desktop" > "$CONTEXT/snap/gui/megapdf.desktop"
cp "$ROOT/assets/branding/linux/hicolor/256x256/apps/megapdf.png" "$CONTEXT/snap/gui/megapdf.png"
if command -v desktop-file-validate >/dev/null; then
    desktop-file-validate "$CONTEXT/snap/gui/megapdf.desktop"
    echo "  desktop entry valid"
fi

python3 "$SRC/make-snapcraft-yaml.py" "$SRC/snapcraft.yaml.in" "$METAINFO" \
    "$VERSION" "$CONTEXT/snap/snapcraft.yaml"

# --- build ----------------------------------------------------------------------

mkdir -p "$OUT"
SNAP_FILE="$OUT/megapdf_${VERSION}_amd64.snap"
rm -f "$SNAP_FILE"
( cd "$CONTEXT" && snapcraft pack --destructive-mode --output "$SNAP_FILE" )

[ -s "$SNAP_FILE" ] || { echo "::error::snapcraft finished without writing $SNAP_FILE" >&2; exit 1; }

# The package, read back rather than trusted: the app is in it in one piece, with both
# native libraries and the notices, and the metadata says what the template said.
unsquashfs -l "$SNAP_FILE" > "$OUT/contents.txt"
for f in lib/megapdf/MegaPDF lib/megapdf/libpdfium.so lib/megapdf/libmegapdf_core.so \
         lib/megapdf/THIRD-PARTY-NOTICES.txt meta/snap.yaml meta/gui/megapdf.desktop \
         meta/gui/megapdf.png; do
    grep -q "squashfs-root/$f\$" "$OUT/contents.txt" \
        || { echo "::error::$f is not in the snap" >&2; exit 1; }
done
echo "  the app, both native libraries, the notices and the desktop entry are in it"

echo "built: $SNAP_FILE ($(du -h "$SNAP_FILE" | cut -f1))"
