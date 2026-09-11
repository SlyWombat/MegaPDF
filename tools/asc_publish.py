#!/usr/bin/env python3
"""Push a MegaPDF release's metadata to App Store Connect.

Everything App Store Connect wants for a version, from the repo, so a
submission is a command and not an afternoon of pasting: the listing copy per
language from docs/app-store-listing.md, the screenshots and preview videos
from a capture folder, the App Review notes from docs/app-review-notes.md,
and the build. Idempotent: run it again and it replaces what is there.

    tools/asc_publish.py status                      what the record looks like now
    tools/asc_publish.py version 1.7.0               name the editable version (or create it)
    tools/asc_publish.py copy                        names, subtitles, descriptions, keywords, URLs
    tools/asc_publish.py screenshots <captures-dir>  <dir>/ios-screenshots/<lang>/*.png
    tools/asc_publish.py previews <captures-dir>     <dir>/ios/<lang>/*-preview.mp4
    tools/asc_publish.py review [attachment...]      notes + contact, plus files for the reviewer
    tools/asc_publish.py build [<build number>]      attach the newest processed build (or the given one)
    tools/asc_publish.py submit                      create and submit the review submission

Credentials come from the environment, as tools/asc.sh takes them:
ASC_KEY_FILE (a .p8), ASC_KEY_ID, ASC_ISSUER_ID. Run under PATH=/usr/bin:/bin
in WSL so python3 and openssl are the Linux ones.
"""
import base64
import hashlib
import json
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
API = "https://api.appstoreconnect.apple.com"
APP_ID = os.environ.get("ASC_APP_ID", "6799522972")          # MegaPDF (iOS)
PLATFORM = os.environ.get("ASC_PLATFORM", "IOS")
# App Store Connect locale codes; the repo (docs, capture folders) calls France
# French plain "fr", App Store Connect calls it "fr-FR".
LOCALES = ["en-CA", "fr-CA", "fr-FR"]
REPO_LOCALE = {"en-CA": "en", "fr-FR": "fr"}

# Listing slots, in order (docs/app-store-listing.md § Screenshots).
SHOT_ORDER = ["viewer", "text", "search", "sign", "draw", "home"]
SHOT_SETS = {"iphone-6_9": "APP_IPHONE_67", "ipad-13": "APP_IPAD_PRO_3GEN_129"}
PREVIEW_SETS = {"iphone-6_9": "IPHONE_67", "ipad-13": "IPAD_PRO_3GEN_129"}


# --- client -----------------------------------------------------------------

def token():
    keyfile = os.environ["ASC_KEY_FILE"]
    kid = os.environ["ASC_KEY_ID"]
    iss = os.environ["ASC_ISSUER_ID"]
    b64 = lambda b: base64.urlsafe_b64encode(b).rstrip(b"=").decode()
    now = int(time.time())
    header = b64(json.dumps({"alg": "ES256", "kid": kid, "typ": "JWT"}).encode())
    payload = b64(json.dumps({"iss": iss, "iat": now, "exp": now + 900,
                              "aud": "appstoreconnect-v1"}).encode())
    der = subprocess.run(["openssl", "dgst", "-sha256", "-sign", keyfile],
                         input=f"{header}.{payload}".encode(),
                         capture_output=True, check=True).stdout
    i, out = 2, []
    for _ in range(2):
        ln = der[i + 1]
        out.append(int.from_bytes(der[i + 2:i + 2 + ln], "big"))
        i += 2 + ln
    sig = b64(out[0].to_bytes(32, "big") + out[1].to_bytes(32, "big"))
    return f"{header}.{payload}.{sig}"


_token = None
_token_at = 0


