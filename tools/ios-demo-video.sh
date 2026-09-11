#!/usr/bin/env bash
# Records the iOS preview video on a Mac: boots a simulator, records it with
# `simctl io recordVideo`, and drives the app through the fill-check-sign story
# (ios/MegaPDFUITests/DemoFlowUITests.swift, scheme MegaPDFDemo) while it rolls.
#
# Usage: tools/ios-demo-video.sh [lang] [device-name] [label] [out-dir]
#   lang         en (default), fr-CA or fr — the catalogue, as in ios-screenshots.yml
#   device-name  a simulator name, default "iPhone 17 Pro Max" (the 6.9" listing size)
#   label        file stem, default iphone-6_9
#   out-dir      default artifacts/video/ios/<lang>
#
# Writes three files: <label>-raw.mp4 (as recorded, springboard lead-in and all),
# <label>-demo.mp4 (trimmed to the app's first frame, real pace) and
# <label>-preview.mp4 (the same, time-compressed to 30 s for App Store Connect,
# whose app previews must run 15-30 s). Needs Xcode, xcodegen and ffmpeg.
# TRIM_ONLY=1 skips the recording and re-cuts an existing <label>-raw.mp4.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LANG_TAG="${1:-en}"
DEVICE="${2:-iPhone 17 Pro Max}"
LABEL="${3:-iphone-6_9}"
OUT="${4:-$ROOT/artifacts/video/ios/$LANG_TAG}"
DD="${DERIVED_DATA:-$HOME/dd-ios}"
mkdir -p "$OUT"

RAW="$OUT/$LABEL-raw.mp4"
if [ "${TRIM_ONLY:-0}" != 1 ]; then
cd "$ROOT/ios"
[ -d Vendor/pdfium.xcframework ] || bash scripts/fetch-pdfium.sh
xcodegen generate >/dev/null
xcodebuild build-for-testing -project MegaPDF.xcodeproj -scheme MegaPDFDemo \
    -destination "platform=iOS Simulator,name=$DEVICE" -derivedDataPath "$DD" \
    CODE_SIGNING_ALLOWED=NO -quiet 2>&1 | grep -v "ld: warning" || true

UDID=$(xcrun simctl list devices available -j | python3 -c "
import json, sys
devs = json.load(sys.stdin)['devices']
print(next(d['udid'] for v in devs.values() for d in v if d['name'] == sys.argv[1]))" "$DEVICE")
xcrun simctl shutdown "$UDID" >/dev/null 2>&1 || true   # settle a device still going down
sleep 5
xcrun simctl boot "$UDID" 2>/dev/null || true
xcrun simctl bootstatus "$UDID" -b >/dev/null
# First-boot banners come and go for a while after bootstatus returns.
sleep 20
xcrun simctl status_bar "$UDID" override --time "9:41" \
    --batteryState charged --batteryLevel 100 --cellularBars 4 --wifiBars 3 || true
xcrun simctl ui "$UDID" appearance light || true

xcrun simctl io "$UDID" recordVideo --codec h264 --force "$RAW" >"$OUT/$LABEL-record.log" 2>&1 &
REC=$!
sleep 3
# TEST_RUNNER_ variables reach the test runner only from xcodebuild's own
# environment — as a build-setting argument the name is accepted and ignored,
# and the first French pass came out in English.
TEST_RUNNER_DEMO_LANG="$LANG_TAG" xcodebuild test-without-building -project MegaPDF.xcodeproj \
    -scheme MegaPDFDemo -destination "id=$UDID" -derivedDataPath "$DD" \
    -only-testing:MegaPDFUITests 2>&1 | grep -E "Test Case|error:|\*\* TEST" || true
sleep 1
kill -INT "$REC"; wait "$REC" 2>/dev/null || true
sleep 2
xcrun simctl shutdown "$UDID" || true
fi

# Trim to the app. Saturation tells the two apart: the home-screen wallpaper
# averages about 30, the app — grey chrome, white page, black ink — under 2,
# launch splash included. (Luma does not: the page with the keyboard down sits
# right on the wallpaper's value.) Before the first app frame is the
# springboard while xcodebuild starts up; after the last, the springboard again
# once the runner has killed the app.
read -r START END <<<"$(ffmpeg -v error -i "$RAW" -vf "scale=32:32,signalstats,metadata=print:key=lavfi.signalstats.SATAVG:file=-" -f null - 2>/dev/null \
    | python3 -c "
import re, sys
t, app = None, []
for line in sys.stdin:
    m = re.search(r'pts_time:([0-9.]+)', line)
    if m: t = float(m.group(1)); continue
    m = re.search(r'SATAVG=([0-9.]+)', line)
    if m and t is not None and float(m.group(1)) < 10:
        app.append(t)
print(max(0.0, app[0] - 0.2) if app else 0, (app[-1] + 0.2) if app else 999999)")"
DEMO="$OUT/$LABEL-demo.mp4"
# Constant 30 fps from here on: the simulator recording is variable-rate,
# and the time compression below only lands on its target from a constant one.
ffmpeg -v error -y -ss "$START" -to "$END" -i "$RAW" -r 30 -fps_mode cfr -c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p -movflags +faststart -an "$DEMO"

# App Store Connect app previews must be 15-30 s; compress time to fit.
DUR=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$DEMO")
FACTOR=$(python3 -c "print(min(1.0, 29.5 / float('$DUR')))")
PREVIEW="$OUT/$LABEL-preview.mp4"
# App Store Connect refuses a preview without an audio track (MOV_RESAVE_STEREO), so a silent stereo one goes in.
ffmpeg -v error -y -i "$DEMO" -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 -vf "setpts=$FACTOR*PTS" -r 30 -fps_mode cfr -c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p -c:a aac -b:a 96k -shortest -movflags +faststart "$PREVIEW"

for f in "$RAW" "$DEMO" "$PREVIEW"; do
    printf '%s  %s\n' "$(ffprobe -v error -show_entries stream=width,height:format=duration -of csv=p=0 "$f" | tr '\n' ' ')" "$f"
done
