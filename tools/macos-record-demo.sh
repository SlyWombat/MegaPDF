#!/usr/bin/env bash
# A true screen recording of the Mac app: the real window, the real pointer,
# driven by synthetic input (cliclick) while screencapture records. Needs the
# SSH context's Screen Recording, Accessibility and System Events grants on the
# capture Mac (tools/mac-mini.md); tools/macos-demo-video.sh is the fallback
# that needs none of them.
#
# Usage: tools/macos-record-demo.sh [app-bundle] [out-dir] [theme] [lang]
#   app-bundle  default ~/app-macos/MegaPDF.app
#   out-dir     default ~/captures/macos/video
#   theme       light (default) or dark — the app follows the system setting,
#               so this only names the files; switch Appearance first if needed
#   lang        en (default), fr-CA or fr — sets the app's --language, the demo
#               agreement (demo-blank vs demo-fr-blank), the name that gets typed
#               and the word Find searches for (#146 §3)
#
# Writes <out-dir>/macos-<lang>-<theme>-recorded-demo.mp4 (real pace, 1920x1080,
# 30 fps) and macos-<lang>-<theme>-recorded-preview.mp4 (time-compressed under 30 s for the
# Mac App Store). The window is placed at a fixed origin; the toolbar buttons
# are measured off a probe shot of that placement (tools/macos-measure-toolbar.py)
# and the page clicks are PDF points mapped through tools/macos-measure-page.py,
# so neither a toolbar redesign nor a translated label needs anything re-typed
# in here. The signature library must hold the demo signature ("Mega W."); the
# script seeds it if the library is empty.
set -euo pipefail
export PATH="/opt/homebrew/bin:$PATH"

APP="${1:-$HOME/app-macos/MegaPDF.app}"
OUT="${2:-$HOME/captures/macos/video}"
THEME="${3:-light}"
LANG_TAG="${4:-en}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$OUT"

# The demo person and the search word per capture language (#146 §3). These are
# the same three values src/MegaPDF.Avalonia/DemoContent.cs uses for --story and
# ios/MegaPDF/DemoContent.swift for the iOS captures — keep them in step. The
# accents are the point: Dave, 2026-09-15.
case "$LANG_TAG" in
    fr-CA) DEMO_DOC=demo-fr-blank.pdf; DEMO_NAME="Hélène Bélanger"; DEMO_FIND="location" ;;
    fr)    DEMO_DOC=demo-fr-blank.pdf; DEMO_NAME="Céline Lefèvre";  DEMO_FIND="location" ;;
    en)    DEMO_DOC=demo-blank.pdf;    DEMO_NAME="Jane Whitfield";  DEMO_FIND="rental"   ;;
    *)     echo "unknown language '$LANG_TAG' (expected en, fr-CA or fr)" >&2; exit 2 ;;
esac

# Window at (0,60), 1920 wide, 28 px title bar: the content is exactly
# 1920x1080 at screen (0,88), which is the Mac App Store's app-preview frame.
#
# Hard against the left, and that is not cosmetic. macOS puts notification
# banners in the top-right corner of the screen, and on the 2560-wide capture
# display a window at x=320 ends at 2240 — the last thirty pixels of every
# recorded frame were the edge of whatever Notification Centre had to say. At
# x=0 the recorded region ends at 1920 with 640 px of clearance, and no system
# setting has to be touched to get it.
WX=0; WY=60
click() { cliclick "c:$1,$2"; }
key()   { cliclick "kp:$1"; }
type_() { cliclick "t:$1"; }

# Fixtures inside the app's container (it is sandboxed when Store-signed; the
# ad-hoc build reads anywhere, but keep one convention).
C="$HOME/Library/Containers/com.megapdf.ios/Data"; mkdir -p "$C/tmp/story"
python3 "$ROOT/tools/gen_test_fixtures.py" "$C/tmp/story" >/dev/null
cp "$C/tmp/story/$DEMO_DOC" "$C/tmp/story/agreement.pdf"

# The demo signature, if the library is empty (index.json is the app's own format).
SIG="$HOME/Library/Application Support/MegaPDF/Signatures"
if [ ! -s "$SIG/index.json" ]; then
    mkdir -p "$SIG"
    python3 - "$SIG" "$ROOT/ios/MegaPDF/Resources/demo-signature.png" <<'PY'
