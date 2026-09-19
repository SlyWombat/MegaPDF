#!/usr/bin/env bash
# The release tarball a Flathub manifest fetches, and its checksum (#254 A3).
#
#     tools/build-linux-app.sh linux-x64 artifacts/linux
#     tools/linux/make-release-tarball.sh [tree-dir] [out-dir]
#
#   tree-dir  the built app, default artifacts/linux/MegaPDF
#   out-dir   default artifacts/release
#
# Output: megapdf-linux-x64-<version>.tar.gz and megapdf-linux-x64-<version>.tar.gz.sha256,
# the same shape the other platforms' tag builds attach to a release.
#
# **One archive, one checksum.** Flathub's manifest lives in its own repository and has
# to name every source with a URL and a hash; a manifest with four of them is four
# things to get right at release time and four things to notice when one of them is
# stale. So the tarball carries the packaging files too — the launcher, the desktop
# entry and the metainfo — under flatpak/, exactly as the build that produced the tree
# had them. What Flathub builds is then provably the same listing this repository
# reviewed, rather than one fetched separately and hoped to match.
#
# It also checks the one thing about a release that is easy to get wrong and impossible
# to see afterwards: that the metainfo's newest <release> is this version, with a date
# that is not months old.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
TREE="${1:-$ROOT/artifacts/linux/MegaPDF}"
OUT="${2:-$ROOT/artifacts/release}"
SRC="$ROOT/tools/linux/flatpak"
MANIFEST="$(ls "$SRC"/*.yml | head -1)"
APP_ID="$(basename "$MANIFEST" .yml)"

[ -x "$TREE/bin/MegaPDF" ] || {
    echo "::error::no app tree at $TREE — run tools/build-linux-app.sh first" >&2; exit 1; }
[ -f "$TREE/VERSION" ] || { echo "::error::$TREE has no VERSION file" >&2; exit 1; }

VERSION="$(tr -d '[:space:]' < "$TREE/VERSION")"
# Named for the package version, which carries a packaging revision when the tarball
# was rebuilt around an unchanged app (tools/linux/PACKAGE-REVISION, #315), so a
# rebuilt tarball never shares a name with the published one it replaces. The
# metainfo check below is about the app, so it stays on VERSION.
NAME="megapdf-linux-x64-$("$ROOT/tools/linux/package-version.sh" "$VERSION")"

# --- the metainfo has to agree with what is being released ------------------------
# A release history whose top entry is the *previous* version is the kind of thing a
# software centre shows for years and nobody notices, because the package is right and
# only the listing is wrong. Checked here, where the version is already in hand.
META="$SRC/$APP_ID.metainfo.xml"
TOP_VERSION="$(grep -oE '<release version="[^"]+"' "$META" | head -1 | sed 's/.*version="//; s/"//')"
TOP_DATE="$(grep -oE '<release version="[^"]+" date="[^"]+"' "$META" | head -1 | sed 's/.*date="//; s/"//')"
if [ "$TOP_VERSION" != "$VERSION" ]; then
    echo "::error::the metainfo's newest <release> is $TOP_VERSION, but this build is $VERSION." >&2
    echo "         Add a <release version=\"$VERSION\" date=\"$(date -u +%F)\"> to $META" >&2
    exit 1
fi
STALE_AFTER_DAYS=${STALE_AFTER_DAYS:-30}
if ! age=$(( ( $(date -u +%s) - $(date -u -d "$TOP_DATE" +%s) ) / 86400 )) 2>/dev/null; then
    echo "::error::the metainfo's newest <release> has no date this script can read: '$TOP_DATE'" >&2
    exit 1
fi
if [ "$age" -gt "$STALE_AFTER_DAYS" ] || [ "$age" -lt -1 ]; then
    echo "::error::the metainfo says $VERSION was released on $TOP_DATE, which is $age day(s)" >&2
    echo "         from today. Set it to the day this release is actually made." >&2
    exit 1
fi
echo "metainfo: newest release is $VERSION, dated $TOP_DATE ($age day(s) ago)"

# --- the tarball ------------------------------------------------------------------
rm -rf "$OUT/$NAME"
mkdir -p "$OUT/$NAME"
cp -a "$TREE/." "$OUT/$NAME/"

# The packaging files, from the same commit as the tree. The desktop entry is
# re-keyed the way tools/linux/build-flatpak.sh re-keys it — Flatpak requires the
# icon name to be the app ID — so the Flathub manifest installs it unchanged.
mkdir -p "$OUT/$NAME/flatpak"
cp "$SRC/megapdf.sh" "$SRC/$APP_ID.metainfo.xml" "$OUT/$NAME/flatpak/"
sed -e "s/^Icon=.*/Icon=$APP_ID/" \
    "$ROOT/tools/linux/megapdf.desktop" > "$OUT/$NAME/flatpak/$APP_ID.desktop"

# Reproducible enough to be worth comparing: sorted, with one owner and one timestamp,
# so two builds of the same tree give the same bytes and a checksum that moved means
# something moved.
TARBALL="$OUT/$NAME.tar.gz"
rm -f "$TARBALL" "$TARBALL.sha256"
tar --sort=name --owner=0 --group=0 --numeric-owner \
    --mtime="@$(date -u -d "${TOP_DATE}T00:00:00Z" +%s)" \
    -C "$OUT" -czf "$TARBALL" "$NAME"
rm -rf "$OUT/$NAME"

( cd "$OUT" && sha256sum "$(basename "$TARBALL")" > "$(basename "$TARBALL").sha256" )

echo "tarball:  $TARBALL ($(du -h "$TARBALL" | cut -f1))"
echo "checksum: $(cut -d' ' -f1 < "$TARBALL.sha256")"
