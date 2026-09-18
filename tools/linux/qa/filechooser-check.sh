#!/bin/bash
# #254 A4(a): does Avalonia open MegaPDF's file dialogs through the XDG FileChooser
# portal, or through its own fallback?
#
#     tools/linux/qa/filechooser-check.sh <backend> <app-tree|flatpak> [out-dir]
#
# tools/Linux-Packaging.md said this "has to be confirmed by hand … which one it picks
# is only observable when a dialog opens, and no headless check can tell them apart."
# It can be told apart from a terminal, twice over:
#
#   1. dbus-monitor on the session bus. A portal dialog is a method call to
#      org.freedesktop.portal.FileChooser.OpenFile / SaveFile. Avalonia's own fallback
#      makes no D-Bus call at all, so the bus says which path was taken.
#   2. The window list. A portal dialog is a window belonging to the *backend* process
#      (WM_CLASS xdg-desktop-portal-gnome), not to MegaPDF.
#
# Neither needs anyone to look at a screen or touch a mouse.
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"

BACKEND=${1:?usage: filechooser-check.sh <gnome|kde|gtk> <app-tree|flatpak> [out-dir]}
APPARG=${2:?}
OUT=${3:-$ROOT/artifacts/qa/filechooser-$BACKEND}
FIX=${FIXTURES:-$ROOT/artifacts/fixtures}
mkdir -p "$OUT"

# A clean slate. A dialog or an app left open by an earlier run takes the keystrokes
# this check sends, and the run then reports that no dialog opened — which is the wrong
# answer arrived at convincingly.
pkill -x MegaPDF 2>/dev/null
pkill -f 'xdg-desktop-portal-(gtk|gnome|kde)$' 2>/dev/null
sleep 1

. "$HERE/portal-session.sh" "$BACKEND"

stale=$(wmctrl -l -x 2>/dev/null | grep -ci 'xdg-desktop-portal' || true)
if [ "${stale:-0}" -gt 0 ]; then
    echo "::error::a portal dialog is already open; this session is not clean:" >&2
    wmctrl -l -x | grep -i 'xdg-desktop-portal' >&2
    exit 1
fi

HOMEDIR=$OUT/home
rm -rf "$HOMEDIR"; mkdir -p "$HOMEDIR"
export HOME=$HOMEDIR XDG_DATA_HOME=$HOMEDIR/.local/share \
       XDG_CONFIG_HOME=$HOMEDIR/.config XDG_CACHE_HOME=$HOMEDIR/.cache

DOC=$HOMEDIR/Rental\ Agreement.pdf
cp "$FIX/demo.pdf" "$DOC"

echo
echo "=== watching the session bus ==="
dbus-monitor --session >"$OUT/bus.log" 2>&1 &
MONITOR=$!
sleep 1

if [ "$APPARG" = flatpak ]; then
    APP=(flatpak run --user ca.electricrv.MegaPDF)
else
    APP=("$APPARG/bin/MegaPDF")
fi

echo "=== launching: ${APP[*]} ==="
"${APP[@]}" "$DOC" >"$OUT/app.log" 2>&1 &
APP_PID=$!

WIN=
for _ in $(seq 1 60); do
    WIN=$(xdotool search --name 'MegaPDF' 2>/dev/null | tail -1)
    [ -n "$WIN" ] && break
    sleep 0.5
done
if [ -z "$WIN" ]; then
    echo "the app never opened a window; see $OUT/app.log"
    kill $APP_PID $MONITOR 2>/dev/null
    exit 1
fi
echo "window:  $WIN"
# The document is still rasterising when the window first maps, and a key binding
# pressed into a window that is still laying out goes nowhere. Settle first.
sleep 8
wmctrl -l -x | sed 's/^/         /'

probe() {   # <label> <trigger> <portal method>
    #   trigger is either "key:<gesture>" or "click:<x>,<y>" in window coordinates.
    local label=$1 trigger=$2 method=$3
    echo
    echo "--- $label ($trigger) ---"
    local before
    before=$(wc -l <"$OUT/bus.log")
    xdotool windowactivate --sync "$WIN" 2>/dev/null
    sleep 1
    case "$trigger" in
        key:*)   xdotool key --window "$WIN" --clearmodifiers "${trigger#key:}" ;;
        # Two arguments, not one: xdotool mousemove takes x and y separately, and
        # "31 24" as a single word is read as a command name.
        click:*) local xy=${trigger#click:}
                 xdotool mousemove --window "$WIN" "${xy%%,*}" "${xy##*,}" click 1 ;;
    esac
    sleep 6
    echo "windows now:"
    wmctrl -l -x | sed 's/^/    /'
    # And a picture of the screen, if there is anything here to take one with: a window
    # list is the measurement, but the dialog belonging to the portal rather than to
    # MegaPDF is the sort of thing worth being able to look at.
    if command -v import >/dev/null 2>&1; then
        import -window root "$OUT/$(echo "$label" | tr ' ()' '-' | tr -s '-').png" 2>/dev/null \
            && echo "    screen: $OUT/$(echo "$label" | tr ' ()' '-' | tr -s '-').png"
    fi
    echo "what the bus saw:"
    tail -n +"$before" "$OUT/bus.log" \
        | grep -E "interface=org\.freedesktop\.(portal|impl\.portal)\.(FileChooser|Request)|member=(OpenFile|SaveFile|Response)" \
        | sed 's/^/    /' | head -20
    if tail -n +"$before" "$OUT/bus.log" | grep -q "member=$method"; then
        echo "    VERDICT: $method was called on the portal."
    else
        echo "    VERDICT: no $method call on the bus — this dialog is NOT the portal's."
    fi
    # Close whatever opened, so the next probe starts clean.
    xdotool key --clearmodifiers Escape 2>/dev/null
    sleep 2
}

# Open twice, by two different routes: the toolbar button a person would press, and
# the keyboard shortcut. A dialog that does not open says nothing unless both were
# tried, and the toolbar button is the route a screenshot can be taken of.
probe "Open (toolbar button)" "click:31,24"     OpenFile
probe "Open (keyboard)"       "key:ctrl+o"      OpenFile
probe "Save as (keyboard)"    "key:ctrl+shift+s" SaveFile

echo
echo "=== the portal's own view ==="
grep -iE "filechooser|openfile|savefile" /tmp/portal.log /tmp/portal-backend.log 2>/dev/null | sed 's/^/    /' | head -20

kill $APP_PID 2>/dev/null
sleep 1
kill $MONITOR 2>/dev/null
echo
echo "bus log: $OUT/bus.log ($(wc -l <"$OUT/bus.log") lines)"
