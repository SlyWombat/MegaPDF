#!/bin/bash
# Builds PDFium with MegaPDF's patch series (#119, #120).
#
# bblanchon/pdfium-binaries does the heavy lifting (depot_tools, gclient checkout,
# its own build patches, gn args, staging). This script pins that project to a known
# commit, adds our patches from tools/pdfium/patches/ to its patch step, runs the
# build for one target, and stamps VERSION with what went in.
#
#   tools/pdfium/build-pdfium.sh <work-dir> <os> <cpu> [environment]
#   tools/pdfium/build-pdfium.sh ~/pdfium-build linux x64
#   tools/pdfium/build-pdfium.sh ~/pdfium-build ios arm64 device
#
# Output: <work-dir>/staging (include/, lib/, VERSION, LICENSE, licenses/) and
# <work-dir>/pdfium-<os>[-<env>]-<cpu>.tgz, laid out like the upstream releases the
# fetch scripts already understand.
#
# Environment:
#   PDFIUM_BRANCH    Chromium branch to build (default chromium/7934, the app's pin)
#   BINARIES_COMMIT  pdfium-binaries commit to build with (default below)
#   START_STEP       resume at a pdfium-binaries step, e.g. 5 to rebuild after editing
#                    sources in <work-dir>/pdfium (patches are then not re-applied)
set -euo pipefail

if [[ $# -lt 3 ]]; then
    sed -n '2,24p' "$0"
    exit 2
fi

WORK=$1
OS=$2
CPU=$3
ENVIRONMENT=${4:-}
PDFIUM_BRANCH=${PDFIUM_BRANCH:-chromium/7934}
# The pdfium-binaries tag that built our pinned release; its own build patches match
# that Chromium branch (master's no longer apply to 7934).
BINARIES_COMMIT=${BINARIES_COMMIT:-chromium/7934}
START_STEP=${START_STEP:-0}
HERE=$(cd "$(dirname "$0")" && pwd)
PATCHES=("$HERE"/patches/*.patch)

if [[ ! -d "$WORK/.git" ]]; then
    git clone https://github.com/bblanchon/pdfium-binaries.git "$WORK"
fi
cd "$WORK"
git fetch -q origin
git checkout -q "$BINARIES_COMMIT" -- build.sh steps patches

# Our series goes in after pdfium-binaries' own patches, in file-name order.
rm -rf patches/megapdf
mkdir -p patches/megapdf
cp "${PATCHES[@]}" patches/megapdf/
if ! grep -q "megapdf" steps/03-patch.sh; then
    cat >>steps/03-patch.sh <<'EOF'

# MegaPDF patch series (tools/pdfium/patches in the MegaPDF repository).
pushd "${PDFium_SOURCE_DIR:-pdfium}"
for MEGAPDF_PATCH in "$PATCHES"/megapdf/*.patch; do
  apply_patch "$MEGAPDF_PATCH"
done
popd
EOF
fi

# pdfium-binaries writes staging/VERSION only when told the version.
export PDFium_VERSION=${PDFIUM_VERSION:-152.0.${PDFIUM_BRANCH#chromium/}.0}
# pdfium-binaries' steps normally run as separate Actions steps that pick up the
# environment and path files GitHub provides. Run in one step, build.sh only sources
# those files, so nothing in them reaches gclient: without DEPOT_TOOLS_WIN_TOOLCHAIN=0
# exported, a Windows build tries to download Google's internal Visual Studio toolchain
# and fails. Point build.sh at its own local files and export what child processes need.
export DEPOT_TOOLS_WIN_TOOLCHAIN=0
env -u GITHUB_ENV -u GITHUB_PATH ./build.sh -b "$PDFIUM_BRANCH" -g "$START_STEP" "$OS" "$CPU" ${ENVIRONMENT:+"$ENVIRONMENT"}

# What this build is: the Chromium build number plus a hash of the patch series, so a
# binary can always be traced back to the exact patches it carries.
SERIES_HASH=$(cat "${PATCHES[@]}" | sha256sum | cut -c1-12)
{
    grep -E "^(MAJOR|MINOR|BUILD|PATCH)=" staging/VERSION 2>/dev/null || true
    echo "MEGAPDF_PATCHES=${#PATCHES[@]}"
    echo "MEGAPDF_SERIES=$SERIES_HASH"
    echo "BINARIES_COMMIT=$BINARIES_COMMIT"
} >staging/VERSION.megapdf
mv staging/VERSION.megapdf staging/VERSION

# pdfium-binaries packed the staging tree before VERSION was stamped; pack it again
# under the same name (pdfium-<os>[-<env>]-<cpu>.tgz).
ARTIFACT="pdfium-$OS${ENVIRONMENT:+-$ENVIRONMENT}-$CPU.tgz"
rm -f "$ARTIFACT"
(cd staging && tar czf "../$ARTIFACT" -- *)
echo "packed $WORK/$ARTIFACT"
echo "built $OS $CPU ${ENVIRONMENT} with ${#PATCHES[@]} MegaPDF patch(es), series $SERIES_HASH"
