#!/usr/bin/env bash
# Builds a .deb of MegaPDF from a tree that tools/build-linux-app.sh has already
# produced (#158) — the portable format, for GitHub Releases.
#
#     tools/build-linux-app.sh linux-x64 artifacts/linux
#     tools/linux/build-deb.sh [tree-dir] [out-dir]
#
# A .deb rather than an AppImage: dpkg-deb --build needs no privileges, no FUSE and no
# tool that is not already on a build machine, where appimagetool needs all three. It
# is also what someone who has just downloaded a file from a Releases page will double
# click. The Flatpak is the one with the sandbox and the store; this is the one that
# installs on a machine.
#
# NOT a Debian-archive package. It installs the self-contained tree under /opt, which
# a package in Debian proper would not do — see tools/Linux-Packaging.md for the
# lintian tags that follow from that and why each is the right answer here.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
TREE="${1:-$ROOT/artifacts/linux/MegaPDF}"
OUT="${2:-$ROOT/artifacts/deb}"

PKG=megapdf
OPTDIR=/opt/MegaPDF

[ -d "$TREE/bin" ] || { echo "::error::no app tree at $TREE — run tools/build-linux-app.sh first" >&2; exit 1; }
VERSION="$(cat "$TREE/VERSION" 2>/dev/null || echo 0.1.0)"
# The package's own version: the app's, plus a packaging revision when the package was
# rebuilt around an unchanged app (tools/linux/PACKAGE-REVISION, #315).
PKG_VERSION="$("$ROOT/tools/linux/package-version.sh" "$VERSION")"
ARCH=amd64

STAGE="$OUT/$PKG-$PKG_VERSION"
rm -rf "$STAGE"
mkdir -p "$STAGE/DEBIAN" "$STAGE$OPTDIR" "$STAGE/usr/bin" \
         "$STAGE/usr/share/applications" "$STAGE/usr/share/doc/$PKG" "$STAGE/usr/share/metainfo"

echo "building $PKG $PKG_VERSION ($ARCH) from $TREE"

# --- the payload ----------------------------------------------------------------
# The published tree goes in one piece: the apphost finds libmegapdf_core.so and
# libpdfium.so beside itself, so splitting it into /usr/lib and /usr/bin the way a
# distribution package would is the one thing that breaks it.
cp -a "$TREE/bin/." "$STAGE$OPTDIR/"
# What the app reads to know it came from a .deb, so its About window can say updates
# arrive with the system's (when the APT repository is set up) rather than send a person
# to the download page (MegaPDF.Core LinuxInstall).
printf 'deb\n' > "$STAGE$OPTDIR/INSTALL-KIND"
ln -sf "$OPTDIR/MegaPDF" "$STAGE/usr/bin/megapdf"

# An absolute Exec, for the reason tools/linux/install.sh gives: a desktop entry is
# launched by the session, whose PATH is not always the login shell's, and an entry
# whose Exec cannot be resolved fails with no message at all.
sed "s|^Exec=megapdf |Exec=$OPTDIR/MegaPDF |" \
    "$ROOT/tools/linux/megapdf.desktop" > "$STAGE/usr/share/applications/$PKG.desktop"

# The AppStream listing, so a software centre (GNOME Software, KDE Discover) shows
# MegaPDF with its description and screenshots rather than a bare desktop entry (#318).
# One source for every channel: the Flatpak's metainfo, with its launchable pointed at
# the desktop file this package installs. The component ID stays the same, which is
# what tells a software centre it is the same app however it was installed.
META_SRC="$ROOT/tools/linux/flatpak/ca.electricrv.MegaPDF.metainfo.xml"
META="$STAGE/usr/share/metainfo/ca.electricrv.MegaPDF.metainfo.xml"
sed 's|<launchable type="desktop-id">[^<]*</launchable>|<launchable type="desktop-id">'"$PKG"'.desktop</launchable>|' \
    "$META_SRC" > "$META"
grep -q "<launchable type=\"desktop-id\">$PKG.desktop</launchable>" "$META" \
    || { echo "::error::the metainfo's launchable was not rewritten to $PKG.desktop" >&2; exit 1; }
if command -v appstreamcli >/dev/null 2>&1; then
    appstreamcli validate --no-net "$META" >/dev/null \
        || { appstreamcli validate --no-net "$META" >&2; echo "::error::the metainfo does not validate" >&2; exit 1; }
fi

# The theme directory has to exist before the copy. `cp -R src dst/` where dst is not
# there copies src *as* dst, so hicolor's contents landed straight in /usr/share/icons
# and every size sat at /usr/share/icons/48x48/apps/megapdf.png — a path no icon theme
# has, so nothing ever found the icon (#158 QA pass).
mkdir -p "$STAGE/usr/share/icons/hicolor"
cp -R "$TREE/share/icons/hicolor/." "$STAGE/usr/share/icons/hicolor/"

# Asserted, because the failure above was silent: cp succeeded, and the package looked
# complete right up to the point where a desktop tried to draw the icon.
for size in 16x16 48x48 256x256 scalable; do
    [ -n "$(find "$STAGE/usr/share/icons/hicolor/$size/apps" -type f -print -quit 2>/dev/null)" ] \
        || { echo "::error::no icon at usr/share/icons/hicolor/$size/apps — the theme directory is wrong" >&2; exit 1; }
done

# Several of these licences require the text to travel with the binary, and no channel
# accepts a package without it (#194). Refused rather than warned about.
[ -s "$TREE/share/doc/MegaPDF/THIRD-PARTY-NOTICES.txt" ] \
    || { echo "::error::the tree has no THIRD-PARTY-NOTICES.txt — build-linux-app.sh should have refused (#194)" >&2; exit 1; }

