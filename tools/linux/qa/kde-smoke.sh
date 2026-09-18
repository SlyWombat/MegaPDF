#!/bin/bash
# #254 A5: the Linux smoke pass, in a real Plasma session.
#
#     tools/linux/qa/kde-smoke.sh <app-tree> [out-dir]
#
# #158's KDE pass was kwin_x11 under Xvfb — a real window manager from the desktop, but
# not the desktop. The note in docs/qa/linux-screen-inventory.md §10 says plasmashell was
# left out because it "wants systemd, logind and a seat". That is true of gnome-shell,
# which aborts in background.js the moment it asks org.freedesktop.login1 for anything.
# It turns out not to be true of Plasma: startplasma-x11 comes up in this container with
# no systemd at all, and brings kwin, plasmashell and the KDE portal backend with it.
#
# So this runs the pass under the whole desktop: the shell that draws the panel, the
# compositor that manages the window, and xdg-desktop-portal-kde answering for the file
# dialogs.
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"

TREE=${1:?usage: kde-smoke.sh <app-tree> [out-dir]}
OUT=${2:-$ROOT/artifacts/qa/kde}
APP="$TREE/bin/MegaPDF"
FIX=${FIXTURES:-$ROOT/artifacts/fixtures}
mkdir -p "$OUT"

export DISPLAY=:99
export XDG_RUNTIME_DIR=/run/user/0
export XDG_CURRENT_DESKTOP=KDE
export XDG_SESSION_TYPE=x11
mkdir -p "$XDG_RUNTIME_DIR" && chmod 700 "$XDG_RUNTIME_DIR"

HOMEDIR=$OUT/home
rm -rf "$HOMEDIR"; mkdir -p "$HOMEDIR"
export HOME=$HOMEDIR XDG_DATA_HOME=$HOMEDIR/.local/share \
       XDG_CONFIG_HOME=$HOMEDIR/.config XDG_CACHE_HOME=$HOMEDIR/.cache
mkdir -p "$XDG_DATA_HOME" "$XDG_CONFIG_HOME" "$XDG_CACHE_HOME"

# A display of its own, and nothing else managing it: Plasma's kwin will not claim the
# manager selection while another window manager holds it, and then everything after
# that is a session with no compositor.
pkill -x Xvfb 2>/dev/null
pkill -x mutter 2>/dev/null
pkill -x kwin_x11 2>/dev/null
pkill -f '^/usr/libexec/xdg-' 2>/dev/null
sleep 2
Xvfb "$DISPLAY" -screen 0 1920x1200x24 -nolisten tcp >/tmp/xvfb-kde.log 2>&1 &
for _ in $(seq 1 50); do xdpyinfo >/dev/null 2>&1 && break; sleep 0.2; done
echo "X:       $DISPLAY up, $(xdpyinfo | awk '/dimensions:/ {print $2}')"

rm -f /run/dbus/pid /run/dbus/system_bus_socket
mkdir -p /run/dbus && dbus-daemon --system --fork
eval "$(dbus-launch --sh-syntax)"
export DBUS_SESSION_BUS_ADDRESS DBUS_SESSION_BUS_PID
echo "bus:     session and system"

echo
echo "=== startplasma-x11 ==="
startplasma-x11 >"$OUT/plasma.log" 2>&1 &
PLASMA=$!
for _ in $(seq 1 90); do
    pgrep -x plasmashell >/dev/null 2>&1 && pgrep -x kwin_x11 >/dev/null 2>&1 && break
    sleep 1
done
sleep 5
for p in kwin_x11 plasmashell kded5 ksmserver; do
    printf '    %-14s %s\n' "$p" "$(pgrep -x "$p" >/dev/null 2>&1 && echo running || echo 'not running')"
done
echo "    window manager: $(wmctrl -m 2>/dev/null | awk '/^Name:/ {print $2}' || echo 'not answering')"
echo "    the shell's own windows:"
wmctrl -l -x 2>/dev/null | sed 's/^/        /'

echo
echo "=== the portal, with KDE's backend ==="
/usr/libexec/xdg-document-portal >/tmp/portal-doc.log 2>&1 &
/usr/libexec/xdg-desktop-portal-kde >/tmp/portal-backend.log 2>&1 &
/usr/libexec/xdg-desktop-portal >/tmp/portal.log 2>&1 &
for _ in $(seq 1 60); do
    gdbus introspect --session --dest org.freedesktop.portal.Desktop \
        --object-path /org/freedesktop/portal/desktop >/dev/null 2>&1 && break
    sleep 0.25
done
gdbus introspect --session --dest org.freedesktop.portal.Desktop \
    --object-path /org/freedesktop/portal/desktop 2>/dev/null \
    | grep -oE 'interface org\.freedesktop\.portal\.(FileChooser|Print|OpenURI)' | sed 's/^/    /'
if gdbus call --session --dest org.freedesktop.DBus --object-path /org/freedesktop/DBus \
     --method org.freedesktop.DBus.GetNameOwner org.freedesktop.impl.portal.desktop.kde >/dev/null 2>&1; then
    echo "    org.freedesktop.impl.portal.desktop.kde is on the bus"
