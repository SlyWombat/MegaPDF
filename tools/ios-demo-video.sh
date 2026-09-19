#!/usr/bin/env bash
# Records the iOS preview video on a Mac: boots a simulator, records it with
# `simctl io recordVideo`, and drives the app through the fill-check-sign story
# (ios/MegaPDFUITests/DemoFlowUITests.swift, scheme MegaPDFDemo) while it rolls.
#
# Usage: tools/ios-demo-video.sh [lang] [device-name] [label] [out-dir]
#   lang         en (default), fr-CA or fr — the catalogue, as in ios-screenshots.yml
#   device-name  a regex matched against the available simulator names, as in
#                ios-screenshots.sh; default "iPhone .*Pro Max" (the 6.9" listing
#                size). A regex rather than an exact name because Xcode renames
#                these every year — "iPad Pro 13-inch (M4)" is "(M5)" under
#                Xcode 26.6, and an exact name simply stopped finding it.
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
DEVICE="${2:-iPhone .*Pro Max}"
LABEL="${3:-iphone-6_9}"
OUT="${4:-$ROOT/artifacts/video/ios/$LANG_TAG}"
DD="${DERIVED_DATA:-$HOME/dd-ios}"
mkdir -p "$OUT"

RAW="$OUT/$LABEL-raw.mp4"
if [ "${TRIM_ONLY:-0}" != 1 ]; then
cd "$ROOT/ios"
[ -d Vendor/pdfium.xcframework ] || bash scripts/fetch-pdfium.sh
xcodegen generate >/dev/null

# Resolve the device before the build, and build for that udid: the pattern is
# a regex, which -destination name= would take literally.
PICKED=$(xcrun simctl list devices available -j | python3 -c "
import json, re, sys
devs = json.load(sys.stdin)['devices']
for v in devs.values():
    for d in v:
        if re.search(sys.argv[1], d['name']):
            print(d['udid'] + '|' + d['name']); sys.exit(0)
sys.exit(1)" "$DEVICE") || { echo "no simulator matches '$DEVICE'" >&2; exit 1; }
UDID="${PICKED%%|*}"
echo "recording $LABEL ($LANG_TAG) on ${PICKED##*|}"

xcodebuild build-for-testing -project MegaPDF.xcodeproj -scheme MegaPDFDemo \
    -destination "id=$UDID" -derivedDataPath "$DD" \
    CODE_SIGNING_ALLOWED=NO -quiet 2>&1 | grep -v "ld: warning" || true

# The version comes out of the built artefact, never out of project.yml: a
# cached Vendor/ or stale derived data is exactly what leaves a previous
# build sitting where the new one is assumed to be (#146 §3).
BUILT="$DD/Build/Products/Debug-iphonesimulator/MegaPDF.app"
[ -d "$BUILT" ] || { echo "no built app at $BUILT" >&2; exit 1; }
printf 'app under test: %s %s, built %s\n' \
    "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$BUILT/Info.plist")" \
    "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$BUILT/Info.plist")" \
    "$(date -r "$BUILT/MegaPDF" '+%Y-%m-%d %H:%M')"
xcrun simctl shutdown "$UDID" >/dev/null 2>&1 || true   # settle a device still going down
sleep 5
xcrun simctl boot "$UDID" 2>/dev/null || true
xcrun simctl bootstatus "$UDID" -b >/dev/null
# An iPad in Windowed Apps draws a resize grabber in the corner of every frame
# (capture-gate-report.md §6): Full Screen Apps first, through Settings.
case "${PICKED##*|}" in
    iPad*)
        SETUP=$(xcodebuild test-without-building -project MegaPDF.xcodeproj -scheme MegaPDFDemo \
                    -destination "id=$UDID" -derivedDataPath "$DD" \
                    -only-testing:MegaPDFUITests/CaptureSimulatorSetupUITests 2>&1) || true
        printf '%s\n' "$SETUP" | grep -q "testIPadRunsAppsFullScreen\]' passed" \
            || { echo "could not put the iPad in Full Screen Apps" >&2; exit 1; } ;;
