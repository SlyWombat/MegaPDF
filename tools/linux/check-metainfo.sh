#!/usr/bin/env bash
# What the AppStream metadata and the Flatpak manifest have to survive before anyone
# opens a pull request against flathub/flathub (#254 A2).
#
#     tools/linux/check-metainfo.sh [metainfo.xml] [manifest.yml]
#
# Three checks, and one deliberate exception.
#
#   1. appstreamcli validate --pedantic --no-net       must be clean.
#   2. flatpak-builder-lint manifest                   must be clean.
#   3. flatpak-builder-lint appstream                  must be clean *except* for
#      `screenshot-image-not-found`.
#
# The exception is the one thing CI cannot fix for itself. AppStream screenshots are
# URLs and both validators fetch them; the Linux captures are staged in this repository
# under website/megapdf/screenshots/linux/ and do not exist at their public address until
# website/deploy.py has run, which is a deliberate step under the release hold (#146).
# So this script insists that screenshot-image-not-found is the *only* thing wrong, says
# how many there are, and passes. On the day the site is deployed there are none left and
# the same run still passes, with nothing here to remember to change.
#
# That also makes the check immune to the network being down: an unreachable host gives
# the same tag on every screenshot, which is the state this already accepts.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
METAINFO="${1:-$ROOT/tools/linux/flatpak/ca.electricrv.MegaPDF.metainfo.xml}"
MANIFEST="${2:-$ROOT/tools/linux/flatpak/ca.electricrv.MegaPDF.yml}"

for f in "$METAINFO" "$MANIFEST"; do
    [ -f "$f" ] || { echo "::error::no such file: $f" >&2; exit 2; }
done

# flatpak-builder-lint ships inside org.flatpak.Builder, which is how Flathub runs it and
# how CI installs it; a distribution package is used if there is one.
if command -v flatpak-builder-lint >/dev/null 2>&1; then
    LINT=(flatpak-builder-lint)
elif flatpak info --user org.flatpak.Builder >/dev/null 2>&1 \
  || flatpak info org.flatpak.Builder >/dev/null 2>&1; then
    LINT=(flatpak run --command=flatpak-builder-lint org.flatpak.Builder)
else
    echo "::error::flatpak-builder-lint not found. Install it with:" >&2
    echo "    flatpak install --user flathub org.flatpak.Builder" >&2
    exit 2
fi

fail=0

echo "=== 1. appstreamcli validate --pedantic --no-net ==="
if appstreamcli validate --pedantic --no-net --explain "$METAINFO"; then
    echo "    clean."
else
    echo "::error::the metainfo does not validate offline — see above" >&2
    fail=1
fi

echo
echo "=== 2. flatpak-builder-lint manifest ==="
if "${LINT[@]}" manifest "$MANIFEST"; then
    echo "    clean."
else
    echo "::error::flatpak-builder-lint rejected the manifest — see above" >&2
    fail=1
fi

echo
echo "=== 3. flatpak-builder-lint appstream ==="
out=$("${LINT[@]}" appstream "$METAINFO" 2>&1)
status=$?
echo "$out"
if [ $status -eq 0 ]; then
    echo "    clean, and the screenshots resolve — the site has been deployed."
else
    # Every issue line appstreamcli prints starts with a severity letter and carries the
    # tag as its third field: "W: ca.electricrv.MegaPDF:77: screenshot-image-not-found".
    others=$(echo "$out" | grep -E '^[EWIP]: ' | awk '{print $3}' \
             | grep -v '^screenshot-image-not-found$' | sort -u)
    missing=$(echo "$out" | grep -c 'screenshot-image-not-found')
    if [ -n "$others" ]; then
        echo "::error::flatpak-builder-lint found more than the undeployed screenshots:" >&2
        echo "$others" | sed 's/^/    /' >&2
        fail=1
    elif [ "$missing" -eq 0 ]; then
        echo "::error::flatpak-builder-lint failed and said nothing this check understands" >&2
        fail=1
    else
        echo
        echo "    $missing screenshot URL(s) do not resolve yet, and nothing else is wrong."
        echo "    That is expected until website/deploy.py has published"
        echo "    website/megapdf/screenshots/linux/ to electricrv.ca — see"
        echo "    tools/Linux-Packaging.md, \"Before a submission\"."
    fi
fi

echo
if [ $fail -ne 0 ]; then
    echo "FAILED"
    exit 1
fi
echo "OK"
