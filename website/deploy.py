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

    /usr/bin/python3 website/deploy.py --support --privacy   # also support/, fr/support/, es/support/

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

`--dry-run` never opens the login file and contacts nothing. It checks that the file
is where a real deploy will look, and says so on its own line, so it is safe to run
from any machine. On a machine without the file that line says NOT FOUND, which is
expected anywhere but the release machine.

The login file is `Lighting Arduino/.env` beside the main checkout, found from git's
common directory, so a worktree finds the same file as the shared checkout (#668).
`MEGAPDF_DEPLOY_ENV_DIR=<folder>` overrides the folder.

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
import unicodedata
import urllib.error
import urllib.parse
import urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SITE = os.path.join(REPO, "website", "megapdf")
ENV_DIR_VARIABLE = "MEGAPDF_DEPLOY_ENV_DIR"   # the folder holding the cPanel .env (#668); a path, never the file's contents
ENV_FILE_NAME = ".env"


def default_env_dir(repo=REPO):
    """The Lighting Arduino folder beside the MAIN checkout (#668).

    From a worktree at <main>/.claude/worktrees/<name>, `dirname(REPO)` is
    <main>/.claude/worktrees, which is wrong. git's common directory is the main
    checkout's .git from either place: relative (".git") from the main checkout,
    absolute from a worktree, and joining it onto `repo` handles both. Outside git
    (an unpacked tarball), it falls back to beside this tree, as before.
    """
    try:
        common = subprocess.run(["git", "-C", repo, "rev-parse", "--git-common-dir"],
                                capture_output=True, text=True, check=True).stdout.strip()
        main_checkout = os.path.dirname(os.path.normpath(os.path.join(repo, common)))
    except (OSError, subprocess.CalledProcessError):
        main_checkout = repo
    return os.path.join(os.path.dirname(main_checkout), "Lighting Arduino")


def env_path():
    """Where the login file is looked for, and whether the override chose it.
    Never opens it: the dry run only asks whether it is there."""
    override = os.environ.get(ENV_DIR_VARIABLE, "").strip()
    folder = os.path.expanduser(override) if override else default_env_dir()
    return os.path.join(folder, ENV_FILE_NAME), bool(override)
DEFAULT_DEST = "/public_html/megapdf"

# Every UAPI call carries the cPanel token in its Authorization header, so the server must
# prove it is ours before anything is sent: the default context checks the certificate
# against the system's trusted store and checks the host name (#726). Verification was off
# here until 2026-11-15 (carried over from the Lighting Arduino deploy script); the host's
# Let's Encrypt certificate for electricrv.ca verifies on port 2083, so nothing is pinned.
# If it ever stops verifying, the deploy should fail -- never turn verification back off.
CTX = ssl.create_default_context()
TLS_NOTE = ("TLS: the server's certificate is checked against the system's trusted store, "
            "host name included, before the token is sent")


class Server:
    """The UAPI endpoint. Constructed only when something is actually uploaded,
    so --dry-run never opens the login file."""

    def __init__(self, path):
        env = {}
        if not os.path.isfile(path):
            raise SystemExit(
                f"deploy.py: no login file at {path}\n"
                f"  Set {ENV_DIR_VARIABLE} to the folder that holds the cPanel .env "
                f"(website/README.md, Deploying). Nothing was uploaded.")
        with open(path) as f:
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
# The language folders (#845). An opt-in directory inside one (fr/support/, #859) is
# gated by the same flag as the English one, so --support carries all three languages
# and leaving it off keeps all three off the server.
LANGS = ("fr", "es")


def opt_in_key(rel):
    """The OPT_IN key that gates a directory under website/megapdf/ (a relative path
    with os.sep separators), or None when it always goes up."""
    parts = [p for p in rel.split(os.sep) if p]
    if not parts:
        return None
    if parts[0] in LANGS and len(parts) > 1 and parts[1] in OPT_IN:
        return parts[1]
    return parts[0] if parts[0] in OPT_IN else None

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
            "dists/stable/main/binary-amd64/Packages",
            "dists/stable/main/binary-arm64/Packages"]
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
    # Both architectures (#871): the amd64 index sets the versions, and the arm64 one must
    # list the newest of them too, or an ARM machine subscribed to the repository would be
    # offered nothing while the page says it is supported.
    def index_versions(arch):
        with open(os.path.join(apt, f"dists/stable/main/binary-{arch}/Packages")) as f:
            return re.findall(r"^Version: (.+)$", f.read(), flags=re.M)
    versions = index_versions("amd64")
    arm_versions = index_versions("arm64")
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
    offered_arm = sorted(set(re.findall(r"megapdf_([0-9][^_\s\"]*)_arm64\.deb", page)))
    if offered_arm != offered:
        raise SystemExit(f"--linux: linux/index.html offers the amd64 .deb {offered} but the "
                         f"arm64 .deb {offered_arm or 'not at all'}. Offer both, same version.")
    if offered[0] not in arm_versions:
        raise SystemExit(f"--linux: the arm64 index holds {arm_versions or 'nothing'}, "
                         f"not the {offered[0]} the page offers.")
    for arch in ("amd64", "arm64"):
        pool = os.path.join(apt, "pool/main/m/megapdf", f"megapdf_{offered[0]}_{arch}.deb")
        if not os.path.isfile(pool):
            raise SystemExit(f"--linux: {os.path.relpath(pool, SITE)} is not in the pool")
    return (f"apt/ verified (amd64 and arm64): signed by apt/megapdf.gpg, holds {', '.join(versions)}; "
            f"linux/index.html offers {offered[0]}; Snap section {'live' if snap else 'held back'}")


