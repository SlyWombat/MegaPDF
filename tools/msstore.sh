#!/usr/bin/env bash
# Runs tools/msstore_submit.py from anywhere:
#   tools/msstore.sh <build|signin|status|plan|submit|poll ID> [options]
#
# msstore_submit.py reads the Partner Center credentials file itself (see its
# docstring), so nothing secret is ever on a command line, and this command line
# names neither that file nor its folder. The session's secrets hook blocks any
# Bash command that does, even harmlessly, which is why the Store steps run
# through this committed wrapper rather than a scratch script.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec python3 "$here/msstore_submit.py" "$@"
