#!/usr/bin/env bash
# Installs a built MegaPDF snap and runs the app under strict confinement (#158).
#
#     tools/linux/check-snap.sh <megapdf_*.snap> <fixtures-dir> [unpacked-tree]
#
# Run as an ordinary user with sudo, on a machine with snapd and AppArmor: a GitHub
# ubuntu-24.04 runner, or an Ubuntu desktop. Not in a container: snapd needs systemd,
# and strict confinement is AppArmor, which is the thing being checked.
#
# What it proves, each as something a confined app could get wrong while the snap still
# builds:
#
#   * the app starts inside the snap, finds its own engine, reads ICU, and renders;
#   * the confinement is the one snapcraft.yaml.in claims: a document in the home
#     folder opens, a hidden file there and a file outside it do not, the host's /tmp
#     is not the app's /tmp, and a USB drive is readable only once removable-media is
#     connected;
#   * a document at the top of the home folder saves. The snap's home plug refuses a
#     hidden file there, and every save writes a hidden temporary file beside the
#     document first, so without AtomicFileWriter's fallback this is the save that fails;
#   * the file dialogs are the XDG portal's, and a Save a copy through the portal's own
#     dialog writes a real file;
#   * printing goes to the print portal, not to lp (LinuxPrinter.InSandbox);
#   * French is read from the locale;
#   * what AppArmor refused along the way, listed rather than guessed at.
#
# `[unpacked-tree]` is the tree the snap wraps. When it is given, the same commands are
# timed outside the snap too, which is the snap's startup cost measured instead of
# assumed.
set -uo pipefail

SNAP_FILE=${1:?usage: check-snap.sh <snap> <fixtures-dir> [unpacked-tree]}
FIXTURES=${2:?usage: check-snap.sh <snap> <fixtures-dir> [unpacked-tree]}
TREE=${3:-}
[ -f "$SNAP_FILE" ] || { echo "::error::no snap at $SNAP_FILE" >&2; exit 1; }
FIXTURES=$(cd "$FIXTURES" && pwd)

failures=0
step()  { echo; echo "=== $* ==="; }
check() { if [ "$1" -eq 0 ]; then echo "  ok"; else echo "  FAIL (exit $1)"; failures=$((failures + 1)); fi; }
expect_fail() { if [ "$1" -ne 0 ]; then echo "  ok (refused, as it should be)"; else echo "  FAIL: it was allowed"; failures=$((failures + 1)); fi; }
START=$(date +%s)

# --- install --------------------------------------------------------------------

step "installing the snap, and the shared content snaps the gnome extension names"
# Named rather than left to snapd's default-provider handling, so a failure to fetch
# them is reported here as that and not later as a library the app cannot find.
sudo snap install gnome-46-2404 >/dev/null
sudo snap install gtk-common-themes >/dev/null
sudo snap install --dangerous "$SNAP_FILE"
snap list megapdf gnome-46-2404 gtk-common-themes
echo
snap connections megapdf

step "home is connected on install, removable-media is not"
snap connections megapdf | awk '$1 == "home" && $3 != "-" {found=1} END {exit !found}'
check $?
snap connections megapdf | awk '$1 == "removable-media" && $3 == "-" {found=1} END {exit !found}'
check $?

# --- a desktop session ----------------------------------------------------------
# What a real session has and a runner has not: a runtime directory, a session bus at
# the path snapd lets a snap reach ($XDG_RUNTIME_DIR/bus — a bus under the host's /tmp
# would be invisible behind the snap's private /tmp), an X server, and the portal with
# a backend. GNOME's portal configuration falls back to the GTK backend when GNOME's own
# is not installed, which is how #254 A4 ran it too.

