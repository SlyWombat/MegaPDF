#!/usr/bin/env bash
# A true screen recording of the Mac app: the real window, the real pointer,
# driven by synthetic input (cliclick) while screencapture records. Needs the
# SSH context's Screen Recording, Accessibility and System Events grants on the
# capture Mac (tools/mac-mini.md); tools/macos-demo-video.sh is the fallback
# that needs none of them.
#
# Usage: tools/macos-record-demo.sh [app-bundle] [out-dir] [theme]
#   app-bundle  default ~/app-macos/MegaPDF.app
#   out-dir     default ~/captures/macos/video
#   theme       light (default) or dark — the app follows the system setting,
#               so this only names the files; switch Appearance first if needed
#
# Writes <out-dir>/macos-<theme>-recorded-demo.mp4 (real pace, 1920x1080, 30 fps)
# and macos-<theme>-recorded-preview.mp4 (time-compressed under 30 s for the
# Mac App Store). The window is placed at a fixed origin and every click is a
# screen coordinate read off a probe shot of that placement — re-probe if the
# toolbar changes. The signature library must hold the demo signature
# ("Mega W."); the script seeds it if the library is empty.
set -euo pipefail
export PATH="/opt/homebrew/bin:$PATH"

APP="${1:-$HOME/app-macos/MegaPDF.app}"
OUT="${2:-$HOME/captures/macos/video}"
THEME="${3:-light}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$OUT"

# Window at (320,60), 1920 wide, 28 px title bar: the content is exactly
# 1920x1080 at screen (320,88). All click coordinates below assume this.
WX=320; WY=60
click() { cliclick "c:$1,$2"; }
key()   { cliclick "kp:$1"; }
type_() { cliclick "t:$1"; }

# Fixtures inside the app's container (it is sandboxed when Store-signed; the
# ad-hoc build reads anywhere, but keep one convention).
C="$HOME/Library/Containers/com.megapdf.mac/Data"; mkdir -p "$C/tmp/story"
python3 "$ROOT/tools/gen_test_fixtures.py" "$C/tmp/story" >/dev/null
cp "$C/tmp/story/demo-blank.pdf" "$C/tmp/story/agreement.pdf"

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
open -na "$APP" --args "$C/tmp/story/agreement.pdf" --window 1920x1080
sleep 5
osascript -e "tell application \"System Events\" to tell process \"MegaPDF\" to set position of window 1 to {$WX, $WY}"
osascript -e 'tell application "System Events" to set frontmost of process "MegaPDF" to true'
sleep 1

# Where the page is. A fresh file opens at whatever zoom fits, so nothing about
# the page's placement is assumed: a shot of the content area is scanned for
# the white page on a grey viewport, and every page click below is a PDF point
# mapped through that measurement, as the iOS choreography does.
screencapture -x -R "$WX,$((WY + 28)),1920,1080" /tmp/megapdf-layout.png
read -r PL PT PS <<<"$(python3 - /tmp/megapdf-layout.png <<'PY'
import struct, subprocess, sys
subprocess.run(["sips", "-s", "format", "bmp", sys.argv[1], "--out", "/tmp/megapdf-layout.bmp"], capture_output=True)
d = open("/tmp/megapdf-layout.bmp", "rb").read()
off = struct.unpack_from("<I", d, 10)[0]; w = struct.unpack_from("<i", d, 18)[0]
h = abs(struct.unpack_from("<i", d, 22)[0]); bpp = struct.unpack_from("<H", d, 28)[0] // 8
row = ((w * bpp) + 3) // 4 * 4
def px(x, y):
    p = off + (h - 1 - y) * row + x * bpp
    return d[p], d[p + 1], d[p + 2]
def white(x, y): return min(px(x, y)) >= 250
y = 700                                   # a row well inside the page, below the text
runs, x = [], 0
while x < w:
    if white(x, y):
        x0 = x
        while x < w and white(x, y): x += 1
        runs.append((x0, x))
    x += 1
left, right = max(runs, key=lambda r: r[1] - r[0])
mid = (left + right) // 2
top = next(yy for yy in range(0, h) if white(mid, yy))
print(left, top, (right - left) / 612.0)
PY
)"
echo "page: left=$PL top=$PT scale=$PS px/pt"
pagex() { python3 -c "print(int($WX + $PL + $1 * $PS))"; }
pagey() { python3 -c "print(int($WY + 28 + $PT + (792 - $1) * $PS))"; }   # PDF y, bottom-left origin

cliclick "m:$((WX + 1800)),$((WY + 700))"   # park the pointer over the empty right margin
sleep 1

RAW="$OUT/macos-$THEME-recorded-raw.mov"
rm -f "$RAW"
screencapture -v -x -V 55 -R "$WX,$((WY + 28)),1920,1080" "$RAW" &
REC=$!
sleep 3

# --- the story, at a human pace (demo agreement layout, gen_test_fixtures.py) --
sleep 2
click "$(pagex 78.5)" "$(pagey 590.5)"; sleep 1.6      # tick "Include delivery and pickup"
click "$(pagex 78.5)" "$(pagey 564.5)"; sleep 2.0      # tick "Damage insurance accepted"
click 644 118; sleep 2.0                               # Sign → library flyout
click 716 210; sleep 1.5                               # the saved signature
click "$(pagex 196)" "$(pagey 437)"; sleep 1.2         # place it on the line
key esc; sleep 2.0                                     # drop the selection
click 702 118; sleep 1.2                               # Add text
click "$(pagex 72)" "$(pagey 376)"; sleep 1.2          # under the line
type_ "Jane Whitfield"; sleep 1.2
key return; sleep 2.2
cliclick kd:cmd t:f ku:cmd; sleep 1.2                  # Find
type_ "rental"; sleep 2.0
key return; sleep 1.3                                  # next match
key return; sleep 1.3
key esc; sleep 3.0                                     # close find, hold the finished page
# -------------------------------------------------------------------------

wait "$REC" || true
sleep 1

DEMO="$OUT/macos-$THEME-recorded-demo.mp4"
ffmpeg -v error -y -ss 1.5 -i "$RAW" -r 30 -fps_mode cfr -c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p -movflags +faststart -an "$DEMO"
DUR=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$DEMO")
FACTOR=$(python3 -c "print(min(1.0, 29.5 / float('$DUR')))")
PREVIEW="$OUT/macos-$THEME-recorded-preview.mp4"
ffmpeg -v error -y -i "$DEMO" -vf "setpts=$FACTOR*PTS" -r 30 -fps_mode cfr -c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p -movflags +faststart -an "$PREVIEW"
for f in "$RAW" "$DEMO" "$PREVIEW"; do
    printf '%s  %s\n' "$(ffprobe -v error -show_entries stream=width,height:format=duration -of csv=p=0 "$f" | tr '\n' ' ')" "$f"
done
