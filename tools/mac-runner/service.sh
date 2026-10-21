#!/bin/bash
# The Mac mini's standing GitHub Actions runner as a LaunchAgent (#614).
#
#   bash ~/Projects/MegaPDF/tools/mac-runner/service.sh install|uninstall|status|restart
#
# Runs ON the Mac, as `claude`, after tools/mac-runner-configure.sh has registered the
# runner. See tools/mac-mini.md § "CI on this machine".
#
# Why not the runner's own `svc.sh`: its plist template sets SessionCreate, which starts
# the job in a new security session outside the GUI login session that the simulators
# and the login keychain belong to, and it loads with `launchctl load`, which from SSH
# lands in the SSH session's domain rather than gui/<uid>. This writes its own plist and
# uses `launchctl bootstrap gui/<uid>` only.
#
# macOS bash is 3.2: no `set -u` with arrays, no mapfile.
set -e

RUNNER=/Users/claude/actions-runner
HOOKS=$RUNNER/megapdf
LOGS=/Users/claude/Library/Logs/megapdf-runner
AGENTS=/Users/claude/Library/LaunchAgents
LABEL=ca.electricrv.megapdf.actions-runner
LOGLABEL=ca.electricrv.megapdf.actions-runner-logs
PLIST=$AGENTS/$LABEL.plist
LOGPLIST=$AGENTS/$LOGLABEL.plist
HERE=$(cd "$(dirname "$0")" && pwd)
DOMAIN=gui/$(id -u)

[ "$(id -un)" = claude ] || { echo "service.sh: run as claude, not $(id -un)" >&2; exit 1; }
# Existence only. The runner directory holds the registration and its keys: never print them.
[ -f "$RUNNER/.runner" ] || { echo "service.sh: no registration in $RUNNER; run tools/mac-runner-configure.sh first" >&2; exit 1; }

state() {  # label. One leading tab: the top-level keys, not the coalitions' own `state = active`.
  launchctl print "$DOMAIN/$1" 2>&1 | grep -E '^[[:space:]](state|pid|last exit code) =' || echo "  $1: not loaded"
}

load() {  # label plist
  launchctl bootout "$DOMAIN/$1" 2>/dev/null || true
  # Never `launchctl load`: from SSH it loads into the wrong domain. If bootstrap is
  # refused, stop here and report the error rather than fall back (#614).
  launchctl bootstrap "$DOMAIN" "$2"
}

install() {
  mkdir -p "$HOOKS" "$LOGS" "$AGENTS"
  # Copies, not links: a `git pull` or checkout in the clone must not change a running
  # security control. Re-run `install` to update them.
  cp "$HERE/job-started.sh" "$HERE/prune-logs.sh" "$HOOKS/"
  chmod 755 "$HOOKS/job-started.sh" "$HOOKS/prune-logs.sh"
  # As the runner's own svc.sh does: the service host runs from the runner root.
  cp "$RUNNER/bin/runsvc.sh" "$RUNNER/runsvc.sh"
  chmod u+x "$RUNNER/runsvc.sh"
  # runsvc.sh exports this as PATH. Without it the service would inherit launchd's bare
  # PATH and lose Homebrew (xcodegen).
  echo "/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin" > "$RUNNER/.path"

  cat > "$PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>$LABEL</string>
  <key>ProgramArguments</key>
  <array>
    <string>$RUNNER/runsvc.sh</string>
  </array>
  <key>WorkingDirectory</key>
  <string>$RUNNER</string>
  <key>RunAtLoad</key>
  <true/>
  <key>KeepAlive</key>
  <dict>
    <key>SuccessfulExit</key>
    <false/>
  </dict>
  <key>ThrottleInterval</key>
  <integer>30</integer>
  <key>LimitLoadToSessionType</key>
  <string>Aqua</string>
  <key>ProcessType</key>
  <string>Interactive</string>
  <key>StandardOutPath</key>
  <string>$LOGS/runner.log</string>
  <key>StandardErrorPath</key>
  <string>$LOGS/runner.log</string>
  <key>EnvironmentVariables</key>
  <dict>
    <key>ACTIONS_RUNNER_SVC</key>
    <string>1</string>
    <key>ACTIONS_RUNNER_HOOK_JOB_STARTED</key>
    <string>$HOOKS/job-started.sh</string>
  </dict>
</dict>
</plist>
EOF

  cat > "$LOGPLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>$LOGLABEL</string>
  <key>ProgramArguments</key>
  <array>
    <string>/bin/bash</string>
    <string>$HOOKS/prune-logs.sh</string>
  </array>
  <key>StartCalendarInterval</key>
  <dict>
    <key>Weekday</key>
    <integer>0</integer>
    <key>Hour</key>
    <integer>4</integer>
    <key>Minute</key>
    <integer>30</integer>
  </dict>
  <key>StandardOutPath</key>
  <string>$LOGS/prune.log</string>
</dict>
</plist>
EOF

  plutil -lint "$PLIST" "$LOGPLIST"
  load "$LABEL" "$PLIST"
  load "$LOGLABEL" "$LOGPLIST"
  state "$LABEL"
}

uninstall() {
  launchctl bootout "$DOMAIN/$LABEL" 2>/dev/null || true
  launchctl bootout "$DOMAIN/$LOGLABEL" 2>/dev/null || true
  rm -f "$PLIST" "$LOGPLIST"
  rm -rf "$HOOKS"
  echo "uninstalled. The registration in $RUNNER is untouched: removing it"
  echo "(config.sh remove) is a separate, deliberate step."
}

status() {
  echo "$LABEL:"; state "$LABEL"
  echo "$LOGLABEL:"; state "$LOGLABEL"
  # Neither log holds a credential.
  for f in "$LOGS/runner.log" "$LOGS/job-started.log"; do
    echo "--- tail $f"; [ -f "$f" ] && tail -n 5 "$f" || echo "(none yet)"
  done
}

restart() {
  launchctl kickstart -k "$DOMAIN/$LABEL"
  state "$LABEL"
}

case "${1:-}" in
  install) install ;;
  uninstall) uninstall ;;
  status) status ;;
  restart) restart ;;
  *) echo "usage: $0 install|uninstall|status|restart" >&2; exit 2 ;;
esac
