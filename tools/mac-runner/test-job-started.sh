#!/bin/bash
# The Mac runner's job-started hook (#614), tested against the events it must allow and
# the ones it must refuse. Runs in CI (.github/workflows/mac-runner-guard.yml, ubuntu) and
# by hand on the Mac against the INSTALLED copy:
#
#   bash tools/mac-runner/test-job-started.sh                                  # in-tree hook
#   bash ~/Projects/MegaPDF/tools/mac-runner/test-job-started.sh ~/actions-runner/megapdf/job-started.sh
#
# Every case runs under `env -i`, so a GITHUB_* variable inherited from a real CI job can
# never leak into a case and make it pass by accident.
HERE=$(cd "$(dirname "$0")" && pwd)
HOOK=${1:-$HERE/job-started.sh}
[ -f "$HOOK" ] || { echo "no hook at $HOOK" >&2; exit 2; }

T=$(mktemp -d)
trap 'rm -rf "$T"' EXIT

REPO=SlyWombat/MegaPDF
IOS="$REPO/.github/workflows/ios-ci.yml"
pr_event() {  # file, head.repo JSON
  printf '{"pull_request":{"number":1,"head":{"repo":%s}}}\n' "$2" > "$T/$1"
}
pr_event same.json    '{"full_name":"SlyWombat/MegaPDF","fork":false}'
pr_event fork.json    '{"full_name":"someone/MegaPDF","fork":true}'
pr_event deleted.json 'null'
pr_event liar.json    '{"full_name":"someone/MegaPDF","fork":false}'
cp "$T/same.json" "$T/other-workflow.json"
cp "$T/same.json" "$T/target.json"
printf '{"ref":"refs/heads/main"}\n' > "$T/push.json"

FAILS=0
check() {  # expected-exit, label, then VAR=value ... for env -i
  local want=$1 label=$2; shift 2
  env -i PATH="/usr/bin:/bin" HOME="$T" MEGAPDF_RUNNER_LOG_DIR="$T/log" "$@" \
    /bin/bash "$HOOK" > "$T/out" 2>&1
  local got=$?
  if [ "$got" = "$want" ]; then echo "PASS  $label (exit $got)"
  else echo "FAIL  $label: expected exit $want, got $got: $(head -1 "$T/out")"; FAILS=$((FAILS+1)); fi
}

check 0 "same-repo pull request" GITHUB_REPOSITORY=$REPO GITHUB_EVENT_NAME=pull_request \
  GITHUB_WORKFLOW_REF="$IOS@refs/pull/1/merge" GITHUB_EVENT_PATH="$T/same.json"
check 1 "fork pull request" GITHUB_REPOSITORY=$REPO GITHUB_EVENT_NAME=pull_request \
  GITHUB_WORKFLOW_REF="$IOS@refs/pull/1/merge" GITHUB_EVENT_PATH="$T/fork.json"
check 1 "deleted fork (head.repo null)" GITHUB_REPOSITORY=$REPO GITHUB_EVENT_NAME=pull_request \
  GITHUB_WORKFLOW_REF="$IOS@refs/pull/1/merge" GITHUB_EVENT_PATH="$T/deleted.json"
check 1 "fork lying about fork:false" GITHUB_REPOSITORY=$REPO GITHUB_EVENT_NAME=pull_request \
  GITHUB_WORKFLOW_REF="$IOS@refs/pull/1/merge" GITHUB_EVENT_PATH="$T/liar.json"
check 1 "another workflow" GITHUB_REPOSITORY=$REPO GITHUB_EVENT_NAME=pull_request \
  GITHUB_WORKFLOW_REF="$REPO/.github/workflows/ci.yml@refs/pull/1/merge" GITHUB_EVENT_PATH="$T/other-workflow.json"
check 0 "push to main" GITHUB_REPOSITORY=$REPO GITHUB_EVENT_NAME=push \
  GITHUB_WORKFLOW_REF="$IOS@refs/heads/main" GITHUB_EVENT_PATH="$T/push.json"
check 1 "pull_request_target" GITHUB_REPOSITORY=$REPO GITHUB_EVENT_NAME=pull_request_target \
  GITHUB_WORKFLOW_REF="$IOS@refs/heads/main" GITHUB_EVENT_PATH="$T/target.json"
check 1 "every GITHUB_* unset"

if [ "$FAILS" -gt 0 ]; then echo "$FAILS case(s) FAILED"; exit 1; fi
echo "all cases passed"
