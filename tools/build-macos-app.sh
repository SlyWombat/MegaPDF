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
RUNNER_TEMP_DIR="${RUNNER_TEMP:-${TMPDIR:-/tmp}}"
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

# ca.electricrv.megapdf is already taken by the iOS diagnostic bundle id, so the
# Mac app is com.megapdf.mac — symmetric with the phone app's com.megapdf.ios.
BUNDLE_ID="${MACOS_BUNDLE_ID:-com.megapdf.mac}"
echo "building MegaPDF $VERSION for $RID"

# PDFium must be present before publish: MegaPDF.Core.csproj copies it from
# libs/pdfium/mac-univ when building on macOS, and silently omits it otherwise.
"$ROOT/tools/fetch-pdfium-mac.sh"

rm -rf "$APP"
PUBLISH="$(mktemp -d)"
# Preserve the failing status explicitly. In bash 3.2 — which is what /bin/bash is
# on a macOS runner — the exit status after an EXIT trap can become the status of
# the trap's own last command, so a successful `rm` silently turned a failed build
# into a green step that uploaded an unsigned bundle.
trap 'code=$?; rm -rf "$PUBLISH"; exit $code' EXIT

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

# The icon, before signing. A file dropped into Contents/Resources afterwards
# invalidates the seal, and given this bundle's signing history that failure
# would surface as something else entirely.
#
# Committed rather than generated here: tools/gen_macos_icon.py needs PIL, and
# this script runs in four workflows. Regenerate it when the branding changes.
ICON="$ROOT/assets/branding/MegaPDF.icns"
if [ ! -f "$ICON" ]; then
    echo "::error::$ICON is missing — regenerate with tools/gen_macos_icon.py" >&2
    exit 1
fi
cp "$ICON" "$APP/Contents/Resources/MegaPDF.icns"