def check_linux_downloads():
    """Refuse a --linux deploy whose page links a GitHub release download that does not
    answer. The page names each file by URL, and the repository checks above say nothing
    about the release: when the arm64 links were added (#871) the page pointed at
    linux-v2.5.0, which has no arm64 files, and every check above would still have
    passed once the repository had them. A draft release's assets 404 publicly too,
    which is what this should catch. Network, so a real deploy only: a dry run promises
    to contact nothing. Returns a line describing what was checked."""
    with open(os.path.join(SITE, "linux", "index.html"), encoding="utf-8") as f:
        page = f.read()
    urls = sorted(set(re.findall(
        r"https://github\.com/SlyWombat/MegaPDF/releases/download/[^\"'\s<>]+", page)))
    if not urls:
        raise SystemExit("--linux: linux/index.html links no release download at all")
    ctx = ssl.create_default_context()
    bad = []
    for url in urls:
        req = urllib.request.Request(url, method="HEAD", headers={"User-Agent": "megapdf-deploy"})
        try:
            with urllib.request.urlopen(req, timeout=30, context=ctx) as resp:
                if resp.status != 200:
                    bad.append(f"{url} ({resp.status})")
        except urllib.error.HTTPError as e:
            bad.append(f"{url} ({e.code})")
        except urllib.error.URLError as e:
            bad.append(f"{url} ({e.reason})")
    if bad:
        raise SystemExit("--linux: linux/index.html links release downloads that do not answer "
                         "(is the release published, with these files?):\n  " + "\n  ".join(bad))
    return f"linux/index.html: all {len(urls)} release downloads answer 200"


# The platforms the support page's answers may be tagged for; "all" means every
# one. Kept here so a typo in data-plat is a refused deploy rather than an answer
# that silently never appears.
SUPPORT_PLATFORMS = {"all", "windows", "mac", "ios", "android", "linux"}
SUPPORT_ADDRESS = "info@electricrv.ca"
# The start of every support subject: "MegaPDF support MP-YYMMDD-XXXX [Platform] [Kind]".
# The Outlook rule on the mailbox and the in-house support system both read it (#635).
SUPPORT_SUBJECT_PREFIX = '"MegaPDF support " + reference()'


# The support page in each language (#859), as paths under website/megapdf/, and the
# lang attribute each one must carry. English is the reference the others are held to.
SUPPORT_PAGES = {"en": ("support/index.html", "en"),
                 "fr": ("fr/support/index.html", "fr-CA"),
                 "es": ("es/support/index.html", "es-419")}
