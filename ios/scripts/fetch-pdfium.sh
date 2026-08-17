#!/usr/bin/env bash
# Fetches the pinned PDFium prebuilts (bblanchon/pdfium-binaries, same build as
# libs/pdfium/*/VERSION) and assembles ios/Vendor/pdfium.xcframework plus the
# CPdfium module headers. Vendor/ is gitignored — run this before xcodegen.
set -euo pipefail

RELEASE="chromium%2F7934"   # 152.0.7934 — keep in lockstep with the other platforms
BASE="https://github.com/bblanchon/pdfium-binaries/releases/download/${RELEASE}"
VENDOR="$(cd "$(dirname "$0")/.." && pwd)/Vendor"

# The modulemap is rewritten even when the xcframework is cached (CI restores
# Vendor/ from a version-keyed cache), so header-list changes take effect
# without a cache-key bump.
write_modulemap() {
    mkdir -p "$VENDOR/pdfium/include"
    cat > "$VENDOR/pdfium/include/module.modulemap" << 'MM'
module CPdfium {
    header "fpdfview.h"
    header "fpdf_annot.h"
    header "fpdf_edit.h"
    header "fpdf_formfill.h"
    header "fpdf_save.h"
    header "fpdf_text.h"
    header "fpdf_transformpage.h"
    export *
}
MM
}

if [ -d "$VENDOR/pdfium.xcframework" ]; then
    echo "pdfium.xcframework already present — skipping fetch, refreshing modulemap"
    write_modulemap
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

mkdir -p "$TMP/sim"
lipo -create \
    "$TMP/ios-simulator-arm64/lib/$LIB" \
    "$TMP/ios-simulator-x64/lib/$LIB" \
    -output "$TMP/sim/$LIB"

# Apple's delivery validation only tolerates libswift* as bare dylibs in an
# app's Frameworks folder; any other bare dylib shunts the package into the
# legacy Swift-app codepath and draws ITMS-90426/90429 boilerplate (#24).
# Package PDFium as a proper framework bundle instead. Also normalize the
# prebuilt's minos (shipped 26.0, above the app's target).
make_framework() {
    local srclib="$1" outdir="$2" patch_version="$3"
    mkdir -p "$outdir/pdfium.framework"
    cp "$srclib" "$outdir/pdfium.framework/pdfium"
    install_name_tool -id "@rpath/pdfium.framework/pdfium" "$outdir/pdfium.framework/pdfium"
    if [ "$patch_version" = "yes" ]; then
        xcrun vtool -set-build-version ios 16.0 26.0 -replace \
            -output "$outdir/pdfium.framework/pdfium" "$outdir/pdfium.framework/pdfium"
    fi
    cat > "$outdir/pdfium.framework/Info.plist" << 'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key><string>en</string>
    <key>CFBundleExecutable</key><string>pdfium</string>
    <key>CFBundleIdentifier</key><string>ca.electricrv.pdfium</string>
    <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
    <key>CFBundleName</key><string>pdfium</string>
    <key>CFBundlePackageType</key><string>FMWK</string>
    <key>CFBundleShortVersionString</key><string>152.0.7934</string>
    <key>CFBundleVersion</key><string>7934</string>
    <key>MinimumOSVersion</key><string>16.0</string>
</dict>
</plist>
PLIST
}

make_framework "$TMP/ios-device-arm64/lib/$LIB" "$TMP/fw-device" yes
make_framework "$TMP/sim/$LIB" "$TMP/fw-sim" no
echo "--- device framework binary version info:"
xcrun otool -l "$TMP/fw-device/pdfium.framework/pdfium" | grep -A4 "LC_BUILD_VERSION" | head -8 || true

xcodebuild -create-xcframework \
    -framework "$TMP/fw-device/pdfium.framework" \
    -framework "$TMP/fw-sim/pdfium.framework" \
    -output "$VENDOR/pdfium.xcframework"

mkdir -p "$VENDOR/pdfium/include"
cp -R "$TMP/ios-device-arm64/include/." "$VENDOR/pdfium/include/"
cp "$TMP/ios-device-arm64/VERSION" "$VENDOR/pdfium/VERSION" 2>/dev/null || true

write_modulemap

rm -rf "$TMP"
echo "done: $VENDOR/pdfium.xcframework"
