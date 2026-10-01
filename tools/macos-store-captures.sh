#!/usr/bin/env bash
# Mac App Store screenshots for one listing language, from the in-house Mac.
#
# The iOS set has had tools/ios-screenshots.sh since #91; the Mac set was taken by
# hand, which is why the dry run (#146 §3) found a shot of whatever the machine had
# last opened. Everything here is either in the repo or made by this script, so the
# same seven images come out on any Mac with the app built.
#
# Seven slots since #613, in the order Dave settled on 2026-10-01: signing, editing and
# reading are what people come for and lead; redaction is minor and moves back. Reading
# replaces the old viewer shot at the front rather than joining it — both are a picture
# of a page, and the reading one says something as well.
#
# Usage: tools/macos-store-captures.sh [lang] [out-dir] [app-bundle]
#   lang        en (default), fr-CA or fr
#   out-dir     default artifacts/store/macos-screenshots/<lang>
#   app-bundle  default ~/app-store/MegaPDF.app, else ~/app-macos/MegaPDF.app
#
# Nothing is uploaded and App Store Connect is never opened; that is a separate,
# deliberate step (tools/asc_publish.py) and not this script's business.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LANG_TAG="${1:-en}"
OUT="${2:-$ROOT/artifacts/store/macos-screenshots/$LANG_TAG}"
APP="${3:-}"
if [ -z "$APP" ]; then
    for candidate in "$HOME/app-store/MegaPDF.app" "$HOME/app-macos/MegaPDF.app"; do
        [ -x "$candidate/Contents/MacOS/MegaPDF" ] && APP="$candidate" && break
    done
fi
BIN="$APP/Contents/MacOS/MegaPDF"
if [ ! -x "$BIN" ]; then
    echo "no app bundle: build one with tools/build-macos-app.sh osx-arm64 ~/app-macos" >&2
    exit 1
fi

# 1440x900 is an accepted Mac App Store size and the one the set is composed for.
# Not 2880x1800: --scale 2 misplaces every page overlay, and the app refuses rather
# than write a wrong image (#146 §3).
WINDOW=1440x900

case "$LANG_TAG" in
    en)    CULTURE=en-CA ;;
    fr-CA) CULTURE=fr-CA ;;
    fr)    CULTURE=fr-FR ;;
    *) echo "lang must be en, fr-CA or fr" >&2; exit 1 ;;
esac

mkdir -p "$OUT"

# The run owns its fixtures. A path that is new each run also means the recents
# store has no zoom to restore for it, so every shot opens at 100% — the dry run
# found 109% on Windows for exactly that reason.
WORK="$(mktemp -d "${TMPDIR:-/tmp}/megapdf-store-XXXXXX")"
trap 'rm -rf "$WORK"' EXIT
python3 "$ROOT/tools/gen_test_fixtures.py" "$WORK/fixtures" >/dev/null

# Copied to the name DemoContent.DocumentFileName gives, because the file name is
# in the status line of every shot with a document open — and the home shot's
# recents say "Contrat de location.pdf", not "demo-fr.pdf". One set, one name.
case "$LANG_TAG" in
    en)    FIXTURE="$WORK/fixtures/demo.pdf";    DOC="$WORK/Rental Agreement.pdf" ;;
    fr-CA) FIXTURE="$WORK/fixtures/demo-fr.pdf";    DOC="$WORK/Contrat de location.pdf" ;;
    # France's own page, not Quebec's under a French name (#310).
    fr)    FIXTURE="$WORK/fixtures/demo-fr-FR.pdf"; DOC="$WORK/Contrat de location.pdf" ;;
esac
[ -f "$FIXTURE" ] || { echo "no demo document at $FIXTURE" >&2; exit 1; }
cp "$FIXTURE" "$DOC"

# The same agreement, with the four sections it names and a landscape rate schedule
# behind it (#613). Two slots need a document that has more than one page in it: a Pages
# sidebar showing a single thumbnail is a picture of nothing, and reading mode's floating
# bar would read "Page 1 of 1". Page 1 is byte-identical to the page every other slot
# shows and the file is opened under the same name, so the set is still one document from
# end to end.
case "$LANG_TAG" in
    en)    LONG_FIXTURE="$WORK/fixtures/demo-pages.pdf" ;;
    fr-CA) LONG_FIXTURE="$WORK/fixtures/demo-fr-pages.pdf" ;;
    fr)    LONG_FIXTURE="$WORK/fixtures/demo-fr-FR-pages.pdf" ;;
