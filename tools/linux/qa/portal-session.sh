#!/bin/bash
# A headless desktop session with the XDG portal behind a named backend, for #254 A4.
#
#     . tools/linux/qa/portal-session.sh gnome      # xdg-desktop-portal-gnome
#     . tools/linux/qa/portal-session.sh kde        # xdg-desktop-portal-kde
#     . tools/linux/qa/portal-session.sh gtk        # xdg-desktop-portal-gtk (what #158 used)
#
# Sourced, not run, so DISPLAY and DBUS_SESSION_BUS_ADDRESS stay in the caller's shell.
#
# What this is, precisely. A real X server, a real window manager from the desktop
# named, the real xdg-desktop-portal and the real backend for that desktop. It is NOT
# gnome-shell or plasmashell: both call org.freedesktop.login1 before they draw
# anything, and a container has no systemd-logind to answer — gnome-shell 46.0 aborts
# in background.js with "Error calling StartServiceByName for org.freedesktop.login1".
# That does not stand in the way of the question A4(a) asks, because the FileChooser
# portal is implemented by the *backend* process, not by the shell.
BACKEND=${1:-gnome}
export DISPLAY=${DISPLAY:-:99}
export XDG_RUNTIME_DIR=/run/user/0
export GTK_USE_PORTAL=1

mkdir -p "$XDG_RUNTIME_DIR" && chmod 700 "$XDG_RUNTIME_DIR"

if ! xdpyinfo >/dev/null 2>&1; then
    Xvfb "$DISPLAY" -screen 0 1920x1200x24 -nolisten tcp >/tmp/xvfb.log 2>&1 &
    for _ in $(seq 1 50); do xdpyinfo >/dev/null 2>&1 && break; sleep 0.2; done
fi
echo "X:       $DISPLAY up, $(xdpyinfo | awk '/dimensions:/ {print $2}')"

if [ -z "${DBUS_SESSION_BUS_ADDRESS:-}" ]; then
    eval "$(dbus-launch --sh-syntax)"
    export DBUS_SESSION_BUS_ADDRESS DBUS_SESSION_BUS_PID
fi
echo "bus:     session bus up"
[ -S /run/dbus/system_bus_socket ] || { mkdir -p /run/dbus; dbus-daemon --system --fork; }

pkill -x mutter 2>/dev/null; pkill -x kwin_x11 2>/dev/null
sleep 0.5
case "$BACKEND" in
    gnome|gtk)
        export XDG_CURRENT_DESKTOP=GNOME
        mutter --x11 >/tmp/wm.log 2>&1 &
        ;;
    kde)
        export XDG_CURRENT_DESKTOP=KDE
        kwin_x11 >/tmp/wm.log 2>&1 &
        ;;
    *) echo "unknown backend '$BACKEND'"; return 1 2>/dev/null || exit 1 ;;
esac
for _ in $(seq 1 50); do wmctrl -m >/dev/null 2>&1 && break; sleep 0.2; done
echo "wm:      $(wmctrl -m 2>/dev/null | awk '/^Name:/ {print $2}' || echo 'not answering') (XDG_CURRENT_DESKTOP=$XDG_CURRENT_DESKTOP)"

# Anchored at the path they are started from. An unanchored -f pattern also matches
# any shell whose own command line mentions the portal — including the one running
# this script, which is a confusing way to lose a session.
pkill -f '^/usr/libexec/xdg-' 2>/dev/null
sleep 0.5
/usr/libexec/xdg-document-portal >/tmp/portal-doc.log 2>&1 &
case "$BACKEND" in
    gnome) /usr/libexec/xdg-desktop-portal-gnome >/tmp/portal-backend.log 2>&1 & ;;
    gtk)   /usr/libexec/xdg-desktop-portal-gtk   >/tmp/portal-backend.log 2>&1 & ;;
    kde)   /usr/libexec/xdg-desktop-portal-kde   >/tmp/portal-backend.log 2>&1 & ;;
esac
/usr/libexec/xdg-desktop-portal >/tmp/portal.log 2>&1 &
for _ in $(seq 1 60); do
    gdbus introspect --session --dest org.freedesktop.portal.Desktop \
        --object-path /org/freedesktop/portal/desktop >/dev/null 2>&1 && break
    sleep 0.25
done
if gdbus introspect --session --dest org.freedesktop.portal.Desktop \
       --object-path /org/freedesktop/portal/desktop >/dev/null 2>&1; then
    echo "portal:  org.freedesktop.portal.Desktop is on the bus, backend $BACKEND"
    gdbus introspect --session --dest org.freedesktop.portal.Desktop \
        --object-path /org/freedesktop/portal/desktop 2>/dev/null \
        | grep -oE 'interface org\.freedesktop\.portal\.(FileChooser|Print|OpenURI)' | sed 's/^/         /'
    # Which process is actually answering the backend name for this desktop. If the
    # named backend did not start, xdg-desktop-portal falls back to another one, and a
    # run that says "gnome" while gtk answers proves nothing.
    for iface in org.freedesktop.impl.portal.desktop.gnome \
                 org.freedesktop.impl.portal.desktop.gtk \
                 org.freedesktop.impl.portal.desktop.kde; do
        owner=$(gdbus call --session --dest org.freedesktop.DBus --object-path /org/freedesktop/DBus \
                  --method org.freedesktop.DBus.GetNameOwner "$iface" 2>/dev/null) || continue
        echo "         backend on the bus: $iface"
    done
else
    echo "portal:  NOT on the bus — see /tmp/portal.log"
fi
