#!/usr/bin/env bash
# Checks the Linux single-instance socket for real (#348 phase 2 Part B):
#
#     tools/linux/check-single-instance.sh <MegaPDF-binary> <fixtures-dir>
#
# Builds nothing itself — point it at an already-built apphost, e.g.
# src/MegaPDF.Avalonia/bin/Release/net8.0/MegaPDF, or an installed
# ~/.local/lib/megapdf/MegaPDF. Needs a display (real X11/Wayland, or Xvfb — the
# app under test is the ordinary GUI one, not the headless self-test harness,
# because the thing being checked is Program.Main's own redirect path, which the
# self-test's CheckSingleInstanceRouting deliberately does not exercise).
#
# What this proves, each a real OS-level fact rather than a headless simulation:
#   * a first launch (with a file) starts one process and starts listening;
#   * a second launch (with a different file), while the first is still up,
#     exits quickly with status 0 and never becomes a second process — the
#     process count for the binary stays at 1 throughout;
#   * the second launch's own stdout says it handed its path to the running
#     instance, and the first launch's stdout says it received it — the two
#     sides of the same handshake, proving the socket round-trip actually
#     happened rather than the second launch merely failing to start.
#
# What it does NOT prove: that both documents render as two tabs in the one
# window on screen — this machine has no way to look at a window from outside
# without a screenshot, and --screenshot is an automation argument that
# deliberately never goes through this socket (a capture run must always get
# its own process). That half — "two paths delivered together become two
# tabs, not a replacement" — is CheckMultiFileOpen/CheckSingleInstanceRouting
# in `dotnet run ... -- --self-test`, which drive the real routing code
# headlessly and are run in the same CI step as this script.
#
# Runs under Xvfb by default, even when a real DISPLAY is already set, for a
# clean and reproducible environment — set MEGAPDF_CHECK_USE_REAL_DISPLAY=1 to
# use the existing DISPLAY/WAYLAND_DISPLAY instead. Worth knowing while reading
# a failure here: a real socket handoff (accept, read, then a background
# thread's Dispatcher.UIThread.Post) used to crash the whole process
# intermittently — Dispatcher.MainLoop itself throwing
# PlatformNotSupportedException — roughly one launch in three, on both WSLg's
# forwarded X11 and a plain Xvfb alike, until Platform/SingleInstance.cs's
# MarkDispatcherRunning gate went in; see that method's doc for the full
# account of what the race actually was and how repeated runs of this exact
# script both found it and confirmed the fix.
set -uo pipefail

BIN=${1:?usage: check-single-instance.sh <MegaPDF-binary> <fixtures-dir>}
FIXTURES=${2:?usage: check-single-instance.sh <MegaPDF-binary> <fixtures-dir>}
[ -x "$BIN" ] || { echo "::error::not an executable: $BIN" >&2; exit 1; }
[ -f "$FIXTURES/fixture.pdf" ] && [ -f "$FIXTURES/forms.pdf" ] || {
    echo "::error::fixtures not found in $FIXTURES (expected fixture.pdf and forms.pdf — run tools/gen_test_fixtures.py first)" >&2
    exit 1
}
BINNAME=$(basename "$BIN")

WORK=$(mktemp -d)
trap 'kill "$FIRST_PID" >/dev/null 2>&1 || true; rm -rf "$WORK"' EXIT

# An isolated runtime dir is load-bearing: a stray socket from a previous run
# must not make this check pass (or fail) for the wrong reason.
export XDG_RUNTIME_DIR="$WORK/runtime"
mkdir -p "$XDG_RUNTIME_DIR"
chmod 700 "$XDG_RUNTIME_DIR"

# The env var, not a re-parsed argument, survives the re-exec below and marks
# it as already done — $0/$1/$2 are re-read unchanged on the way back in, so
# BIN/FIXTURES above are still right the second time.
if [ -z "${MEGAPDF_CHECK_IN_XVFB:-}" ] && [ "${MEGAPDF_CHECK_USE_REAL_DISPLAY:-0}" != "1" ] \
   && command -v xvfb-run >/dev/null 2>&1; then
    export MEGAPDF_CHECK_IN_XVFB=1
    exec xvfb-run -a --server-args="-screen 0 1280x1024x24" "$0" "$BIN" "$FIXTURES"