else
    echo "    org.freedesktop.impl.portal.desktop.kde is NOT on the bus"
fi

fails=0
step() { echo; echo "=== $* ==="; }
check() { if [ "$1" -eq 0 ]; then echo "  ok"; else echo "  FAIL (exit $1)"; fails=$((fails + 1)); fi; }

step "--self-test: fill, check, sign, save, reopen"
timeout 180 "$APP" --self-test "$FIX" >"$OUT/self-test.log" 2>&1
check $?
tail -3 "$OUT/self-test.log" | sed 's/^/    /'

step "--render-check: the engine loads and a page renders"
timeout 120 "$APP" --render-check "$FIX/demo.pdf" >"$OUT/render.log" 2>&1
check $?
tail -2 "$OUT/render.log" | sed 's/^/    /'

step "--language-check: the POSIX chain, read on this desktop"
LANGUAGE=fr_CA timeout 60 "$APP" --language-check >"$OUT/language.log" 2>&1
check $?
tail -3 "$OUT/language.log" | sed 's/^/    /'

step "--print-check: which printing route this session offers"
timeout 60 "$APP" --print-check >"$OUT/print.log" 2>&1
check $?
tail -2 "$OUT/print.log" | sed 's/^/    /'

step "a real window, managed by kwin, with plasmashell drawing the desktop"
DOC="$HOMEDIR/Rental Agreement.pdf"
cp "$FIX/demo.pdf" "$DOC"
timeout 120 "$APP" --window 1280x800 --screenshot "$OUT/kde-window.png" --hold 6 "$DOC" \
    >"$OUT/window.log" 2>&1 &
APP_PID=$!
WIN=
for _ in $(seq 1 60); do
    WIN=$(xdotool search --name 'MegaPDF' 2>/dev/null | tail -1)
    [ -n "$WIN" ] && break
    sleep 0.5
done
if [ -n "$WIN" ]; then
    echo "    window id:  $WIN"
    echo "    WM_CLASS:   $(xprop -id "$WIN" WM_CLASS 2>/dev/null | sed 's/^WM_CLASS(STRING) = //')"
    echo "    title:      $(xdotool getwindowname "$WIN" 2>/dev/null)"
    echo "    geometry:   $(xdotool getwindowgeometry "$WIN" 2>/dev/null | tr '\n' ' ')"
    echo "    managed by kwin:"
    wmctrl -l -x 2>/dev/null | grep -i megapdf | sed 's/^/        /'
else
    echo "    no window appeared"
    fails=$((fails + 1))
fi
wait $APP_PID
check $?
grep -E '^(screenshot|toolbar|menu bar):' "$OUT/window.log" | sed 's/^/    /'

step "the file dialogs, in this session, with KDE's portal backend answering"
# The same observation as #254 A4(a), made again under the other desktop: a portal
# dialog is a method call on the bus, and Avalonia's own fallback makes none.
dbus-monitor --session >"$OUT/bus.log" 2>&1 &
MONITOR=$!
sleep 1
timeout 120 "$APP" --window 1280x800 --hold 40 "$DOC" >"$OUT/dialogs.log" 2>&1 &
DLG_PID=$!
WIN2=
for _ in $(seq 1 60); do
    WIN2=$(xdotool search --name 'MegaPDF' 2>/dev/null | tail -1)
    [ -n "$WIN2" ] && break
    sleep 0.5
done
if [ -n "$WIN2" ]; then
    sleep 8
    for probe in "Open:ctrl+o:OpenFile" "Save as:ctrl+shift+s:SaveFile"; do
        label=${probe%%:*}; rest=${probe#*:}; keys=${rest%%:*}; method=${rest##*:}
        before=$(wc -l <"$OUT/bus.log")
        xdotool windowactivate --sync "$WIN2" 2>/dev/null
        sleep 1
        xdotool key --window "$WIN2" --clearmodifiers "$keys"
        sleep 6
        if tail -n +"$before" "$OUT/bus.log" | grep -q "member=$method"; then
            echo "    $label ($keys): org.freedesktop.portal.FileChooser.$method was called"
            tail -n +"$before" "$OUT/bus.log" \
                | grep -E "interface=org\.freedesktop\.impl\.portal\.FileChooser" \
                | head -1 | sed 's/^/        handed to the backend: /'
        else
            echo "    $label ($keys): no $method on the bus — NOT the portal"
            fails=$((fails + 1))
        fi
        xdotool key --clearmodifiers Escape 2>/dev/null
        sleep 2
    done
else
    echo "    no window appeared for the dialog probe"
    fails=$((fails + 1))
fi
kill $DLG_PID 2>/dev/null; sleep 1; kill $MONITOR 2>/dev/null

echo
echo "=== the desktop the app reports ==="
echo "    XDG_CURRENT_DESKTOP=$XDG_CURRENT_DESKTOP  XDG_SESSION_TYPE=$XDG_SESSION_TYPE"

kill $PLASMA 2>/dev/null
echo
if [ $fails -eq 0 ]; then echo "KDE: all checks passed"; else echo "::error::KDE: $fails check(s) failed"; fi
exit $((fails > 0))
