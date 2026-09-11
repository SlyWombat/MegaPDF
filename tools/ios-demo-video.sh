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
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LANG_TAG="${1:-en}"
DEVICE="${2:-iPhone 17 Pro Max}"
LABEL="${3:-iphone-6_9}"
OUT="${4:-$ROOT/artifacts/video/ios/$LANG_TAG}"
DD="${DERIVED_DATA:-$HOME/dd-ios}"
mkdir -p "$OUT"

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
xcrun simctl boot "$UDID" 2>/dev/null || true
xcrun simctl bootstatus "$UDID" -b >/dev/null
# First-boot banners come and go for a while after bootstatus returns.
sleep 20
xcrun simctl status_bar "$UDID" override --time "9:41" \
    --batteryState charged --batteryLevel 100 --cellularBars 4 --wifiBars 3 || true
xcrun simctl ui "$UDID" appearance light || true

RAW="$OUT/$LABEL-raw.mp4"
xcrun simctl io "$UDID" recordVideo --codec h264 --force "$RAW" >"$OUT/$LABEL-record.log" 2>&1 &
REC=$!
sleep 3
xcodebuild test-without-building -project MegaPDF.xcodeproj -scheme MegaPDFDemo \
    -destination "id=$UDID" -derivedDataPath "$DD" -only-testing:MegaPDFUITests \
    TEST_RUNNER_DEMO_LANG="$LANG_TAG" 2>&1 | grep -E "Test Case|error:|\*\* TEST" || true
sleep 1
kill -INT "$REC"; wait "$REC" 2>/dev/null || true
sleep 2
xcrun simctl shutdown "$UDID" || true

# Trim the springboard lead-in: the first frame whose average luma is bright
# is the app's white page (the home screen wallpaper never gets near it).
START=$(ffmpeg -v error -i "$RAW" -vf "scale=32:32,signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=-" -f null - 2>/dev/null \
    | python3 -c "
import re, sys
t = None
for line in sys.stdin:
    m = re.search(r'pts_time:([0-9.]+)', line)
    if m: t = float(m.group(1)); continue
    m = re.search(r'YAVG=([0-9.]+)', line)
    if m and t is not None and float(m.group(1)) > 170:
        print(max(0.0, t - 0.3)); break
else:
    print(0)")
DEMO="$OUT/$LABEL-demo.mp4"
# Constant 30 fps from here on: the simulator recording is variable-rate,
# and the time compression below only lands on its target from a constant one.
ffmpeg -v error -y -ss "$START" -i "$RAW" -r 30 -fps_mode cfr -c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p -movflags +faststart -an "$DEMO"

# App Store Connect app previews must be 15-30 s; compress time to fit.
DUR=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$DEMO")
FACTOR=$(python3 -c "print(min(1.0, 29.5 / float('$DUR')))")
PREVIEW="$OUT/$LABEL-preview.mp4"
ffmpeg -v error -y -i "$DEMO" -vf "setpts=$FACTOR*PTS" -r 30 -fps_mode cfr -c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p -movflags +faststart -an "$PREVIEW"

for f in "$RAW" "$DEMO" "$PREVIEW"; do
    printf '%s  %s\n' "$(ffprobe -v error -show_entries stream=width,height:format=duration -of csv=p=0 "$f" | tr '\n' ' ')" "$f"
done
