#!/usr/bin/env python3
"""Google Play store listing and production release via the Play Developer API.

The listing text per language comes from android/RELEASING.md (the
copy-by-language section that tools/gen_listing_copy.py generates), the
screenshots from a capture folder, the release notes from the same section.
play_submit.py stays the internal-track pipeline step; this is the deliberate,
by-hand production step that android/RELEASING.md describes.

    play_listing.py status                          read-only: listings, images, tracks
    play_listing.py push <captures> [--production <vc>] [--commit]
                                                    one edit: text, screenshots, and
                                                    optionally a production release;
                                                    validated always, committed only
                                                    with --commit
    play_listing.py readback <captures> [--production <vc>]
                                                    fresh edit, compare to the sources

<captures> may be `--text-only` instead: the listing text and the release notes
are set or compared, and the screenshots on Play are left exactly as they are.

<captures> holds {phone,tablet}/{en,fr-CA,fr-FR}/android-<pose>.png.
Key: PLAY_SA_PATH (a file) or PLAY_SA_JSON, as play_submit.py takes it.
"""
import hashlib
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import play_submit as ps  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BASE = f"/androidpublisher/v3/applications/{ps.PACKAGE}"
UPLOAD = f"/upload/androidpublisher/v3/applications/{ps.PACKAGE}"
LOCALES = {"en-CA": "en", "fr-CA": "fr-CA", "fr-FR": "fr-FR"}   # Play -> capture folder
# Listing order (docs/release-notes/2.0/capture-gate-report.md §7): the signed
# agreement first, the 2.0 text editing next, redaction last because a marked
# outline sells least; the tablet never leads with its mostly empty home.
ORDER = ["viewer", "text-edit", "text", "search", "sign", "draw", "home", "redact"]
IMAGE_TYPES = {"phone": "phoneScreenshots", "tablet": "tenInchScreenshots"}
LIMITS = {"title": 30, "shortDescription": 80, "fullDescription": 4000, "notes": 500}


def copy_by_language():
    """{play locale: {title, shortDescription, fullDescription, notes}}."""
    text = open(os.path.join(ROOT, "android", "RELEASING.md"), encoding="utf-8").read()
    body = text.split("<!-- copy-by-language -->", 1)[1].split("<!-- /copy-by-language -->", 1)[0]
    out = {}
    for sec in re.split(r"^### ", body, flags=re.M)[1:]:
        loc = re.search(r"`([a-z]{2}-[A-Z]{2})`", sec.splitlines()[0]).group(1)
        fields = dict(re.findall(
            r"\*\*(Title|Short description|Full description|Release notes)\*\*[^\n]*\n```\n(.*?)\n```",
            sec, flags=re.S))
        out[loc] = {"title": fields["Title"], "shortDescription": fields["Short description"],
                    "fullDescription": fields["Full description"], "notes": fields["Release notes"]}
        for k, lim in LIMITS.items():
            if len(out[loc][k]) > lim:
                raise SystemExit(f"{loc} {k} is {len(out[loc][k])} > {lim}")
    missing = set(LOCALES) - set(out)
    if missing:
        raise SystemExit(f"no copy for {sorted(missing)}")
    return out


def shots(captures, device, folder):
    files = [os.path.join(captures, device, folder, f"android-{p}.png") for p in ORDER]
    for f in files:
        if not os.path.isfile(f):
            raise SystemExit(f"missing {f}")
    return files


def sha1(path):
    return hashlib.sha1(open(path, "rb").read()).hexdigest()


class Edit:
    def __init__(self):
        self.token = ps.get_token()
        self.id = ps.api(self.token, "POST", f"{BASE}/edits")["id"]

    def call(self, method, path, **kw):
        return ps.api(self.token, method, f"{BASE}/edits/{self.id}{path}", **kw)

    def upload(self, path, file):
        return ps.api(self.token, "POST", f"{UPLOAD}/edits/{self.id}{path}?uploadType=media",
                      content_type="image/png", raw=open(file, "rb").read())

    def discard(self):
        ps.api(self.token, "DELETE", f"{BASE}/edits/{self.id}")