esac
# First-boot banners come and go for a while after bootstatus returns.
sleep 20
xcrun simctl status_bar "$UDID" override --time "9:41" \
    --batteryState charged --batteryLevel 100 --cellularBars 4 --wifiBars 3 || true
xcrun simctl ui "$UDID" appearance light || true
# A fresh container, as ios-screenshots.sh takes one: the demo signature
# library is seeded only when the store is empty, and this machine's
# simulators keep whatever the last run left in it — the App Review
# walkthrough draws a second signature and saves it (#100).
xcrun simctl uninstall "$UDID" com.megapdf.ios 2>/dev/null || true

xcrun simctl io "$UDID" recordVideo --codec h264 --force "$RAW" >"$OUT/$LABEL-record.log" 2>&1 &
REC=$!
sleep 3
# TEST_RUNNER_ variables reach the test runner only from xcodebuild's own
# environment — as a build-setting argument the name is accepted and ignored,
# and the first French pass came out in English.
#
# One test, named in full. `-only-testing:MegaPDFUITests` is the whole bundle,
# which is now four UI-test classes — the App Review walkthrough, redaction,
# body-text editing and the Recents accessibility pass — and every one of them
# would be driven on camera and land in the clip. The story is the preview.
TEST_RUNNER_DEMO_LANG="$LANG_TAG" xcodebuild test-without-building -project MegaPDF.xcodeproj \
    -scheme MegaPDFDemo -destination "id=$UDID" -derivedDataPath "$DD" \
    -only-testing:MegaPDFUITests/DemoFlowUITests/testFillCheckSignStory 2>&1 \
    | grep -E "Test Case|error:|\*\* TEST" || true
sleep 1
kill -INT "$REC"; wait "$REC" 2>/dev/null || true
sleep 2
xcrun simctl shutdown "$UDID" || true
fi

# Trim to the app. Two measurements, because one does not separate three
# things. Measured over the six 2.0 runs, on a light-appearance simulator
# (forced above):
#
#   the springboard   SATAVG 29-32   YAVG 158-168
#   the launch screen SATAVG  0.0    YAVG  16          (black, and unsaturated)
#   the app, drawing  SATAVG  0-9.6  YAVG 103-217
#
# Saturation alone lets the black launch screen through, and luma alone cannot
# be trusted — the page with the keyboard down sits right on the wallpaper's
# value. Unsaturated *and* bright is the app with something on screen.
#
# Neither end is padded, and that is the point. The 0.2 s this used to keep on
# each side put six frames of the iOS home screen — other apps' icons, and the
# UI-test runner's icon among them — at the head of every clip that went to the
# store, and put the home screen back at the tail. In the six runs the frame
# before the start reads Y 80-100 (the app fading in) and the frame after the
# end is either the springboard at SATAVG 31.8 or black at Y 16.
read -r START END <<<"$(ffmpeg -v error -i "$RAW" -vf "scale=32:32,signalstats,metadata=print:key=lavfi.signalstats.SATAVG:file=-,metadata=print:key=lavfi.signalstats.YAVG:file=-" -f null - 2>/dev/null \
    | python3 -c "
import re, sys
# Two metadata filters each print their own frame header, so one frame's two
# values do not arrive together: collect by timestamp, decide afterwards.
t, seen = None, {}
for line in sys.stdin:
    m = re.search(r'pts_time:([0-9.]+)', line)
    if m: t = float(m.group(1)); continue
    m = re.search(r'\.(SATAVG|YAVG)=([0-9.]+)', line)
    if m and t is not None: seen.setdefault(t, {})[m.group(1)] = float(m.group(2))
app = [t for t in sorted(seen)
       if seen[t].get('SATAVG', 99) < 1 and seen[t].get('YAVG', 0) > 100]
if not app:
    sys.exit('the recording never shows the app: no unsaturated frame above Y 100')
print(app[0], app[-1])")"
echo "trimmed to the app: $START s .. $END s"
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
