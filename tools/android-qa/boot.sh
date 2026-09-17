#!/usr/bin/env bash
# Boots one of the QA AVDs headless and waits until it is usable.
#   boot.sh <avd-name> [port]
set -euo pipefail

AVD="$1"
PORT="${2:-5554}"
SERIAL="emulator-$PORT"
LOG="/work/out/out/emulator-$AVD.log"

pkill -f "qemu-system-x86_64.*-port $PORT" 2>/dev/null || true
adb start-server >/dev/null 2>&1 || true

nohup emulator -avd "$AVD" \
    -port "$PORT" \
    -no-window -no-audio -no-boot-anim -no-snapshot-save \
    -gpu swiftshader_indirect \
    -camera-back none -camera-front none \
    -wipe-data \
    > "$LOG" 2>&1 &

echo "booting $AVD on $SERIAL (log: $LOG)"
for _ in $(seq 1 180); do
    state=$(adb -s "$SERIAL" get-state 2>/dev/null || true)
    [ "$state" = "device" ] && break
    sleep 2
done
adb -s "$SERIAL" wait-for-device
for _ in $(seq 1 180); do
    [ "$(adb -s "$SERIAL" shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = "1" ] && break
    sleep 2
done
[ "$(adb -s "$SERIAL" shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = "1" ] \
    || { echo "FAILED to boot $AVD"; tail -30 "$LOG"; exit 1; }

# A deterministic device: no animations, no lock screen, no clock drift, and a
# demo status bar so captures never carry the emulator's real clock or a
# settings-gear icon (#49).
adb -s "$SERIAL" shell settings put global window_animation_scale 0
adb -s "$SERIAL" shell settings put global transition_animation_scale 0
adb -s "$SERIAL" shell settings put global animator_duration_scale 0
adb -s "$SERIAL" shell settings put global sysui_demo_allowed 1
adb -s "$SERIAL" shell settings put secure show_ime_with_hard_keyboard 0
adb -s "$SERIAL" shell svc power stayon true || true
adb -s "$SERIAL" shell wm dismiss-keyguard || true

echo "$AVD ready on $SERIAL"
adb -s "$SERIAL" shell wm size
adb -s "$SERIAL" shell wm density
