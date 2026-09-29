#!/usr/bin/env bash
# #544: wraps the instrumented gradle invocation so a crash leaves something to read.
#
# This has to be a real script file, not a multi-line `script:` block in the workflow:
# reactivecircus/android-emulator-runner runs each line of a multi-line script as its
# own separate shell invocation (visible in the log as one `[command]/usr/bin/sh -c
# <line>` per line), so a variable such as $logcat_pid set on one line is already gone
# by the next — that cost the first version of this its own green run, on `kill
# "$logcat_pid"` failing with an empty argument.
#
# Runs with the working directory the emulator-runner action was given (android/), so
# ./gradlew resolves the same way the old inline one-liner did.
set -uo pipefail

adb logcat -c
adb logcat -v threadtime >"$GITHUB_WORKSPACE/instrumented-logcat.txt" &
logcat_pid=$!

# Rootable on the "default" (non-Google) system image this job uses; needed to pull
# /data/tombstones and /data/anr below. Best-effort: a build that refuses it still runs
# the tests, just without those two files.
adb root >/dev/null 2>&1

./gradlew --no-daemon --continue :engine:connectedDebugAndroidTest :app:connectedDebugAndroidTest
status=$?

# A beat for the crash's own last lines to reach the log before the capture stops.
sleep 3
kill "$logcat_pid" 2>/dev/null
wait "$logcat_pid" 2>/dev/null

mkdir -p "$GITHUB_WORKSPACE/instrumented-diagnostics/tombstones" \
         "$GITHUB_WORKSPACE/instrumented-diagnostics/anr"
adb pull /data/tombstones "$GITHUB_WORKSPACE/instrumented-diagnostics/tombstones" >/dev/null 2>&1
adb pull /data/anr "$GITHUB_WORKSPACE/instrumented-diagnostics/anr" >/dev/null 2>&1

exit "$status"