def api(method, path, body=None, quiet=False):
    global _token, _token_at
    if _token is None or time.time() - _token_at > 600:
        _token, _token_at = token(), time.time()
    url = path if path.startswith("http") else API + path
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method,
                                 headers={"Authorization": f"Bearer {_token}",
                                          "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=120) as r:
            raw = r.read()
            return json.loads(raw) if raw else {}
    except urllib.error.HTTPError as e:
        detail = e.read().decode(errors="replace")
        if not quiet:
            print(f"  ! {method} {path} -> {e.code}: {detail[:600]}", file=sys.stderr)
        raise


def paged(path):
    out = []
    while path:
        d = api("GET", path)
        out.extend(d.get("data", []))
        path = d.get("links", {}).get("next")
    return out


def upload_asset(create_path, relationship, set_id, file_path, extra_attrs=None):
    """The three-step App Store Connect asset upload: reserve, PUT the parts, commit.
    `relationship` is the singular relationship name (appScreenshotSet); the
    related type is its plural."""
    size = os.path.getsize(file_path)
    attrs = {"fileName": os.path.basename(file_path), "fileSize": size}
    attrs.update(extra_attrs or {})
    res = api("POST", create_path, {"data": {
        "type": create_path.rsplit("/", 1)[-1], "attributes": attrs,
        "relationships": {relationship: {"data": {"type": relationship + "s", "id": set_id}}}}})
    asset = res["data"]
    ops = asset["attributes"]["uploadOperations"]
    with open(file_path, "rb") as f:
        for op in ops:
            f.seek(op["offset"])
            chunk = f.read(op["length"])
            headers = {h["name"]: h["value"] for h in op["requestHeaders"]}
            req = urllib.request.Request(op["url"], data=chunk, method=op["method"], headers=headers)
            with urllib.request.urlopen(req, timeout=300) as r:
                r.read()
    md5 = hashlib.md5(open(file_path, "rb").read()).hexdigest()
    api("PATCH", f"{create_path}/{asset['id']}", {"data": {
        "type": asset["type"], "id": asset["id"],
        "attributes": {"uploaded": True, "sourceFileChecksum": md5}}})
    return asset["id"]


# --- repo content -----------------------------------------------------------

def listing_copy():
    """Per-locale fields from docs/app-store-listing.md: the fenced block after
    each **Field** heading inside each '### … — `locale`' section."""
    text = open(os.path.join(ROOT, "docs/app-store-listing.md"), encoding="utf-8").read()
    start = text.index("<!-- copy-by-language -->")
    end = text.index("\n## ", start + 30)
    section = text[start:end]
    out = {}
    for m in re.finditer(r"^### .*?`([a-zA-Z-]+)`\s*$", section, re.M):
        locale = m.group(1)
        nxt = re.search(r"^### ", section[m.end():], re.M)
        block = section[m.end(): m.end() + nxt.start() if nxt else len(section)]
        fields = {}
        for fm in re.finditer(r"^\*\*([A-Za-z' ]+)\*\* \[\d+\].*?\n```\n(.*?)\n```", block, re.M | re.S):
            fields[fm.group(1).strip()] = fm.group(2).strip()
        out[locale] = fields
    return out


def review_notes():
    """The Notes field text from docs/app-review-notes.md: between the
    '## Notes field text' heading and the next '---'."""
    text = open(os.path.join(ROOT, "docs/app-review-notes.md"), encoding="utf-8").read()
    start = text.index("## Notes field text") + len("## Notes field text")
    end = text.index("\n---", start)
    notes = text[start:end].strip()
    # Blockquotes are notes to ourselves, not to the reviewer.
    notes = "\n".join(l for l in notes.splitlines() if not l.startswith(">"))
    notes = re.sub(r"\n{3,}", "\n\n", notes)
    # Markdown emphasis does not survive into a plain-text field.
    notes = re.sub(r"\*\*(.+?)\*\*", r"\1", notes)
    notes = notes.replace("`", "")
    return notes


# --- steps ------------------------------------------------------------------

def editable_version():
    versions = paged(f"/v1/apps/{APP_ID}/appStoreVersions?filter[platform]={PLATFORM}&limit=20")
    editable = ("PREPARE_FOR_SUBMISSION", "REJECTED", "DEVELOPER_REJECTED", "METADATA_REJECTED",
                "INVALID_BINARY", "WAITING_FOR_REVIEW", "READY_FOR_REVIEW")
    for v in versions:
        if v["attributes"].get("appVersionState") in editable or v["attributes"].get("appStoreState") in editable:
            return v
    return None


def cmd_status():
    app = api("GET", f"/v1/apps/{APP_ID}")["data"]["attributes"]
    print(f"{app['name']} ({app['bundleId']}), primary {app['primaryLocale']}")
    for v in paged(f"/v1/apps/{APP_ID}/appStoreVersions?limit=20"):
        a = v["attributes"]
        print(f"  version {a['versionString']:8} {a['platform']:5} {a.get('appVersionState')}  {v['id']}")
    for b in api("GET", f"/v1/builds?filter[app]={APP_ID}&sort=-uploadedDate&limit=5")["data"]:
        a = b["attributes"]
        print(f"  build {a['version']:6} {a['processingState']:10} {a['uploadedDate'][:16]}  {b['id']}")


def cmd_version(version_string):
    v = editable_version()
    if v is None:
        v = api("POST", "/v1/appStoreVersions", {"data": {
            "type": "appStoreVersions",
            "attributes": {"platform": PLATFORM, "versionString": version_string},
            "relationships": {"app": {"data": {"type": "apps", "id": APP_ID}}}}})["data"]
        print(f"created version {version_string}: {v['id']}")
    elif v["attributes"]["versionString"] != version_string:
        api("PATCH", f"/v1/appStoreVersions/{v['id']}", {"data": {
            "type": "appStoreVersions", "id": v["id"],
            "attributes": {"versionString": version_string}}})
        print(f"renamed {v['attributes']['versionString']} -> {version_string}: {v['id']}")
    else:
        print(f"version {version_string} already editable: {v['id']}")
    api("PATCH", f"/v1/appStoreVersions/{v['id']}", {"data": {
        "type": "appStoreVersions", "id": v["id"],
        "attributes": {"copyright": "2026 Electric RV", "releaseType": "MANUAL"}}})
    return v


def version_localizations(vid):
    return {l["attributes"]["locale"]: l for l in
            paged(f"/v1/appStoreVersions/{vid}/appStoreVersionLocalizations?limit=50")}


def cmd_copy():
    v = editable_version()
    if v is None:
        sys.exit("no editable version — run `version` first")
    copy = listing_copy()
    locs = version_localizations(v["id"])
    for locale in LOCALES:
        fields = copy.get(locale) or copy.get(REPO_LOCALE.get(locale, locale))
        if not fields:
            print(f"  no copy for {locale} in docs/app-store-listing.md; skipped")
            continue
        attrs = {"description": fields["Description"], "keywords": fields["Keywords"],
                 "promotionalText": fields.get("Promotional text", ""),
                 "supportUrl": "https://github.com/SlyWombat/MegaPDF",
                 "marketingUrl": "https://electricrv.ca/megapdf/"}
        if locale in locs:
            api("PATCH", f"/v1/appStoreVersionLocalizations/{locs[locale]['id']}", {"data": {
                "type": "appStoreVersionLocalizations", "id": locs[locale]["id"], "attributes": attrs}})
        else:
            attrs["locale"] = locale
            api("POST", "/v1/appStoreVersionLocalizations", {"data": {
                "type": "appStoreVersionLocalizations", "attributes": attrs,
                "relationships": {"appStoreVersion": {"data": {"type": "appStoreVersions", "id": v["id"]}}}}})
        print(f"  version copy {locale}: description {len(fields['Description'])}, keywords {len(fields['Keywords'])}")

    # Name and subtitle live on the app info, not the version.
    infos = api("GET", f"/v1/apps/{APP_ID}/appInfos")["data"]
    info = next((i for i in infos if i["attributes"].get("appStoreState") not in ("READY_FOR_SALE",)), infos[0])
    ilocs = {l["attributes"]["locale"]: l for l in
             paged(f"/v1/appInfos/{info['id']}/appInfoLocalizations?limit=50")}
    for locale in LOCALES:
        fields = copy.get(locale) or copy.get(REPO_LOCALE.get(locale, locale))
        if not fields:
            continue
        attrs = {"name": fields["Name"], "subtitle": fields["Subtitle"],
                 "privacyPolicyUrl": "https://electricrv.ca/megapdf/privacy/"}
        if locale in ilocs:
            api("PATCH", f"/v1/appInfoLocalizations/{ilocs[locale]['id']}", {"data": {
                "type": "appInfoLocalizations", "id": ilocs[locale]["id"], "attributes": attrs}})
        else:
            attrs["locale"] = locale
            api("POST", "/v1/appInfoLocalizations", {"data": {
                "type": "appInfoLocalizations", "attributes": attrs,
                "relationships": {"appInfo": {"data": {"type": "appInfos", "id": info["id"]}}}}})
        print(f"  app info {locale}: {fields['Name']} — {fields['Subtitle']}")


def _replace_set_contents(list_path, item_type, delete_path_prefix):
    for item in paged(list_path):
        api("DELETE", f"{delete_path_prefix}/{item['id']}")


def cmd_screenshots(captures):
    v = editable_version()
    locs = version_localizations(v["id"])
    for locale in LOCALES:
        folder = os.path.join(captures, "ios-screenshots", REPO_LOCALE.get(locale, locale))
        if locale not in locs or not os.path.isdir(folder):
            print(f"  {locale}: no localization or no folder {folder}; skipped")
            continue
        lid = locs[locale]["id"]
        sets = {s["attributes"]["screenshotDisplayType"]: s for s in
                paged(f"/v1/appStoreVersionLocalizations/{lid}/appScreenshotSets?limit=50")}
        for label, display in SHOT_SETS.items():
            if display in sets:
                sid = sets[display]["id"]
                _replace_set_contents(f"/v1/appScreenshotSets/{sid}/appScreenshots?limit=50",
                                      "appScreenshots", "/v1/appScreenshots")
            else:
                sid = api("POST", "/v1/appScreenshotSets", {"data": {
                    "type": "appScreenshotSets", "attributes": {"screenshotDisplayType": display},
                    "relationships": {"appStoreVersionLocalization": {"data": {
                        "type": "appStoreVersionLocalizations", "id": lid}}}}})["data"]["id"]
            n = 0
            for state in SHOT_ORDER:
                path = os.path.join(folder, f"{label}-{state}.png")
                if os.path.exists(path):
                    upload_asset("/v1/appScreenshots", "appScreenshotSet", sid, path)
                    n += 1
            print(f"  {locale} {display}: {n} screenshots")


def cmd_previews(captures):
    v = editable_version()
    locs = version_localizations(v["id"])
    for locale in LOCALES:
        folder = os.path.join(captures, "ios", REPO_LOCALE.get(locale, locale))
        if locale not in locs or not os.path.isdir(folder):
            print(f"  {locale}: no localization or no folder {folder}; skipped")
            continue
        lid = locs[locale]["id"]
        sets = {s["attributes"]["previewType"]: s for s in
                paged(f"/v1/appStoreVersionLocalizations/{lid}/appPreviewSets?limit=50")}
        for label, ptype in PREVIEW_SETS.items():
            path = os.path.join(folder, f"{label}-preview.mp4")
            if not os.path.exists(path):
                continue
            if ptype in sets:
                sid = sets[ptype]["id"]
                _replace_set_contents(f"/v1/appPreviewSets/{sid}/appPreviews?limit=50",
                                      "appPreviews", "/v1/appPreviews")
            else:
                sid = api("POST", "/v1/appPreviewSets", {"data": {
                    "type": "appPreviewSets", "attributes": {"previewType": ptype},
                    "relationships": {"appStoreVersionLocalization": {"data": {
                        "type": "appStoreVersionLocalizations", "id": lid}}}}})["data"]["id"]
            upload_asset("/v1/appPreviews", "appPreviewSet", sid, path, {"mimeType": "video/mp4"})
            print(f"  {locale} {ptype}: {os.path.basename(path)}")


def cmd_review(attachments):
    v = editable_version()
    notes = review_notes()
    try:
        detail = api("GET", f"/v1/appStoreVersions/{v['id']}/appStoreReviewDetail", quiet=True)["data"]
    except urllib.error.HTTPError:
        detail = None
    attrs = {"notes": notes, "demoAccountRequired": False}
    if detail:
        api("PATCH", f"/v1/appStoreReviewDetails/{detail['id']}", {"data": {
            "type": "appStoreReviewDetails", "id": detail["id"], "attributes": attrs}})
        did = detail["id"]
        c = detail["attributes"]
        print(f"  review detail updated; contact {c.get('contactFirstName')} {c.get('contactLastName')} "
              f"{c.get('contactEmail')} {c.get('contactPhone')}")
    else:
        attrs.update({"contactFirstName": "David", "contactLastName": "Seaman",
                      "contactEmail": "dave@drscapital.com",
                      "contactPhone": os.environ.get("ASC_CONTACT_PHONE", "")})
        did = api("POST", "/v1/appStoreReviewDetails", {"data": {
            "type": "appStoreReviewDetails", "attributes": attrs,
            "relationships": {"appStoreVersion": {"data": {"type": "appStoreVersions", "id": v["id"]}}}}})["data"]["id"]
        print("  review detail created")
    print(f"  notes: {len(notes)} characters")
    if attachments:
        for a in paged(f"/v1/appStoreReviewDetails/{did}/appStoreReviewAttachments?limit=50"):
            api("DELETE", f"/v1/appStoreReviewAttachments/{a['id']}")
        for path in attachments:
            upload_asset("/v1/appStoreReviewAttachments", "appStoreReviewDetail", did, path)
            print(f"  attached {os.path.basename(path)} ({os.path.getsize(path):,} bytes)")


def cmd_build(number=None):
    v = editable_version()
    builds = api("GET", f"/v1/builds?filter[app]={APP_ID}&sort=-uploadedDate&limit=10")["data"]
    if number:
        builds = [b for b in builds if b["attributes"]["version"] == str(number)]
    builds = [b for b in builds if b["attributes"]["processingState"] == "VALID" and not b["attributes"]["expired"]]
    if not builds:
        sys.exit("no processed build to attach yet")
    b = builds[0]
    api("PATCH", f"/v1/appStoreVersions/{v['id']}/relationships/build",
        {"data": {"type": "builds", "id": b["id"]}})
    print(f"  attached build {b['attributes']['version']} ({b['attributes']['uploadedDate'][:16]}) to {v['attributes']['versionString']}")


def cmd_submit():
    v = editable_version()
    # An unsubmitted submission may already exist from an earlier attempt
    # (previews still transcoding, say); adding a second is refused.
    pending = api("GET", f"/v1/reviewSubmissions?filter[app]={APP_ID}&filter[state]=READY_FOR_REVIEW&limit=1")["data"]
    sub = pending[0] if pending else api("POST", "/v1/reviewSubmissions", {"data": {
        "type": "reviewSubmissions", "attributes": {"platform": PLATFORM},
        "relationships": {"app": {"data": {"type": "apps", "id": APP_ID}}}}})["data"]
    items = api("GET", f"/v1/reviewSubmissions/{sub['id']}/items")["data"]
    if items:
        print(f"  submission {sub['id']} already holds {len(items)} item(s)")
    else:
        api("POST", "/v1/reviewSubmissionItems", {"data": {
            "type": "reviewSubmissionItems",
            "relationships": {"reviewSubmission": {"data": {"type": "reviewSubmissions", "id": sub["id"]}},
                              "appStoreVersion": {"data": {"type": "appStoreVersions", "id": v["id"]}}}}})
    api("PATCH", f"/v1/reviewSubmissions/{sub['id']}", {"data": {
        "type": "reviewSubmissions", "id": sub["id"], "attributes": {"submitted": True}}})
    print(f"  submitted {v['attributes']['versionString']} for review ({sub['id']})")


def main(argv):
    if not argv:
        sys.exit(__doc__)
    cmd, args = argv[0], argv[1:]
    {"status": lambda: cmd_status(),
     "version": lambda: cmd_version(args[0]),
     "copy": lambda: cmd_copy(),
     "screenshots": lambda: cmd_screenshots(args[0]),
     "previews": lambda: cmd_previews(args[0]),
     "review": lambda: cmd_review(args),
     "build": lambda: cmd_build(args[0] if args else None),
     "submit": lambda: cmd_submit()}[cmd]()


if __name__ == "__main__":
    main(sys.argv[1:])
