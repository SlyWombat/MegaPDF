#!/usr/bin/env bash
# Builds the Linux app from src/MegaPDF.Avalonia (ADR-002 Option B, #158) as a
# self-contained tree that runs from wherever it is unpacked, plus the
# freedesktop pieces that make it an application rather than a binary: a .desktop
# entry, the application/pdf association, and the hicolor icons.
#
# Usage: tools/build-linux-app.sh [rid] [out-dir]
#   rid     linux-x64 (default). linux-arm64 is refused: the patched PDFium series
#           cross-builds for it (#254 A6) but no release carries the arm64 archive
#           yet, and this script would produce an arm64 app with an x64 engine in it.
#           tools/Linux-Packaging.md lists the three changes and who unblocks them.
#   out-dir defaults to artifacts/linux
#
# NOT a package. Flathub, AppImage and .deb are separate work with their own
# signing and update stories; what this produces is what each of them would
# wrap, and what tools/linux/install.sh installs for a single user.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:-linux-x64}"
OUT="${2:-$ROOT/artifacts/linux}"
PROJECT="$ROOT/src/MegaPDF.Avalonia/MegaPDF.Avalonia.csproj"
APP="$OUT/MegaPDF"

case "$RID" in
    linux-x64) ;;
    linux-arm64)
        echo "::error::linux-arm64 needs an arm64 PDFium archive in the pinned release." >&2
        echo "         The series cross-builds for arm64 (#254 A6); no release carries the" >&2
        echo "         archive yet, so this would ship an arm64 app around an x64 engine." >&2
        echo "         See tools/Linux-Packaging.md, \"linux-arm64\"." >&2
        exit 1 ;;
    *) echo "::error::unsupported rid '$RID' (expected linux-x64)" >&2; exit 1 ;;
esac

if [ "$(uname -s)" != "Linux" ]; then
    echo "::error::this script must run on Linux (the linux apphost is produced there)" >&2
    exit 1
fi

VERSION="$(grep -oE '<Version>[^<]+</Version>' "$PROJECT" | head -1 | sed 's/<[^>]*>//g')"
VERSION="${VERSION:-0.1.0}"
echo "building MegaPDF $VERSION for $RID"

# PDFium must be present before publish: MegaPDF.Core.csproj copies it from
# libs/pdfium/linux-x64 when building on Linux, and silently omits it otherwise.
"$ROOT/tools/fetch-pdfium-linux.sh"

rm -rf "$APP"
mkdir -p "$APP/bin"

# PublishSingleFile for the same reason the Mac bundle uses it — one file to
# install, and no loose .pdb or runtimeconfig.json to explain — but WITHOUT
# IncludeNativeLibrariesForSelfExtract. Self-extracting the native libraries
# would put libpdfium.so in a temp directory on first run, and a machine with
# /tmp mounted noexec (which hardened and multi-user installs do have) would
# then fail to load the engine at startup with an error naming neither.
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false \
    -o "$APP/bin"

find "$APP/bin" -name '*.pdb' -delete

for lib in libpdfium.so libmegapdf_core.so; do
    if [ ! -f "$APP/bin/$lib" ]; then
        echo "::error::$lib is not in the publish output — the Core copy item did not fire." >&2
        exit 1
    fi
done
[ -x "$APP/bin/MegaPDF" ] || { echo "::error::no executable at $APP/bin/MegaPDF" >&2; exit 1; }

# The engine must be loaded from beside the apphost, never from the system
# library path: a distribution's own libpdfium is not the patched build this app
# depends on (libs/pdfium/RELEASE, 25 MegaPDF patches), and loading it would
# change behaviour in ways no test here would catch. .NET probes the app
# directory first for a bare DllImport name, which is what CoreNative and
# PdfiumNative both use, and libmegapdf_core.so carries RUNPATH $ORIGIN for its
# own link to pdfium. Asserted rather than assumed:
if ! objdump -p "$APP/bin/libmegapdf_core.so" 2>/dev/null | grep -qE 'R(UN)?PATH.*\$ORIGIN'; then
    echo "::error::libmegapdf_core.so has no \$ORIGIN runpath — it would load the system libpdfium" >&2
    objdump -p "$APP/bin/libmegapdf_core.so" | grep -E 'R(UN)?PATH' >&2 || true
    exit 1
fi

# --- The freedesktop pieces -------------------------------------------------

mkdir -p "$APP/share/applications"
cp "$ROOT/tools/linux/megapdf.desktop" "$APP/share/applications/megapdf.desktop"

# Committed, not generated here: tools/gen_linux_icons.py needs PIL, and this
# script has to run on a bare CI runner. Regenerate them when the branding
# changes.
ICONS="$ROOT/assets/branding/linux/hicolor"
[ -d "$ICONS" ] || { echo "::error::$ICONS is missing — regenerate with tools/gen_linux_icons.py" >&2; exit 1; }
mkdir -p "$APP/share/icons"
cp -R "$ICONS" "$APP/share/icons/"

mkdir -p "$APP/share/doc/MegaPDF"
cp "$ROOT/LICENSE" "$APP/share/doc/MegaPDF/LICENSE"

# The bundled third-party notices, which #176 generated for the Avalonia apps
# (tools/gen_third_party_notices.py, the "macos" entry). The set is the same one
# Linux needs — Avalonia, SkiaSharp, HarfBuzzSharp, MicroCom, Tmds.DBus.Protocol,
# CommunityToolkit.Mvvm, the .NET runtime and PDFium — because it is the same app
# over the same dependencies. Required: no distribution channel accepts a package
# without it, and several of those licences require the text to travel with the
# binary. Failing rather than warning, because a package built without it cannot
# ship (#194).
NOTICES="$ROOT/src/MegaPDF.Avalonia/Assets/THIRD-PARTY-NOTICES.txt"
if [ ! -f "$NOTICES" ]; then
    echo "::error::$NOTICES is missing — regenerate with tools/gen_third_party_notices.py" >&2
    exit 1
fi
cp "$NOTICES" "$APP/share/doc/MegaPDF/THIRD-PARTY-NOTICES.txt"

cp "$ROOT/tools/linux/install.sh" "$APP/install.sh"
cp "$ROOT/tools/linux/uninstall.sh" "$APP/uninstall.sh"
chmod +x "$APP/install.sh" "$APP/uninstall.sh"

printf '%s\n' "$VERSION" > "$APP/VERSION"

echo "built: $APP"
du -sh "$APP"
