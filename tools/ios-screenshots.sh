#!/usr/bin/env bash
# App Store screenshots from a Mac with Xcode, for one listing language: the
# same captures ios-screenshots.yml takes in CI, runnable on the in-house Mac
# so a fresh set never has to wait for a runner. Uses the app's
# `-screenshot <state>` launch mode on the two required simulators (6.9" iPhone
# and 13" iPad); docs/app-store-listing.md maps the files to listing slots.
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
    # First-boot banners come and go for a while after bootstatus returns.
    sleep 30
    xcrun simctl status_bar "$udid" override --time "9:41" \
        --batteryState charged --batteryLevel 100 --cellularBars 4 --wifiBars 3 || true
    xcrun simctl install "$udid" "$APP"
    xcrun simctl ui "$udid" appearance light || true
    for state in home viewer search sign draw text; do
        xcrun simctl launch "$udid" com.megapdf.ios -screenshot "$state" ${LANG_ARGS[@]+${LANG_ARGS[@]+"${LANG_ARGS[@]}"}} >/dev/null
        sleep 8
        xcrun simctl io "$udid" screenshot "$OUT/$label-$state.png" >/dev/null
        xcrun simctl terminate "$udid" com.megapdf.ios || true
        sleep 1
    done
    xcrun simctl ui "$udid" appearance dark || true
    for state in search sign; do
        xcrun simctl launch "$udid" com.megapdf.ios -screenshot "$state" ${LANG_ARGS[@]+${LANG_ARGS[@]+"${LANG_ARGS[@]}"}} >/dev/null
        sleep 8
        xcrun simctl io "$udid" screenshot "$OUT/$label-$state-dark.png" >/dev/null
        xcrun simctl terminate "$udid" com.megapdf.ios || true
        sleep 1
    done
    xcrun simctl ui "$udid" appearance light || true
    xcrun simctl shutdown "$udid" || true
}

capture 'iPhone .*Pro Max' iphone-6_9
capture 'iPad Pro 13' ipad-13
ls -la "$OUT"
