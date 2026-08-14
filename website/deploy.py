#!/usr/bin/env python3
"""Deploy website/megapdf/ to electricrv.ca via the cPanel UAPI.

Credentials are NOT stored here: they come from the Lighting Arduino project's
.env (CPANEL_HOST/PORT/USER/TOKEN), which is the house convention. Same
Fileman/upload_files pattern as that project's server/deploy.py.

    /usr/bin/python3 website/deploy.py            # upload the landing page + images
    /usr/bin/python3 website/deploy.py --privacy  # also re-upload privacy/

Privacy is opt-in because it is the URL both app-store listings point at, and it
should only change deliberately.
"""

import json
import mimetypes
import os
import ssl
import sys
import urllib.parse
import urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SITE = os.path.join(REPO, "website", "megapdf")
ENV_PATH = os.path.join(
    os.path.dirname(REPO), "Lighting Arduino", ".env"
)

env = {}
with open(ENV_PATH) as f:
    for line in f:
        line = line.strip()
        if line and not line.startswith("#") and "=" in line:
            k, v = line.split("=", 1)
            env[k.strip()] = v.strip()

HOST = env.get("CPANEL_HOST", "electricrv.ca")
PORT = env.get("CPANEL_PORT", "2083")
USER = env.get("CPANEL_USER", "")
TOKEN = env.get("CPANEL_TOKEN", "")
BASE_URL = f"https://{HOST}:{PORT}/execute"
AUTH = f"cpanel {USER}:{TOKEN}"

CTX = ssl.create_default_context()
CTX.check_hostname = False
CTX.verify_mode = ssl.CERT_NONE


def upload(local_path, remote_dir):
    name = os.path.basename(local_path)
    boundary = "----MegaPDFDeploy"
    with open(local_path, "rb") as f:
        data = f.read()
    body = (
        f"--{boundary}\r\n"
        f'Content-Disposition: form-data; name="dir"\r\n\r\n{remote_dir}\r\n'
        f"--{boundary}\r\n"
        f'Content-Disposition: form-data; name="overwrite"\r\n\r\n1\r\n'
        f"--{boundary}\r\n"
        f'Content-Disposition: form-data; name="file-1"; filename="{name}"\r\n'
        f"Content-Type: application/octet-stream\r\n\r\n"
    ).encode() + data + f"\r\n--{boundary}--\r\n".encode()
    req = urllib.request.Request(
        f"{BASE_URL}/Fileman/upload_files",
        data=body,
        method="POST",
        headers={"Authorization": AUTH,
                 "Content-Type": f"multipart/form-data; boundary={boundary}"},
    )
    resp = urllib.request.urlopen(req, timeout=60, context=CTX)
    return json.loads(resp.read().decode()).get("status") == 1


def main():
    include_privacy = "--privacy" in sys.argv
    targets = []
    for name in sorted(os.listdir(SITE)):
        path = os.path.join(SITE, name)
        if os.path.isfile(path):
            targets.append((path, "/public_html/megapdf"))
    if include_privacy:
        pdir = os.path.join(SITE, "privacy")
        for name in sorted(os.listdir(pdir)):
            path = os.path.join(pdir, name)
            if os.path.isfile(path):
                targets.append((path, "/public_html/megapdf/privacy"))

    failures = 0
    for path, remote in targets:
        ok = upload(path, remote)
        size = os.path.getsize(path)
        print(f"  {'OK  ' if ok else 'FAIL'} {os.path.basename(path):<28} {size:>9,} B -> {remote}/")
        if not ok:
            failures += 1
    print(f"\n{len(targets) - failures}/{len(targets)} uploaded"
          + (" — privacy/ included" if include_privacy else " — privacy/ untouched"))
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