# Lines of the composed message that the support system parses (#635). They stay in
# English on every page; a translated page that translated one of them would be a page
# whose mail the system cannot read.
SUPPORT_INTERFACE = ['"Reference: "', '"App: MegaPDF"', '"Platform: "', '"Kind: "',
                     '"The support page suggested"', '"The support page suggested nothing."',
                     '"  [reader: not it]"', '"  [reader: that was it]"', '"Answer ids: "',
                     'windows: "Windows", mac: "Mac", ios: "iPhone / iPad"',
                     'android: "Android", linux: "Linux"',
                     'bug: "Something is broken", question: "How do I…?"',
                     'idea: "An idea or a request"']
# The matcher's normaliser (#859): accents folded away on both the typed text and the
# terms. Every page must carry this exact line, because fold_term() below assumes it.
SUPPORT_NORM = 'String(s).normalize("NFD").replace(/[\\u0300-\\u036f]/g, "").toLowerCase()'


def fold_term(term):
    """A data-match/data-strong/data-not term as the page's norm() sees it: accents
    folded away, as `normalize("NFD")` and dropping U+0300-U+036F do in the browser."""
    decomposed = unicodedata.normalize("NFD", term)
    return "".join(c for c in decomposed if not "\u0300" <= c <= "\u036f")


def support_answers(page, where):
    """The answers list of one support page, checked for what its own matcher needs:
    [(id, data-plat), ...] in page order. Refuses on the first fault, naming `where`."""
    blocks = re.findall(r'<details class="answer"([^>]*)>(.*?)</details>', page, flags=re.S)
    if len(blocks) < 5:
        raise SystemExit(f"--support: {where}: the answers list holds {len(blocks)} answers. "
                         "That is not a support page; check that details.answer markup is intact.")
    found = []
    for attrs, body in blocks:
        aid = re.search(r'\bid="([^"]+)"', attrs)
        if not aid:
            raise SystemExit(f"--support: {where}: an answer has no id (attributes: "
                             f"{attrs.strip()!r}). The composed message names answers by id.")
        aid = aid.group(1)
        if "<summary>" not in body or 'class="body"' not in body:
            raise SystemExit(f"--support: {where}: answer {aid} has no <summary> or no .body; the "
                             "matcher reads the title from one and the text from the other.")
        plats = (re.search(r'\bdata-plat="([^"]*)"', attrs) or _empty()).group(1).split()
        if not plats:
            raise SystemExit(f"--support: {where}: answer {aid} has no data-plat (use "
                             'data-plat="all" for one that applies everywhere)')
        unknown = sorted(set(plats) - SUPPORT_PLATFORMS)
        if unknown:
            raise SystemExit(f"--support: {where}: answer {aid} names the unknown platform(s) "
                             f"{', '.join(unknown)}; known: {', '.join(sorted(SUPPORT_PLATFORMS))}")
        if "all" in plats and len(plats) > 1:
            raise SystemExit(f'--support: {where}: answer {aid} is data-plat="all" and also '
                             "names platforms; pick one or the other")

        def listed(name):
            return [t.strip() for t in
                    (re.search(rf'\b{name}="([^"]*)"', attrs) or _empty()).group(1).split("|")
                    if t.strip()]
        terms, strong, nots = listed("data-match"), listed("data-strong"), listed("data-not")
        if not terms and not strong:
            raise SystemExit(f"--support: {where}: answer {aid} has no data-match or data-strong "
                             "terms, so nothing anyone types can ever surface it")
        for t in terms + strong + nots:
            # The matcher folds accents away, lowercases, and replaces everything that is
            # not a letter or a digit with a space, then looks for the term between
            # spaces. A term with a capital or a symbol in it is written in a form
            # nobody's text will ever be in; write it as plain lowercase words.
            if t != t.lower() or re.search(r"[^a-z0-9 œæ]", fold_term(t)):
                raise SystemExit(f"--support: {where}: answer {aid} has the unmatchable term "
                                 f"{t!r} — the matcher lowercases, folds accents and strips "
                                 "punctuation, so a term with a capital or a symbol in it "
                                 "never fires")
        # data-strong is worth 2 on its own and data-match 1, so a term in both would
        # quietly score 3 and outrank everything. Compared as the matcher compares them,
        # so "français" and "francais" are the same term.
        both = sorted({fold_term(t) for t in terms} & {fold_term(t) for t in strong})
        if both:
            raise SystemExit(f"--support: {where}: answer {aid} lists {', '.join(both)} in both "
                             "data-match and data-strong (accents aside); pick one")
        # A short list is the whole point of data-strong: one of these words alone
        # surfaces the answer, so a long list of them is the matcher guessing again.
        if len(strong) > 6:
            raise SystemExit(f"--support: {where}: answer {aid} has {len(strong)} data-strong "
                             "terms. Those fire on their own, so keep them to the few words "
                             "that really can only mean this answer (six at most).")
        found.append((aid, " ".join(plats)))
    ids = [a for a, _ in found]
    dupes = sorted({i for i in ids if ids.count(i) > 1})
    if dupes:
        raise SystemExit(f"--support: {where}: duplicate answer id(s) {', '.join(dupes)}")
    return found


