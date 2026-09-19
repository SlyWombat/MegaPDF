#!/usr/bin/env bash
# Prints the version the Linux packages carry for an app version (#315):
#
#     tools/linux/package-version.sh 2.0.0      # -> 2.0.0, or 2.0.0-2
#
# The app version comes from the csproj. A packaging revision in
# tools/linux/PACKAGE-REVISION ("<app version> <revision>") adds a Debian-style
# "-<revision>" suffix, but only while it names this app version and is above 1, so a
# stale revision can never follow the app to its next version. dpkg orders
# 2.0.0 < 2.0.0-2 < 2.0.1, which is what makes `apt upgrade` take a revision.
set -euo pipefail
VERSION="${1:?usage: package-version.sh <app-version>}"
FILE="$(cd "$(dirname "$0")" && pwd)/PACKAGE-REVISION"
if [ -f "$FILE" ]; then
    read -r for rev < <(grep -vE '^\s*(#|$)' "$FILE" | head -1) || true
    if [ "${for:-}" = "$VERSION" ] && [[ "${rev:-}" =~ ^[0-9]+$ ]] && [ "$rev" -gt 1 ]; then
        echo "$VERSION-$rev"; exit 0
    fi
fi
echo "$VERSION"
