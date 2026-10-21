#!/bin/bash
# Weekly (ca.electricrv.megapdf.actions-runner-logs, #614): the runner never prunes its own
# _diag, and launchd never rotates a StandardOutPath.
LOGS=/Users/claude/Library/Logs/megapdf-runner
find /Users/claude/actions-runner/_diag -type f -name '*.log' -mtime +14 -delete 2>/dev/null
for f in "$LOGS/runner.log" "$LOGS/job-started.log"; do
  [ -f "$f" ] && [ "$(stat -f %z "$f")" -gt 10485760 ] && cp "$f" "$f.1" && : > "$f"
done
echo "$(date -u +%FT%TZ) pruned"
