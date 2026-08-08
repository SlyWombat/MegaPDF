#!/usr/bin/env bash
# Fetches the pinned PDFium prebuilts (bblanchon/pdfium-binaries, same build as
# libs/pdfium/*/VERSION) and assembles ios/Vendor/pdfium.xcframework plus the
# CPdfium module headers. Vendor/ is gitignored — run this before xcodegen.
set -euo pipefail

RELEASE="chromium%2F7934"   # 152.0.7934 — keep in lockstep with the other platforms
BASE="https://github.com/bblanchon/pdfium-binaries/releases/download/${RELEASE}"
VENDOR="$(cd "$(dirname "$0")/.." && pwd)/Vendor"

if [ -d "$VENDOR/pdfium.xcframework" ]; then
    echo "pdfium.xcframework already present — skipping fetch"
    exit 0
fi

TMP="$VENDOR/tmp"
mkdir -p "$TMP"
for slice in ios-device-arm64 ios-simulator-arm64 ios-simulator-x64; do
    echo "fetching $slice..."
    curl -fsSL "$BASE/pdfium-$slice.tgz" -o "$TMP/$slice.tgz"
    mkdir -p "$TMP/$slice"
    tar xzf "$TMP/$slice.tgz" -C "$TMP/$slice"
done

libname() { ls "$TMP/$1/lib" | head -1; }
LIB="$(libname ios-device-arm64)"
echo "library file: $LIB"   # bblanchon iOS slices ship a dynamic libpdfium.dylib

# Keep the dylib extension — mixing looks static to xcodebuild — and normalize
# the install name so the embedded copy resolves via @rpath.
mkdir -p "$TMP/sim"
lipo -create \
    "$TMP/ios-simulator-arm64/lib/$LIB" \
    "$TMP/ios-simulator-x64/lib/$LIB" \
    -output "$TMP/sim/$LIB"
if [[ "$LIB" == *.dylib ]]; then
    install_name_tool -id "@rpath/$LIB" "$TMP/ios-device-arm64/lib/$LIB"
    install_name_tool -id "@rpath/$LIB" "$TMP/sim/$LIB"
    # The prebuilt declares minos = sdk = 26.0 — higher than the app's iOS 16
    # deployment target, which confuses Apple's delivery validation
    # (ITMS-90426/90429). Rewrite to minos 16.0 while keeping a current SDK
    # stamp (a 16.0 sdk field trips the minimum-SDK policy instead).
    echo "--- device dylib version info before:"
    xcrun otool -l "$TMP/ios-device-arm64/lib/$LIB" | grep -A4 -E "LC_BUILD_VERSION|LC_VERSION_MIN" | head -12 || true
    xcrun vtool -set-build-version ios 16.0 26.0 -replace \
        -output "$TMP/ios-device-arm64/lib/$LIB" "$TMP/ios-device-arm64/lib/$LIB"
    echo "--- device dylib version info after:"
    xcrun otool -l "$TMP/ios-device-arm64/lib/$LIB" | grep -A4 "LC_BUILD_VERSION" | head -8 || true
fi

xcodebuild -create-xcframework \
    -library "$TMP/ios-device-arm64/lib/$LIB" \
    -library "$TMP/sim/$LIB" \
    -output "$VENDOR/pdfium.xcframework"

mkdir -p "$VENDOR/pdfium/include"
cp -R "$TMP/ios-device-arm64/include/." "$VENDOR/pdfium/include/"
cp "$TMP/ios-device-arm64/VERSION" "$VENDOR/pdfium/VERSION" 2>/dev/null || true

cat > "$VENDOR/pdfium/include/module.modulemap" << 'MM'
module CPdfium {
    header "fpdfview.h"
    header "fpdf_annot.h"
    header "fpdf_edit.h"
    header "fpdf_formfill.h"
    header "fpdf_save.h"
    export *
}
MM

rm -rf "$TMP"
echo "done: $VENDOR/pdfium.xcframework"
