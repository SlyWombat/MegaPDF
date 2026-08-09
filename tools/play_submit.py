#!/usr/bin/env python3
"""Google Play internal-track submission via the Play Developer API.

Used by android-release.yml (secret PLAY_SERVICE_ACCOUNT_JSON) and manually.
RS256 JWT via openssl (no google libs). Usage as a library or:
  play_api.py probe   — token + edits.insert probe for the package
  play_api.py submit <aab-path>  — full internal-track submission
"""
import base64
import json
import subprocess
import sys
import time
import urllib.request
import urllib.error

import os
# Key comes from PLAY_SA_PATH (a file) or PLAY_SA_JSON (raw contents, CI secret).
SA_PATH = os.environ.get("PLAY_SA_PATH", "")
if not SA_PATH and os.environ.get("PLAY_SA_JSON"):
    import tempfile
    _f = tempfile.NamedTemporaryFile("w", suffix=".json", delete=False)
    _f.write(os.environ["PLAY_SA_JSON"]); _f.close()
    SA_PATH = _f.name
PACKAGE = "ca.electricrv.megapdf"
SCOPE = "https://www.googleapis.com/auth/androidpublisher"


def b64(b):
    return base64.urlsafe_b64encode(b).rstrip(b"=").decode()


def get_token():
    sa = json.load(open(SA_PATH))
    now = int(time.time())
    header = b64(json.dumps({"alg": "RS256", "typ": "JWT"}).encode())
    claims = b64(json.dumps({
        "iss": sa["client_email"], "scope": SCOPE,
        "aud": "https://oauth2.googleapis.com/token",
        "iat": now, "exp": now + 3600}).encode())
    signing_input = f"{header}.{claims}".encode()
    import tempfile, os
    with tempfile.NamedTemporaryFile("w", suffix=".pem", delete=False) as f:
        f.write(sa["private_key"])
        keyfile = f.name
    try:
        sig = subprocess.run(["openssl", "dgst", "-sha256", "-sign", keyfile],
                             input=signing_input, capture_output=True, check=True).stdout
    finally:
        os.unlink(keyfile)
    jwt = f"{header}.{claims}.{b64(sig)}"
    body = ("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer"
            f"&assertion={jwt}").encode()
    req = urllib.request.Request("https://oauth2.googleapis.com/token", data=body,
                                 headers={"Content-Type": "application/x-www-form-urlencoded"})
    resp = json.loads(urllib.request.urlopen(req, timeout=30).read())
    return resp["access_token"]


def api(token, method, path, body=None, content_type="application/json", raw=None):
    base = "https://androidpublisher.googleapis.com"
    data = raw if raw is not None else (json.dumps(body).encode() if body else None)
    req = urllib.request.Request(base + path, method=method, data=data,
                                 headers={"Authorization": f"Bearer {token}",
                                          **({"Content-Type": content_type} if data else {})})
    try:
        r = urllib.request.urlopen(req, timeout=300).read()
        return json.loads(r) if r else {}
    except urllib.error.HTTPError as e:
        raise SystemExit(f"{method} {path} -> {e.code}: {e.read().decode()[:400]}")


def probe():
    token = get_token()
    print("token OK")
    edit = api(token, "POST", f"/androidpublisher/v3/applications/{PACKAGE}/edits")
    print("edit created:", edit["id"], "— package is addressable, API access confirmed")
    api(token, "DELETE", f"/androidpublisher/v3/applications/{PACKAGE}/edits/{edit['id']}")
    print("edit discarded")


def submit(aab):
    token = get_token()
    print("token OK")
    edit = api(token, "POST", f"/androidpublisher/v3/applications/{PACKAGE}/edits")["id"]
    print("edit:", edit)
    data = open(aab, "rb").read()
    up = api(token, "POST",
             f"/upload/androidpublisher/v3/applications/{PACKAGE}/edits/{edit}/bundles?uploadType=media",
             content_type="application/octet-stream", raw=data)
    vc = up["versionCode"]
    print("bundle uploaded, versionCode:", vc)
    api(token, "PUT",
        f"/androidpublisher/v3/applications/{PACKAGE}/edits/{edit}/tracks/internal",
        body={"track": "internal",
              "releases": [{"versionCodes": [str(vc)], "status": "completed"}]})
    print("internal track set")
    api(token, "POST", f"/androidpublisher/v3/applications/{PACKAGE}/edits/{edit}:commit")
    print("edit committed — release live on internal track")


if __name__ == "__main__":
    if sys.argv[1] == "probe":
        probe()
    elif sys.argv[1] == "submit":
        submit(sys.argv[2])
