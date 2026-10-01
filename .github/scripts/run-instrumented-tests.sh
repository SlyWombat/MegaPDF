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

# Rootable on the "default" (non-Google) system image this job uses; needed to pull
# /data/tombstones and /data/anr below. Best-effort: a build that refuses it still runs
# the tests, just without those two files.
#
# #611: this has to happen BEFORE the logcat capture starts, not after. `adb root` restarts
# adbd on the device, which tears down every connection it was serving — including the
# `adb logcat` started on the line above it, which then exits with nothing written. That is
# why four of the six #611 failures uploaded a 0-byte instrumented-logcat.txt and only the
# two that happened to lose the race uploaded a usable one: the diagnostics #545 added were
# absent exactly when they were needed. `wait-for-device` is what makes the restart safe to
# follow; without it the logcat below can attach to the dying adbd instead.
adb root >/dev/null 2>&1
adb wait-for-device

adb logcat -c
adb logcat -v threadtime >"$GITHUB_WORKSPACE/instrumented-logcat.txt" &
logcat_pid=$!

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
