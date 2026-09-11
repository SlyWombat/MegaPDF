#!/usr/bin/env bash
# The Mac preview video, assembled from frames the app renders of itself
# (`--story`, App.axaml.cs): open the unfilled demo agreement, tick, sign,
# print the name, find. No screen recording — that needs privacy permissions
# an SSH session on the capture Mac cannot hold — so the clip is a sequence
# of real window states held for a moment each, not a pointer moving.
#
# Usage: tools/macos-demo-video.sh [app-bundle] [out-dir] [WxH] [theme]
#   app-bundle  default artifacts/macos/MegaPDF.app (tools/build-macos-app.sh)
#   out-dir     default artifacts/video/macos
#   WxH         window size, default 1440x900 (a Mac App Store preview size)
#   theme       light (default) or dark
#
# Writes <out-dir>/frames/<theme>/NN-step.png and <out-dir>/macos-<theme>-preview.mp4
# (30 fps, H.264, no audio, ~20 s). Needs ffmpeg and python3.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP="${1:-$ROOT/artifacts/macos/MegaPDF.app}"
OUT="${2:-$ROOT/artifacts/video/macos}"
SIZE="${3:-1440x900}"
THEME="${4:-light}"
BIN="$APP/Contents/MacOS/MegaPDF"
[ -x "$BIN" ] || { echo "no app at $APP — run tools/build-macos-app.sh first" >&2; exit 1; }

# The app is sandboxed: the document it opens must live in its container.
CONTAINER="$HOME/Library/Containers/com.megapdf.mac/Data"
STAGE="$CONTAINER/tmp/story"
mkdir -p "$STAGE" "$OUT/frames/$THEME"
python3 "$ROOT/tools/gen_test_fixtures.py" "$STAGE" >/dev/null
cp "$ROOT/ios/MegaPDF/Resources/demo-signature.png" "$STAGE/signature.png"

# The window renderer needs a live display. On the capture Mac the only one is
# the KVM's HDMI capture, which sleeps after ten idle minutes and takes the
# render timer with it ("not able to start the RenderTimer … -6661"); a burst
# of simulated user activity wakes it.
caffeinate -u -t 5 || true
for _ in $(seq 1 20); do
    system_profiler SPDisplaysDataType 2>/dev/null | grep -qE "^\s+[A-Za-z0-9 _-]+:$" -A0 && \
        system_profiler SPDisplaysDataType 2>/dev/null | awk '/Displays:/{f=1;next} f&&/^ {8}[^ ]/{found=1} END{exit !found}' && break
    sleep 1
done

THEME_ARGS=""
[ "$THEME" = dark ] && THEME_ARGS="--theme dark"
# The render timer also fails, intermittently, while simulators are booting on
# the same machine (the display reconfigures under it); a second attempt a few
# seconds later has always worked.
for attempt in 1 2 3; do
    rm -f "$OUT/frames/$THEME"/*.png
    if "$BIN" "$STAGE/demo-blank.pdf" --window "$SIZE" $THEME_ARGS \
        --story "$OUT/frames/$THEME" --signature "$STAGE/signature.png"; then
        break
    fi
    echo "story render failed (attempt $attempt); retrying" >&2
    sleep 5
done

# Hold each state; the first and last a little longer so the clip breathes.
LIST="$OUT/frames/$THEME/list.txt"
python3 - "$OUT/frames/$THEME" "$LIST" <<'PY'
import os, sys
d, out = sys.argv[1], sys.argv[2]
frames = sorted(f for f in os.listdir(d) if f.endswith(".png"))
with open(out, "w") as f:
    for i, name in enumerate(frames):
        hold = 3.0 if i in (0, len(frames) - 1) else 2.2
        f.write(f"file '{os.path.join(d, name)}'\nduration {hold}\n")
    f.write(f"file '{os.path.join(d, frames[-1])}'\n")  # concat needs the last file twice
PY
CLIP="$OUT/macos-$THEME-preview.mp4"
# App Store Connect refuses a preview without an audio track (MOV_RESAVE_STEREO), so a silent stereo one goes in.
ffmpeg -v error -y -f concat -safe 0 -i "$LIST" -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 -vf "format=yuv420p" -r 30 -fps_mode cfr \
    -c:v libx264 -preset slow -crf 18 -c:a aac -b:a 96k -shortest -movflags +faststart "$CLIP"
ffprobe -v error -show_entries stream=width,height:format=duration -of csv=p=0 "$CLIP" | tr '\n' ' '
echo " $CLIP"