export XDG_RUNTIME_DIR=/run/user/$(id -u)
sudo mkdir -p "$XDG_RUNTIME_DIR"
sudo chown "$(id -u):$(id -g)" "$XDG_RUNTIME_DIR"
chmod 700 "$XDG_RUNTIME_DIR"
export DBUS_SESSION_BUS_ADDRESS="unix:path=$XDG_RUNTIME_DIR/bus"
rm -f "$XDG_RUNTIME_DIR/bus"
BUS_PID=$(dbus-daemon --session --address="$DBUS_SESSION_BUS_ADDRESS" --fork --print-pid)
export DISPLAY=:99
Xvfb "$DISPLAY" -screen 0 1440x900x24 -nolisten tcp >/tmp/megapdf-snap-xvfb.log 2>&1 &
XVFB_PID=$!
for _ in $(seq 1 50); do xdpyinfo >/dev/null 2>&1 && break; sleep 0.2; done
export XDG_CURRENT_DESKTOP=GNOME GTK_USE_PORTAL=1
if command -v metacity >/dev/null; then metacity --sm-disable >/tmp/megapdf-snap-wm.log 2>&1 & fi
/usr/libexec/xdg-desktop-portal >/tmp/megapdf-snap-portal.log 2>&1 &
PORTAL_PID=$!
for _ in $(seq 1 50); do
    busctl --user status org.freedesktop.portal.Desktop >/dev/null 2>&1 && break
    sleep 0.2
done
dbus-monitor --session >/tmp/megapdf-snap-bus.log 2>&1 &
MONITOR_PID=$!
cleanup() {
    kill "$MONITOR_PID" "$PORTAL_PID" "$XVFB_PID" "$BUS_PID" 2>/dev/null
    pkill -f 'xdg-desktop-portal-gtk$' 2>/dev/null
    pkill -f '/usr/libexec/xdg-document-portal$' 2>/dev/null
    pkill -x metacity 2>/dev/null
}
trap cleanup EXIT

megapdf() { snap run megapdf "$@"; }
shell()   { snap run --shell megapdf -c "$1"; }

# Everything the app is asked to read lives where a person's documents would.
WORK="$HOME/MegaPDF snap check"
rm -rf "$WORK"; mkdir -p "$WORK"
cp -r "$FIXTURES/." "$WORK/"

# --- inside the snap ------------------------------------------------------------

step "inside the snap: the tree is whole and the app can tell it is in a snap"
shell '
    set -e
    test -n "$SNAP_NAME"
    echo "  SNAP_NAME=$SNAP_NAME, SNAP=$SNAP, HOME=$HOME"
    for f in MegaPDF libmegapdf_core.so libpdfium.so THIRD-PARTY-NOTICES.txt; do
        test -s "$SNAP/lib/megapdf/$f"
    done
    echo "  the apphost, both native libraries and the notices are in \$SNAP/lib/megapdf"
    test -f "$SNAP/meta/gui/megapdf.desktop"
'
check $?

step "--render-check: the engine loads inside the snap and a page renders"
megapdf --render-check "$WORK/stamped.pdf"
check $?

step "--language-check: ICU is there, and French is read from the locale"
megapdf --language-check
check $?
out=$(LANG=fr_CA.UTF-8 LANGUAGE=fr_CA:fr megapdf --language-check 2>&1)
echo "$out" | sed 's/^/  /'
echo "$out" | grep -q 'would run in fr-CA'
check $?

# --- what the confinement allows ------------------------------------------------

step "a document in the home folder opens (the home plug)"
megapdf --render-check "$HOME/MegaPDF snap check/stamped.pdf" >/dev/null
check $?

step "a hidden file in the home folder does not"
echo "hidden" > "$HOME/.megapdf-snap-hidden-probe"
shell "cat '$HOME/.megapdf-snap-hidden-probe'" >/dev/null 2>&1
expect_fail $?
rm -f "$HOME/.megapdf-snap-hidden-probe"

step "a file outside the home folder does not"
sudo install -m 644 "$FIXTURES/stamped.pdf" /opt/megapdf-snap-probe.pdf
megapdf --render-check /opt/megapdf-snap-probe.pdf >/dev/null 2>&1
expect_fail $?
sudo rm -f /opt/megapdf-snap-probe.pdf

step "the host's /tmp is not the snap's /tmp"
echo "host" > /tmp/megapdf-snap-tmp-probe
shell "test -e /tmp/megapdf-snap-tmp-probe"
expect_fail $?
rm -f /tmp/megapdf-snap-tmp-probe

