#!/usr/bin/env bash
# Builds MegaPDF.app for macOS from src/MegaPDF.Avalonia (ADR-002 Option B).
# Must run ON macOS: codesign is macOS-only, and the .NET apphost for an osx-*
# RID is produced there.
#
# Usage: tools/build-macos-app.sh [rid] [out-dir]
#   rid     osx-arm64 (default) or osx-x64
#   out-dir defaults to artifacts/macos
#
# Signing here is AD-HOC (`--sign -`), which is enough to run locally and on a
# machine that trusts it, and NOT enough to distribute: that needs a Developer ID
# Application certificate plus notarytool stapling (ADR-002 §7).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:-osx-arm64}"
OUT="${2:-$ROOT/artifacts/macos}"
PROJECT="$ROOT/src/MegaPDF.Avalonia/MegaPDF.Avalonia.csproj"
APP="$OUT/MegaPDF.app"

case "$RID" in
    osx-arm64|osx-x64) ;;
    *) echo "::error::unsupported rid '$RID' (expected osx-arm64 or osx-x64)" >&2; exit 1 ;;
esac

if [ "$(uname -s)" != "Darwin" ]; then
    echo "::error::this script must run on macOS (codesign and the osx apphost are macOS-only)" >&2
    exit 1
fi

VERSION="$(grep -oE '<Version>[^<]+</Version>' "$PROJECT" | head -1 | sed 's/<[^>]*>//g')"
VERSION="${VERSION:-0.1.0}"
echo "building MegaPDF $VERSION for $RID"

# PDFium must be present before publish: MegaPDF.Core.csproj copies it from
# libs/pdfium/mac-univ when building on macOS, and silently omits it otherwise.
"$ROOT/tools/fetch-pdfium-mac.sh"

rm -rf "$APP"
PUBLISH="$(mktemp -d)"
trap 'rm -rf "$PUBLISH"' EXIT

# PublishSingleFile is what makes this bundle signable, not just tidier.
# codesign treats EVERY loose file in Contents/MacOS as code it must account
# for — .pdb, then MegaPDF.runtimeconfig.json, then the next one. A JSON file
# cannot carry a signature, so deleting offenders one at a time never converges.
# Single-file embeds the managed assemblies and the runtime config into the
# apphost, leaving only signable Mach-O binaries beside it: the executable and
# the native .dylibs. DebugType=none keeps symbols out for the same reason.
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false \
    -o "$PUBLISH"

if [ ! -f "$PUBLISH/libpdfium.dylib" ]; then
    echo "::error::libpdfium.dylib is not in the publish output — the Core copy item did not fire." >&2
    exit 1
fi

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH/." "$APP/Contents/MacOS/"
# Belt and braces: publish settings above should mean there are none, but a
# stray .pdb is the difference between a signable bundle and a failed build.
find "$APP/Contents/MacOS" -name '*.pdb' -delete

cat > "$APP/Contents/Info.plist" << PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>MegaPDF</string>
    <key>CFBundleDisplayName</key><string>MegaPDF</string>
    <key>CFBundleIdentifier</key><string>ca.electricrv.megapdf</string>
    <key>CFBundleExecutable</key><string>MegaPDF</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSHumanReadableCopyright</key><string>Electric RV</string>
    <!-- Opening a PDF from Finder is the whole point of the app; without this the
         Open With menu never lists it. Viewer, not Editor: MegaPDF does not claim
         to own .pdf, it offers to open one. -->
    <key>CFBundleDocumentTypes</key>
    <array>
        <dict>
            <key>CFBundleTypeName</key><string>PDF document</string>
            <key>CFBundleTypeRole</key><string>Viewer</string>
            <key>LSHandlerRank</key><string>Alternate</string>
            <key>LSItemContentTypes</key><array><string>com.adobe.pdf</string></array>
        </dict>
    </array>
</dict>
PLIST
echo '</plist>' >> "$APP/Contents/Info.plist"

# Inside-out signing. The ADR-002 spike established that a .NET bundle cannot be
# signed in one call: codesign rejects the bundle with "code object is not signed
# at all / In subcomponent: <some>.dll" until every nested binary is signed first.
echo "bundle contents before signing:"
ls -1 "$APP/Contents/MacOS"

# Native libraries stay outside the single-file apphost and each needs its own
# signature before the bundle can seal them.
echo "signing nested binaries..."
find "$APP/Contents/MacOS" \( -name '*.dylib' -o -name '*.so' \) -print0 \
    | xargs -0 -n1 codesign --force --timestamp=none --sign -
# Then the bundle — and ONLY the bundle. Signing Contents/MacOS/MegaPDF directly
# makes codesign treat that directory as the bundle root and demand a signature
# for every sibling file in it, including MegaPDF.runtimeconfig.json, which is
# not code and cannot have one. Signing the .app instead lets codesign sign the
# main executable itself and seal everything else as resources, which is what
# CodeResources is for.
# MACOS_ENTITLEMENTS opts the bundle into the App Sandbox (Mac App Store
# requires it). Off by default so the plain build keeps working while the
# sandbox is still being proven out.
SIGN_ARGS=(--force --timestamp=none --sign -)
if [ -n "${MACOS_ENTITLEMENTS:-}" ]; then
    [ -f "$MACOS_ENTITLEMENTS" ] || { echo "::error::entitlements file not found: $MACOS_ENTITLEMENTS" >&2; exit 1; }
    echo "signing with entitlements: $MACOS_ENTITLEMENTS"
    SIGN_ARGS+=(--entitlements "$MACOS_ENTITLEMENTS")
fi
codesign "${SIGN_ARGS[@]}" "$APP"

echo "--- entitlements actually embedded in the signature:"
codesign -d --entitlements - --xml "$APP" 2>/dev/null | plutil -convert xml1 -o - - 2>/dev/null \
    || codesign -d --entitlements - "$APP" 2>&1 | head -20 \
    || echo "(none)"

echo "--- verify:"
codesign --verify --deep --verbose=2 "$APP" 2>&1 | head -10 || echo "::warning::ad-hoc verification reported problems (expected until Developer ID signing lands)"

echo "done: $APP"
du -sh "$APP"
