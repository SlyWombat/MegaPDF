#!/usr/bin/env bash
# Builds the shared engine core as a native library for the .NET apps (#38, ADR-003):
#
#     tools/build-core.sh          # macOS -> core/build/osx/libmegapdf_core.dylib (universal)
#                                  # Linux -> core/build/linux-x64/libmegapdf_core.so
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
    Linux)  OUT="linux-x64" ;;
    *) echo "::error::unsupported host $(uname -s); Windows uses tools/build-core.ps1" >&2; exit 1 ;;
esac

BUILD="$ROOT/core/build/$OUT"
GEN=()
command -v ninja >/dev/null 2>&1 && GEN=(-G Ninja)
cmake -S "$ROOT/core" -B "$BUILD" -DCMAKE_BUILD_TYPE=Release "${GEN[@]+"${GEN[@]}"}"
cmake --build "$BUILD" --config Release

ls -1 "$BUILD"/libmegapdf_core.* >/dev/null
echo "built: $(ls -1 "$BUILD"/libmegapdf_core.*)"
