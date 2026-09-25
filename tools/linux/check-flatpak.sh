#!/usr/bin/env bash
# Installs a built MegaPDF Flatpak and runs the app inside its sandbox (#158).
#
#     tools/linux/check-flatpak.sh [bundle] [fixtures-dir]
#
# The point is not that a bundle exists — it is that what is inside it starts, finds
# its own engine, does the work, and can reach nothing it was not given. Every step
# here is something a package can get wrong while still building.
#
# A container is not a desktop session, and flatpak wants three things a desktop has
# and a container has not: XDG_RUNTIME_DIR, a session bus and a system bus. Without
# the last, `flatpak run` stops with "Could not connect: No such file or directory"
# and names nothing. Set up here rather than left to be rediscovered.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
BUNDLE=${1:-$ROOT/artifacts/flatpak/ca.electricrv.MegaPDF.flatpak}
FIXTURES=${2:-$ROOT/artifacts/fixtures}
APP_ID=ca.electricrv.MegaPDF

[ -f "$BUNDLE" ] || { echo "::error::no bundle at $BUNDLE — run tools/linux/build-flatpak.sh first" >&2; exit 1; }

export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/tmp/megapdf-xdg-runtime}"
mkdir -p "$XDG_RUNTIME_DIR" && chmod 700 "$XDG_RUNTIME_DIR"
if [ ! -S /run/dbus/system_bus_socket ] && command -v dbus-daemon >/dev/null; then
    mkdir -p /run/dbus && dbus-daemon --system --fork 2>/dev/null || true
fi
if [ -z "${DBUS_SESSION_BUS_ADDRESS:-}" ]; then
    exec dbus-run-session -- "$0" "$@"
fi

failures=0
step()  { echo; echo "=== $* ==="; }
check() { if [ "$1" -eq 0 ]; then echo "  ok"; else echo "  FAIL (exit $1)"; failures=$((failures + 1)); fi; }

step "installing the bundle"
flatpak install -y --noninteractive --user "$BUNDLE" >/dev/null 2>&1 \
    || flatpak install -y --noninteractive --user --reinstall "$BUNDLE" >/dev/null 2>&1
flatpak list --user --columns=application,version,branch

step "what the sandbox actually grants"
# Printed, not asserted: the manifest is the assertion, and seeing the resolved set is
# what catches a finish-arg that did something other than what it looks like.
flatpak info --show-permissions "$APP_ID"

step "inside the sandbox: it knows it is sandboxed, and everything came with it"
flatpak run --user --command=sh "$APP_ID" -c '
    set -e
    test -f /.flatpak-info
    echo "  /.flatpak-info is there, so the app can tell"
    test -x /app/bin/megapdf
    test -x /app/bin/megapdf-cli
    test -s /app/lib/megapdf/libmegapdf_core.so
    test -s /app/lib/megapdf/libpdfium.so
    echo "  the launcher, megapdf-cli and both native libraries are in place"
    echo "  notices:       $(wc -l < /app/lib/megapdf/THIRD-PARTY-NOTICES.txt) lines, beside the binary"
    echo "  metainfo:      $(ls /app/share/metainfo/)"
    echo "  desktop entry: $(ls /app/share/applications/)"
'
check $?

step "the person's home is not readable from inside — which is the point"
# Flatpak leaves $HOME spelled the same inside the sandbox and binds the app's own
# directories over it, so what matters is what can be read, not what the variable says.
MARKER="$HOME/.megapdf-outside-marker"
echo "a file only the real home has" > "$MARKER"
flatpak run --user --command=sh "$APP_ID" -c '
    if cat "'"$MARKER"'" >/dev/null 2>&1; then
        echo "  the real home leaked into the sandbox"
        exit 1
    fi
    echo "  a file in the real home is not readable from inside"
    echo "  what the app sees as its home: $(ls -A "$HOME" 2>/dev/null | tr "\n" " ")"
'
check $?
rm -f "$MARKER"

step "megapdf-cli extracts text inside the sandbox (#142, #356)"
# The exact invocation the design and the docs promise: --command looks the binary up
# on PATH inside the sandbox, which is why it is exported to /app/bin rather than left
# only in /app/lib/megapdf.
out=$(flatpak run --user --filesystem="$FIXTURES:ro" --command=megapdf-cli "$APP_ID" \
    extract "$FIXTURES/demo.pdf")
