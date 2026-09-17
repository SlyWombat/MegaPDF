#!/usr/bin/env bash
# Removes what tools/linux/install.sh installed (#158). Same PREFIX, or the
# same default.
#
#     ./uninstall.sh
#     PREFIX=~/apps ./uninstall.sh
#
# Your signature library and settings are NOT removed: they are your data, they
# all live under ~/.local/share/MegaPDF (.NET maps LocalApplicationData to
# XDG_DATA_HOME on Linux), and an uninstall that silently threw away a signature
# you spent ten minutes photographing would be the worst thing this script could
# do. The path is printed instead.
set -euo pipefail

PREFIX="${PREFIX:-$HOME/.local}"
LIBDIR="$PREFIX/lib/megapdf"
BINDIR="$PREFIX/bin"
DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
DESKTOP_DIR="$DATA_HOME/applications"
ICON_DIR="$DATA_HOME/icons/hicolor"

rm -rf "$LIBDIR"
# Only our own symlink, and only if it still points into the tree we removed —
# never a megapdf someone else's package manager put there.
if [ -L "$BINDIR/megapdf" ] && [ "$(readlink "$BINDIR/megapdf")" = "$LIBDIR/MegaPDF" ]; then
    rm -f "$BINDIR/megapdf"
fi
rm -f "$DESKTOP_DIR/megapdf.desktop"

# Only the files this app installed, by name. Removing the size directories
# would take every other application's icons with them. Guarded: with
# `set -o pipefail`, a find over a directory that is not there fails the whole
# script, and an uninstall that stops half way is worse than one that finds
# nothing to do.
if [ -d "$ICON_DIR" ]; then
    find "$ICON_DIR" \( -name 'megapdf.png' -o -name 'megapdf.svg' \) -delete
fi

command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$DESKTOP_DIR" 2>/dev/null || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -f -t "$ICON_DIR" 2>/dev/null || true

echo "MegaPDF removed from $PREFIX."
echo "Your signatures, settings and recent documents were kept:"
echo "  $DATA_HOME/MegaPDF"
