#!/usr/bin/env bash
# App Store screenshots from a Mac with Xcode, for one listing language: the
# same captures ios-screenshots.yml takes in CI, runnable on the in-house Mac
# so a fresh set never has to wait for a runner. Uses the app's
# `-screenshot <state>` launch mode on the two required simulators (6.9" iPhone
# and 13" iPad); docs/app-store-listing.md maps the files to listing slots.
#
# Nine listing slots since #613 (Dave, 2026-10-01): reading and the page tools lead,
# the way every other platform's re-shoot now opens, redaction moves back, and `viewer`
# — the old lead — is demoted to the review set. `draw` keeps its slot, pending Dave's
# sign-off (the App Store takes up to ten images, so the squeeze that cut it from
# Android's eight-slot Play set does not apply here); see PR #651 for the open question.
#
# Usage: tools/ios-screenshots.sh [lang] [out-dir]
#   lang     en (default), fr-CA or fr
#   out-dir  default artifacts/store/screenshots-ios/<lang>
# Needs a simulator build already in $DERIVED_DATA (default ~/dd-ios), as
# tools/ios-demo-video.sh leaves one; otherwise builds it.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LANG_TAG="${1:-en}"
OUT="${2:-$ROOT/artifacts/store/screenshots-ios/$LANG_TAG}"
DD="${DERIVED_DATA:-$HOME/dd-ios}"
APP="$DD/Build/Products/Debug-iphonesimulator/MegaPDF.app"
mkdir -p "$OUT"

if [ ! -d "$APP" ]; then
    cd "$ROOT/ios"
    [ -d Vendor/pdfium.xcframework ] || bash scripts/fetch-pdfium.sh
    xcodegen generate >/dev/null
    xcodebuild build -project MegaPDF.xcodeproj -scheme MegaPDF -sdk iphonesimulator \
        -configuration Debug -derivedDataPath "$DD" CODE_SIGNING_ALLOWED=NO -quiet 2>&1 | grep -v "ld: warning" || true
fi

# Empty for English — and an empty array under set -u is an error on macOS
# bash 3.2, hence the ${arr[@]+...} form where it is expanded.
LANG_ARGS=()
case "$LANG_TAG" in
    fr-CA) LANG_ARGS=(-AppleLanguages "(fr-CA)" -AppleLocale fr_CA) ;;
    fr)    LANG_ARGS=(-AppleLanguages "(fr)" -AppleLocale fr_FR) ;;
esac

pick_device() {
    xcrun simctl list devices available -j | python3 -c "
import json, re, sys
devs = json.load(sys.stdin)['devices']
for v in devs.values():
    for d in v:
        if re.search(sys.argv[1], d['name']):
            print(d['udid'] + '|' + d['name']); sys.exit(0)
sys.exit(1)" "$1"
}

# Settings → Multitasking & Gestures → Full Screen Apps, driven by a UI test
# (ios/MegaPDFUITests/CaptureSimulatorSetupUITests.swift). The setting sticks to
# the simulator, so this only changes something the first time.
ipad_full_screen() {
    local log
    [ -f "$ROOT/ios/MegaPDF.xcodeproj/project.pbxproj" ] || (cd "$ROOT/ios" && xcodegen generate >/dev/null)
    log=$(cd "$ROOT/ios" && xcodebuild test -project MegaPDF.xcodeproj -scheme MegaPDFDemo \
            -destination "id=$1" -derivedDataPath "$DD" CODE_SIGNING_ALLOWED=NO \
            -only-testing:MegaPDFUITests/CaptureSimulatorSetupUITests 2>&1) || true
    printf '%s\n' "$log" | grep -E "Test Case .*(passed|failed)|error:" || true
    printf '%s\n' "$log" | grep -q "testIPadRunsAppsFullScreen\]' passed" \
        || { echo "could not put $1 in Full Screen Apps" >&2; return 1; }
}

