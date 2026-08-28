#!/usr/bin/env bash
# Minimal App Store Connect API client, self-contained so this repo does not
# depend on another project's layout.
#
# Usage: tools/asc.sh [GET|POST|PATCH|DELETE] <path> [json-body-file]
#
# Credentials, in order of preference:
#   ASC_KEY_P8 / ASC_KEY_ID / ASC_ISSUER_ID   (env — what CI has)
#   ASC_KEY_FILE + ASC_ISSUER_ID              (a .p8 on disk)
#
# ⚠️ Run with PATH=/usr/bin:/bin under WSL. `python3` there resolves to the
# Windows Python shim, which cannot read the key file and signs nothing usable —
# the same landmine docs/mobile-app-blueprint.md records for SlyTab's helper.
set -euo pipefail

if [[ "${1:-}" =~ ^(GET|POST|PATCH|DELETE)$ ]]; then
    METHOD="$1"; APIPATH="${2:?path required}"; BODY="${3:-}"
else
    METHOD="GET"; APIPATH="${1:?path required}"; BODY=""
fi

TMP="$(mktemp -d)"
trap 'code=$?; rm -rf "$TMP"; exit $code' EXIT

if [ -n "${ASC_KEY_P8:-}" ]; then
    if printf '%s' "$ASC_KEY_P8" | grep -q "BEGIN PRIVATE KEY"; then
        printf '%s' "$ASC_KEY_P8" > "$TMP/key.p8"
    else
        printf '%s' "$ASC_KEY_P8" | base64 -d > "$TMP/key.p8"
    fi
    KEY_ID="${ASC_KEY_ID:?ASC_KEY_ID required alongside ASC_KEY_P8}"
else
    KEYFILE="${ASC_KEY_FILE:?set ASC_KEY_P8 or ASC_KEY_FILE}"
    cp "$KEYFILE" "$TMP/key.p8"
    KEY_ID="${ASC_KEY_ID:-$(basename "$KEYFILE" .p8 | sed 's/.*_//')}"
fi
ISSUER="${ASC_ISSUER_ID:?ASC_ISSUER_ID required}"

TOKEN="$(python3 - "$TMP/key.p8" "$KEY_ID" "$ISSUER" <<'PY'
import base64, json, subprocess, sys, time
keyfile, kid, iss = sys.argv[1:4]
b64 = lambda b: base64.urlsafe_b64encode(b).rstrip(b'=').decode()
now = int(time.time())
header = b64(json.dumps({"alg": "ES256", "kid": kid, "typ": "JWT"}).encode())
payload = b64(json.dumps({"iss": iss, "iat": now, "exp": now + 900,
                          "aud": "appstoreconnect-v1"}).encode())
der = subprocess.run(["openssl", "dgst", "-sha256", "-sign", keyfile],
                     input=f"{header}.{payload}".encode(),
                     capture_output=True, check=True).stdout
# DER ECDSA-Sig-Value -> raw r||s, 32 bytes each
i, out = 2, []
for _ in range(2):
    ln = der[i + 1]
    out.append(int.from_bytes(der[i + 2:i + 2 + ln], "big"))
    i += 2 + ln
sig = b64(out[0].to_bytes(32, "big") + out[1].to_bytes(32, "big"))
print(f"{header}.{payload}.{sig}")
PY
)"

ARGS=(-sS --globoff -m 120 -X "$METHOD" -H "Authorization: Bearer $TOKEN")
[ -n "$BODY" ] && ARGS+=(-H "Content-Type: application/json" --data-binary "@$BODY")
curl "${ARGS[@]}" "https://api.appstoreconnect.apple.com$APIPATH"