def check_support(include_privacy):
    """Refuse a --support deploy that would publish the support pages without the
    privacy section covering them, or with an answers list their own matcher cannot
    use. Returns a line describing what was checked.

    There is one support page per language (#859): English, French and Spanish. Each
    gets every check below, and the French and Spanish ones are also held to the
    English one: the same answer ids with the same platforms (the mailbox links
    support/#<id> in any language), and the same machine-read message lines.

    The page is static and posts nothing, but it is still the page that composes a
    message to us, so the policy has to be part of the same upload (#418, the same
    ordering #501 followed). The rest is the answers list: it is the single copy of
    every answer — the browser-side matcher reads these very elements — so an
    attribute the matcher cannot read is an answer nobody will ever be shown, and
    that is worth refusing a deploy over rather than discovering months later.
    """
    pages = {}
    for lang, (rel, _) in SUPPORT_PAGES.items():
        path = os.path.join(SITE, rel)
        if not os.path.isfile(path):
            raise SystemExit(f"--support: website/megapdf/{rel} is not there")
        with open(path, encoding="utf-8") as f:
            pages[lang] = f.read()

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

    # #635: the mailbox this page writes to is read and answered by an automated
    # system, and the policy says so in the paragraph with this anchor. The page and the
    # sentence go up together or not at all, the same ordering the section above has.
    if 'id="support-triage"' not in policy:
        raise SystemExit('--support: privacy/index.html has no id="support-triage" '
                         "paragraph. Support mail is read and answered by an automated "
                         "system (#635), and the policy has to say so before, or in the "
                         "same deploy as, the page that sends mail to it.")

    reference = None
    for lang, page in pages.items():
        rel, lang_attr = SUPPORT_PAGES[lang]
        where = rel
        if not re.search(rf'<html lang="{re.escape(lang_attr)}">', page):
            raise SystemExit(f'--support: {where} is not <html lang="{lang_attr}">')

        # #635: the support system sorts mail by the subject this page writes, so the
        # subject's shape is an interface, not wording. See website/README.md, 'The
        # support mailbox'. Refuse a page that no longer builds it, in any language.
        if SUPPORT_SUBJECT_PREFIX not in page:
            raise SystemExit(f"--support: {where} no longer builds a subject that starts "
                             f"{SUPPORT_SUBJECT_PREFIX!r}. The Outlook rule and the support "
                             "system key on it (#635); change them first.")
        missing = [k for k in SUPPORT_INTERFACE if k not in page]
        if missing:
            raise SystemExit(f"--support: {where} no longer writes {', '.join(missing)} into "
                             "the message. The support system parses those lines in English "
                             "on every page (#635, #859); translate only what a person reads.")
        if SUPPORT_NORM not in page:
            raise SystemExit(f"--support: {where}'s matcher no longer folds accents with "
                             f"{SUPPORT_NORM!r}. The term check here assumes it does (#859).")

        if SUPPORT_ADDRESS not in page:
            raise SystemExit(f"--support: {where} never names {SUPPORT_ADDRESS}. "
                             "It is the one page whose job is that address.")

        # Every page names the other two, both as hreflang alternates and in its
        # language switcher, so a reader and a search engine can each find their own.
        for other in SUPPORT_PAGES:
            if not re.search(rf'<link rel="alternate" hreflang="{other}" href="https://megapdf\.ca/'
                             rf'{"" if other == "en" else other + "/"}support/">', page):
                raise SystemExit(f'--support: {where} has no <link rel="alternate" '
                                 f'hreflang="{other}"> for the {other} support page')
            if not re.search(rf'<nav class="langs"[^>]*>(?:(?!</nav>).)*hreflang="{other}"',
                             page, flags=re.S):
                raise SystemExit(f"--support: {where}'s language switcher has no link to the "
                                 f"{other} page")

        # The policy says, in so many words, that this page submits nothing and sends
        # nothing. That is a promise about the bytes, so the deploy checks the bytes:
        # anything that could carry what a reader typed off their machine has to fail
        # here rather than be noticed by someone reading the source months later.
        # Checked on the page as it will be uploaded — regions resolved and comments
        # stripped — because the source's own notes talk about forms and fetches in prose.
        for label, live in (("with Linux live", True), ("with Linux held back", False)):
            sent = resolve(page, {"linux": live, "snap": live, "support": True})
            if re.search(r"<form\b", sent, flags=re.I):
                raise SystemExit(f"--support: {where} has a <form> ({label}). Policy §4 says "
                                 "it has no form to submit and nothing to submit it to. If "
                                 "that is changing, the policy changes first.")
            leak = re.search(r"\bfetch\s*\(|XMLHttpRequest|sendBeacon|WebSocket|EventSource"
                             r"|method\s*=\s*[\"']post", sent, flags=re.I)
            if leak:
                raise SystemExit(f"--support: {where} contains {leak.group(0)!r} ({label}), "
                                 "which could send what someone typed. Policy §4 promises it "
                                 "cannot. If that is changing, the policy changes first.")
            scripts = re.findall(r'<script[^>]*\bsrc="([^"]+)"', sent)
            if scripts != ["https://electricrv.ca/api/analytics/a.js"]:
                raise SystemExit(f"--support: {where}'s external scripts are {scripts} "
                                 f"({label}) — the only one it may load is the page-view "
                                 "counter disclosed in policy §3.")
            # <link rel="canonical"> and rel="alternate" name pages and load nothing
            # (third_party_loads() skips them for the same reason); everything else
            # that names an origin has to be one the policy accounts for.
            named = re.sub(r'<link\b[^>]*\brel="(?:canonical|alternate)"[^>]*>', "", sent)
            origins = sorted({m for m in re.findall(r"https?://([a-z0-9.\-]+)/", named)})
            allowed = ["electricrv.ca", "github.com"]  # #846: no third-party fonts
            if origins != allowed:
                raise SystemExit(f"--support: {where} names the origin(s) {origins} "
                                 f"({label}); only {allowed} are accounted for in the privacy "
                                 "policy.")

        answers = support_answers(page, where)
        if reference is None:
            reference = answers
        elif answers != reference:
            # The mailbox links support/#<id> whatever language the reader wrote in, and
            # the platform filter has to treat a reader the same in every language.
            ref, got = dict(reference), dict(answers)
            lost = sorted(set(ref) - set(got))
            extra = sorted(set(got) - set(ref))
            plats = sorted(a for a in set(ref) & set(got) if ref[a] != got[a])
            raise SystemExit(f"--support: {where}'s answers differ from support/index.html's"
                             + (f"; missing {', '.join(lost)}" if lost else "")
                             + (f"; extra {', '.join(extra)}" if extra else "")
                             + (f"; other data-plat on {', '.join(plats)}" if plats else "")
                             + ("; same answers in another order" if not (lost or extra or plats)
                                else "")
                             + ". Ids and platforms are the same in every language.")

        # The support page must not link a part of the site a deploy can leave off.
        # Resolved with Linux held back, no reference to linux/ may survive.
        held = resolve(page, {"linux": False, "snap": False, "support": True})
        if re.search(r'href="(?:\.\./)+linux/', held):
            raise SystemExit(f"--support: {where} links linux/ outside a <!--linux:live--> "
                             "region, so that link 404s on a deploy without --linux. Move it "
                             "inside one.")
    return (f"support/ checked in {len(pages)} languages ({', '.join(pages)}): "
            f"{len(reference)} answers each, all matchable, same ids and platforms; "
            f'privacy/ carries the id="support" section and the id="support-triage" '
            "paragraph and goes up with it")


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
        key = opt_in_key(rel)
        if key and not wanted[OPT_IN[key]]:
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