fi
if [ -z "${DISPLAY:-}" ] && [ -z "${WAYLAND_DISPLAY:-}" ]; then
    echo "::error::no DISPLAY/WAYLAND_DISPLAY and no xvfb-run available" >&2
    exit 1
fi

failures=0
check() { if [ "$1" -eq 0 ]; then echo "  [PASS] $2"; else echo "  [FAIL] $2"; failures=$((failures + 1)); fi; }

echo "=== first launch (fixture.pdf), backgrounded ==="
"$BIN" "$FIXTURES/fixture.pdf" >"$WORK/first.log" 2>&1 &
FIRST_PID=$!

# Poll for the socket rather than a fixed sleep: StartListening runs after
# PrepareUserDirectories/ApplyLanguage/the JIT warms up, which is not
# instant, and a fixed sleep is either too short on a loaded CI runner or
# needlessly long everywhere else.
SOCK="$XDG_RUNTIME_DIR/megapdf.sock"
for _ in $(seq 1 100); do
    [ -S "$SOCK" ] && break
    kill -0 "$FIRST_PID" >/dev/null 2>&1 || { echo "::error::first launch exited before it started listening"; cat "$WORK/first.log" >&2; exit 1; }
    sleep 0.1
done
check "$([ -S "$SOCK" ] && echo 0 || echo 1)" "the socket exists once the first instance is up ($SOCK)"

COUNT_BEFORE=$(pgrep -c -x "$BINNAME" 2>/dev/null || echo 0)
check "$([ "$COUNT_BEFORE" -eq 1 ] && echo 0 || echo 1)" "exactly one process before the second launch (found $COUNT_BEFORE)"

echo "=== second launch (forms.pdf), foreground, must redirect and exit ==="
START_NS=$(date +%s%N)
"$BIN" "$FIXTURES/forms.pdf" >"$WORK/second.log" 2>&1
SECOND_STATUS=$?
END_NS=$(date +%s%N)
ELAPSED_MS=$(( (END_NS - START_NS) / 1000000 ))
check "$([ "$SECOND_STATUS" -eq 0 ] && echo 0 || echo 1)" "the second launch exits 0 (it exited $SECOND_STATUS)"
# Redirecting is a socket round-trip; starting a whole second Avalonia process
# (PDFium, the window, layout) is a great deal slower. 3s is generous headroom
# on a loaded CI runner while still catching "it just started its own app".
check "$([ "$ELAPSED_MS" -lt 3000 ] && echo 0 || echo 1)" "the second launch returned quickly (${ELAPSED_MS}ms), not by starting its own app"

grep -q "single-instance: handed 1 path(s) to the already-running instance" "$WORK/second.log"
check $? "the second launch's own log says it handed its path over"

# Give the first instance's accept thread + UI-thread dispatch a moment to log
# the receipt and the routing — both are asynchronous with respect to the ack
# the client already got: the accept thread logs "received" as soon as it reads
# the paths, but "routing" only prints once the UI thread's Dispatcher.Post
# actually runs the callback, which can trail behind on a busy/starting-up window.
wait_for_line() {
    for _ in $(seq 1 100); do
        grep -q "$1" "$WORK/first.log" && return 0
        sleep 0.1
    done
    return 1
}
wait_for_line "single-instance: received 1 path(s) from a redirected launch"
check $? "the first instance's log says it received the redirected path"
wait_for_line "single-instance: routing 1 path(s) from a redirected launch into tabs"
check $? "the first instance's log says it routed the path into a tab"

COUNT_AFTER=$(pgrep -c -x "$BINNAME" 2>/dev/null || echo 0)
check "$([ "$COUNT_AFTER" -eq 1 ] && echo 0 || echo 1)" "still exactly one process after the second launch (found $COUNT_AFTER)"
check "$(kill -0 "$FIRST_PID" >/dev/null 2>&1 && echo 0 || echo 1)" "and it is still the first instance (its pid is still alive)"

echo
if [ "$failures" -eq 0 ]; then
    echo "check-single-instance: PASS"
else
    echo "::error::check-single-instance: $failures check(s) failed"
fi
exit "$([ "$failures" -eq 0 ] && echo 0 || echo 1)"
