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
    # `fully true` matters: without it SystemUI draws the "no internet" exclamation
    # over the Wi-Fi icon, because the emulator has no validated connection — and
    # that badge was in all 24 shots of the 2026-09-17 dry run (#146).
    adb shell am broadcast -a com.android.systemui.demo -e command network -e wifi show -e level 4 -e fully true || true
    adb shell am broadcast -a com.android.systemui.demo -e command network -e mobile hide || true
    adb shell am broadcast -a com.android.systemui.demo -e command notifications -e visible false || true
}
demo_status_bar
# The first capture of a run has come out with the real clock even so (a
# fr-CA run, 2026-09-10): assert once more after SystemUI has had a moment.
sleep 5
demo_status_bar

# On a large screen the launcher draws a taskbar across the bottom of every app,
# carrying whatever the device happens to have pinned — on the QA tablet that was
# Chrome, Photos, the emulator's own app and a camera — with the navigation buttons
# beside them. None of it is ours and all of it was in the frame. Disabling the
# launcher for the run removes the taskbar and the buttons together, and the app's
# own bottom bar then runs to the edge exactly as it does on a phone.
#
# Put back on the way out, however this exits: a device with no launcher has no Home
# to return to. Harmless on a phone, where there is no taskbar to remove — the
# captures come out byte-identical either way (#146).
LAUNCHER=com.android.launcher3
restore_launcher() {
    adb shell pm enable "$LAUNCHER" > /dev/null 2>&1 || true
}
if adb shell pm disable-user --user 0 "$LAUNCHER" > /dev/null 2>&1; then
    trap restore_launcher EXIT INT TERM
    sleep 3
fi

OUT="/tmp/shots/$LANG_TAG"
mkdir -p "$OUT"
MISSED=""
for state in home viewer search sign draw text text-edit redact; do
    adb shell am force-stop ca.electricrv.megapdf || true
    # The pose's verdict on itself, in the app's own log: cleared before the launch and read
    # after the capture, so anything found can only have come from this state. This is the
    # `::error::` convention the Mac and Linux scripts read from stdout, and it is here
    # because a pose that does not fire has nothing to say: the fr-CA `redact` shot of
    # 2026-09-20 came out with no mark on the page at all, twice, and the run was green.
    adb logcat -c || true
    adb shell am start -n ca.electricrv.megapdf/com.megapdf.android.MainActivity --es screenshot "$state"
    sleep 10
    demo_status_bar
    adb exec-out screencap -p > "$OUT/android-$state.png"
    if adb logcat -d -s megapdf-screenshot:E 2>/dev/null | grep -q '::error::'; then
        echo "FAILED $state — the pose reported an error:" >&2
        adb logcat -d -s megapdf-screenshot:E | grep '::error::' >&2
        MISSED="$MISSED $state"
    fi
done
ls -la "$OUT"

# The images are still uploaded — a run that fails after the shots is more use than one
# that dies at the first bad state — but the step goes red, so the set cannot be taken
# for finished. Reviewing the folder means reading which states missed, above.
if [ -n "$MISSED" ]; then
    echo "the $LANG_TAG set is not ready:$MISSED" >&2
    exit 1
fi
