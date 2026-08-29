#!/usr/bin/env bash
# Wraps a Mac-App-Store-signed MegaPDF.app in the signed .pkg the App Store takes,
# and optionally uploads it.
#
# Usage: tools/package-macos-pkg.sh <path-to-MegaPDF.app> <out.pkg>
#
# Requires:
#   MACOS_INSTALLER_IDENTITY   "3rd Party Mac Developer Installer: ..."
# Optional (upload):
#   ASC_KEY_P8 / ASC_KEY_ID / ASC_ISSUER_ID, and MACOS_UPLOAD=1
#
# The .app must already be signed with "Apple Distribution" and carry
# Contents/embedded.provisionprofile — productbuild wraps, it does not fix.
set -euo pipefail

APP="${1:?usage: package-macos-pkg.sh <MegaPDF.app> <out.pkg>}"
OUT="${2:?usage: package-macos-pkg.sh <MegaPDF.app> <out.pkg>}"
: "${MACOS_INSTALLER_IDENTITY:?MACOS_INSTALLER_IDENTITY is required}"

[ -d "$APP" ] || { echo "::error::not a bundle: $APP" >&2; exit 1; }

# Catch the two things that make an upload fail with an unhelpful message, here
# where the cause is still obvious.
if [ ! -f "$APP/Contents/embedded.provisionprofile" ]; then
    echo "::error::no embedded.provisionprofile in the bundle — the App Store will reject this" >&2
    exit 1
fi
if ! codesign -d --entitlements - "$APP" 2>&1 | grep -q "app-sandbox"; then
    echo "::error::the bundle is not sandboxed — the Mac App Store requires it" >&2
    exit 1
fi

echo "--- what signed the bundle:"
codesign -dvv "$APP" 2>&1 | grep -E "Authority|Identifier|TeamIdentifier" | head -5 || true

# --component ... /Applications is what tells the installer where the app goes.
echo "building $OUT"
productbuild --component "$APP" /Applications \
    --sign "$MACOS_INSTALLER_IDENTITY" \
    "$OUT"

echo "--- package signature:"
pkgutil --check-signature "$OUT" 2>&1 | head -6 || true
ls -lh "$OUT"

if [ "${MACOS_UPLOAD:-0}" != "1" ]; then
    echo "built but not uploaded (set MACOS_UPLOAD=1 to deliver to App Store Connect)"
    exit 0
fi

: "${ASC_KEY_P8:?ASC_KEY_P8 required to upload}"
: "${ASC_KEY_ID:?ASC_KEY_ID required to upload}"
: "${ASC_ISSUER_ID:?ASC_ISSUER_ID required to upload}"

# altool wants the key in ./private_keys/AuthKey_<id>.p8 relative to one of a few
# fixed locations; ~/.appstoreconnect/private_keys is the documented one.
KEYDIR="$HOME/.appstoreconnect/private_keys"
mkdir -p "$KEYDIR"
if printf '%s' "$ASC_KEY_P8" | grep -q "BEGIN PRIVATE KEY"; then
    printf '%s' "$ASC_KEY_P8" > "$KEYDIR/AuthKey_$ASC_KEY_ID.p8"
else
    printf '%s' "$ASC_KEY_P8" | base64 -d > "$KEYDIR/AuthKey_$ASC_KEY_ID.p8"
fi
# Same reason as build-macos-app.sh: a successful cleanup must not mask a
# failed productbuild or upload (#62).
trap 'code=$?; rm -f "$KEYDIR/AuthKey_$ASC_KEY_ID.p8"; exit $code' EXIT

echo "validating with App Store Connect..."
xcrun altool --validate-app -f "$OUT" -t macos \
    --apiKey "$ASC_KEY_ID" --apiIssuer "$ASC_ISSUER_ID"

echo "uploading..."
xcrun altool --upload-app -f "$OUT" -t macos \
    --apiKey "$ASC_KEY_ID" --apiIssuer "$ASC_ISSUER_ID"

echo "delivered to App Store Connect"
