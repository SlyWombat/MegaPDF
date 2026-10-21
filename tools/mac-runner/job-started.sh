#!/bin/bash
# Runs before every job this runner takes (ACTIONS_RUNNER_HOOK_JOB_STARTED, #614).
# A non-zero exit fails the job before its first step. This is the fork guard that a
# pull request cannot edit: ios-ci.yml's runs-on expression lives in the PR's own copy
# of the workflow, this file lives on the machine.
set -u
REPO="SlyWombat/MegaPDF"
LOG="${MEGAPDF_RUNNER_LOG_DIR:-$HOME/Library/Logs/megapdf-runner}/job-started.log"
say() { mkdir -p "$(dirname "$LOG")"; printf '%s %s run=%s event=%s workflow=%s\n' \
          "$(date -u +%FT%TZ)" "$1" "${GITHUB_RUN_ID:-?}" "${GITHUB_EVENT_NAME:-?}" "${GITHUB_WORKFLOW_REF:-?}" >> "$LOG"; }
refuse() { say "REFUSED($1)"; echo "megapdf runner refused: $1"; exit 1; }

[ "${GITHUB_REPOSITORY:-}" = "$REPO" ] || refuse "repository ${GITHUB_REPOSITORY:-unset}"
case "${GITHUB_WORKFLOW_REF:-}" in
  "$REPO/.github/workflows/ios-ci.yml@"*) ;;
  *) refuse "workflow ${GITHUB_WORKFLOW_REF:-unset} is not ios-ci.yml" ;;
esac
case "${GITHUB_EVENT_NAME:-}" in
  push|workflow_dispatch) ;;
  pull_request)
    /usr/bin/python3 -I -c '
import json, sys
ev = json.load(open(sys.argv[1]))
repo = ((ev.get("pull_request") or {}).get("head") or {}).get("repo") or {}
sys.exit(0 if repo.get("full_name") == sys.argv[2] and repo.get("fork") is False else 1)
' "${GITHUB_EVENT_PATH:-/nonexistent}" "$REPO" || refuse "pull request head is not $REPO (a fork, or a deleted fork)" ;;
  *) refuse "event ${GITHUB_EVENT_NAME:-unset}" ;;
esac
say "allowed"
exit 0
