#!/bin/bash
# Register the Mac mini as this repository's self-hosted GitHub Actions runner (#614).
# Runs ON the Mac. See tools/mac-mini.md § "CI on this machine" for the whole picture.
#
# The runner is registered --ephemeral, so it takes one job and then removes its own
# registration: this has to be run again before each job. Invoke it from a machine
# that has `gh` (there is deliberately no GitHub credential on the Mac):
#
#   gh api -X POST repos/SlyWombat/MegaPDF/actions/runners/registration-token --jq .token \
#     | ssh mac-mini 'bash /Users/claude/Projects/MegaPDF/tools/mac-runner-configure.sh'
#
# The credential arrives on STDIN and is handed to the runner through the environment
# variable it reads for that argument. It is never passed as a command-line argument:
# anything in argv shows up in the process table for any later command on this shared
# machine, which is how a Home Assistant token leaked on 2026-09-08.
set -euo pipefail
export PATH="/opt/homebrew/bin:$PATH"

DIR="${RUNNER_DIR:-/Users/claude/actions-runner}"
VERSION="${RUNNER_VERSION:-2.337.0}"
SHA256="5a2cd92908a93d7276a194e1de6008099f3e7946f3f8e14aa7a1a7b4a31fdec2"
TARBALL="$DIR/actions-runner-osx-arm64-$VERSION.tar.gz"

read -r CRED
[ -n "${CRED:-}" ] || { echo "no registration credential on stdin" >&2; exit 1; }

mkdir -p "$DIR"
if [ ! -x "$DIR/config.sh" ]; then
  if [ ! -f "$TARBALL" ]; then
    curl -fsSL -o "$TARBALL" \
      "https://github.com/actions/runner/releases/download/v$VERSION/actions-runner-osx-arm64-$VERSION.tar.gz"
  fi
  # Checked rather than trusted: this tarball becomes a process with full access to
  # the `claude` account.
  echo "$SHA256  $TARBALL" | shasum -a 256 -c -
  tar xzf "$TARBALL" -C "$DIR"
fi

cd "$DIR"

# --ephemeral   one job, then the registration is gone. A compromised job cannot
#               persist into the next one.
# --labels      self-hosted, macOS and ARM64 are added automatically; megapdf-ios is
#               the one ios-ci.yml asks for, so nothing else can drift onto this
#               machine by accident.
# --work        _work under the runner's own directory, nowhere near the clones in
#               ~/Projects and ~/mp-*.
ACTIONS_RUNNER_INPUT_TOKEN="$CRED" ./config.sh \
  --unattended \
  --replace \
  --url "https://github.com/SlyWombat/MegaPDF" \
  --name "mac-mini-megapdf" \
  --labels "megapdf-ios" \
  --work "_work" \
  --ephemeral
unset CRED

cat <<'EOF'

CONFIGURED. Now start it (it will take one job and exit):

  cd /Users/claude/actions-runner && nohup ./run.sh >> ~/runner.log 2>&1 &
EOF
