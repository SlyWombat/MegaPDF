#!/usr/bin/env bash
# Play Store screenshot capture — run inside android-emulator-runner with a
# booted emulator. Installs the debug APK, sets a clean demo status bar, and
# captures the marketing states to /tmp/shots.
set -euo pipefail

adb install app/build/outputs/apk/debug/app-debug.apk
adb shell settings put global sysui_demo_allowed 1
adb shell am broadcast -a com.android.systemui.demo -e command enter || true
adb shell am broadcast -a com.android.systemui.demo -e command clock -e hhmm 0941 || true
adb shell am broadcast -a com.android.systemui.demo -e command battery -e level 100 -e plugged false || true
adb shell am broadcast -a com.android.systemui.demo -e command network -e wifi show -e level 4 || true
adb shell am broadcast -a com.android.systemui.demo -e command notifications -e visible false || true

mkdir -p /tmp/shots
for state in home viewer search sign draw text; do
    adb shell am force-stop ca.electricrv.megapdf || true
    adb shell am start -n ca.electricrv.megapdf/com.megapdf.android.MainActivity --es screenshot "$state"
    sleep 10
    adb exec-out screencap -p > "/tmp/shots/android-$state.png"
done
ls -la /tmp/shots
