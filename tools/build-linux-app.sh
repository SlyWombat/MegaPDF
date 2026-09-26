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

# megapdf-cli (#142, #355, #356): the standalone extraction binary, built with the core
# rather than with the .NET app. Built and copied in first, beside where the publish
# below will land libmegapdf_core.so and libpdfium.so — the RPATH the CMake target sets
# ($ORIGIN) only resolves if the two native libraries end up in the same directory.
command -v cmake >/dev/null 2>&1 || {
    echo "::error::cmake not found. apt install cmake." >&2
    exit 1
}
CLI_BUILD="$ROOT/core/build/linux-x64"
CLI_GEN=()
command -v ninja >/dev/null 2>&1 && CLI_GEN=(-G Ninja)
cmake -S "$ROOT/core" -B "$CLI_BUILD" -DCMAKE_BUILD_TYPE=Release "${CLI_GEN[@]+"${CLI_GEN[@]}"}"
cmake --build "$CLI_BUILD" --target megapdf_cli --config Release
[ -x "$CLI_BUILD/megapdf-cli" ] || { echo "::error::megapdf_cli build did not produce $CLI_BUILD/megapdf-cli" >&2; exit 1; }
cp "$CLI_BUILD/megapdf-cli" "$APP/bin/megapdf-cli"

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
[ -x "$APP/bin/megapdf-cli" ] || { echo "::error::no executable at $APP/bin/megapdf-cli" >&2; exit 1; }

# The engine must be loaded from beside the apphost, never from the system
# library path: a distribution's own libpdfium is not the patched build this app
# depends on (libs/pdfium/RELEASE, 25 MegaPDF patches), and loading it would
# change behaviour in ways no test here would catch. .NET probes the app
# directory first for a bare DllImport name, which is what CoreNative and
# PdfiumNative both use, and libmegapdf_core.so carries RUNPATH $ORIGIN for its
# own link to pdfium. megapdf-cli carries the same $ORIGIN RUNPATH (CMake sets it
# on the target directly, core/CMakeLists.txt), for the same reason: a distribution's
# own libpdfium must never be what it loads. Asserted rather than assumed, for both.
#
# The objdump listing is captured first and grepped second, not piped into grep -q:
# under this script's `set -o pipefail`, grep -q closes its end of the pipe the moment
# it matches, objdump takes SIGPIPE writing the rest of the (long) listing, and the
# pipeline reports failure for a library whose runpath is exactly right. That refused
# a correct build on a WSL machine 200 times out of 200 (#395); make-release-tarball.sh
# hit the same thing with tar in CI.
for bin in libmegapdf_core.so megapdf-cli; do
    DYN="$(objdump -p "$APP/bin/$bin" 2>/dev/null || true)"
    if ! grep -qE 'R(UN)?PATH.*\$ORIGIN' <<< "$DYN"; then
        echo "::error::$bin has no \$ORIGIN runpath — it would load the system libpdfium" >&2
        grep -E 'R(UN)?PATH' <<< "$DYN" >&2 || true
        exit 1
    fi
done

