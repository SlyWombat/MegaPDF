#!/usr/bin/env bash
# The two simulators ios-ci.yml creates, booted one at a time (#681, #693, #706).
#
#   ci-simulator.sh first-boot <udid>         boot, finish the first-boot migration, shut down
#   ci-simulator.sh ready <udid> [<other>]    shut <other> down, boot <udid>, and return once
#                                             it is booted, the machine has settled, and
#                                             xcodebuild's own device discovery can see it
#
# WHY ONE AT A TIME. Measured over every hosted run of this workflow from 2026-10-17 to
# 2026-10-21 (1,026 UI-test launches, 25 failures): a test that started in the first five
# minutes of the iPhone UI lane took 105 s on average, 49 s in the next five, and 25-35 s after
# fifteen; ten of the twenty-five failures were in those first five minutes and seventeen in the
# first ten. The lane began straight after the iPad's FIRST boot — data migration and all — on a
# three-core runner, with the iPhone booted beside it, and the app launches inside those minutes
# took up to 350 s. The Mac mini, ten cores, never showed it. Two booted iOS 26 simulators were
# being asked to share a runner that has room for one, during the most expensive minutes of the
# second one's life.
#
# So the iPad gets its first boot over with at the start of the job, alone, and is shut down;
# each UI lane then runs with only its own simulator up; and before a lane starts this waits —
# for a condition, with a cap, not for a number of seconds — for the machine to have settled.
#
# WHY THE DISCOVERY CHECK (#693). Six hosted runs failed before any UI test with xcodebuild's
# "Unable to find a device matching the provided destination specifier", listing no simulators
# at all, exactly 61 s after it started looking — its own destination timeout, on a runner that
# was still busy with the iPad. `xcrun xcdevice` asks the same discovery xcodebuild does, so
# polling it here says whether xcodebuild will see the device, and logs what it saw when it
# does not; the test steps also pass a longer -destination-timeout.
set -uo pipefail

log() { printf '[ci-simulator %s] %s\n' "$(date -u +%H:%M:%S)" "$*"; }

loadavg() { sysctl -n vm.loadavg | awk '{print $2}'; }

state() {
    xcrun simctl list devices -j | python3 -c '
import json, sys
want = sys.argv[1:]
for runtime, devices in json.load(sys.stdin)["devices"].items():
    for d in devices:
        if d["udid"] in want:
            print("  %s: %s, available=%s, %s" % (d["name"], d["state"], d["isAvailable"], runtime.split(".")[-1]))
' "$@"
}

boot() {
    local udid="$1" started=$SECONDS
    xcrun simctl boot "$udid" 2>/dev/null || true
    xcrun simctl bootstatus "$udid" -b | tail -n 2
    # The flag a first keyboard would have left, so the first keyboard on a fresh simulator
    # does not come up under iOS's QuickPath tip (#406).
    xcrun simctl spawn "$udid" defaults write com.apple.keyboard.preferences \
        DidShowContinuousPathIntroduction -bool true
    log "booted $udid in $((SECONDS - started)) s"
}

# Waits, up to a cap, for the one-minute load average to fall to the number of cores. Never
# fails: it is a settle, and the line it prints is the measurement.
settle() {
    local cap="${1:-300}" cores started=$SECONDS
    cores=$(sysctl -n hw.ncpu)
    while (( SECONDS - started < cap )); do
        if awk -v l="$(loadavg)" -v c="$cores" 'BEGIN { exit !(l <= c) }'; then
            log "settled: load $(loadavg) on $cores cores after $((SECONDS - started)) s"
            return 0
        fi
        sleep 10
    done
    log "not settled after ${cap} s: load $(loadavg) on $cores cores; going ahead"
}

# Polls xcodebuild's own device discovery until it lists the device. Fails after the cap.
visible() {
    local udid="$1" cap="${2:-300}" started=$SECONDS
    while (( SECONDS - started < cap )); do
        if xcrun xcdevice list --timeout 5 2>/dev/null | grep -q "\"$udid\""; then
            log "xcodebuild's device discovery lists $udid after $((SECONDS - started)) s"
            return 0
        fi
    done
    log "xcodebuild's device discovery did NOT list $udid in ${cap} s; simctl says:"
    state "$udid"
    xcrun simctl list runtimes
    return 1
}

case "${1:-}" in
    first-boot)
        udid="$2"
        log "first boot of $udid, alone; load $(loadavg)"
        boot "$udid"
        xcrun simctl shutdown "$udid"
        state "$udid"
        ;;
    ready)
        udid="$2" other="${3:-}"
        if [[ -n "$other" ]]; then
            xcrun simctl shutdown "$other" 2>/dev/null || true
        fi
        log "readying $udid; load $(loadavg)"
        boot "$udid"
        settle 300
        visible "$udid" 300 || exit 1
        state "$udid" ${other:+"$other"}
        ;;
    *)
        echo "usage: $0 first-boot <udid> | ready <udid> [<other-udid>]" >&2
        exit 2
        ;;
esac
