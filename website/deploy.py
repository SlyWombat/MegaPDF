#!/usr/bin/env python3
"""Deploy website/megapdf/ to electricrv.ca via the cPanel UAPI.

The login is NOT stored here: it comes from the Lighting Arduino project's .env
(CPANEL_HOST/PORT/USER/TOKEN), which is the house convention. Same
Fileman/upload_files pattern as that project's server/deploy.py.

    /usr/bin/python3 website/deploy.py --dry-run   # list what would go where
    /usr/bin/python3 website/deploy.py             # upload the landing page + images
    /usr/bin/python3 website/deploy.py --privacy   # also re-upload privacy/

Privacy is opt-in because it is the URL both app-store listings point at, and it
should only change deliberately.

`--dry-run` reads nothing but the working tree: no .env, no network, no UAPI
call. It is the only mode that is safe to run from a machine that is not Dave's.

`--dest` puts the same tree somewhere else on the server, so a release can be
looked at before it replaces the live page:

    /usr/bin/python3 website/deploy.py --dest /public_html/megapdf-preview

A destination that does not exist yet is created, one level at a time;
--dry-run names each directory it would create. The preview path is not linked
from anywhere and is not in robots.txt — it is a URL you know, not a secret.
"""

import argparse
import json
import os
import ssl
import urllib.error
import urllib.parse
import urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SITE = os.path.join(REPO, "website", "megapdf")
ENV_PATH = os.path.join(os.path.dirname(REPO), "Lighting Arduino", ".env")
DEFAULT_DEST = "/public_html/megapdf"

CTX = ssl.create_default_context()
CTX.check_hostname = False
CTX.verify_mode = ssl.CERT_NONE


class Server:
    """The UAPI endpoint. Constructed only when something is actually uploaded,
    so --dry-run needs no .env and works anywhere."""

    def __init__(self):
        env = {}
        with open(ENV_PATH) as f:
            for line in f:
                line = line.strip()
                if line and not line.startswith("#") and "=" in line:
                    k, v = line.split("=", 1)
                    env[k.strip()] = v.strip()
        host = env.get("CPANEL_HOST", "electricrv.ca")
        port = env.get("CPANEL_PORT", "2083")
        self.base_url = f"https://{host}:{port}/execute"
        self.auth = f"cpanel {env.get('CPANEL_USER', '')}:{env.get('CPANEL_TOKEN', '')}"

    def _post(self, endpoint, body, content_type):
        req = urllib.request.Request(
            f"{self.base_url}/{endpoint}",
            data=body,
            method="POST",
            headers={"Authorization": self.auth, "Content-Type": content_type},
        )
        resp = urllib.request.urlopen(req, timeout=60, context=CTX)
        return json.loads(resp.read().decode())

    def mkdir(self, remote_dir):
        """Create one directory. Already-there is success — cPanel reports it as
        an error, and re-running a deploy must not be a failure."""
        parent, name = os.path.split(remote_dir.rstrip("/"))
        body = urllib.parse.urlencode({"path": parent, "name": name}).encode()
        try:
            out = self._post("Fileman/mkdir", body, "application/x-www-form-urlencoded")
        except urllib.error.HTTPError as exc:
            return False, f"HTTP {exc.code}"
        if out.get("status") == 1:
            return True, "created"
        why = "; ".join(out.get("errors") or []) or "refused"
        if "exist" in why.lower():
            return True, "already there"
        return False, why

    def upload(self, local_path, remote_dir):
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
        out = self._post("Fileman/upload_files", body,
                         f"multipart/form-data; boundary={boundary}")
        return out.get("status") == 1


def plan(dest, include_privacy):
    """Every file under website/megapdf/, with the remote directory it goes to.

    Subdirectories are walked, so screenshots/linux/ (the AppStream set, #254 A1)
    goes up with everything else. privacy/ is the one exception: it is skipped
    unless asked for.
    """
    targets = []
    for root, dirs, files in os.walk(SITE):
        rel = os.path.relpath(root, SITE)
        rel = "" if rel == "." else rel
        if rel.split(os.sep)[0] == "privacy" and not include_privacy:
            dirs[:] = []
            continue
        dirs.sort()
        remote = dest if not rel else dest + "/" + rel.replace(os.sep, "/")
        for name in sorted(files):
            targets.append((os.path.join(root, name), remote))
    return targets


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--privacy", action="store_true",
                    help="also upload privacy/ — the URL both store listings point at")
    ap.add_argument("--dry-run", action="store_true",
                    help="list what would be uploaded and where; no .env, no network")
    ap.add_argument("--dest", default=DEFAULT_DEST, metavar="PATH",
                    help=f"remote directory (default {DEFAULT_DEST})")
    args = ap.parse_args()

    dest = args.dest.rstrip("/")
    targets = plan(dest, args.privacy)
    # Every remote directory below dest, parents first. dest itself is in the
    # list too: --dest may name a path that does not exist yet.
    needed = {dest}
    for _, remote in targets:
        parts = remote[len(dest):].strip("/").split("/")
        for i in range(1, len(parts) + 1):
            if parts[0]:
                needed.add(dest + "/" + "/".join(parts[:i]))
    needed = sorted(needed, key=lambda d: (d.count("/"), d))

    total = sum(os.path.getsize(p) for p, _ in targets)

    if args.dry_run:
        print("DRY RUN — nothing is uploaded and nothing is contacted.\n")
        print(f"  from   {SITE}")
        print(f"  to     {dest}/ on the server\n")
        print("  directories, parents first:")
        for d in needed:
            print(f"    {d}/")
        print("\n  files:")
        for path, remote in targets:
            print(f"    {os.path.relpath(path, SITE):<34} {os.path.getsize(path):>9,} B"
                  f" -> {remote}/")
        print(f"\n{len(targets)} files, {total:,} B, into {len(needed)} directories"
              + (" — privacy/ included" if args.privacy else " — privacy/ untouched"))
        print("To do it for real, run the same command without --dry-run.")
        return 0

    failures = 0
    server = Server()
    for d in needed:
        if d == DEFAULT_DEST:
            continue  # the live directory has been there since 2026-08
        ok, why = server.mkdir(d)
        print(f"  {'DIR ' if ok else 'FAIL'} {d}/  ({why})")
        if not ok:
            failures += 1
    for path, remote in targets:
        ok = server.upload(path, remote)
        print(f"  {'OK  ' if ok else 'FAIL'} {os.path.relpath(path, SITE):<34}"
              f" {os.path.getsize(path):>9,} B -> {remote}/")
        if not ok:
            failures += 1
    print(f"\n{len(targets) - failures}/{len(targets)} uploaded to {dest}/"
          + (" — privacy/ included" if args.privacy else " — privacy/ untouched"))
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
