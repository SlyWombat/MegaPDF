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

mkdir -p /tmp/shots
for state in home viewer search sign draw text; do
    adb shell am force-stop ca.electricrv.megapdf || true
    adb shell am start -n ca.electricrv.megapdf/com.megapdf.android.MainActivity --es screenshot "$state"
    sleep 10
    demo_status_bar
    adb exec-out screencap -p > "/tmp/shots/android-$state.png"
done
ls -la /tmp/shots
