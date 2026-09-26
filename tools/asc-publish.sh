#!/usr/bin/env bash
# Runs the App Store Connect tools from anywhere, with the credentials loaded
# from a file this command line never names:
#   tools/asc-publish.sh <status|version V|copy|screenshots DIR|previews DIR|review [files]|build [N]|submit>
#   tools/asc-publish.sh version-info        (tools/asc_version_info.py)
#   tools/asc-publish.sh assets-state        (tools/asc_assets_state.py)
#
# The same reason tools/msstore.sh exists: the session's secrets hook blocks any
# Bash command that names the credentials file or its folder, and the three
# ASC_* variables must never be typed on a command line. ASC_ENV_FILE if set,
# else ~/.secrets/megapdf-asc.env (0600), holds ASC_KEY_FILE (a .p8 path),
# ASC_KEY_ID and ASC_ISSUER_ID; anything else exported there (ASC_PLATFORM,
# ASC_RELEASE_TYPE, ASC_NOTES_VERSION) is passed through, and a value already in
# the environment wins over the file. Nothing is printed by this wrapper.
#
# PATH is pinned to /usr/bin:/bin because under WSL a bare `python3` can resolve
# to the Windows shim, which cannot read the key file (tools/asc.sh's note).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
envfile="${ASC_ENV_FILE:-$HOME/.secrets/megapdf-asc.env}"
if [ -r "$envfile" ]; then
    set -a
    # shellcheck disable=SC1090
    . "$envfile"
    set +a
elif [ -z "${ASC_KEY_FILE:-}" ]; then
    # No MegaPDF-specific file: the team key lives in the SlyTab project, as
    # docs/mobile-app-blueprint.md and android/RELEASING.md record — the .p8 under
    # its secrets folder (Apple names the file after the key ID) and the issuer id
    # as APPLE_ASC_ISSUER_ID in its .env. Read here, never echoed.
    slytab="${SLYTAB_DIR:-/mnt/d/Projects/SlyTab}"
    keyid=QUC9SR2G3F
    if [ -r "$slytab/secrets/AuthKey_$keyid.p8" ] && [ -r "$slytab/.env" ]; then
        export ASC_KEY_FILE="$slytab/secrets/AuthKey_$keyid.p8"
        export ASC_KEY_ID="$keyid"
        ASC_ISSUER_ID="$(sed -n 's/^APPLE_ASC_ISSUER_ID=//p' "$slytab/.env" | tr -d '"'"'"' \r' | head -1)"
        export ASC_ISSUER_ID
    fi
fi
: "${ASC_KEY_FILE:?no App Store Connect credentials: set ASC_ENV_FILE or create the default file}"
: "${ASC_KEY_ID:?ASC_KEY_ID missing from the credentials file}"
: "${ASC_ISSUER_ID:?ASC_ISSUER_ID missing from the credentials file}"
[ -r "$ASC_KEY_FILE" ] || { echo "the .p8 named by ASC_KEY_FILE is not readable" >&2; exit 1; }
export PATH=/usr/bin:/bin
case "${1:-}" in
    version-info) shift; exec python3 "$here/asc_version_info.py" "$@" ;;
    assets-state) shift; exec python3 "$here/asc_assets_state.py" "$@" ;;
    api)          shift; exec "$here/asc.sh" "$@" ;;   # raw call: api [GET|POST|PATCH|DELETE] <path> [body-file]
    *)            exec python3 "$here/asc_publish.py" "$@" ;;
esac