# #846: hosts a page may load anything from. Links people click (the stores, GitHub)
# are not loads and are not checked; fonts, scripts, styles, images and frames are.
OWN_HOSTS = {"electricrv.ca", "megapdf.ca", "www.megapdf.ca"}
LOAD = re.compile(
    r"<(?:script|img|iframe|source|video|audio|embed)\b[^>]*?\b(?:src|poster)\s*=\s*[\"']([^\"']+)"
    r"|<link\b([^>]*)>"
    r"|url\(\s*[\"']?([^\"')]+)"
    r"|@import\s+[\"']([^\"']+)", re.I)


def third_party_loads(html):
    """Every URL the page would fetch from a host that is not ours. The privacy
    policy says no third party is involved in this site (§3), so a font, script,
    stylesheet or image from anywhere else is a broken promise, not a style
    choice. <link rel="canonical"> and rel="alternate" name pages; they load nothing."""
    found = []
    for m in LOAD.finditer(strip_comments(html)):
        url = m.group(1) or m.group(3) or m.group(4)
        if m.group(2) is not None:
            attrs = m.group(2)
            rel = re.search(r"\brel\s*=\s*[\"']([^\"']+)", attrs, re.I)
            if rel and set(rel.group(1).lower().split()) <= {"canonical", "alternate"}:
                continue
            href = re.search(r"\bhref\s*=\s*[\"']([^\"']+)", attrs, re.I)
            url = href.group(1) if href else None
        if not url:
            continue
        host = re.match(r"(?:https?:)?//([^/:?#]+)", url.strip(), re.I)
        if host and host.group(1).lower() not in OWN_HOSTS:
            found.append(url.strip())
    return found