def cmd_status():
    e = Edit()
    try:
        d = e.call("GET", "/details")
        print("default language:", d.get("defaultLanguage"))
        for l in e.call("GET", "/listings").get("listings", []):
            print(f"listing {l['language']}: title={l.get('title')!r} "
                  f"short={len(l.get('shortDescription', ''))} full={len(l.get('fullDescription', ''))}")
            for t in ("phoneScreenshots", "sevenInchScreenshots", "tenInchScreenshots"):
                imgs = e.call("GET", f"/listings/{l['language']}/{t}").get("images", [])
                print(f"    {t}: {len(imgs)}")
        for t in e.call("GET", "/tracks").get("tracks", []):
            for r in t.get("releases", []):
                print(f"track {t['track']}: {r.get('name')} vc={r.get('versionCodes')} "
                      f"status={r.get('status')} notes={[n['language'] for n in r.get('releaseNotes', [])]}")
    finally:
        e.discard()


def production_release(copy, vc):
    return {"track": "production", "releases": [{
        "versionCodes": [str(vc)], "status": "completed",
        "releaseNotes": [{"language": loc, "text": copy[loc]["notes"]} for loc in LOCALES]}]}


def cmd_push(captures, vc, commit):
    copy = copy_by_language()
    plan = None if captures is None else {
        loc: {dev: shots(captures, dev, folder) for dev in IMAGE_TYPES}
        for loc, folder in LOCALES.items()}
    e = Edit()
    try:
        for loc in LOCALES:
            c = copy[loc]
            e.call("PUT", f"/listings/{loc}", body={
                "language": loc, "title": c["title"],
                "shortDescription": c["shortDescription"], "fullDescription": c["fullDescription"]})
            print(f"{loc}: text set")
            for dev, itype in (IMAGE_TYPES.items() if plan else ()):
                e.call("DELETE", f"/listings/{loc}/{itype}")
                for f in plan[loc][dev]:
                    e.upload(f"/listings/{loc}/{itype}", f)
                print(f"{loc}: {itype} <- {len(plan[loc][dev])}")
        if vc:
            e.call("PUT", "/tracks/production", body=production_release(copy, vc))
            print(f"production <- vc {vc}, completed, notes in {list(LOCALES)}")
        e.call("POST", ":validate")
        print("edit validates")
        if commit:
            e.call("POST", ":commit")
            print("edit committed", e.id)
        else:
            e.discard()
            print("dry run: edit discarded")
    except SystemExit:
        try:
            e.discard()
        except SystemExit:
            pass
        raise


def cmd_readback(captures, vc):
    copy = copy_by_language()
    e = Edit()
    bad = 0
    try:
        for loc, folder in LOCALES.items():
            l = e.call("GET", f"/listings/{loc}")
            for k in ("title", "shortDescription", "fullDescription"):
                ok = l.get(k) == copy[loc][k]
                bad += not ok
                print(f"{loc} {k}: {'same' if ok else 'DIFFERENT'} ({len(l.get(k, ''))})")
            for dev, itype in (IMAGE_TYPES.items() if captures else ()):
                got = [i["sha1"] for i in e.call("GET", f"/listings/{loc}/{itype}").get("images", [])]
                want = [sha1(f) for f in shots(captures, dev, folder)]
                ok = got == want
                bad += not ok
                print(f"{loc} {itype}: {len(got)} images, "
                      f"{'same files in the same order' if ok else 'DIFFERENT'}")
        if vc:
            t = e.call("GET", "/tracks/production")
            r = next((r for r in t.get("releases", []) if str(vc) in r.get("versionCodes", [])), None)
            notes = {n["language"]: n["text"] for n in (r or {}).get("releaseNotes", [])}
            ok = bool(r) and r.get("status") == "completed" and all(
                notes.get(loc) == copy[loc]["notes"] for loc in LOCALES)
            bad += not ok
            print(f"production vc {vc}: {r.get('status') if r else 'ABSENT'}, "
                  f"notes {'same' if ok else 'DIFFERENT'} {sorted(notes)}")
    finally:
        e.discard()
    print("readback:", "all match" if not bad else f"{bad} differences")
    return bad


def main(argv):
    vc = argv[argv.index("--production") + 1] if "--production" in argv else None
    if not argv:
        raise SystemExit(__doc__)
    captures = None if len(argv) > 1 and argv[1] == "--text-only" else (argv[1] if len(argv) > 1 else None)
    if argv[0] == "status":
        cmd_status()
    elif argv[0] == "push":
        cmd_push(captures, vc, "--commit" in argv)
    elif argv[0] == "readback":
        sys.exit(1 if cmd_readback(captures, vc) else 0)
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    main(sys.argv[1:])
