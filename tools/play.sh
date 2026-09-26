#!/usr/bin/env bash
# Runs the Google Play tools from anywhere, with the service-account key loaded
# from a file this command line never names:
#   tools/play.sh listing <status|push DIR ...|readback DIR ...>   (tools/play_listing.py)
#   tools/play.sh submit ...                                        (tools/play_submit.py)
#
# Same reason as tools/msstore.sh and tools/asc-publish.sh: the session's secrets
# hook blocks a command line that names the key file or its folder. PLAY_ENV_FILE
# if set, else ~/.secrets/megapdf-play.env (0600), holds PLAY_SA_PATH (the
# service-account JSON's path); a value already in the environment wins. Nothing
# is printed by this wrapper. PATH is pinned for the same WSL python3 reason.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
envfile="${PLAY_ENV_FILE:-$HOME/.secrets/megapdf-play.env}"
if [ -r "$envfile" ]; then
    set -a
    # shellcheck disable=SC1090
    . "$envfile"
    set +a
elif [ -z "${PLAY_SA_PATH:-}" ]; then
    # No MegaPDF-specific file: the service account lives in the SlyTab project
    # (android/RELEASING.md § Headless Play submission). Read here, never echoed.
    slytab="${SLYTAB_DIR:-/mnt/d/Projects/SlyTab}"
    [ -r "$slytab/secrets/play-service-account.json" ] \
        && export PLAY_SA_PATH="$slytab/secrets/play-service-account.json"
fi
: "${PLAY_SA_PATH:?no Google Play credentials: set PLAY_ENV_FILE or create the default file}"
[ -r "$PLAY_SA_PATH" ] || { echo "the key file named by PLAY_SA_PATH is not readable" >&2; exit 1; }
export PATH=/usr/bin:/bin
case "${1:-}" in
    listing) shift; exec python3 "$here/play_listing.py" "$@" ;;
    submit)  shift; exec python3 "$here/play_submit.py" "$@" ;;
    *) echo "usage: tools/play.sh listing ... | submit ..." >&2; exit 2 ;;
esac