capture() {
    local pattern="$1" label="$2" picked udid name
    picked=$(pick_device "$pattern") || { echo "no simulator matches $pattern" >&2; return 1; }
    udid="${picked%%|*}"; name="${picked##*|}"
    echo "capturing $label on $name"
    # A device the previous script is still shutting down refuses to boot, and
    # under set -e that ended the English run with nothing captured. Settle it.
    xcrun simctl shutdown "$udid" >/dev/null 2>&1 || true
    sleep 5
    xcrun simctl boot "$udid" 2>/dev/null || true
    xcrun simctl bootstatus "$udid" -b >/dev/null
    # The first keyboard on a fresh simulator comes up under iOS's QuickPath tip
    # ("Swipe to type"), which is what text-edit shot in CI (#406). The in-house
    # simulators had shown it once and kept this flag; a new one has not.
    xcrun simctl spawn "$udid" defaults write com.apple.keyboard.preferences \
        DidShowContinuousPathIntroduction -bool true
    # An iPad in Windowed Apps draws a resize grabber in the corner of every app,
    # identical in every image, so no comparison sees it (capture-gate-report.md
    # §6). Put it in Full Screen Apps, through Settings, before shooting.
    case "$name" in iPad*) ipad_full_screen "$udid" ;; esac
    # First-boot banners come and go for a while after bootstatus returns.
    sleep 30
    xcrun simctl status_bar "$udid" override --time "9:41" \
        --batteryState charged --batteryLevel 100 --cellularBars 4 --wifiBars 3 || true
    # A fresh container every time: the seeded demo library only appears when the
    # store is empty, and an in-house simulator keeps whatever a previous run or
    # UI test left behind (#100).
    xcrun simctl uninstall "$udid" com.megapdf.ios 2>/dev/null || true
    xcrun simctl install "$udid" "$APP"
    shot() {   # shot <state> <dir> <suffix>
        local state="$1" dir="$2" suffix="$3"
        local outlog="$OUT/$dir/$label-$state$suffix.out.log"
        local errlog="$OUT/$dir/$label-$state$suffix.err.log"
        # `-screenshot reading` and `-screenshot pages` say, on their own stdout/stderr,
        # whether the mode they pose actually turned on — the same thing Windows'
        # Shot-Reading.ps1 asks the automation tree and Mac/Linux's App.axaml.cs prints
        # to the console (#613). `--stdout`/`--stderr` are what get that out of the
        # simulator: without them the app's own log goes to the unified log, which
        # nothing here reads. Captured for every state, not just those two, so a
        # silent failure elsewhere is just as loud.
        xcrun simctl launch --stdout="$outlog" --stderr="$errlog" "$udid" com.megapdf.ios \
            -screenshot "$state" ${LANG_ARGS[@]+${LANG_ARGS[@]+"${LANG_ARGS[@]}"}}
        # The six-page document reading/pages open is still small, but it is a cold
        # open plus (for pages) a grid of thumbnails rendering, so both get the same
        # margin every other state already had room to spare in.
        sleep 10
        xcrun simctl io "$udid" screenshot "$OUT/$dir/$label-$state$suffix.png" >/dev/null
        xcrun simctl terminate "$udid" com.megapdf.ios || true
        sleep 1
        if grep -q '::error::' "$errlog" 2>/dev/null; then
            echo "FAILED $label-$state$suffix — the state did not pose:" >&2
            grep '::error::' "$errlog" >&2
            return 1
        fi
        echo "  $label-$state$suffix: $(grep -h '^screenshot ' "$outlog" "$errlog" 2>/dev/null | tail -1)"
    }

    mkdir -p "$OUT/listing" "$OUT/review"
    xcrun simctl ui "$udid" appearance light || true
    # The nine listing slots, in listing order (#613, tools/capture-gate/stores.py's
    # "ios" profile). Each is its own launch: a state left over from the shot before
    # is the defect the Windows set was bitten by twice.
    #   reading   the agreement with the chrome gone and the bar pinned up
    #   text-edit the body-text editor open on the heading, mid-correction (#113)
    #   sign      the signature library flyout
    #   draw      the draw-a-signature pad — pending Dave's sign-off on keeping it
    #   pages     the Pages panel beside/under the document, two pages picked out
    #   text      a typed name on the blank line, nothing selected
    #   search    the find bar with a term and a hit count
    #   redact    a line marked and selected, with the chrome that takes it off
    #   home      the empty window with a recents list
    for state in reading text-edit sign draw pages text search redact home; do
        shot "$state" listing ""
    done
    # `viewer` is no longer a listing slot (reading replaced it at the front), but it
    # is still worth having for the QA inventory, so it moves here rather than away.
    shot viewer review ""
    # Everything else is for review, in its own folder. The dry run's point: a
    # folder of more images than the table has slots is how a review shot ends
    # up on a store listing (#146 §3).
    xcrun simctl ui "$udid" appearance dark || true
    for state in search sign redact; do
        shot "$state" review "-dark"
    done
    xcrun simctl ui "$udid" appearance light || true
    xcrun simctl shutdown "$udid" || true
}

capture 'iPhone .*Pro Max' iphone-6_9
capture 'iPad Pro 13' ipad-13
echo "listing slots:"; ls -la "$OUT/listing"/*.png
echo "review shots:"; ls -la "$OUT/review"/*.png