step "a document on a USB drive: refused until removable-media is connected, then read"
sudo mkdir -p /media/megapdf-usb
sudo install -m 644 "$FIXTURES/stamped.pdf" /media/megapdf-usb/stamped.pdf
megapdf --render-check /media/megapdf-usb/stamped.pdf >/dev/null 2>&1
expect_fail $?
sudo snap connect megapdf:removable-media
megapdf --render-check /media/megapdf-usb/stamped.pdf >/dev/null
check $?
sudo snap disconnect megapdf:removable-media
sudo rm -rf /media/megapdf-usb

# --- saving ---------------------------------------------------------------------

step "--self-test, every save made at the top of the home folder"
# The self-test's saves (fill, check, sign, redact, edit text, save a copy, save over
# the original) all go to --save-dir. At the top of the home folder the snap may write
# the document but not the hidden temporary file a save writes beside it first, which
# is the case AtomicFileWriter falls back for (#158).
before=$(ls -A "$HOME")
megapdf --self-test "$WORK" --save-dir "$HOME" 2>&1 | tail -4
check "${PIPESTATUS[0]}"
left=$(comm -13 <(echo "$before") <(ls -A "$HOME") | grep -E 'megapdf-tmp|megapdf-verify' || true)
if [ -n "$left" ]; then
    echo "  FAIL: temporary files left in the home folder:"; echo "$left" | sed 's/^/    /'
    failures=$((failures + 1))
else
    echo "  no temporary file left behind in the home folder"
fi

# --- a real window, the portal, printing ----------------------------------------

step "--window: a real window inside the snap, drawn"
SHOTS="$HOME/megapdf-snap-shots"
mkdir -p "$SHOTS"
megapdf --window 1440x900 --screenshot "$SHOTS/window.png" "$WORK/demo.pdf" 2>&1 | tail -2
check "${PIPESTATUS[0]}"
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

step "--print-check: printing goes to the print portal inside the snap, not to lp"
out=$(megapdf --print-check "$WORK/fixture.pdf" 2>&1)
echo "$out" | tail -3 | sed 's/^/  /'
echo "$out" | grep -q 'in a snap sandbox, printing through org.freedesktop.portal.Print'
check $?

step "--portal-print-check: the portal is there to hand a document to"
megapdf --portal-print-check 2>&1 | tail -2 | sed 's/^/  /'
check "${PIPESTATUS[0]}"

step "the file dialogs: Open and Save a copy are the portal's, and Save a copy writes"
# The same two measurements #254 A4 used: a portal dialog is a FileChooser method call on
# the session bus, and a window that belongs to the portal's backend, not to MegaPDF.
COPY="$HOME/megapdf-snap-portal-copy.pdf"
rm -f "$COPY"
megapdf "$WORK/demo.pdf" >/tmp/megapdf-snap-app.log 2>&1 &
APP_PID=$!
WIN=
for _ in $(seq 1 60); do
    WIN=$(xdotool search --name 'MegaPDF' 2>/dev/null | tail -1)
    [ -n "$WIN" ] && break
    sleep 0.5
done
if [ -z "$WIN" ]; then
    echo "  FAIL: the app never opened a window"; failures=$((failures + 1))
