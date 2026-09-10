#!/usr/bin/env bash
# Play Store screenshot capture — run inside android-emulator-runner with a
# booted emulator. Installs the debug APK, sets a clean demo status bar, and
# captures the marketing states to /tmp/shots.
set -euo pipefail

adb install app/build/outputs/apk/debug/app-debug.apk

# SystemUI drops demo commands sent before it has finished starting, and every
# command here is best-effort, so a race produced green runs whose screenshots
# carried the emulator's real clock and a settings-gear icon (#49). Wait for the
# device to say it is up first...
adb wait-for-device
for _ in $(seq 1 60); do
    [ "$(adb shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = "1" ] && break
    sleep 2
done

adb shell settings put global sysui_demo_allowed 1

# MEGAPDF_LANG=fr-CA (or fr-FR) switches the app's language through Android 13's
# per-app locale (#91): the app then loads demo-fr.pdf and the French names, and
# every label is French. The system UI stays in the emulator's language, which
# is fine — the listing crops to the app. Unset or "en" leaves the default.
LANG_TAG="${MEGAPDF_LANG:-en}"
if [ "$LANG_TAG" != "en" ]; then
    adb shell cmd locale set-app-locales ca.electricrv.megapdf --user 0 --locales "$LANG_TAG"
    adb shell cmd locale get-app-locales ca.electricrv.megapdf --user 0 || true
fi

# ...and re-assert the demo status bar before every capture rather than once at
# the start. The commands are idempotent and cost nothing, and by the time a
# screenshot is taken SystemUI is certainly running — which is what actually
# makes this deterministic. The initial wait only narrows the window.
demo_status_bar() {
    adb shell am broadcast -a com.android.systemui.demo -e command enter || true
    adb shell am broadcast -a com.android.systemui.demo -e command clock -e hhmm 0941 || true
    adb shell am broadcast -a com.android.systemui.demo -e command battery -e level 100 -e plugged false || true
    adb shell am broadcast -a com.android.systemui.demo -e command network -e wifi show -e level 4 || true
    adb shell am broadcast -a com.android.systemui.demo -e command notifications -e visible false || true
}
demo_status_bar

OUT="/tmp/shots/$LANG_TAG"
mkdir -p "$OUT"
for state in home viewer search sign draw text; do
    adb shell am force-stop ca.electricrv.megapdf || true
    adb shell am start -n ca.electricrv.megapdf/com.megapdf.android.MainActivity --es screenshot "$state"
    sleep 10
    demo_status_bar
    adb exec-out screencap -p > "$OUT/android-$state.png"
done
ls -la "$OUT"
