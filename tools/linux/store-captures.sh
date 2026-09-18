#!/usr/bin/env bash
# The Linux listing screenshots — the set AppStream points a software centre at (#254 A1).
#
# The other four platforms' listing sets have had a script each since #146 §3; Linux had
# only the QA matrix, which is a different job: that one shoots eleven poses in three
# languages, two themes and four widths to find layout defects, and every image in it is
# named for a cell rather than for a listing slot. This shoots the six slots the Mac and
# Windows listings already use, once per language, at one size.
#
# Usage: tools/linux/store-captures.sh [lang] [out-dir] [app-tree]
#   lang      en (default), fr-CA or fr-FR
#   out-dir   default website/megapdf/screenshots/linux/<lang>
#   app-tree  default artifacts/linux/MegaPDF, else artifacts/linux
#
# Nothing is uploaded. These files are staged in the site source and only reach
# electricrv.ca when website/deploy.py is run, which is a separate, deliberate step —
# and the AppStream <screenshot> URLs do not resolve until it has been (see
# tools/Linux-Packaging.md, "Before a submission").
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
LANG_TAG="${1:-en}"
OUT="${2:-$ROOT/website/megapdf/screenshots/linux/$LANG_TAG}"
TREE="${3:-}"
if [ -z "$TREE" ]; then
    for candidate in "$ROOT/artifacts/linux/MegaPDF" "$ROOT/artifacts/linux"; do
        [ -x "$candidate/bin/MegaPDF" ] && TREE="$candidate" && break
    done
fi
BIN="$TREE/bin/MegaPDF"
if [ ! -x "$BIN" ]; then
    echo "no app tree: build one with tools/build-linux-app.sh linux-x64 artifacts/linux" >&2
    exit 1
fi

# 1280x800 is the "desktop" slot the capture gate holds the Linux set to
# (tools/capture-gate/stores.py), it is above the toolbar's full-label breakpoint, and
# it is comfortably inside what AppStream asks of a screenshot. Not 2x: --scale 2 draws
# page overlays at twice their offset, and the app refuses rather than write a wrong
# image — which would take the redact and search slots with it.
WINDOW=1280x800

case "$LANG_TAG" in
    en)    CULTURE=en-CA ;;
    fr-CA) CULTURE=fr-CA ;;
    fr-FR) CULTURE=fr-FR ;;
    *) echo "lang must be en, fr-CA or fr-FR" >&2; exit 1 ;;
esac

mkdir -p "$OUT"

# The run owns its display. A capture renders the window to a bitmap rather than reading
# the screen, so no window manager is needed and no compositor is involved — but
# Avalonia.X11 still needs an X connection to open the window at all.
STARTED_X=
if ! DISPLAY="${DISPLAY:-}" xdpyinfo >/dev/null 2>&1; then
    command -v Xvfb >/dev/null || { echo "no X display and no Xvfb to make one" >&2; exit 1; }
    export DISPLAY=:${XVFB_DISPLAY:-99}
    Xvfb "$DISPLAY" -screen 0 1920x1200x24 -nolisten tcp >/tmp/megapdf-capture-xvfb.log 2>&1 &
    STARTED_X=$!
    for _ in $(seq 1 50); do xdpyinfo >/dev/null 2>&1 && break; sleep 0.2; done
    xdpyinfo >/dev/null 2>&1 || { echo "Xvfb did not come up: /tmp/megapdf-capture-xvfb.log" >&2; exit 1; }
fi

# A home of its own, for the reason the Mac set has a fresh fixture path: the recents
# store remembers a zoom per document, so a machine that has opened this file before
# would shoot it at whatever it was left at. The Windows dry run found 109 % that way.
WORK="$(mktemp -d "${TMPDIR:-/tmp}/megapdf-linux-store-XXXXXX")"
cleanup() {
    [ -n "$STARTED_X" ] && kill "$STARTED_X" 2>/dev/null
    rm -rf "$WORK"
}
trap cleanup EXIT
export HOME="$WORK/home"
export XDG_DATA_HOME="$HOME/.local/share"
export XDG_CONFIG_HOME="$HOME/.config"
export XDG_CACHE_HOME="$HOME/.cache"
mkdir -p "$XDG_DATA_HOME" "$XDG_CONFIG_HOME" "$XDG_CACHE_HOME"

python3 "$ROOT/tools/gen_test_fixtures.py" "$WORK/fixtures" >/dev/null

