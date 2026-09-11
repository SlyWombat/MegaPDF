#!/usr/bin/env bash
# The App Review screen recording (docs/app-review-notes.md, item 1): the whole
# core flow in one take on a simulator, from a cold launch, opening the review
# test form through the real Files picker. Driven by
# DemoFlowUITests.testAppReviewWalkthrough (scheme MegaPDFDemo).
#
# Usage: tools/ios-review-video.sh [device-name] [out-dir]
#   device-name  default "iPhone 17 Pro Max"
#   out-dir      default artifacts/video/ios/review
#
# Writes review-walkthrough.mp4 (real pace, trimmed to the app, 30 fps H.264).
# Apple asked for a physical device; a simulator take covers the content and
# is labelled as such in the notes. Needs Xcode, xcodegen and ffmpeg.
set -euo pipefail
export PATH="/opt/homebrew/bin:$PATH"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEVICE="${1:-iPhone 17 Pro Max}"
OUT="${2:-$ROOT/artifacts/video/ios/review}"
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
xcrun simctl shutdown "$UDID" >/dev/null 2>&1 || true
sleep 5
xcrun simctl boot "$UDID" 2>/dev/null || true
xcrun simctl bootstatus "$UDID" -b >/dev/null
sleep 20

# The test form into the simulator's "On My iPhone": the local file provider's
# storage lives in a shared app group under the device's data directory.
DATA="$HOME/Library/Developer/CoreSimulator/Devices/$UDID/data"
STORE=$(find "$DATA/Containers/Shared/AppGroup" -maxdepth 2 -type d -name "File Provider Storage" 2>/dev/null | head -1)
if [ -z "$STORE" ]; then
    # Not created until Files has run once; launching it makes the directory.
    xcrun simctl launch "$UDID" com.apple.DocumentsApp >/dev/null 2>&1 || true
    sleep 5
    xcrun simctl terminate "$UDID" com.apple.DocumentsApp >/dev/null 2>&1 || true
    STORE=$(find "$DATA/Containers/Shared/AppGroup" -maxdepth 2 -type d -name "File Provider Storage" 2>/dev/null | head -1)
fi
[ -n "$STORE" ] || { echo "no File Provider Storage on $DEVICE" >&2; exit 1; }
cp "$ROOT/docs/review/MegaPDF-Test-Form.pdf" "$STORE/"
echo "staged MegaPDF-Test-Form.pdf in $STORE"

# A clean app: no recents, no signatures, so the take starts where a new user does.
xcrun simctl uninstall "$UDID" com.megapdf.ios >/dev/null 2>&1 || true
xcrun simctl install "$UDID" "$DD/Build/Products/Debug-iphonesimulator/MegaPDF.app"
xcrun simctl status_bar "$UDID" override --time "9:41" \
    --batteryState charged --batteryLevel 100 --cellularBars 4 --wifiBars 3 || true
xcrun simctl ui "$UDID" appearance light || true

RAW="$OUT/review-walkthrough-raw.mp4"
xcrun simctl io "$UDID" recordVideo --codec h264 --force "$RAW" >"$OUT/record.log" 2>&1 &
REC=$!
sleep 3
xcodebuild test-without-building -project MegaPDF.xcodeproj -scheme MegaPDFDemo \
    -destination "id=$UDID" -derivedDataPath "$DD" \
    -only-testing:MegaPDFUITests/DemoFlowUITests/testAppReviewWalkthrough \
    2>&1 | grep -E "Test Case|error:|\*\* TEST" || true
sleep 1
kill -INT "$REC"; wait "$REC" 2>/dev/null || true
sleep 2
xcrun simctl shutdown "$UDID" || true

# Trim to the app by saturation, as tools/ios-demo-video.sh does. The home
# screen is in the take on purpose (Apple asked for the launch), so keep two
# seconds of it before the first app frame.
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
print(max(0.0, app[0] - 2.0) if app else 0, (app[-1] + 0.5) if app else 999999)")"
OUTFILE="$OUT/review-walkthrough.mp4"
ffmpeg -v error -y -ss "$START" -to "$END" -i "$RAW" -r 30 -fps_mode cfr -c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p -movflags +faststart -an "$OUTFILE"
ffprobe -v error -show_entries stream=width,height:format=duration -of csv=p=0 "$OUTFILE" | tr '\n' ' '
echo " $OUTFILE"
