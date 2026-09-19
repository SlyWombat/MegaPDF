#!/usr/bin/env bash
# inside the android-qa container: build, boot, install, run the save-after-reboot check
set -uo pipefail
TAG="${1:-before}"
cd /work/repo/android
./gradlew --no-daemon -q :app:assembleDebug > /work/out/build-$TAG.log 2>&1 || { echo "BUILD FAILED"; tail -30 /work/out/build-$TAG.log; echo "REPRO-DONE rc=99"; exit 99; }
aapt2=$(ls $ANDROID_HOME/build-tools/*/aapt2 | tail -1)
$aapt2 dump badging app/build/outputs/apk/debug/app-debug.apk | head -1
[ -d ~/.android/avd/megapdf-small.avd ] || bash /work/repo/tools/android-qa/make-avds.sh > /dev/null
mkdir -p /work/out/out
bash /work/repo/tools/android-qa/boot.sh megapdf-small 5554 || { echo "REPRO-DONE rc=98"; exit 98; }
adb -s emulator-5554 install -r app/build/outputs/apk/debug/app-debug.apk
python3 /work/repo/tools/android-qa/save_after_reboot.py --serial emulator-5554 --out /work/out/$TAG
rc=$?
adb -s emulator-5554 emu kill > /dev/null 2>&1 || true
echo "REPRO-DONE rc=$rc"