cat > "$APP/Contents/Info.plist" << PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>MegaPDF</string>
    <key>CFBundleDisplayName</key><string>MegaPDF</string>
    <key>CFBundleIdentifier</key><string>${BUNDLE_ID}</string>
    <key>CFBundleExecutable</key><string>MegaPDF</string>
    <!-- Without this the app shows the blank generic document in Finder, the
         Dock and the app switcher, and the App Store rejects it (#73). -->
    <key>CFBundleIconFile</key><string>MegaPDF</string>
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

# Then the bundle — and ONLY the bundle. Signing Contents/MacOS/MegaPDF directly
# makes codesign treat that directory as the bundle root and demand a signature
# for every sibling file in it, including MegaPDF.runtimeconfig.json, which is
# not code and cannot have one. Signing the .app instead lets codesign sign the
# main executable itself and seal everything else as resources, which is what
# CodeResources is for.
# Three signing modes, selected by what is in the environment:
#
#   ad-hoc (default)   `--sign -`. Runnable locally after clearing quarantine.
#   Developer ID       MACOS_DEVID_P12_B64 set. Real identity + Hardened Runtime
#                      + secure timestamp, which is what notarization requires.
#   sandbox            MACOS_ENTITLEMENTS points at sandbox.entitlements. For the
#                      Mac App Store build.
#
# The identity and the entitlements are chosen independently on purpose: the
# tester build is Developer ID + hardened (no sandbox), the Store build is
# sandbox + Mac App Distribution.
IDENTITY="-"

# A Mac App Store build carries its provisioning profile inside the bundle.
# Without it the App Store signature is invalid, and that surfaces at upload
# rather than at build.
if [ -n "${MACOS_PROVISION_PROFILE:-}" ]; then
    [ -f "$MACOS_PROVISION_PROFILE" ] || { echo "::error::provisioning profile not found: $MACOS_PROVISION_PROFILE" >&2; exit 1; }
    cp "$MACOS_PROVISION_PROFILE" "$APP/Contents/embedded.provisionprofile"
    echo "embedded provisioning profile"
fi

if [ -n "${MACOS_SIGN_P12_B64:-}" ]; then
    # Named-identity path, used by the Mac App Store build with the "Apple
    # Distribution" certificate — the modern type that signs both iOS and Mac
    # App Store apps. Same keychain dance as Developer ID; what differs is the
    # identity and the entitlements the caller supplies.
    : "${MACOS_SIGN_P12_PASSWORD:?MACOS_SIGN_P12_PASSWORD is required alongside MACOS_SIGN_P12_B64}"
    : "${MACOS_SIGN_IDENTITY:?MACOS_SIGN_IDENTITY is required alongside MACOS_SIGN_P12_B64}"

    KEYCHAIN="$RUNNER_TEMP_DIR/megapdf-signing.keychain-db"
    KEYCHAIN_PW="$(openssl rand -base64 24)"
    security create-keychain -p "$KEYCHAIN_PW" "$KEYCHAIN"
    security set-keychain-settings -lut 21600 "$KEYCHAIN"
    security unlock-keychain -p "$KEYCHAIN_PW" "$KEYCHAIN"

    echo "$MACOS_SIGN_P12_B64" | base64 -d > "$RUNNER_TEMP_DIR/sign.p12"
    security import "$RUNNER_TEMP_DIR/sign.p12" -k "$KEYCHAIN" \
        -P "$MACOS_SIGN_P12_PASSWORD" -T /usr/bin/codesign
    rm -f "$RUNNER_TEMP_DIR/sign.p12"

    # The installer identity goes in the same keychain, so productbuild can find
    # it later in the job without a second unlock.
    if [ -n "${MACOS_INSTALLER_P12_B64:-}" ]; then
        echo "$MACOS_INSTALLER_P12_B64" | base64 -d > "$RUNNER_TEMP_DIR/installer.p12"
        security import "$RUNNER_TEMP_DIR/installer.p12" -k "$KEYCHAIN" \
            -P "${MACOS_INSTALLER_P12_PASSWORD:?}" -T /usr/bin/productbuild -T /usr/bin/productsign
        rm -f "$RUNNER_TEMP_DIR/installer.p12"
    fi

    security set-key-partition-list -S apple-tool:,apple:,codesign:,productbuild:,productsign: \
        -s -k "$KEYCHAIN_PW" "$KEYCHAIN" >/dev/null
    security list-keychains -d user -s "$KEYCHAIN" $(security list-keychains -d user | tr -d '"')

    IDENTITY="$MACOS_SIGN_IDENTITY"
    echo "signing identity: $IDENTITY"
    security find-identity -v "$KEYCHAIN" | head -5

elif [ -n "${MACOS_DEVID_P12_B64:-}" ]; then
    : "${MACOS_DEVID_P12_PASSWORD:?MACOS_DEVID_P12_PASSWORD is required alongside MACOS_DEVID_P12_B64}"
    : "${MACOS_DEVID_IDENTITY:?MACOS_DEVID_IDENTITY is required alongside MACOS_DEVID_P12_B64}"

    # A throwaway keychain, not the login keychain: CI has no login keychain
    # worth touching, and this one dies with the runner.
    KEYCHAIN="$RUNNER_TEMP_DIR/megapdf-signing.keychain-db"
    KEYCHAIN_PW="$(openssl rand -base64 24)"
    security create-keychain -p "$KEYCHAIN_PW" "$KEYCHAIN"
    security set-keychain-settings -lut 21600 "$KEYCHAIN"
    security unlock-keychain -p "$KEYCHAIN_PW" "$KEYCHAIN"

    echo "$MACOS_DEVID_P12_B64" | base64 -d > "$RUNNER_TEMP_DIR/devid.p12"
    security import "$RUNNER_TEMP_DIR/devid.p12" -k "$KEYCHAIN" \
        -P "$MACOS_DEVID_P12_PASSWORD" -T /usr/bin/codesign
    rm -f "$RUNNER_TEMP_DIR/devid.p12"

    # Without this codesign blocks on a GUI prompt that will never be answered.
    security set-key-partition-list -S apple-tool:,apple:,codesign: \
        -s -k "$KEYCHAIN_PW" "$KEYCHAIN" >/dev/null
    security list-keychains -d user -s "$KEYCHAIN" $(security list-keychains -d user | tr -d '"')

    IDENTITY="$MACOS_DEVID_IDENTITY"
    echo "signing identity: $IDENTITY (hardened runtime, secure timestamp)"
    security find-identity -v -p codesigning "$KEYCHAIN" | head -3
else
    echo "signing identity: ad-hoc (no MACOS_DEVID_P12_B64 in the environment)"
fi

# Built up with += rather than by expanding TIMESTAMP_ARGS/HARDENED_ARGS. On
# bash 3.2 `"${EMPTY[@]}"` under `set -u` is an unbound-variable error, which is
# exactly how this script previously died one line before signing.
SIGN_ARGS=(--force)
if [ "$IDENTITY" = "-" ]; then
    SIGN_ARGS+=(--timestamp=none)
elif [ -n "${MACOS_SIGN_P12_B64:-}" ]; then
    # Mac App Store: a secure timestamp, but NOT the Hardened Runtime. That is a
    # notarization requirement for direct distribution; the Store gates on the
    # sandbox instead, and claiming both muddies what the build is asserting.
    SIGN_ARGS+=(--timestamp)
else
    # Developer ID + notarization requires both of these.
    SIGN_ARGS+=(--timestamp --options runtime)
fi
SIGN_ARGS+=(--sign "$IDENTITY")

if [ -n "${MACOS_ENTITLEMENTS:-}" ]; then
    [ -f "$MACOS_ENTITLEMENTS" ] || { echo "::error::entitlements file not found: $MACOS_ENTITLEMENTS" >&2; exit 1; }
    echo "entitlements: $MACOS_ENTITLEMENTS"
    SIGN_ARGS+=(--entitlements "$MACOS_ENTITLEMENTS")
fi

# Nested Mach-O libraries must carry their own signatures before the bundle can
# seal them, with the same identity and hardening the bundle will use.
echo "signing nested binaries..."
NESTED_ARGS=(--force)
if [ "$IDENTITY" = "-" ]; then
    NESTED_ARGS+=(--timestamp=none)
elif [ -n "${MACOS_SIGN_P12_B64:-}" ]; then
    NESTED_ARGS+=(--timestamp)
else
    NESTED_ARGS+=(--timestamp --options runtime)
fi
NESTED_ARGS+=(--sign "$IDENTITY")
find "$APP/Contents/MacOS" \( -name '*.dylib' -o -name '*.so' \) -print0 \
    | xargs -0 -n1 codesign "${NESTED_ARGS[@]}"

codesign "${SIGN_ARGS[@]}" "$APP"

# Fail loudly rather than uploading something unsigned: this is the check that
# would have caught the bash 3.2 bug above on the run that introduced it.
if [ -n "${MACOS_ENTITLEMENTS:-}" ]; then
    codesign -d --entitlements - "$APP" 2>&1 | grep -q "app-sandbox\|allow-jit" \
        || { echo "::error::entitlements were requested but are not in the signature" >&2; exit 1; }
fi

echo "--- entitlements actually embedded in the signature:"
codesign -d --entitlements - --xml "$APP" 2>/dev/null | plutil -convert xml1 -o - - 2>/dev/null \
    || codesign -d --entitlements - "$APP" 2>&1 | head -20 \
    || echo "(none)"

echo "--- verify:"
codesign --verify --deep --verbose=2 "$APP" 2>&1 | head -10 || echo "::warning::ad-hoc verification reported problems (expected until Developer ID signing lands)"

echo "done: $APP"
du -sh "$APP"
