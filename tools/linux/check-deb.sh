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
bash "$ROOT/tools/linux/package-check.sh" "$OPTDIR" "$FIXTURES" "" "deb"
rc=$?

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