# Beside the binary, not only in /usr/share/doc. Debian and Ubuntu ship dpkg
# configurations that throw that directory away — every Docker image of either does,
# through path-exclude=/usr/share/doc/* in /etc/dpkg/dpkg.cfg.d/excludes, and so do
# minimal installs — and the notices were duly discarded on install when this package
# put them there alone. /opt is not excluded by anything. The copy under /usr/share/doc
# stays for whoever looks where the convention says to look; `copyright` survives the
# exclusion because the same configurations path-include it.
cp "$TREE/share/doc/MegaPDF/THIRD-PARTY-NOTICES.txt" "$STAGE$OPTDIR/THIRD-PARTY-NOTICES.txt"
cp "$TREE/share/doc/MegaPDF/THIRD-PARTY-NOTICES.txt" "$STAGE/usr/share/doc/$PKG/"
cp "$TREE/share/doc/MegaPDF/LICENSE" "$STAGE/usr/share/doc/$PKG/copyright"

# --- control --------------------------------------------------------------------
# The dependencies are the ones the app loads at run time, which objdump cannot see:
# .NET dlopens ICU and Avalonia.X11 dlopens the X libraries, so neither appears in any
# NEEDED entry. Established by installing this package in a bare container and running
# it — a missing libicu is a FailFast at the first CultureInfo, with a message about
# installing libicu and nothing about MegaPDF.
#
# The libicu alternatives: the soname is versioned and every release ships a different
# one, so naming just one would make the package refuse to install on every other
# release. The range runs from Ubuntu 22.04's libicu70 upwards, past the newest any
# release ships today (Ubuntu 26.04's libicu78, which 2.0.0 left out, #315), so the
# next one or two releases install too. .NET's ICU loader probes for the newest
# libicuuc it can find, so a soname the app has never met is still one it can load.
# Names that don't exist yet cost nothing: apt just takes the first one that does.
ICU_DEPS="$(for n in $(seq 80 -1 70); do printf 'libicu%s | ' "$n"; done | sed 's/ | $//')"
{
    echo "Package: $PKG"
    echo "Version: $PKG_VERSION"
    echo "Architecture: $ARCH"
    echo "Maintainer: Electric RV <noreply@electricrv.ca>"
    echo "Section: text"
    echo "Priority: optional"
    echo "Homepage: https://electricrv.ca/megapdf/"
    echo "Depends: libc6 (>= 2.35), libgcc-s1, libstdc++6, zlib1g, libfontconfig1, libfreetype6," \
         "libx11-6, libice6, libsm6, libxext6, libxi6, libxrandr2, libxcursor1," \
         "libssl3t64 | libssl3, $ICU_DEPS"
    # libssl: .NET's cryptography on Linux is OpenSSL, loaded at run time like ICU, and
    # a save needs it. It used to arrive only through cups-client's Recommends chain,
    # so a minimal install without recommends crashed at the first save (#316).
    # Installed-Size, in KiB, is what apt reports before it installs (#317).
    echo "Installed-Size: $(du -sk --exclude=DEBIAN "$STAGE" | cut -f1)"
    # Neither is needed to start, and a hard dependency on either would keep MegaPDF
    # off a machine that simply does not print or sign.
    echo "Recommends: cups-client"
    echo "Suggests: fonts-urw-base35"
    echo "Description: Fill, check and sign PDFs"
    echo " MegaPDF does the one job most people actually have with a PDF: someone sent"
    echo " you a form, and you need to send it back filled in, checked off and signed."
    echo " ."
    echo " It checks real form fields and plain printed squares alike, types on any"
    echo " line in a face that matches the form, retypes the document's own text, places"
    echo " a drawn or photographed signature, and verifies every document before it"
    echo " touches your original, so a failed save cannot corrupt the file you were sent."
    echo " ."
    echo " No account, no subscription and no network connection of any kind."
} > "$STAGE/DEBIAN/control"

# Refreshing the caches is what puts the app in the menu and its icon on the file; both
# are best-effort, because a container or a chroot has neither database.
cat > "$STAGE/DEBIAN/postinst" <<'POSTINST'
#!/bin/sh
set -e
if [ "$1" = "configure" ]; then
    update-desktop-database -q /usr/share/applications 2>/dev/null || true
    gtk-update-icon-cache -qf /usr/share/icons/hicolor 2>/dev/null || true
fi
exit 0
POSTINST

cat > "$STAGE/DEBIAN/postrm" <<'POSTRM'
#!/bin/sh
set -e
if [ "$1" = "remove" ] || [ "$1" = "purge" ]; then
    update-desktop-database -q /usr/share/applications 2>/dev/null || true
    gtk-update-icon-cache -qf /usr/share/icons/hicolor 2>/dev/null || true
fi
exit 0
POSTRM
chmod 755 "$STAGE/DEBIAN/postinst" "$STAGE/DEBIAN/postrm"

# Nothing in the tree is a config file, and dpkg must not treat the engine as one.
find "$STAGE$OPTDIR" -type f -name '*.so' -exec chmod 644 {} +
chmod 755 "$STAGE$OPTDIR/MegaPDF"

DEB="$OUT/${PKG}_${PKG_VERSION}_${ARCH}.deb"
rm -f "$DEB"
# xz over the default: the payload is ninety megabytes of mostly-compressible IL and
# native code, and a Releases download is the one place the size is felt.
dpkg-deb --root-owner-group -Zxz --build "$STAGE" "$DEB" >/dev/null

echo "built: $DEB"
du -h "$DEB" | cut -f1 | sed 's/^/  size  /'
dpkg-deb --info "$DEB" | sed -n '2,6p'
