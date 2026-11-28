#!/usr/bin/env bash
# Builds the shared engine core as a native library for the .NET apps (#38, ADR-003):
#
#     tools/build-core.sh [rid]    # macOS -> core/build/osx/libmegapdf_core.dylib (universal)
#                                  # Linux -> core/build/linux-<arch>/libmegapdf_core.so
#
# On Linux rid is linux-x64 or linux-arm64 and defaults to the host's (uname -m); building
# for the other one is refused, since this builds natively and does not cross-compile (#871).
#
# MegaPDF.Core.csproj runs this itself when the artifact is missing or older than
# core/*.cpp, so `dotnet build` is normally all anyone types. Needs CMake and a C++
# toolchain (Xcode command line tools on macOS). On macOS the pdfium prebuilt must
# already be at libs/pdfium/mac-univ (tools/fetch-pdfium-mac.sh).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export PATH="/opt/homebrew/bin:/usr/local/bin:$PATH"

command -v cmake >/dev/null 2>&1 || {
    echo "::error::cmake not found. macOS: brew install cmake. Linux: apt install cmake." >&2
    exit 1
}

case "$(uname -s)" in
    Darwin) OUT="osx" ;;
    Linux)
        case "$(uname -m)" in
            x86_64)          HOST="linux-x64" ;;
            aarch64|arm64)   HOST="linux-arm64" ;;
            *) echo "::error::unsupported Linux architecture $(uname -m)" >&2; exit 1 ;;
        esac
        OUT="${1:-$HOST}"
        if [ "$OUT" != "$HOST" ]; then
            echo "::error::asked for $OUT but this host is $HOST; the core is built natively, not cross-compiled" >&2
            exit 1
        fi ;;
    *) echo "::error::unsupported host $(uname -s); Windows uses tools/build-core.ps1" >&2; exit 1 ;;
esac

BUILD="$ROOT/core/build/$OUT"
GEN=()
command -v ninja >/dev/null 2>&1 && GEN=(-G Ninja)
cmake -S "$ROOT/core" -B "$BUILD" -DCMAKE_BUILD_TYPE=Release "${GEN[@]+"${GEN[@]}"}"
cmake --build "$BUILD" --config Release

ls -1 "$BUILD"/libmegapdf_core.* >/dev/null
echo "built: $(ls -1 "$BUILD"/libmegapdf_core.*)"
