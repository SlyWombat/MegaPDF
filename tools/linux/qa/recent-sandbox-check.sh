#!/bin/bash
# #254 A4(b), in the configuration that actually ships: inside the Flatpak sandbox.
#
#     tools/linux/qa/recent-sandbox-check.sh <bundle.flatpak> [out-dir]
#
# Outside the sandbox the answer could be argued away — a host application can reach the
# real file whatever the portal does. Inside it cannot: the only thing the app can open
# is what the document store gave it. So the question "does a recent document reopen
# across a restart" is only really answered here.
#
# The dialog is still not worked by hand. Documents.Add plus GrantPermissions is exactly
# what the FileChooser portal does when someone picks a file — the same store entry, the
# same permission, the same path inside the sandbox — so the app is given the result of a
# file dialog without one having to be operated.
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"

BUNDLE=${1:?usage: recent-sandbox-check.sh <bundle.flatpak> [out-dir]}
OUT=${2:-$ROOT/artifacts/qa/recent-sandbox}
APP_ID=ca.electricrv.MegaPDF
FIX=${FIXTURES:-$ROOT/artifacts/fixtures}
mkdir -p "$OUT"

export HOME=${HOME:-/root}
. "$HERE/portal-session.sh" gtk

# Deliberately *not* under $OUT: step 4 asks whether the sandbox can reach a file it was
# never granted, and that question is only honest if the answer cannot come from some
# other grant.
SRCDIR=$OUT/not-granted
rm -rf "$SRCDIR"; mkdir -p "$SRCDIR"
REAL=$SRCDIR/Rental\ Agreement.pdf
cp "$FIX/demo.pdf" "$REAL"

echo
echo "=== install the bundle ==="
flatpak uninstall -y --user "$APP_ID" >/dev/null 2>&1
flatpak install -y --user --noninteractive "$BUNDLE" >/dev/null 2>&1 \
    || { echo "could not install $BUNDLE"; exit 1; }
flatpak info --user "$APP_ID" | sed -n '1,4p' | sed 's/^/    /'

echo
echo "=== a document-store handle, granted to the app, as a file dialog would ==="
DOCID=$(python3 - "$REAL" "$APP_ID" <<'PY'
import os, sys
from gi.repository import Gio, GLib
path, app_id = sys.argv[1], sys.argv[2]
fd = os.open(path, os.O_RDONLY)
bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
fdlist = Gio.UnixFDList.new()
handle = fdlist.append(fd)
reply, _ = bus.call_with_unix_fd_list_sync(
    "org.freedesktop.portal.Documents", "/org/freedesktop/portal/documents",
    "org.freedesktop.portal.Documents", "Add",
    GLib.Variant("(hbb)", (handle, True, True)),
    GLib.VariantType("(s)"), Gio.DBusCallFlags.NONE, -1, fdlist, None)
doc_id = reply.unpack()[0]
# The permissions the FileChooser portal grants the application it answered.
bus.call_sync("org.freedesktop.portal.Documents", "/org/freedesktop/portal/documents",
              "org.freedesktop.portal.Documents", "GrantPermissions",
              GLib.Variant("(ssas)", (doc_id, app_id, ["read", "write"])),
              None, Gio.DBusCallFlags.NONE, -1, None)
print(doc_id)
PY
) || DOCID=
[ -n "$DOCID" ] || { echo "    the document portal would not take the file"; exit 1; }
INSIDE="/run/user/$(id -u)/doc/$DOCID/$(basename "$REAL")"
echo "    document id:         $DOCID"
echo "    path in the sandbox: $INSIDE"

# --render-check, not --screenshot: the app inside the sandbox cannot write to a host
# directory it was not granted, so a screenshot would either need a grant that spoils
# step 4 or would vanish into the sandbox's own /tmp and be believed. --render-check
# prints what it read and exits non-zero when it could not read it, which is the
# question being asked.
run_in_sandbox() {   # <label> <pdf>
    local label=$1 pdf=$2
    timeout 120 flatpak run --user "$APP_ID" --render-check "$pdf" >"$OUT/$label.log" 2>&1
    local rc=$?
    if [ $rc -eq 0 ]; then echo "    OPENED (exit 0)"; else echo "    COULD NOT OPEN (exit $rc)"; fi
    grep -E 'render-check|::error::|PdfLoad|Exception' "$OUT/$label.log" | head -3 | sed 's/^/        /'
    return $rc
}

echo
echo "=== 1. can the sandboxed app see it at all? ==="
run_in_sandbox open "$INSIDE"

# And once with a window, which is what puts the path into the recents store.
timeout 120 flatpak run --user "$APP_ID" --window 1280x800 --screenshot /tmp/shot.png \
    "$INSIDE" >"$OUT/window.log" 2>&1
echo "    windowed run: exit $?"
grep -E '^screenshot:|::error::' "$OUT/window.log" | head -2 | sed 's/^/        /'

DATA=$HOME/.var/app/$APP_ID/data
RECENT=$DATA/MegaPDF/recent.json
echo "    recent.json:"
[ -f "$RECENT" ] && sed 's/^/        /' "$RECENT" || echo "        NOT WRITTEN at $RECENT"

echo
echo "=== 2. the app has quit; open the recorded path again ==="
RECORDED=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))[0]['Path'])" "$RECENT" 2>/dev/null)
echo "    recorded: ${RECORDED:-<none>}"
run_in_sandbox reopen "${RECORDED:-$INSIDE}"

echo
echo "=== 3. restart xdg-document-portal — what a log out and back in does to it ==="
pkill -f '^/usr/libexec/xdg-document-portal'
sleep 2
/usr/libexec/xdg-document-portal >/tmp/portal-doc2.log 2>&1 &
for _ in $(seq 1 40); do
    gdbus introspect --session --dest org.freedesktop.portal.Documents \
        --object-path /org/freedesktop/portal/documents >/dev/null 2>&1 && break
    sleep 0.25
done
sleep 2
run_in_sandbox after-restart "${RECORDED:-$INSIDE}"

echo
echo "=== 4. and the same file by its real path, which was never granted ==="
# Two ways, because --render-check answers a missing file and an unreadable one with the
# same usage line, and "it did not work" is worth being specific about.
echo "    ls, from inside the sandbox:"
timeout 60 flatpak run --user --command=ls "$APP_ID" -l "$REAL" 2>&1 | head -2 | sed 's/^/        /'
if run_in_sandbox denied "$REAL"; then
    echo "        ^ this should NOT have worked: the app read a file it was never granted."
fi
echo "    (the granted path, for comparison:)"
timeout 60 flatpak run --user --command=ls "$APP_ID" -l "$INSIDE" 2>&1 | head -2 | sed 's/^/        /'

flatpak uninstall -y --user "$APP_ID" >/dev/null 2>&1
echo
echo "logs: $OUT"