def staged(targets, live):
    """The files as they will be uploaded: every .html resolved into a temporary
    copy under its own name, everything else as it is on disk."""
    tmp = tempfile.mkdtemp(prefix="megapdf-deploy-")
    out = []
    for i, (path, remote) in enumerate(targets):
        if path.endswith(".html"):
            with open(path, encoding="utf-8") as f:
                html = resolve(f.read(), live)
            outside = third_party_loads(html)
            if outside:
                shutil.rmtree(tmp, ignore_errors=True)
                raise SystemExit(f"{os.path.relpath(path, SITE)} loads from outside this site: "
                                 f"{', '.join(outside)}. Policy §3 says no third party is "
                                 "involved; serve it from here instead (#846).")
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
    login_path, login_overridden = env_path()
    # A real deploy reads the login file before anything is staged, so a missing
    # one stops here with no temporary folder left behind (#668).
    server = None if args.dry_run else Server(login_path)
    if args.linux:
        # Before anything is staged or uploaded, for the same reason as the login (#871).
        downloads_note = ("linux/index.html release downloads: not checked (a dry run contacts "
                          "nothing; the real deploy checks each one answers 200)"
                          if args.dry_run else check_linux_downloads())
        linux_note = linux_note + "\n" + downloads_note
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
        where = f"from ${ENV_DIR_VARIABLE}" if login_overridden else "beside the main checkout"
        if os.path.isfile(login_path):
            print(f"Login file ({where}): {login_path} — found")
        else:
            print(f"Login file ({where}): {login_path} — NOT FOUND. A real deploy stops here; "
                  f"set {ENV_DIR_VARIABLE} to the folder that holds it.")
        print(TLS_NOTE)
        print("To do it for real, run the same command without --dry-run.")
        shutil.rmtree(tmp, ignore_errors=True)
        return 0

    failures = 0
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