# Copied to the name DemoContent.DocumentFileName gives, because that name is in the
# window title of every shot with a document open — and the home shot's recents say
# "Contrat de location.pdf", not "demo-fr.pdf". One set, one name (#146 §3).
case "$LANG_TAG" in
    en)    FIXTURE="$WORK/fixtures/demo.pdf";    DOC="$WORK/Rental Agreement.pdf" ;;
    fr-CA|fr-FR) FIXTURE="$WORK/fixtures/demo-fr.pdf"; DOC="$WORK/Contrat de location.pdf" ;;
esac
[ -f "$FIXTURE" ] || { echo "no demo document at $FIXTURE" >&2; exit 1; }
cp "$FIXTURE" "$DOC"

# The signature the library shot shows, named rather than left to the app's search:
# passing it is also what makes the flyout show exactly one known card instead of
# whatever library the machine has.
SIG="$ROOT/tools/assets/megawoman-sig.jpg"
[ -f "$SIG" ] || { echo "no demo signature at $SIG" >&2; exit 1; }

{
    echo "app:      $TREE"
    echo "binary:   $(sha256sum "$BIN" | cut -d' ' -f1)"
    echo "language: $LANG_TAG ($CULTURE)"
    echo "window:   $WINDOW"
    echo "display:  $DISPLAY${STARTED_X:+ (Xvfb started by this run)}"
    echo "document: $(basename "$DOC") (from $(basename "$FIXTURE"))"
    echo "taken:    $(date -u +%Y-%m-%dT%H:%M:%SZ)"
} | tee "$OUT/RUN.txt"

# The six listing slots, in listing order — the same six the Mac set shoots, so the two
# desktop listings show the same app doing the same things.
#   viewer  the demo agreement open, as it will print
#   text    a typed name on the blank line, nothing selected
#   search  the find bar with a term and a hit count
#   sign    the signature library flyout
#   redact  the tool armed with a line marked (2.0's headline feature)
#   home    the empty window with a recents list
shoot() {
    local slot="$1" state="$2" doc="$3" sig="${4:-}"
    local log="$OUT/$slot.log"
    local args=("--window" "$WINDOW" "--language" "$CULTURE" "--screenshot" "$OUT/$slot.png")
    [ -n "$state" ] && args+=("--screenshot-state" "$state")
    [ -n "$sig" ] && args+=("--signature" "$sig")
    [ -n "$doc" ] && args=("$doc" "${args[@]}")
    if ! timeout 120 "$BIN" "${args[@]}" >"$log" 2>&1; then
        echo "FAILED $slot — see $log" >&2
        grep '::error::' "$log" >&2 || tail -5 "$log" >&2
        return 1
    fi
    if grep -q '::error::' "$log"; then
        echo "FAILED $slot — the state did not pose:" >&2
        grep '::error::' "$log" >&2
        return 1
    fi
    echo "  $slot: $(grep -E '^(screenshot|toolbar):' "$log" | tail -1)"
}

echo "capturing $LANG_TAG"
shoot 01-viewer ""       "$DOC"
shoot 02-text   text     "$DOC"
shoot 03-search find     "$DOC"
shoot 04-sign   sign     "$DOC"   "$SIG"
shoot 05-redact redact   "$DOC"
# No document: the home shot is the empty window, and its recents come from DemoContent
# rather than from whatever this machine last opened.
shoot 06-home   home     ""

# Six files, all 1280x800, none empty. The capture gate is the review
# (`tools/capture-gate/gate.py --store linux`); this is the part that must not even
# reach it.
python3 - "$OUT" "$WINDOW" <<'PY'
import sys, struct, pathlib
out = pathlib.Path(sys.argv[1])
want = tuple(int(n) for n in sys.argv[2].split("x"))
slots = ["01-viewer", "02-text", "03-search", "04-sign", "05-redact", "06-home"]
bad = []
for slot in slots:
    p = out / f"{slot}.png"
    if not p.exists():
        bad.append(f"{slot}: missing"); continue
    head = p.read_bytes()[:33]
    if head[:8] != b"\x89PNG\r\n\x1a\n":
        bad.append(f"{slot}: not a PNG"); continue
    w, h = struct.unpack(">II", head[16:24])
    if (w, h) != want:
        bad.append(f"{slot}: {w}x{h}, wanted {want[0]}x{want[1]}")
print(f"{len(slots) - len(bad)}/{len(slots)} slots at {want[0]}x{want[1]}")
for line in bad:
    print("  " + line)
sys.exit(1 if bad else 0)
PY

echo "done: $OUT"
