#!/usr/bin/env bash
# Installs MegaPDF for the person running it — no root, nothing outside $HOME
# (#158). Run it from inside an unpacked MegaPDF tree:
#
#     ./install.sh            # into ~/.local
#     PREFIX=~/apps ./install.sh
#
# What this gives you: MegaPDF in the applications menu, PDF files offering it in
# Open With, the right icon everywhere, and `megapdf file.pdf` on the command
# line. Undo it all with ./uninstall.sh.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
PREFIX="${PREFIX:-$HOME/.local}"
LIBDIR="$PREFIX/lib/megapdf"
BINDIR="$PREFIX/bin"
DESKTOP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICON_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor"
META_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/metainfo"

[ -x "$HERE/bin/MegaPDF" ] || { echo "error: run this from inside an unpacked MegaPDF tree" >&2; exit 1; }

echo "installing MegaPDF $(cat "$HERE/VERSION" 2>/dev/null || echo '') into $PREFIX"

# The whole tree, replaced rather than merged: a leftover .so from an older
# version beside a new apphost is the one failure mode worth designing out, and
# it would show up as a crash in the engine rather than as a bad install.
rm -rf "$LIBDIR"
mkdir -p "$LIBDIR" "$BINDIR" "$DESKTOP_DIR"
cp -R "$HERE/bin/." "$LIBDIR/"
[ -d "$HERE/share/doc" ] && cp -R "$HERE/share/doc" "$LIBDIR/doc"

ln -sf "$LIBDIR/MegaPDF" "$BINDIR/megapdf"
# megapdf-cli (#142, #356), the same way: a symlink whose $ORIGIN runpath resolves to
# $LIBDIR once followed, exactly like tools/linux/build-deb.sh's /usr/bin/megapdf-cli.
[ -x "$LIBDIR/megapdf-cli" ] && ln -sf "$LIBDIR/megapdf-cli" "$BINDIR/megapdf-cli"

# The desktop entry is rewritten with an absolute Exec rather than shipped with
# "Exec=megapdf": ~/.local/bin is on PATH for a login shell on every current
# distribution, but a desktop entry is launched by the session, whose PATH is
# not always the same one — and an entry whose Exec cannot be resolved fails
# with no message at all.
sed "s|^Exec=megapdf |Exec=$LIBDIR/MegaPDF |" \
    "$HERE/share/applications/megapdf.desktop" > "$DESKTOP_DIR/megapdf.desktop"
chmod 644 "$DESKTOP_DIR/megapdf.desktop"

mkdir -p "$ICON_DIR"
cp -R "$HERE/share/icons/hicolor/." "$ICON_DIR/"

# The AppStream listing, so a software centre shows MegaPDF with its description and
# screenshots (#318). The tarball carries the Flatpak's copy under flatpak/; its
# launchable is pointed at the desktop file installed above. Skipped quietly for a
# tree that has none (a build straight out of tools/build-linux-app.sh).
META_SRC="$HERE/flatpak/ca.electricrv.MegaPDF.metainfo.xml"
if [ -f "$META_SRC" ]; then
    mkdir -p "$META_DIR"
    sed 's|<launchable type="desktop-id">[^<]*</launchable>|<launchable type="desktop-id">megapdf.desktop</launchable>|' \
        "$META_SRC" > "$META_DIR/ca.electricrv.MegaPDF.metainfo.xml"
fi

# These caches are what make the entry appear without logging out. Each is
# best-effort: a desktop that has none of them reads the directories directly.
# update-desktop-database is also what puts MegaPDF in a PDF's Open With menu,
# by writing megapdf.desktop under application/pdf in mimeinfo.cache.
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$DESKTOP_DIR" 2>/dev/null || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -f -t "$ICON_DIR" 2>/dev/null || true

# Deliberately NOT `xdg-mime default megapdf.desktop application/pdf`. Installing
# an app is not the same as asking for every PDF on the machine, and an installer
# that quietly takes the association away from whatever was handling it is the
# behaviour people uninstall software over. The Mac bundle says the same thing in
# its own words — LSHandlerRank=Alternate — and MegaPDF appears in Open With
# either way. Choosing it is the person's to do:
#     xdg-mime default megapdf.desktop application/pdf

echo "installed:"
echo "  $LIBDIR/MegaPDF"
echo "  $BINDIR/megapdf"
[ -x "$LIBDIR/megapdf-cli" ] && echo "  $BINDIR/megapdf-cli"
echo "  $DESKTOP_DIR/megapdf.desktop"
echo
echo "MegaPDF now offers itself in a PDF's Open With menu. To make it the one that"
echo "opens PDFs by default:  xdg-mime default megapdf.desktop application/pdf"
case ":$PATH:" in
    *":$BINDIR:"*) ;;
    *) echo "note: $BINDIR is not on your PATH yet, so the 'megapdf' command won't be found in"
       echo "      this terminal. On most distributions it joins PATH at your next login: log out"
       echo "      and back in. The applications menu works straight away." ;;
esac
