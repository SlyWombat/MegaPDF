#!/usr/bin/env bash
# Play Store screenshot capture — run inside android-emulator-runner with a
# booted emulator. Installs the debug APK, sets a clean demo status bar, and
# captures the marketing states to /tmp/shots.
#
# Eight states since #613; the list and the reasons are at the loop below.
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

# Android's own first-run lesson in immersive mode: the first time an app hides the
# system bars, SystemUI puts a teal panel over the top half of the screen — "Viewing
# full screen / To exit, swipe down from the top / GOT IT" — and dims everything
# under it. Reading mode (#507) hides the bars, so the #613 reading pose is the first
# capture in this script's history to meet it, and the first run of the new set came
# back with that panel over all three languages' lead image, in English in the French
# sets because the string is SystemUI's and the emulator's system language is English.
# The capture gate could not see it: the panel is not the posed status bar, so the
# "no status bar in this pose" check passed on a frame that was three-quarters system
# dialog.
#
# Confirming it up front is the same kind of thing as the demo status bar and the
# disabled launcher below: a first-run affordance of the device, not of the app,
# removed so the image is the app. The setting is per-device and this emulator is
# thrown away with the run.
adb shell settings put secure immersive_mode_confirmations confirmed

# MEGAPDF_LANG=fr-CA (or fr-FR) switches the app's language through Android 13's
# per-app locale (#91): the app then loads demo-fr.pdf (fr-CA) or demo-fr-FR.pdf
# (fr-FR, France's own text since #310) and the French names, and
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
# The eight Play listing slots, in the order Dave settled on 2026-10-01 (#613): signing,
# editing and reading are what people come for and lead; redaction moves back. Two changes
# to the set itself, not just to its order:
#
#   reading  replaces `viewer` at the front rather than joining it. Both are a picture of a
#            page, and this one says something as well.
#   pages    is new, and `draw` comes out to make room: Google Play takes eight phone
#            screenshots and this would have been the ninth. Draw is the secondary half of
#            signing and the `sign` shot already carries that story.
#
# Shot in listing order, and only these eight. A folder with more images in it than the
# listing has slots is how a review shot reaches a store page (the iOS set's own lesson,
# docs/app-store-listing.md § Screenshots).
for state in reading text-edit sign pages text search redact home; do
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
