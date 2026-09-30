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

The support page is opt-in and, unlike everything else, it *needs* --privacy:

    /usr/bin/python3 website/deploy.py --support --privacy   # also support/

The policy's own §11 promises it is updated before anything that collects data
ships, and a page that composes a message to us is that kind of thing. So
--support refuses to start without --privacy, and refuses again unless the
policy it is uploading alongside actually carries the support section (an
`id="support"` heading — the anchor the page links). Without --support the page
stays off the server and every other page goes up with its `support:` regions
resolved to nothing, so nothing links a page that is not there.

Linux is opt-in too (#158), because it ships after the other platforms:

    /usr/bin/python3 website/deploy.py --linux          # also linux/ and the APT repo
    /usr/bin/python3 website/deploy.py --linux --snap   # ...and the Snap Store section

Without --linux, linux/ and apt/ stay off the server, and every page is uploaded
with its `<!--linux:live-->…<!--linux:soon …linux:end-->` regions resolved to the
"soon" text, so no page links a Linux page or repository that is not there. With
--linux the live text goes up instead, and the upload refuses to start unless
apt/ holds a complete repository whose signature verifies against apt/megapdf.gpg
and whose .deb is the version linux/index.html offers. `--snap` does the same for
the `snap:` regions of linux/index.html, for when the Snap Store listing is live.

`--dry-run` reads nothing but the working tree: no .env, no network, no UAPI
call. It is the only mode that is safe to run from a machine that is not Dave's.

`--only` uploads just the named parts of the tree, for a partial release. Linux 2.0
went out before the stores approved 2.0, so only its parts went up, next to the
live 1.x landing page, which `--landing` supplies instead of the staged one:

    /usr/bin/python3 website/deploy.py --linux --privacy \
        --only linux,apt,privacy,screenshots/linux --landing /path/to/live-index.html

Each name is a path under website/megapdf/ (a directory or a file). `--landing`
uploads the given file as index.html at the destination, as it is apart from its
HTML comments, and without it the landing page is left alone.

Every page goes up with its HTML comments removed (#322): they are notes for whoever
edits the source, and one on the live landing page once named an internal path.

`--dest` puts the same tree somewhere else on the server, so a release can be
looked at before it replaces the live page:

    /usr/bin/python3 website/deploy.py --dest /public_html/megapdf-preview

A destination that does not exist yet is created by the upload itself;
--dry-run names each directory that will appear. The preview path is not linked
from anywhere and is not in robots.txt — it is a URL you know, not a secret.
"""

import argparse
import json
import os
import re
import shutil
import ssl
import subprocess
import tempfile
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


# Directories that go up only when asked for, and the flag that asks.
OPT_IN = {"privacy": "privacy", "linux": "linux", "apt": "linux", "support": "support"}

REGION = r"<!--{name}:live-->(.*?)<!--{name}:soon(.*?){name}:end-->"


def resolve(html, live):
    """The page as it should go up: each gated region resolved to its live or its
    "soon" text. `live` maps a region name ("linux", "snap") to whether it is live."""
    for name, is_live in live.items():
        html = re.sub(REGION.format(name=name),
                      lambda m, on=is_live: m.group(1) if on else m.group(2),
                      html, flags=re.S)
    leftover = re.search(r"<!--\w+:(live|soon)", html)
    if leftover:
        raise SystemExit(f"a gated region is malformed or unknown near: {html[leftover.start():leftover.start() + 60]!r}")
    return strip_comments(html)


def strip_comments(html):
    """Every HTML comment removed before a page goes up. Comments in the source are
    notes for whoever edits it (issue numbers, paths, who decides what), and the
    public page is not the place for them: one on the live landing page named an
    internal file path (#322). Conditional comments (<!--[if ...]>) are markup, not
    notes, and stay."""
    return re.sub(r"<!--(?!\[if).*?-->", "", html, flags=re.S)


def check_linux(snap):
    """Refuse a --linux deploy that would publish a Linux page with no working
    repository behind it. Returns a line describing what was checked."""
    apt = os.path.join(SITE, "apt")
    need = ["megapdf.gpg", "megapdf.sources", "dists/stable/InRelease",
            "dists/stable/Release", "dists/stable/Release.gpg",
            "dists/stable/main/binary-amd64/Packages"]
    missing = [n for n in need if not os.path.isfile(os.path.join(apt, n))]
    if missing:
        raise SystemExit("--linux: apt/ is not a complete repository (missing "
                         + ", ".join(missing) + "). Build it with tools/linux/make-apt-repo.sh "
                         "website/megapdf/apt <the release .deb>, or download the "
                         "MegaPDF-apt-repository artefact of the tag's Linux release run into it.")
    keyring = os.path.join(apt, "megapdf.gpg")
    for args in (["dists/stable/InRelease"], ["dists/stable/Release.gpg", "dists/stable/Release"]):
        run = subprocess.run(["gpgv", "--keyring", keyring] + [os.path.join(apt, a) for a in args],
                             capture_output=True, text=True)
        if run.returncode != 0:
            raise SystemExit(f"--linux: {args[0]} does not verify against apt/megapdf.gpg")
    with open(os.path.join(apt, "dists/stable/main/binary-amd64/Packages")) as f:
        versions = re.findall(r"^Version: (.+)$", f.read(), flags=re.M)
    with open(os.path.join(SITE, "linux", "index.html")) as f:
        page = f.read()
    offered = sorted(set(re.findall(r"megapdf_([0-9][^_\s\"]*)_amd64\.deb", page)))
    if len(offered) != 1 or offered[0] not in versions:
        raise SystemExit(f"--linux: linux/index.html offers {offered or 'no .deb'}, "
                         f"but the repository holds {versions}. Update the page's version.")
    # The repository keeps every published .deb, so the page must offer the newest of
    # them, by dpkg's ordering (2.0.0 < 2.0.0-2 < 2.0.1), not merely one it holds.
    newer = [v for v in versions if v != offered[0] and subprocess.run(
        ["dpkg", "--compare-versions", v, "gt", offered[0]]).returncode == 0]
    if newer:
        raise SystemExit(f"--linux: linux/index.html offers {offered[0]}, but the repository "
                         f"also holds the newer {', '.join(newer)}. Offer the newest.")
    pool = os.path.join(apt, "pool/main/m/megapdf", f"megapdf_{offered[0]}_amd64.deb")
    if not os.path.isfile(pool):
        raise SystemExit(f"--linux: {os.path.relpath(pool, SITE)} is not in the pool")
    return (f"apt/ verified: signed by apt/megapdf.gpg, holds {', '.join(versions)}; "
            f"linux/index.html offers {offered[0]}; Snap section {'live' if snap else 'held back'}")


# The platforms the support page's answers may be tagged for; "all" means every
# one. Kept here so a typo in data-plat is a refused deploy rather than an answer
# that silently never appears.
SUPPORT_PLATFORMS = {"all", "windows", "mac", "ios", "android", "linux"}
SUPPORT_ADDRESS = "info@electricrv.ca"


def check_support(include_privacy):
    """Refuse a --support deploy that would publish the support page without the
    privacy section covering it, or with an answers list its own matcher cannot
    use. Returns a line describing what was checked.

    The page is static and posts nothing, but it is still the page that composes a
    message to us, so the policy has to be part of the same upload (#418, the same
    ordering #501 followed). The rest is the answers list: it is the single copy of
    every answer — the browser-side matcher reads these very elements — so an
    attribute the matcher cannot read is an answer nobody will ever be shown, and
    that is worth refusing a deploy over rather than discovering months later.
    """
    page_path = os.path.join(SITE, "support", "index.html")
    if not os.path.isfile(page_path):
        raise SystemExit("--support: website/megapdf/support/index.html is not there")
    with open(page_path, encoding="utf-8") as f:
        page = f.read()

    if not include_privacy:
        raise SystemExit("--support needs --privacy: the support page is disclosed in "
                         "the privacy policy, and the policy's own rule is that the "
                         "disclosure goes up first or in the same deploy. Run "
                         "--support --privacy.")
    policy_path = os.path.join(SITE, "privacy", "index.html")
    with open(policy_path, encoding="utf-8") as f:
        policy = f.read()
    if 'id="support"' not in policy:
        raise SystemExit('--support: privacy/index.html has no id="support" section, so '
                         "the support page would link an anchor that is not there and "
                         "would go up undisclosed. Add the section to the policy first.")

    if SUPPORT_ADDRESS not in page:
        raise SystemExit(f"--support: the support page never names {SUPPORT_ADDRESS}. "
                         "It is the one page whose job is that address.")

    # The policy says, in so many words, that this page submits nothing and sends
    # nothing. That is a promise about the bytes, so the deploy checks the bytes:
    # anything that could carry what a reader typed off their machine has to fail here
    # rather than be noticed by someone reading the source months later. Checked on the
    # page as it will be uploaded — regions resolved and comments stripped — because the
    # source's own notes talk about forms and fetches in prose.
    for label, live in (("with Linux live", True), ("with Linux held back", False)):
        sent = resolve(page, {"linux": live, "snap": live, "support": True})
        if re.search(r"<form\b", sent, flags=re.I):
            raise SystemExit(f"--support: the support page has a <form> ({label}). Policy §4 "
                             "says it has no form to submit and nothing to submit it to. If "
                             "that is changing, the policy changes first.")
        leak = re.search(r"\bfetch\s*\(|XMLHttpRequest|sendBeacon|WebSocket|EventSource"
                         r"|method\s*=\s*[\"']post", sent, flags=re.I)
        if leak:
            raise SystemExit(f"--support: the support page contains {leak.group(0)!r} ({label}), "
                             "which could send what someone typed. Policy §4 promises it "
                             "cannot. If that is changing, the policy changes first.")
        scripts = re.findall(r'<script[^>]*\bsrc="([^"]+)"', sent)
        if scripts != ["https://electricrv.ca/api/analytics/a.js"]:
            raise SystemExit(f"--support: the support page's external scripts are {scripts} "
                             f"({label}) — the only one it may load is the page-view counter "
                             "disclosed in policy §3.")
        origins = sorted({m for m in re.findall(r"https?://([a-z0-9.\-]+)/", sent)})
        allowed = ["electricrv.ca", "fonts.googleapis.com", "github.com"]
        if origins != allowed:
            raise SystemExit(f"--support: the support page names the origin(s) {origins} "
                             f"({label}); only {allowed} are accounted for in the privacy "
                             "policy.")

    # Each answer, as <details class="answer" ...> ... </details>.
    blocks = re.findall(r'<details class="answer"([^>]*)>(.*?)</details>', page, flags=re.S)
    if len(blocks) < 5:
        raise SystemExit(f"--support: the answers list holds {len(blocks)} answers. That is "
                         "not a support page; check that details.answer markup is intact.")
    ids = []
    for attrs, body in blocks:
        aid = re.search(r'\bid="([^"]+)"', attrs)
        if not aid:
            raise SystemExit(f"--support: an answer has no id (attributes: {attrs.strip()!r}). "
                             "The composed message names answers by id.")
        aid = aid.group(1)
        ids.append(aid)
        if "<summary>" not in body or 'class="body"' not in body:
            raise SystemExit(f"--support: answer {aid} has no <summary> or no .body; the "
                             "matcher reads the title from one and the text from the other.")
        plats = (re.search(r'\bdata-plat="([^"]*)"', attrs) or _empty()).group(1).split()
        if not plats:
            raise SystemExit(f"--support: answer {aid} has no data-plat (use "
                             'data-plat="all" for one that applies everywhere)')
        unknown = sorted(set(plats) - SUPPORT_PLATFORMS)
        if unknown:
            raise SystemExit(f"--support: answer {aid} names the unknown platform(s) "
                             f"{', '.join(unknown)}; known: {', '.join(sorted(SUPPORT_PLATFORMS))}")
        if "all" in plats and len(plats) > 1:
            raise SystemExit(f'--support: answer {aid} is data-plat="all" and also names '
                             "platforms; pick one or the other")
        terms = [t.strip() for t in
                 (re.search(r'\bdata-match="([^"]*)"', attrs) or _empty()).group(1).split("|")
                 if t.strip()]
        strong = [t.strip() for t in
                  (re.search(r'\bdata-strong="([^"]*)"', attrs) or _empty()).group(1).split("|")
                  if t.strip()]
        if not terms and not strong:
            raise SystemExit(f"--support: answer {aid} has no data-match or data-strong terms, "
                             "so nothing anyone types can ever surface it")
        for t in terms + strong:
            # The matcher lowercases the text and replaces everything that is not a
            # letter, a digit or an accented letter with a space, then looks for the
            # term between spaces. A term with anything else in it, or with a capital,
            # can never match.
            if t != t.lower() or re.search(r"[^a-z0-9 àâäçéèêëîïôöùûüÿœæ]", t):
                raise SystemExit(f"--support: answer {aid} has the unmatchable term {t!r} — "
                                 "the matcher lowercases and strips punctuation, so a term "
                                 "with a capital or a symbol in it never fires")
        # data-strong is worth 2 on its own and data-match 1, so a term in both would
        # quietly score 3 and outrank everything. Whichever it belongs in, it belongs in
        # one of them.
        both = sorted(set(terms) & set(strong))
        if both:
            raise SystemExit(f"--support: answer {aid} lists {', '.join(both)} in both "
                             "data-match and data-strong; pick one")
        # A short list is the whole point of data-strong: one of these words alone
        # surfaces the answer, so a long list of them is the matcher guessing again.
        if len(strong) > 6:
            raise SystemExit(f"--support: answer {aid} has {len(strong)} data-strong terms. "
                             "Those fire on their own, so keep them to the few words that "
                             "really can only mean this answer (six at most).")
    dupes = sorted({i for i in ids if ids.count(i) > 1})
    if dupes:
        raise SystemExit(f"--support: duplicate answer id(s) {', '.join(dupes)}")

    # The support page must not link a part of the site a deploy can leave off.
    # Resolved with Linux held back, no reference to linux/ may survive.
    held = resolve(page, {"linux": False, "snap": False, "support": True})
    if "../linux/" in held:
        raise SystemExit("--support: the support page links ../linux/ outside a "
                         "<!--linux:live--> region, so that link 404s on a deploy without "
                         "--linux. Move it inside one.")
    return (f"support/ checked: {len(blocks)} answers, all matchable; "
            f'privacy/ carries the id="support" section and goes up with it')


def _empty():
    """A no-match stand-in whose .group(1) is "", so a missing attribute reads as
    absent rather than raising."""
    return re.match(r"()", "")


def plan(dest, include_privacy, include_linux=False, include_support=False):
    """Every file under website/megapdf/, with the remote directory it goes to.

    Subdirectories are walked, so screenshots/linux/ (the AppStream set, #254 A1)
    goes up with everything else. privacy/, linux/, apt/ and support/ are the
    exceptions: each is skipped unless its flag asks for it.
    """
    wanted = {"privacy": include_privacy, "linux": include_linux,
              "support": include_support}
    targets = []
    for root, dirs, files in os.walk(SITE):
        rel = os.path.relpath(root, SITE)
        rel = "" if rel == "." else rel
        top = rel.split(os.sep)[0]
        if top in OPT_IN and not wanted[OPT_IN[top]]:
            dirs[:] = []
            continue
        dirs.sort()
        remote = dest if not rel else dest + "/" + rel.replace(os.sep, "/")
        for name in sorted(files):
            if top == "apt" and name == "FINGERPRINT":
                continue  # the build's own check, not something to serve
            targets.append((os.path.join(root, name), remote))
    return targets


def only(targets, names):
    """The targets whose source is one of `names` (paths under website/megapdf/),
    or inside one. Refuses a name that matches nothing, so a typo cannot quietly
    upload less than asked for."""
    names = [n.strip().strip("/") for n in names.split(",") if n.strip()]
    kept, hit = [], set()
    for path, remote in targets:
        rel = os.path.relpath(path, SITE).replace(os.sep, "/")
        for n in names:
            if rel == n or rel.startswith(n + "/"):
                kept.append((path, remote))
                hit.add(n)
                break
    unmatched = [n for n in names if n not in hit]
    if unmatched:
        raise SystemExit(f"--only: nothing to upload for {', '.join(unmatched)} "
                         "(an opt-in directory also needs its flag: --privacy, --linux)")
    return kept


def staged(targets, live):
    """The files as they will be uploaded: every .html resolved into a temporary
    copy under its own name, everything else as it is on disk."""
    tmp = tempfile.mkdtemp(prefix="megapdf-deploy-")
    out = []
    for i, (path, remote) in enumerate(targets):
        if path.endswith(".html"):
            with open(path, encoding="utf-8") as f:
                html = resolve(f.read(), live)
            copy_dir = os.path.join(tmp, str(i))
            os.makedirs(copy_dir)
            copy = os.path.join(copy_dir, os.path.basename(path))
            with open(copy, "w", encoding="utf-8") as f:
                f.write(html)
            out.append((copy, remote, path))
        else:
            out.append((path, remote, path))
    return tmp, out


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--privacy", action="store_true",
                    help="also upload privacy/ — the URL both store listings point at")
    ap.add_argument("--linux", action="store_true",
                    help="also upload linux/ and the APT repository in apt/, and link them")
    ap.add_argument("--snap", action="store_true",
                    help="with --linux: show the Snap Store section of linux/")
    ap.add_argument("--support", action="store_true",
                    help="also upload support/, and link it — needs --privacy")
    ap.add_argument("--dry-run", action="store_true",
                    help="list what would be uploaded and where; no .env, no network")
    ap.add_argument("--only", metavar="NAMES",
                    help="upload only these comma-separated paths under website/megapdf/")
    ap.add_argument("--landing", metavar="FILE",
                    help="upload FILE as index.html at the destination (with --only)")
    ap.add_argument("--dest", default=DEFAULT_DEST, metavar="PATH",
                    help=f"remote directory (default {DEFAULT_DEST})")
    args = ap.parse_args()

    if args.snap and not args.linux:
        ap.error("--snap needs --linux: the Snap section is on the Linux page")
    linux_note = check_linux(args.snap) if args.linux else "Linux held back: linux/ and apt/ stay off the server, pages say \"coming soon\""
    support_note = (check_support(args.privacy) if args.support
                    else "Support page held back: support/ stays off the server and no page links it")
    live = {"linux": args.linux, "snap": args.snap, "support": args.support}

    if args.landing and not args.only:
        ap.error("--landing goes with --only: without it the staged index.html goes up")
    if args.landing and not os.path.isfile(args.landing):
        ap.error(f"--landing: no such file {args.landing}")

    dest = args.dest.rstrip("/")
    chosen = plan(dest, args.privacy, args.linux, args.support)
    if args.only:
        chosen = only(chosen, args.only)
    tmp, targets = staged(chosen, live)
    if args.landing:
        # Uploads keep the local file's name, so the landing page goes up from a
        # copy called index.html.
        landing = os.path.join(tmp, "landing", "index.html")
        os.makedirs(os.path.dirname(landing))
        with open(args.landing, encoding="utf-8") as f:
            page = strip_comments(f.read())
        with open(landing, "w", encoding="utf-8") as f:
            f.write(page)
        targets.append((landing, dest, os.path.join(SITE, "index.html")))
    # Every remote directory below dest, parents first. dest itself is in the
    # list too: --dest may name a path that does not exist yet.
    needed = {dest}
    for _, remote, _ in targets:
        parts = remote[len(dest):].strip("/").split("/")
        for i in range(1, len(parts) + 1):
            if parts[0]:
                needed.add(dest + "/" + "/".join(parts[:i]))
    needed = sorted(needed, key=lambda d: (d.count("/"), d))

    total = sum(os.path.getsize(p) for p, _, _ in targets)

    if args.dry_run:
        print("DRY RUN — nothing is uploaded and nothing is contacted.\n")
        print(f"  from   {SITE}")
        print(f"  to     {dest}/ on the server\n")
        print("  directories, parents first:")
        for d in needed:
            print(f"    {d}/")
        print("\n  files:")
        for path, remote, source in targets:
            print(f"    {os.path.relpath(source, SITE):<34} {os.path.getsize(path):>9,} B"
                  f" -> {remote}/")
        print(f"\n{len(targets)} files, {total:,} B, into {len(needed)} directories"
              + (" — privacy/ included" if args.privacy else " — privacy/ untouched"))
        print(linux_note)
        print(support_note)
        print("To do it for real, run the same command without --dry-run.")
        shutil.rmtree(tmp, ignore_errors=True)
        return 0

    failures = 0
    server = Server()
    # No mkdir: this server's UAPI has no Fileman/mkdir ("could not find the
    # function"), and Fileman/upload_files creates every missing directory in a
    # file's path by itself. Seen on the Linux 2.0 deploy (2026-09-19), which
    # created apt/pool/main/m/megapdf/ and the rest that way.
    for path, remote, source in targets:
        ok = server.upload(path, remote)
        print(f"  {'OK  ' if ok else 'FAIL'} {os.path.relpath(source, SITE):<34}"
              f" {os.path.getsize(path):>9,} B -> {remote}/")
        if not ok:
            failures += 1
    shutil.rmtree(tmp, ignore_errors=True)
    print(f"\n{len(targets) - failures}/{len(targets)} uploaded to {dest}/"
          + (" — privacy/ included" if args.privacy else " — privacy/ untouched"))
    print(linux_note)
    print(support_note)
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
