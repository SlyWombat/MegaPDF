#!/usr/bin/env bash
# Installs a built MegaPDF .deb and runs the app from what it installed (#158).
#
#     tools/linux/check-deb.sh [deb] [fixtures-dir]
#
# Run it on a machine with nothing on it — a bare ubuntu:24.04 container is ideal.
# That is the whole point: no .NET, no ICU and no X libraries, so anything the package
# forgot to declare shows up here rather than on someone's laptop. It installs through
# apt so the dependencies are resolved the way a person's would be, and removes the
# package again at the end.
#
# Needs root, because it installs a package.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
DEB=${1:-$(ls "$ROOT"/artifacts/deb/megapdf_*_amd64.deb 2>/dev/null | head -1)}
FIXTURES=${2:-$ROOT/artifacts/fixtures}
OPTDIR=/opt/MegaPDF

[ -n "${DEB:-}" ] && [ -f "$DEB" ] || { echo "::error::no .deb — run tools/linux/build-deb.sh first" >&2; exit 1; }

echo "=== before: what is not on this machine ==="
command -v dotnet >/dev/null && echo "  a dotnet is here, so this proves less than it should" || echo "  no dotnet"
echo "  libicu sonames on the library path: $(ldconfig -p | grep -c libicu)"

echo
echo "=== installing, letting apt resolve what the package says it needs ==="
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null 2>&1
apt-get install -y -qq "$DEB" 2>&1 | tail -2
dpkg-query -W -f='  dpkg: ${Package} ${Version} — ${Status}\n' megapdf

echo
echo "=== the dependencies that are not in any NEEDED entry ==="
# .NET dlopens ICU and Avalonia.X11 dlopens the X libraries, so neither is visible to
# objdump. If the control file forgot one, it is missing here.
for p in libicu76 libicu74 libicu72 libfontconfig1 libx11-6 libstdc++6; do
    status=$(dpkg-query -W -f='${Status}' "$p" 2>/dev/null) || continue
    printf '  %-16s %s\n' "$p" "$status"
done

echo
echo "=== the launcher is on PATH and points into the package ==="
command -v megapdf && readlink -f "$(command -v megapdf)"

echo
echo "=== megapdf-cli extracts text out of the installed package (#142, #356) ==="
cli_rc=0
command -v megapdf-cli && readlink -f "$(command -v megapdf-cli)"
megapdf-cli --version
if [ -s "$FIXTURES/demo.pdf" ]; then
    out=$(megapdf-cli extract "$FIXTURES/demo.pdf")
    if [ -n "$out" ]; then
        echo "  ok    extracted $(echo "$out" | wc -l) line(s) of text from demo.pdf"
    else
        echo "  FAIL  megapdf-cli extract produced no text"
        cli_rc=$((cli_rc + 1))
    fi
else
    echo "  FAIL  no $FIXTURES/demo.pdf to extract"
    cli_rc=$((cli_rc + 1))
fi

echo
# A .deb installed from the file itself, with no repository behind it: DebFile.
bash "$ROOT/tools/linux/package-check.sh" "$OPTDIR" "$FIXTURES" "" "deb" DebFile
rc=$?
rc=$((rc + cli_rc))

echo
echo "=== the desktop entry and the icons, as a desktop would look for them ==="
# The icons have to be under a theme directory. They were not: a cp into a destination
# that did not exist yet unpacked hicolor's contents straight into /usr/share/icons, so
# every size sat at a path no icon theme has and nothing would ever have drawn the app's
# icon (#158 QA pass). Silent, and invisible to every other check there is.
missing=0
for size in 16x16 22x22 24x24 32x32 48x48 64x64 128x128 256x256 512x512; do
    [ -s "/usr/share/icons/hicolor/$size/apps/megapdf.png" ] || missing=$((missing + 1))
done
if [ "$missing" -eq 0 ] && [ -s /usr/share/icons/hicolor/scalable/apps/megapdf.svg ]; then
    echo "  ok    all nine hicolor sizes and the scalable icon are where a theme looks"
else
    echo "  FAIL  $missing of nine hicolor sizes missing$([ -s /usr/share/icons/hicolor/scalable/apps/megapdf.svg ] || echo ', and no scalable icon')"
    find /usr/share/icons -name 'megapdf.*' -print -quit | sed 's/^/        first icon found at: /'
    rc=$((rc + 1))
fi
if desktop-file-validate /usr/share/applications/megapdf.desktop 2>&1; then
    echo "  ok    the installed desktop entry validates"
else
    echo "  FAIL  the installed desktop entry does not validate"
    rc=$((rc + 1))
fi
# It offers application/pdf; it must not claim it. The Mac bundle says
# LSHandlerRank=Alternate and this is the same promise.
if grep -rq 'application/pdf=megapdf' /usr/share/applications/mimeapps.list \
        /etc/xdg/mimeapps.list "$HOME/.config/mimeapps.list" 2>/dev/null; then
    echo "  FAIL  installing wrote MegaPDF into a mimeapps.list — it claimed the type"
    rc=$((rc + 1))
else
    echo "  ok    installing claimed no default handler"
fi

echo
echo "=== the /usr/share/doc copy, which a dpkg path-exclude may have pruned ==="
# Not a failure either way. It is here because it is how we found out that the notices
# needed a second home: every Debian and Ubuntu container image ships
# path-exclude=/usr/share/doc/*, so the copy that lived only there was discarded on
# install while dpkg reported success.
if [ -s /usr/share/doc/megapdf/THIRD-PARTY-NOTICES.txt ]; then
    echo "  the doc copy survived on this machine"
else
    echo "  pruned here — which is why the copy beside the binary is the one that counts"
fi
[ -s /usr/share/doc/megapdf/copyright ] && echo "  copyright survived (the same excludes path-include it)"

if command -v xvfb-run >/dev/null; then
    echo
    echo "=== the window opens, from the installed package ==="
    SHOTS=$(mktemp -d)
    xvfb-run -a -s "-screen 0 1440x900x24" megapdf --window 1440x900 \
        --screenshot "$SHOTS/window.png" "$FIXTURES/demo.pdf" 2>&1 | tail -2
    [ -s "$SHOTS/window.png" ] || { echo "  the window rendered nothing"; rc=$((rc + 1)); }
    rm -rf "$SHOTS"
fi

echo
echo "=== removing it again ==="
apt-get remove -y -qq megapdf >/dev/null 2>&1
[ -e "$OPTDIR" ] && { echo "  $OPTDIR survived the remove"; rc=$((rc + 1)); } || echo "  $OPTDIR is gone"
[ -e /usr/bin/megapdf ] && { echo "  the launcher survived the remove"; rc=$((rc + 1)); } || echo "  the launcher is gone"

echo
[ "$rc" -eq 0 ] && echo "deb: all checks passed" || echo "::error::deb: $rc check(s) failed"
exit "$rc"
