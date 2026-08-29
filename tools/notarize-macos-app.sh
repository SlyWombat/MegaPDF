#!/usr/bin/env bash
# Submits a Developer-ID-signed MegaPDF.app to Apple's notary service, waits for
# the verdict, and staples the ticket into the bundle.
#
# Stapling is the point: a stapled app opens by double-click on a Mac that has
# never seen it before, with no right-click-Open dance and no quarantine
# clearing. That is the difference between "here is a build" and "here is
# something a tester can actually use".
#
# Usage: tools/notarize-macos-app.sh <path-to-MegaPDF.app>
#
# Requires (all already repo secrets, shared with the iOS pipeline):
#   ASC_KEY_P8, ASC_KEY_ID, ASC_ISSUER_ID
set -euo pipefail

APP="${1:?usage: notarize-macos-app.sh <path-to-MegaPDF.app>}"
[ -d "$APP" ] || { echo "::error::not a bundle: $APP" >&2; exit 1; }
: "${ASC_KEY_P8:?ASC_KEY_P8 is required}"
: "${ASC_KEY_ID:?ASC_KEY_ID is required}"
: "${ASC_ISSUER_ID:?ASC_ISSUER_ID is required}"

TMP="$(mktemp -d)"
# Preserve the real status: on macOS runners /bin/bash is 3.2, where the status
# after an EXIT trap can become the trap's own last command — so a successful
# `rm` turns a failed notarization into a green step, and an unstapled bundle
# ships to a tester whose Mac then refuses to open it (#62).
trap 'code=$?; rm -rf "$TMP"; exit $code' EXIT

# The secret may hold the .p8 verbatim or base64-encoded; accept either rather
# than depending on how it happened to be set.
if printf '%s' "$ASC_KEY_P8" | grep -q "BEGIN PRIVATE KEY"; then
    printf '%s' "$ASC_KEY_P8" > "$TMP/key.p8"
else
    printf '%s' "$ASC_KEY_P8" | base64 -d > "$TMP/key.p8"
fi
grep -q "BEGIN PRIVATE KEY" "$TMP/key.p8" || { echo "::error::ASC_KEY_P8 is neither a PEM key nor base64 of one" >&2; exit 1; }

# ditto, not zip: it preserves the symlinks, extended attributes and the code
# signature. A plain `zip` can invalidate the signature it is carrying.
echo "packing for submission..."
ditto -c -k --keepParent "$APP" "$TMP/upload.zip"

echo "submitting to Apple's notary service (this uploads the app)..."
xcrun notarytool submit "$TMP/upload.zip" \
    --key "$TMP/key.p8" --key-id "$ASC_KEY_ID" --issuer "$ASC_ISSUER_ID" \
    --wait --timeout 30m 2>&1 | tee "$TMP/submit.log"

SUBMISSION_ID="$(grep -oE '\bid: [0-9a-f-]{36}' "$TMP/submit.log" | head -1 | awk '{print $2}')"
if ! grep -qE "status: Accepted" "$TMP/submit.log"; then
    echo "::error::notarization was not accepted. Apple's reasons:"
    [ -n "$SUBMISSION_ID" ] && xcrun notarytool log "$SUBMISSION_ID" \
        --key "$TMP/key.p8" --key-id "$ASC_KEY_ID" --issuer "$ASC_ISSUER_ID" || true
    exit 1
fi

echo "accepted — stapling the ticket into the bundle"
xcrun stapler staple "$APP"
xcrun stapler validate "$APP"

echo "--- Gatekeeper's verdict on a machine that has never seen this app:"
spctl --assess --type execute --verbose=4 "$APP" 2>&1 | head -5 || true

echo "done: $APP is notarized and stapled"