# The oldest system this package promises to run on (#395). build-deb.sh declares
# libc6 (>= 2.35) -- Ubuntu 22.04 -- and Debian 12 ships libstdc++ 12 (GLIBCXX_3.4.30).
# A native binary built on the runner (Ubuntu 24.04: glibc 2.39, GCC 13) can quietly
# want more than that: megapdf-cli once needed __isoc23_strtol (GLIBC_2.38) and
# std::ios_base_library_init (GLIBCXX_3.4.32), so the .deb installed on Debian 12 and
# the CLI then refused to load there with "version GLIBC_2.38 not found", while the
# app beside it ran. check-apt-repo.sh now runs the CLI in its Debian 12 and Ubuntu
# 22.04 containers; this catches it earlier, on every ELF in the tree, by reading the
# versioned symbols each one imports. Measured on the 2.1.1 release tree: the apphost
# 2.16 / 3.4.21, libpdfium.so 2.16, libSkiaSharp.so 2.17, libHarfBuzzSharp.so 2.14,
# libmegapdf_core.so 2.35 / 3.4.30 -- so the ceilings are exactly what the core needs
# today, and the first thing to want more will be the first to fail here.
GLIBC_MAX=2.35
GLIBCXX_MAX=3.4.30
for path in "$APP"/bin/*; do
    file -b "$path" 2>/dev/null | grep -q '^ELF' || continue
    bin="$(basename "$path")"
    SYMS="$(objdump -T "$path" 2>/dev/null || true)"
    # `|| true` on each: a library with no libstdc++ import at all (libpdfium.so,
    # SkiaSharp) makes grep exit 1, which under pipefail is the pipeline's status and
    # under -e would end the build with no message.
    need_glibc="$(grep -oE 'GLIBC_[0-9.]+' <<< "$SYMS" | sed 's/GLIBC_//' | sort -V | tail -1 || true)"
    need_glibcxx="$(grep -oE 'GLIBCXX_[0-9.]+' <<< "$SYMS" | sed 's/GLIBCXX_//' | sort -V | tail -1 || true)"
    for pair in "glibc:${need_glibc:-0}:$GLIBC_MAX" "libstdc++:${need_glibcxx:-0}:$GLIBCXX_MAX"; do
        IFS=: read -r lib need max <<< "$pair"
        if [ "$(printf '%s\n%s\n' "$need" "$max" | sort -V | tail -1)" != "$max" ]; then
            echo "::error::$bin needs $lib symbol version $need, above the $max this package promises (Debian 12 / Ubuntu 22.04)" >&2
            grep -E "GLIBC(XX)?_$need\b" <<< "$SYMS" | awk '{print "         " $NF " (" $(NF-1) ")"}' >&2
            exit 1
        fi
    done
    echo "$bin: needs glibc <= ${need_glibc:-none}, libstdc++ <= ${need_glibcxx:-none} (ceilings $GLIBC_MAX / $GLIBCXX_MAX)"
done

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

# megapdf-cli(1) (#395): the manual page, uncompressed, where a tree unpacked by hand
# can read it with `man -l share/man/man1/megapdf-cli.1`. install.sh puts it under
# $PREFIX/share/man/man1 and build-deb.sh gzips it into /usr/share/man/man1, so
# `man megapdf-cli` works from either. Its OPTIONS and EXIT STATUS are the --help text
# in core/cli/megapdf_cli.cpp; checked here for the one drift that is easy to miss,
# a version line that stopped matching the binary's.
MANPAGE="$ROOT/tools/linux/megapdf-cli.1"
[ -s "$MANPAGE" ] || { echo "::error::$MANPAGE is missing" >&2; exit 1; }
if ! grep -qF "\"MegaPDF $VERSION\"" "$MANPAGE"; then
    echo "::error::$MANPAGE's .TH line does not say \"MegaPDF $VERSION\" — update it with the version" >&2
    exit 1
fi
mkdir -p "$APP/share/man/man1"
cp "$MANPAGE" "$APP/share/man/man1/megapdf-cli.1"

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

# Modes, normalised rather than inherited (#395): a checkout on a Windows-mounted
# filesystem (WSL's DrvFs) reports every file as 0777, and cp carries that into the
# tree, so a tarball built there ships a 0777 libpdfium.so and the .deb's icons and
# notices come out executable (lintian: executable-not-elf-or-script). What a tree is
# for is fixed, so its modes are set here: everything 0644, the four things that run
# 0755. The same tree built on ext4 already looks like this; the chmod only makes the
# other case match.
find "$APP" -type f -exec chmod 644 {} +
chmod 755 "$APP/bin/MegaPDF" "$APP/bin/megapdf-cli" "$APP/install.sh" "$APP/uninstall.sh"

# Beside the binary, where the app reads it (MegaPDF.Core LinuxInstall): this tree is
# the tarball, and tools/linux/build-deb.sh rewrites it in the .deb's copy. The Flatpak
# and the Snap are built from this tree too and are told apart by their environment.
printf 'tarball\n' > "$APP/bin/INSTALL-KIND"

echo "built: $APP"
du -sh "$APP"