esac
[ -f "$LONG_FIXTURE" ] || { echo "no multi-page demo document at $LONG_FIXTURE" >&2; exit 1; }
mkdir -p "$WORK/long"
LONG_DOC="$WORK/long/$(basename "$DOC")"
cp "$LONG_FIXTURE" "$LONG_DOC"

# The signature the library shot shows. Named here rather than left to the app's
# search, which walks up from the binary and so finds nothing inside a .app — and
# passing it is also what makes the flyout show exactly one known card instead of
# whatever this machine's library has in it (the `sign` state runs against a
# throwaway library directory of its own).
SIG="$ROOT/tools/assets/megawoman-sig.jpg"
[ -f "$SIG" ] || { echo "no demo signature at $SIG" >&2; exit 1; }

{
    echo "app:      $APP"
    echo "binary:   $(shasum -a 256 "$BIN" | cut -d' ' -f1)"
    echo "language: $LANG_TAG ($CULTURE)"
    echo "window:   $WINDOW"
    echo "document: $(basename "$DOC") (from $(basename "$FIXTURE"))"
    # Two slots open a longer document under the same name (#613), so the record of
    # what was shot has to say which file each of them came from.
    echo "pages doc: $(basename "$LONG_DOC") (from $(basename "$LONG_FIXTURE")), slots 01-reading and 04-pages"
    echo "taken:    $(date -u +%Y-%m-%dT%H:%M:%SZ)"
} | tee "$OUT/RUN.txt"

# The seven listing slots, in listing order (#613). Each is its own process: a state
# left over from the shot before is the defect the Windows set was bitten by twice.
#   reading the agreement with the chrome gone and the floating bar up
#   text    a typed name on the blank line, nothing selected
#   sign    the signature library flyout
#   pages   the Pages sidebar beside the document, two pages picked out
#   search  the find bar with a term and a hit count
#   redact  a line marked and selected, with the chrome that takes it off (2.1, #329)
#   home    the empty window with a recents list
shoot() {
    # Two statements, not one `local a= b=$a`: bash 3.2 declares every name in a
    # local list before assigning any of them, so the second would read an unset var.
    local slot="$1" state="$2" doc="$3"
    local sig="${4:-}"
    local log="$OUT/$slot.log"
    local args=("--window" "$WINDOW" "--language" "$CULTURE" "--screenshot" "$OUT/light-$slot.png")
    [ -n "$state" ] && args+=("--screenshot-state" "$state")
    [ -n "$sig" ] && args+=("--signature" "$sig")
    [ -n "$doc" ] && args=("$doc" "${args[@]}")
    # Launched by absolute path, never `open`: this Mac has ~20 stale
    # com.megapdf.ios bundles registered and LaunchServices is free to pick any
    # of them. The sha256 above says which binary this was.
    if ! "$BIN" "${args[@]}" >"$log" 2>&1; then
        echo "FAILED $slot — see $log" >&2
        grep '::error::' "$log" >&2 || true
        return 1
    fi
    if grep -q '::error::' "$log"; then
        echo "FAILED $slot — the state did not pose:" >&2
        grep '::error::' "$log" >&2
        return 1
    fi
    echo "  $slot: $(grep '^screenshot:' "$log" | tail -1)"
}

echo "capturing $LANG_TAG"
shoot 01-reading reading "$LONG_DOC"
shoot 02-text    text    "$DOC"
shoot 03-sign    sign    "$DOC"   "$SIG"
shoot 04-pages   pages   "$LONG_DOC"
shoot 05-search  find    "$DOC"
shoot 06-redact  redact  "$DOC"
# No document: the home shot is the empty window, and its recents come from
# DemoContent rather than from whatever this machine last opened.
shoot 07-home    home    ""

# The gate, checked here rather than by eye: seven files, all 1440x900, none empty.
python3 - "$OUT" "$WINDOW" <<'PY'
import sys, struct, pathlib
out = pathlib.Path(sys.argv[1])
want = tuple(int(n) for n in sys.argv[2].split("x"))
slots = ["01-reading", "02-text", "03-sign", "04-pages", "05-search", "06-redact",
         "07-home"]
bad = []
for slot in slots:
    p = out / f"light-{slot}.png"
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