rc=$?
if [ "$rc" -eq 0 ] && [ -n "$out" ]; then
    echo "  ok, extracted $(echo "$out" | wc -l) line(s) of text from demo.pdf"
else
    echo "  FAIL (exit $rc)"
fi
check "$rc"

step "--render-check: the engine loads inside the sandbox and a page renders"
flatpak run --user --filesystem="$FIXTURES:ro" --command=/app/lib/megapdf/MegaPDF "$APP_ID" \
    --render-check "$FIXTURES/stamped.pdf"
check $?

step "--language-check: ICU comes from the runtime, and the POSIX chain is read"
# The one check that fails loudly on a runtime without ICU: .NET FailFasts at the first
# CultureInfo with a message about libicu and nothing about MegaPDF.
flatpak run --user --command=/app/lib/megapdf/MegaPDF "$APP_ID" --language-check >/dev/null
check $?

step "--install-kind: the app knows Flatpak brings its updates"
# Built from the tarball, so the tree beside the binary says "tarball"; the sandbox has
# to win over that, or About would send a Flatpak user to the download page (#158).
kind=$(flatpak run --user --command=/app/lib/megapdf/MegaPDF "$APP_ID" --install-kind 2>/dev/null)
echo "  $kind"
[ "$kind" = "install-kind: Flatpak" ]
check $?

step "--print-check: what printing does in a sandbox with no lp in it"
# Reported, not asserted. Printing has no portal route yet; what this proves is that
# the app says so rather than failing obscurely.
flatpak run --user --filesystem="$FIXTURES:ro" --command=/app/lib/megapdf/MegaPDF "$APP_ID" \
    --print-check "$FIXTURES/fixture.pdf" 2>&1 | tail -4

step "--self-test: fill, check, sign, save, reopen, inside the sandbox"
WORK=$(mktemp -d)
cp -r "$FIXTURES/." "$WORK/"
# Writable, because the self-test saves. A real save goes through the portal, which
# needs no permission at all; this is a harness reaching a directory directly.
flatpak run --user --filesystem="$WORK" --command=/app/lib/megapdf/MegaPDF "$APP_ID" \
    --self-test "$WORK" 2>&1 | tail -3
check "${PIPESTATUS[0]}"

step "--window: a real window, under Xvfb, inside the sandbox"
SHOTS=$(mktemp -d)
xvfb-run -a -s "-screen 0 1440x900x24" \
    flatpak run --user --filesystem="$WORK" --filesystem="$SHOTS" \
    --command=/app/lib/megapdf/MegaPDF "$APP_ID" \
    --window 1440x900 --screenshot "$SHOTS/window.png" "$WORK/demo.pdf" 2>&1 | tail -3
check "${PIPESTATUS[0]}"
# A window that opened and drew nothing would still write a file, so look at the pixels.
python3 - "$SHOTS/window.png" <<'PYEOF' || failures=$((failures + 1))
import struct, sys, zlib
data = open(sys.argv[1], 'rb').read()
pos, idat, w, h = 8, b'', 0, 0
while pos < len(data):
    length, kind = struct.unpack('>I4s', data[pos:pos + 8])
    body = data[pos + 8:pos + 8 + length]
    if kind == b'IHDR':
        w, h = struct.unpack('>II', body[:8])
    elif kind == b'IDAT':
        idat += body
    pos += 12 + length
raw = zlib.decompress(idat)
stride = w * 4 + 1
colours = {raw[y * stride + 1 + x:y * stride + 4 + x]
           for y in range(0, h, 16) for x in range(0, w * 4, 64)}
print(f"  {w}x{h}, {len(colours)} distinct sampled colours")
if len(colours) < 8:
    print("  the window opened but drew a flat image")
    sys.exit(1)
print("  the window drew a real document")
PYEOF
rm -rf "$WORK" "$SHOTS"

echo
if [ "$failures" -eq 0 ]; then
    echo "flatpak: all checks passed"
else
    echo "::error::flatpak: $failures check(s) failed"
fi
exit "$failures"
