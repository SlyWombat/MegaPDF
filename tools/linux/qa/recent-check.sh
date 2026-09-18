#!/bin/bash
# #254 A4(b): does a document opened through the portal reopen after a restart?
#
#     tools/linux/qa/recent-check.sh <app-tree> [out-dir]
#
# tools/Linux-Packaging.md says: "A file opened through the portal is a handle in the
# document store, and the app records the path it was given. Whether that path survives
# a restart depends on the portal's persistence, which the app does not currently ask
# for." That is a question about the *path*, and it does not need anyone to work a file
# dialog: org.freedesktop.portal.Documents.Add makes exactly the handle the FileChooser
# portal would have made, and the app is then given it on the command line.
#
# Four states are measured rather than argued:
#   1. the path the document store hands out, and whether it opens at all;
#   2. what the app wrote into recent.json;
#   3. whether that path still resolves after the app has quit and started again;
#   4. whether it still resolves after xdg-document-portal itself has been restarted,
#      which is what a log out and back in does to it.
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"

TREE=${1:?usage: recent-restart-check.sh <app-tree> [out-dir]}
OUT=${2:-$ROOT/artifacts/qa/recent}
APP="$TREE/bin/MegaPDF"
FIX=${FIXTURES:-$ROOT/artifacts/fixtures}
mkdir -p "$OUT"

# The home goes first, *before* the portal is started. xdg-document-portal keeps its
# store under $XDG_DATA_HOME/flatpak/db/documents, so a portal started under one home and
# restarted under another reads an empty database and every handle looks lost — which is
# exactly the wrong answer to the question being asked here, arrived at convincingly.
HOMEDIR=$OUT/home
rm -rf "$HOMEDIR"; mkdir -p "$HOMEDIR"
export HOME=$HOMEDIR XDG_DATA_HOME=$HOMEDIR/.local/share \
       XDG_CONFIG_HOME=$HOMEDIR/.config XDG_CACHE_HOME=$HOMEDIR/.cache
mkdir -p "$XDG_DATA_HOME"

. "$HERE/portal-session.sh" gtk

REAL=$HOMEDIR/Rental\ Agreement.pdf
cp "$FIX/demo.pdf" "$REAL"

echo
echo "=== 1. a document-store handle, the one the FileChooser portal would have made ==="
# Documents.Add(fd, reuse_existing, persistent) -> the id the fuse mount is keyed on.
DOCID=$(python3 - "$REAL" <<'PY'
import os, sys
from gi.repository import Gio, GLib
fd = os.open(sys.argv[1], os.O_RDONLY)
bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
fdlist = Gio.UnixFDList.new()
handle = fdlist.append(fd)
reply, _ = bus.call_with_unix_fd_list_sync(
    "org.freedesktop.portal.Documents", "/org/freedesktop/portal/documents",
    "org.freedesktop.portal.Documents", "Add",
    GLib.Variant("(hbb)", (handle, True, True)),
    GLib.VariantType("(s)"), Gio.DBusCallFlags.NONE, -1, fdlist, None)
print(reply.unpack()[0])
PY
) || DOCID=
if [ -z "$DOCID" ]; then
    echo "    the document portal would not take the file; see /tmp/portal-doc.log"
    tail -5 /tmp/portal-doc.log
    exit 1
fi
# GetMountPoint answers with a NUL-terminated byte array, which gdbus prints as a list
# of numbers; not worth unpicking in shell.
MOUNT=$(python3 - <<'MP'
from gi.repository import Gio, GLib
bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
reply = bus.call_sync("org.freedesktop.portal.Documents", "/org/freedesktop/portal/documents",
                      "org.freedesktop.portal.Documents", "GetMountPoint", None,
                      GLib.VariantType("(ay)"), Gio.DBusCallFlags.NONE, -1, None)
print(bytes(reply.unpack()[0]).rstrip(b"\0").decode())
MP
)
MOUNT=${MOUNT:-$XDG_RUNTIME_DIR/doc}
PORTAL_PATH="$MOUNT/$DOCID/$(basename "$REAL")"
echo "    document id:   $DOCID"
echo "    mount point:   $MOUNT"
echo "    portal path:   $PORTAL_PATH"
echo "    readable now:  $([ -r "$PORTAL_PATH" ] && echo yes || echo NO)"
[ -r "$PORTAL_PATH" ] && echo "    same bytes:    $(cmp -s "$REAL" "$PORTAL_PATH" && echo yes || echo NO)"

echo
echo "=== 2. open it, and see what the app records ==="
timeout 90 "$APP" --window 1280x800 --screenshot "$OUT/opened.png" "$PORTAL_PATH" \
    >"$OUT/open.log" 2>&1
echo "    exit: $?   screenshot: $(grep -c '^screenshot:' "$OUT/open.log") line(s)"
grep -E '^screenshot:' "$OUT/open.log" | sed 's/^/    /'
RECENT=$XDG_DATA_HOME/MegaPDF/recent.json
if [ -f "$RECENT" ]; then
    echo "    recent.json:"
    sed 's/^/        /' "$RECENT"
else
    echo "    recent.json: NOT WRITTEN at $RECENT"
    find "$HOMEDIR" -name 'recent*' 2>/dev/null | sed 's/^/        found: /'
fi

echo
echo "=== 3. the app has quit; does the recorded path still resolve? ==="
echo "    exists: $([ -e "$PORTAL_PATH" ] && echo yes || echo NO)"
echo "    readable: $([ -r "$PORTAL_PATH" ] && echo yes || echo NO)"
timeout 90 "$APP" --window 1280x800 --screenshot "$OUT/reopened.png" "$PORTAL_PATH" \
    >"$OUT/reopen.log" 2>&1
echo "    reopening it directly: exit $?"
grep -E '^screenshot:|::error::' "$OUT/reopen.log" | sed 's/^/    /'

echo
echo "=== 4. restart xdg-document-portal — what a log out and back in does to it ==="
pkill -f xdg-document-portal
sleep 2
/usr/libexec/xdg-document-portal >/tmp/portal-doc2.log 2>&1 &
for _ in $(seq 1 40); do
    gdbus introspect --session --dest org.freedesktop.portal.Documents \
        --object-path /org/freedesktop/portal/documents >/dev/null 2>&1 && break
    sleep 0.25
done
sleep 2
echo "    exists: $([ -e "$PORTAL_PATH" ] && echo yes || echo NO)"
echo "    readable: $([ -r "$PORTAL_PATH" ] && echo yes || echo NO)"
timeout 90 "$APP" --window 1280x800 --screenshot "$OUT/after-portal-restart.png" "$PORTAL_PATH" \
    >"$OUT/after.log" 2>&1
echo "    opening it after the portal restart: exit $?"
grep -E '^screenshot:|::error::' "$OUT/after.log" | sed 's/^/    /'

echo
echo "=== 5. and what the app's own home screen makes of the recents list ==="
timeout 90 "$APP" --window 1280x800 --screenshot "$OUT/home-after.png" \
    >"$OUT/home.log" 2>&1
echo "    exit: $?"
grep -E '^screenshot:|recent' "$OUT/home.log" | sed 's/^/    /'
[ -f "$RECENT" ] && { echo "    recent.json now:"; sed 's/^/        /' "$RECENT"; }