import json, os, shutil, sys, uuid, datetime
sig, src = sys.argv[1:3]
gid = uuid.uuid4(); png = os.path.join(sig, f"{gid.hex}.png"); shutil.copy(src, png)
json.dump([{"Id": str(gid), "Name": "Mega W.", "PngPath": png,
            "CreatedUtc": datetime.datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%S.0000000Z")}],
          open(os.path.join(sig, "index.json"), "w"), indent=2)
PY
fi

pkill -x MegaPDF 2>/dev/null || true; sleep 1
open -na "$APP" --args "$C/tmp/story/agreement.pdf" --window 1920x1080 --language "$LANG_TAG"
sleep 5
osascript -e "tell application \"System Events\" to tell process \"MegaPDF\" to set position of window 1 to {$WX, $WY}"
osascript -e 'tell application "System Events" to set frontmost of process "MegaPDF" to true'
sleep 1
# No fit-page step. At 1920x1080 the whole 612x792 page is on screen at 100 %,
# which is where the app opens it and what the Mac listing stills show; the
# button this used to click belonged to the pre-#144 toolbar and by 2.0 pointed
# at empty bar.

# Where the toolbar buttons are. They are measured, not written down: #144 made
# the bar one row and moved every one of them, and the labels are translated —
# "Ajouter du texte" is nearly twice the width of "Add text", so everything to
# its right sits somewhere else in a French run. The order is the same in every
# language, so they are taken by index:
#
#   1 Open  2 Save │ 3 Sign  4 Add text  5 Cover  6 Redact │ 7 Undo  8 Redo
#   │ 9 zoom-out  10 zoom  11 zoom-in  12 More
screencapture -x -R "$WX,$((WY + 28)),1920,1080" /tmp/megapdf-toolbar.png
read -r TBY TB <<<"$(python3 "$ROOT/tools/macos-measure-toolbar.py" /tmp/megapdf-toolbar.png)"
set -- $TB
[ $# -ge 12 ] || { echo "measured $# toolbar buttons, expected 12" >&2; exit 1; }
BTN_Y=$((WY + 28 + TBY))
SIGN_X=$((WX + $3)); ADDTEXT_X=$((WX + $4))
echo "toolbar: row $BTN_Y, Sign at $SIGN_X, Add text at $ADDTEXT_X"
# The signature flyout hangs under the Sign button, its left edge on the
# button's, and the one saved signature is a card 315 px wide filling it. Its
# centre is 156 px right of the button's centre and 121 px down into the
# content — measured on the 2.0 bar, and the card is wide enough that the few
# pixels the anchor moves when "Signer" is wider than "Sign" do not matter.
SIGCARD_X=$((SIGN_X + 156)); SIGCARD_Y=$((WY + 28 + 121))

# Where the page is. A fresh file opens at whatever zoom fits, and a mode
# banner (Add text, placing a signature) pushes the page down while it
# shows, so nothing about the page's placement is assumed: a shot of the
# content area is measured (tools/macos-measure-page.py) and every page
# click below is a PDF point mapped through the latest measurement, as the
# iOS choreography does. Re-measured after each mode change.
measure_page() {
    screencapture -x -R "$WX,$((WY + 28)),1920,1080" /tmp/megapdf-layout.png
    read -r PL PT PS <<<"$(python3 "$ROOT/tools/macos-measure-page.py" /tmp/megapdf-layout.png)"
    echo "page: left=$PL top=$PT scale=$PS px/pt"
}
measure_page
pagex() { python3 -c "print(int($WX + $PL + $1 * $PS))"; }
pagey() { python3 -c "print(int($WY + 28 + $PT + (792 - $1) * $PS))"; }   # PDF y, bottom-left origin

cliclick "m:$((WX + 1800)),$((WY + 700))"   # park the pointer over the empty right margin
sleep 1

RAW="$OUT/macos-$LANG_TAG-$THEME-recorded-raw.mov"
rm -f "$RAW"
# screencapture cannot be stopped. SIGINT it ignores — it runs the full -V
# either way — and SIGTERM kills it and takes the unfinalised file with it,
# both measured on this machine. So -V is a ceiling with room to spare, the
# wall clock says how long the story actually took, and the cut below keeps
# exactly that. Leaving the whole take in was worth seeing: the story runs
# about forty seconds, the recorder ran to 120, and the time compression then
# fitted two minutes into thirty seconds — a preview at four times speed.
CEILING=90
REC_T0=$(python3 -c "import time; print(time.time())")
screencapture -v -x -V "$CEILING" -R "$WX,$((WY + 28)),1920,1080" "$RAW" &
REC=$!
sleep 3

# --- the story, at a human pace (demo agreement layout, gen_test_fixtures.py) --
sleep 2
click "$(pagex 78.5)" "$(pagey 590.5)"; sleep 1.6      # tick "Include delivery and pickup"
click "$(pagex 78.5)" "$(pagey 564.5)"; sleep 2.0      # tick "Damage insurance accepted"
click "$SIGN_X" "$BTN_Y"; sleep 2.0                    # Sign → library flyout
click "$SIGCARD_X" "$SIGCARD_Y"; sleep 1.5             # the saved signature
measure_page                                           # the placement banner moved the page
click "$(pagex 196)" "$(pagey 426)"; sleep 1.2         # place it on the line
key esc; sleep 2.0                                     # drop the selection
click "$ADDTEXT_X" "$BTN_Y"; sleep 1.2                 # Add text
measure_page                                           # the mode banner moved the page
click "$(pagex 72)" "$(pagey 350)"; sleep 1.2          # printed name, clear of the "Sign above the line" label
type_ "$DEMO_NAME"; sleep 1.2
key return; sleep 2.2
cliclick kd:cmd t:f ku:cmd; sleep 1.2                  # Find
type_ "$DEMO_FIND"; sleep 2.0
key return; sleep 1.3                                  # next match
key return; sleep 1.3
key esc; sleep 3.0                                     # close find, hold the finished page
# -------------------------------------------------------------------------

# A second past the last step, so the finished page gets a beat. Measured from
# the fork rather than from the file's own zero, which is a little later still
# — the difference goes on the same end and is wanted there.
TAKE=$(python3 -c "import time; print(round(time.time() - $REC_T0 + 1.0, 2))")
echo "story took ${TAKE}s of a ${CEILING}s ceiling"
wait "$REC" || true
sleep 2
python3 -c "import sys; sys.exit(0 if $TAKE < $CEILING - 2 else 1)" || {
    echo "the story reached the recorder's ceiling — the take is cut short; raise CEILING" >&2
    exit 1
}

DEMO="$OUT/macos-$LANG_TAG-$THEME-recorded-demo.mp4"
# -t after -i, deliberately. screencapture writes a variable-rate movie — 253
# frames over ninety seconds, because it only stores a frame when the screen
# changes — and before -i, -t is an input option that on such a file cuts three
# and a half seconds late (measured: 36.87 s where 33.12 s was asked for).
# After -i it is an output duration and lands exactly.
ffmpeg -v error -y -ss 1.5 -i "$RAW" -t "$(python3 -c "print(round($TAKE - 1.5, 2))")" -r 30 -fps_mode cfr -c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p -movflags +faststart -an "$DEMO"
DUR=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$DEMO")
FACTOR=$(python3 -c "print(min(1.0, 29.5 / float('$DUR')))")
PREVIEW="$OUT/macos-$LANG_TAG-$THEME-recorded-preview.mp4"
# App Store Connect refuses a preview without an audio track (MOV_RESAVE_STEREO), so a silent stereo one goes in.
ffmpeg -v error -y -i "$DEMO" -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 -vf "setpts=$FACTOR*PTS" -r 30 -fps_mode cfr -c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p -c:a aac -b:a 96k -shortest -movflags +faststart "$PREVIEW"
for f in "$RAW" "$DEMO" "$PREVIEW"; do
    printf '%s  %s\n' "$(ffprobe -v error -show_entries stream=width,height:format=duration -of csv=p=0 "$f" | tr '\n' ' ')" "$f"
done
