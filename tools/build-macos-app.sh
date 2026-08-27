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

dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false -o "$PUBLISH"

if [ ! -f "$PUBLISH/libpdfium.dylib" ]; then
    echo "::error::libpdfium.dylib is not in the publish output — the Core copy item did not fire." >&2
    exit 1
fi

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH/." "$APP/Contents/MacOS/"

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
echo "signing nested binaries..."
find "$APP/Contents/MacOS" \( -name '*.dylib' -o -name '*.so' -o -name '*.dll' \) -print0 \
    | xargs -0 -n1 codesign --force --timestamp=none --sign - 2>/dev/null || true
codesign --force --timestamp=none --sign - "$APP/Contents/MacOS/MegaPDF"
codesign --force --timestamp=none --sign - "$APP"

echo "--- verify:"
codesign --verify --deep --verbose=2 "$APP" 2>&1 | head -10 || echo "::warning::ad-hoc verification reported problems (expected until Developer ID signing lands)"

echo "done: $APP"
du -sh "$APP"