else
    sleep 8   # the page is still rasterising when the window maps
    for probe in "ctrl+o:OpenFile" "ctrl+shift+s:SaveFile"; do
        keys=${probe%%:*}; method=${probe##*:}
        mark=$(wc -l </tmp/megapdf-snap-bus.log)
        xdotool windowactivate --sync "$WIN" 2>/dev/null; sleep 1
        xdotool key --clearmodifiers "$keys"
        sleep 6
        if tail -n +"$mark" /tmp/megapdf-snap-bus.log | grep -q "member=$method"; then
            echo "  $keys: org.freedesktop.portal.FileChooser.$method was called"
        else
            echo "  FAIL: $keys made no $method call on the bus"; failures=$((failures + 1))
        fi
        if [ "$method" = SaveFile ]; then
            # The portal's own dialog has the name field focused. A full path typed there
            # is taken as the destination, and Enter accepts it.
            DLG=$(xdotool search --classname 'xdg-desktop-portal-gtk' 2>/dev/null | tail -1)
            [ -n "$DLG" ] && xdotool windowactivate --sync "$DLG" 2>/dev/null
            sleep 1
            xdotool key --clearmodifiers ctrl+a
            xdotool type --delay 20 "$COPY"
            xdotool key Return
            for _ in $(seq 1 40); do [ -s "$COPY" ] && break; sleep 0.5; done
            if [ -s "$COPY" ] && pdftotext "$COPY" - >/dev/null 2>&1; then
                echo "  Save a copy through the portal wrote $(stat -c %s "$COPY") bytes, and it reads back"
            else
                echo "  FAIL: Save a copy through the portal wrote nothing at $COPY"
                failures=$((failures + 1))
            fi
        else
            xdotool key --clearmodifiers Escape
            sleep 2
        fi
    done
fi
kill "$APP_PID" 2>/dev/null; sleep 1; pkill -f 'snap/megapdf/.*/MegaPDF' 2>/dev/null

# --- startup cost ---------------------------------------------------------------

if [ -n "$TREE" ] && [ -x "$TREE/bin/MegaPDF" ]; then
    step "startup cost: the same command in the snap and out of the unpacked tree"
    timed() {  # <label> <command...>; best of three, after one untimed run
        local label=$1; shift
        "$@" >/dev/null 2>&1
        local best=
        for _ in 1 2 3; do
            local t0 t1 ms
            t0=$(date +%s%N); "$@" >/dev/null 2>&1; t1=$(date +%s%N)
            ms=$(( (t1 - t0) / 1000000 ))
            if [ -z "$best" ] || [ "$ms" -lt "$best" ]; then best=$ms; fi
        done
        printf '  %-44s %6d ms\n' "$label" "$best"
    }
    sync; echo 3 | sudo tee /proc/sys/vm/drop_caches >/dev/null
    t0=$(date +%s%N); megapdf --language-check >/dev/null 2>&1; t1=$(date +%s%N)
    printf '  %-44s %6d ms\n' "snap, --language-check, cold cache" $(( (t1 - t0) / 1000000 ))
    sync; echo 3 | sudo tee /proc/sys/vm/drop_caches >/dev/null
    t0=$(date +%s%N); "$TREE/bin/MegaPDF" --language-check >/dev/null 2>&1; t1=$(date +%s%N)
    printf '  %-44s %6d ms\n' "tree, --language-check, cold cache" $(( (t1 - t0) / 1000000 ))
    timed "snap, --language-check, warm"  snap run megapdf --language-check
    timed "tree, --language-check, warm"  "$TREE/bin/MegaPDF" --language-check
    timed "snap, --render-check, warm"    snap run megapdf --render-check "$WORK/stamped.pdf"
    timed "tree, --render-check, warm"    "$TREE/bin/MegaPDF" --render-check "$WORK/stamped.pdf"
fi

# --- what AppArmor refused ------------------------------------------------------

step "what AppArmor refused the snap during this check"
# Listed, not asserted. The probes above are refused on purpose, and so is the hidden
# temporary file a save first tries at the top of the home folder; anything else here is
# something the snap wanted and was not given, and is worth reading.
sudo journalctl -k -o cat --since "@$START" 2>/dev/null \
    | grep 'apparmor="DENIED"' | grep 'snap.megapdf' \
    | sed -E 's/.*operation="([^"]*)".*profile="([^"]*)".*name="([^"]*)".*requested_mask="([^"]*)".*/\1 \3 (\4)/' \
    | sort | uniq -c | sort -rn | head -30 | sed 's/^/  /'
echo "  (end of list)"

rm -rf "$WORK" "$SHOTS" "$COPY"
sudo snap remove megapdf >/dev/null

echo
if [ "$failures" -eq 0 ]; then
    echo "snap: all checks passed"
else
    echo "::error::snap: $failures check(s) failed"
fi
exit "$failures"
