#!/usr/bin/env python3
"""Build (or rebuild) manifest.tsv for the #434 public corpus.

The manifest is the corpus. The PDFs are never committed: each row names a URL, the
sha256 of the bytes that URL serves, and where to put them, so fetch.sh can reproduce
the set from scratch on any machine and prove it got the same bytes.

Every URL is pinned to a commit, never a branch, so a row's sha256 stays true. Rebuild
with a newer commit and the hashes change; that is the point of rebuilding.

    tools/stress/public-corpus/build-manifest.py --work /var/tmp/pc-build
    tools/stress/public-corpus/build-manifest.py --checkout verapdf=/path/to/clone ...

Categories are assigned from what is actually in the file (see classify()), not from the
directory it came from, so a row says what the battery will meet.

What this script cannot do from a sandboxed CI container: see SOURCES_BLOCKED below and
the "Network reality" section of README.md. `govinfo` and `safedocs` are out of scope for
now (#434 lists them, but nobody has yet built and verified a generator for either); their
hosts are also unreachable from Anthropic's cloud sandbox, so `--add-source` for them still
refuses rather than shipping an unverified generator.

`irs` and `uscis`, by contrast, ARE implemented and verified (#434 federal-forms
extension, kdocker3, 2026-09-27): both hosts are reachable from a real machine, just not
from the cloud sandbox this file was first written in. Run

    tools/stress/public-corpus/build-manifest.py --add-source irs
    tools/stress/public-corpus/build-manifest.py --add-source uscis

from a machine that can reach www.irs.gov / www.uscis.gov to (re)fetch the federal-forms
rows and merge them into an existing --out manifest; a plain rebuild (no --add-source)
never touches them, so it stays exactly as reproducible from a blocked sandbox as before.
See FEDERAL_SOURCES below and README.md, "Extending".
"""

import argparse
import collections
import hashlib
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

# --------------------------------------------------------------------------------------
# Sources. Each is a public GitHub repository, pinned to a commit, whose licence permits
# redistribution. #434 settled the licence question for all of these; do not re-litigate
# it here, and do not add a source without a licence that allows a third party to fetch
# and keep the files.
# --------------------------------------------------------------------------------------

Source = collections.namedtuple(
    "Source", "key repo commit subdir licence attribution")

SOURCES = [
    Source(
        key="verapdf",
        repo="veraPDF/veraPDF-corpus",
        commit="bb75f4f0073d9350dfd058c0162a367e6fadf25e",
        subdir="",
        licence="CC-BY-4.0",
        attribution="veraPDF Consortium, veraPDF test corpus, CC BY 4.0",
    ),
    Source(
        key="qpdf",
        repo="qpdf/qpdf",
        commit="4eba95899886e851cc41d76886483b347612f2a8",
        subdir="qpdf/qtest",
        licence="Apache-2.0",
        attribution="Jay Berkenbilt and the qpdf contributors, qpdf test suite, Apache-2.0",
    ),
    Source(
        key="pdfium",
        repo="chromium/pdfium",
        commit="a84323421e94f484faca52dd9d027934eba42ab8",
        subdir="testing/resources",
        licence="BSD-3-Clause",
        attribution="The PDFium Authors, PDFium test resources, BSD-3-Clause",
    ),
]

SOURCES_BY_KEY = {s.key: s for s in SOURCES}

# Hosts #434 names for which no verified generator exists yet. `irs` and `uscis` used to
# be listed here too; they moved out once a generator for each was actually written and
# run against the real host (see FEDERAL_SOURCES below). Kept as a hard refusal rather
# than a half-built generator: a manifest row whose hash nobody computed is worse than a
# missing one.
SOURCES_BLOCKED = {
    "govinfo": "www.govinfo.gov",
    "safedocs": "downloads.digitalcorpora.org",
}

# --------------------------------------------------------------------------------------
# US federal fillable forms (IRS, USCIS). Public domain: 17 U.S.C. Sec 105 -- "Copyright
# protection... is not available for any work of the United States Government" -- so
# every row from either source carries licence US-PD-17-USC-105, no attribution required
# (attribution is recorded anyway, as a courtesy and a provenance trail).
#
# Unlike SOURCES above, there is no repository and no commit to pin: each row is fetched
# straight from the agency's own PDF hosting and hashed as fetched. That is a real
# difference in what "reproducible" means here -- a rebuild reproduces the bytes an
# agency serves *today*, not a bytes-identical artifact from four years ago -- and it is
# why these sources are opt-in via --add-source rather than part of the default rebuild
# (see the module docstring). If IRS or USCIS revise a form between two rebuilds, the
# sha256 changes and fetch.sh's hard-failure-on-mismatch (never silently re-fetched over)
# is exactly the right behaviour: it says the source moved, which is true.
#
# Fetched with Python's urllib, not curl: this container's apt-installed curl (8.5,
# OpenSSL 3.0.13) is TLS/HTTP2-fingerprinted and refused outright (403, `server:
# AkamaiGHost`) by www.uscis.gov, even with a browser User-Agent string -- verified
# 2026-09-27 on kdocker3. urllib's stack is not fingerprinted the same way and is not
# refused. curl against www.irs.gov and www.govinfo.gov was not refused either way; the
# switch to urllib is made for all federal fetching regardless, so one code path is
# tested and trusted rather than two.
#
# Selection (150-300 asked for; 186 chosen -- 136 IRS + 50 USCIS -- grouped so the choice
# is legible rather than "however many the scraper happened to find"; counts below are
# exact, collections.Counter(g for _, g in IRS_FORMS + USCIS_FORMS)):
#
#   1040-family (21)   the most-filed federal form and its core schedules: filing-status
#                       radio group, checkboxes throughout, Schedule 8812/EIC repeating
#                       dependent rows, Schedule E's repeating property rows.
#   simple (17)         near-one-page forms with no repeating structure at all -- a
#                       baseline that a "real form is hard" narrative should not overstate.
#   signature (4)       Form 2848 (POA, with its own repeating representative table),
#                       8821, 8879, 8453 -- forms whose entire purpose is a signature field.
#   credits (28)        adjustments and credits with repeating rows (depreciation assets,
#                       capital-gain transactions, noncash-contribution items, monthly
#                       premium-credit tables) and checkbox/radio groups.
#   long (34)           multi-page business/entity returns -- 1120, 1065, 990, 706, 709,
#                       1041, 5471, 8865, 3520(-A), the CIS 433 series -- with deep
#                       /Parent hierarchies and repeating subforms (K-1 schedules,
#                       shareholder/partner tables): the shape #174's page-import refuses
#                       today, and the reason this source exists.
#   employment (12)     checkbox-heavy quarterly/annual employment and excise returns.
#   info-returns (20)   the 1098/1099 families: multi-copy layouts, void/corrected
#                       checkboxes -- the shape a payroll or accounting integration feeds.
#   uscis-simple (11)   short USCIS forms: I-9's checkbox-heavy attestation, G-28/G-325A/
#                       G-1145/G-639 administrative forms.
#   uscis-long (39)     long petitions and applications with deep hierarchical/repeating
#                       sections (family members, employment and address history) -- the
#                       immigration-side counterpart of the IRS "long" group. Includes
#                       I-864's repeating household-member table and N-400, the longest
#                       and most heavily sectioned form either agency publishes.
#
# Every filename below was resolved from the agency's own "about this form" page (IRS) or
# its own forms-by-number listing (USCIS) and confirmed to fetch as a real, current
# %PDF-starting file -- see README.md, "Extending", for how to re-verify or extend this
# list. f1099b.pdf 404s as of 2026-09-27 (IRS's own about-page link is stale) and is
# skipped rather than guessed at.
IRS_FORMS = [
    ("f1040.pdf", "1040-family"), ("f1040s.pdf", "1040-family"),
    ("f1040x.pdf", "1040-family"), ("f1040es.pdf", "1040-family"),
    ("f1040v.pdf", "1040-family"), ("f1040nr.pdf", "1040-family"),
    ("f1040s1.pdf", "1040-family"), ("f1040s2.pdf", "1040-family"),
    ("f1040s3.pdf", "1040-family"), ("f1040sa.pdf", "1040-family"),
    ("f1040sb.pdf", "1040-family"), ("f1040sc.pdf", "1040-family"),
    ("f1040sd.pdf", "1040-family"), ("f1040se.pdf", "1040-family"),
    ("f1040sf.pdf", "1040-family"), ("f1040sh.pdf", "1040-family"),
    ("f1040sr.pdf", "1040-family"), ("f1040sse.pdf", "1040-family"),
    ("f1040sei.pdf", "1040-family"), ("f1040s8.pdf", "1040-family"),
    ("f1040sj.pdf", "1040-family"),

    ("fw4.pdf", "simple"), ("fw9.pdf", "simple"), ("fw2.pdf", "simple"),
    ("fw2c.pdf", "simple"), ("fw3.pdf", "simple"), ("f4506t.pdf", "simple"),
    ("fss4.pdf", "simple"), ("f4868.pdf", "simple"), ("f8888.pdf", "simple"),
    ("f3903.pdf", "simple"), ("f8862.pdf", "simple"), ("f461.pdf", "simple"),
    ("f8867.pdf", "simple"), ("fw7.pdf", "simple"), ("f9465.pdf", "simple"),
    ("f56.pdf", "simple"), ("f2159.pdf", "simple"),

    ("f2848.pdf", "signature"), ("f8821.pdf", "signature"),
    ("f8879.pdf", "signature"), ("f8453.pdf", "signature"),

    ("f4562.pdf", "credits"), ("f8949.pdf", "credits"), ("f2106.pdf", "credits"),
    ("f8283.pdf", "credits"), ("f8962.pdf", "credits"), ("f8863.pdf", "credits"),
    ("f8880.pdf", "credits"), ("f5695.pdf", "credits"), ("f2441.pdf", "credits"),
    ("f8889.pdf", "credits"), ("f8917.pdf", "credits"), ("f1116.pdf", "credits"),
    ("f6251.pdf", "credits"), ("f8995.pdf", "credits"), ("f8606.pdf", "credits"),
    ("f8615.pdf", "credits"), ("f8582.pdf", "credits"), ("f8843.pdf", "credits"),
    ("f8919.pdf", "credits"), ("f8941.pdf", "credits"), ("f8829.pdf", "credits"),
    ("f2210.pdf", "credits"), ("f3800.pdf", "credits"), ("f4136.pdf", "credits"),
    ("f8801.pdf", "credits"), ("f8995a.pdf", "credits"), ("f2553.pdf", "credits"),
    ("f8832.pdf", "credits"),

    ("f1120.pdf", "long"), ("f1120sd.pdf", "long"), ("f1120sg.pdf", "long"),
    ("f1120sh.pdf", "long"), ("f1120s.pdf", "long"), ("f1120sk2.pdf", "long"),
    ("f1120sk3.pdf", "long"), ("f1120f.pdf", "long"), ("f1065.pdf", "long"),
    ("f1065sb1.pdf", "long"), ("f1065sk1.pdf", "long"), ("f1065sk2.pdf", "long"),
    ("f1065sk3.pdf", "long"), ("f990.pdf", "long"), ("f990sa.pdf", "long"),
    ("f990sc.pdf", "long"), ("f990sd.pdf", "long"), ("f990sr.pdf", "long"),
    ("f990ez.pdf", "long"), ("f706.pdf", "long"), ("f706sa.pdf", "long"),
    ("f706sr.pdf", "long"), ("f709.pdf", "long"), ("f1041.pdf", "long"),
    ("f1041sk1.pdf", "long"), ("f5471.pdf", "long"), ("f5471sm.pdf", "long"),
    ("f8865.pdf", "long"), ("f3520.pdf", "long"), ("f3520a.pdf", "long"),
    ("f8938.pdf", "long"), ("f433a.pdf", "long"), ("f433b.pdf", "long"),
    ("f433f.pdf", "long"),

    ("f940.pdf", "employment"), ("f940sa.pdf", "employment"),
    ("f940sr.pdf", "employment"), ("f941.pdf", "employment"),
    ("f941sb.pdf", "employment"), ("f941sd.pdf", "employment"),
    ("f941sr.pdf", "employment"), ("f941x.pdf", "employment"),
    ("f943.pdf", "employment"), ("f944.pdf", "employment"),
    ("f945.pdf", "employment"), ("f720.pdf", "employment"),

    ("f1098.pdf", "info-returns"), ("f1098c.pdf", "info-returns"),
    ("f1098e.pdf", "info-returns"), ("f1098t.pdf", "info-returns"),
    ("f1099div.pdf", "info-returns"), ("f1099int.pdf", "info-returns"),
    ("f1099msc.pdf", "info-returns"), ("f1099nec.pdf", "info-returns"),
    ("f1099r.pdf", "info-returns"), ("f1099g.pdf", "info-returns"),
    ("f1099k.pdf", "info-returns"), ("f1099c.pdf", "info-returns"),
    ("f1099s.pdf", "info-returns"), ("f1099a.pdf", "info-returns"),
    ("f1099q.pdf", "info-returns"), ("f1099ltc.pdf", "info-returns"),
    ("f1099sa.pdf", "info-returns"), ("f1099cap.pdf", "info-returns"),
    ("f1099ptr.pdf", "info-returns"), ("f1099oid.pdf", "info-returns"),
]

USCIS_FORMS = [
    ("i-9.pdf", "uscis-simple"), ("g-28.pdf", "uscis-simple"),
    ("g-325a.pdf", "uscis-simple"), ("g-1145.pdf", "uscis-simple"),
    ("g-639.pdf", "uscis-simple"), ("i-134.pdf", "uscis-simple"),
    ("i-912.pdf", "uscis-simple"), ("i-192.pdf", "uscis-simple"),
    ("i-193.pdf", "uscis-simple"), ("i-290b.pdf", "uscis-simple"),
    ("i-102.pdf", "uscis-simple"),

    ("i-129.pdf", "uscis-long"), ("i-129f.pdf", "uscis-long"),
    ("i-130.pdf", "uscis-long"), ("i-131.pdf", "uscis-long"),
    ("i-131a.pdf", "uscis-long"), ("i-140.pdf", "uscis-long"),
    ("i-360.pdf", "uscis-long"), ("i-485.pdf", "uscis-long"),
    ("i-539.pdf", "uscis-long"), ("i-589.pdf", "uscis-long"),
    ("i-600.pdf", "uscis-long"), ("i-600a.pdf", "uscis-long"),
    ("i-601.pdf", "uscis-long"), ("i-601a.pdf", "uscis-long"),
    ("i-751.pdf", "uscis-long"), ("i-765.pdf", "uscis-long"),
    ("i-765ws.pdf", "uscis-long"), ("i-821.pdf", "uscis-long"),
    ("i-824.pdf", "uscis-long"), ("i-829.pdf", "uscis-long"),
    ("i-864.pdf", "uscis-long"), ("i-864a.pdf", "uscis-long"),
    ("i-864ez.pdf", "uscis-long"), ("i-918.pdf", "uscis-long"),
    ("i-929.pdf", "uscis-long"), ("i-90.pdf", "uscis-long"),
    ("n-400.pdf", "uscis-long"), ("n-565.pdf", "uscis-long"),
    ("n-600.pdf", "uscis-long"), ("n-600k.pdf", "uscis-long"),
    ("i-212.pdf", "uscis-long"), ("i-134a.pdf", "uscis-long"),
    ("i-817.pdf", "uscis-long"), ("i-914.pdf", "uscis-long"),
    ("g-845.pdf", "uscis-long"), ("n-336.pdf", "uscis-long"),
    ("i-800.pdf", "uscis-long"), ("i-800a.pdf", "uscis-long"),
    ("ar-11.pdf", "uscis-long"),
]

FEDERAL_LICENCE = "US-PD-17-USC-105"
FEDERAL_ATTRIBUTION_IRS = ("U.S. Internal Revenue Service -- a U.S. Government work, "
                            "no copyright (17 U.S.C. Sec 105)")
FEDERAL_ATTRIBUTION_USCIS = ("U.S. Citizenship and Immigration Services -- a U.S. "
                              "Government work, no copyright (17 U.S.C. Sec 105)")

HttpSource = collections.namedtuple(
    "HttpSource", "key base_url licence attribution forms")

FEDERAL_SOURCES = {
    "irs": HttpSource(
        key="irs",
        base_url="https://www.irs.gov/pub/irs-pdf/",
        licence=FEDERAL_LICENCE,
        attribution=FEDERAL_ATTRIBUTION_IRS,
        forms=IRS_FORMS,
    ),
    "uscis": HttpSource(
        key="uscis",
        base_url="https://www.uscis.gov/sites/default/files/document/forms/",
        licence=FEDERAL_LICENCE,
        attribution=FEDERAL_ATTRIBUTION_USCIS,
        forms=USCIS_FORMS,
    ),
}

# Sent with every federal-forms request, over both HTTP error and success, so a curious
# server operator looking at their own logs can tell what hit them and why -- the same
# courtesy fetch.sh's own rate limiting is meant to show in practice, not just in words.
FEDERAL_USER_AGENT = "MegaPDF-corpus-builder/1.0 (+https://github.com/SlyWombat/MegaPDF)"

# Minimum seconds between two requests to the same federal host: sequential, one
# connection, at manifest-*generation* time. (fetch.sh's own --jobs-per-host, which caps
# the corpus *download* everyone else does, is a separate control at 2 -- see fetch.sh.)
FEDERAL_REQUEST_GAP = 0.4

# --------------------------------------------------------------------------------------
# Selection. Quotas keep one source from swamping the set and keep the priority category
# -- forms -- whole: every form we can reach is taken, because forms are what MegaPDF is
# for and the category #434 calls the private corpus thinnest on.
# --------------------------------------------------------------------------------------

QUOTAS = {
    "form": None,      # None = take every one found
    "tagged": 300,
    "malformed": 150,
    "scan": 100,
    "report": 250,
}

CATEGORIES = ["form", "tagged", "malformed", "scan", "report"]


def classify(data, relpath):
    """Category from the file's own bytes.

    Deliberately coarse and cheap: a scan over the raw bytes, no parse. A parser would
    be a second implementation of the thing under test, and a corpus manifest that only
    builds when the engine already works is no use for finding out that it does not.
    """
    has_acroform = b"/AcroForm" in data
    has_struct = b"/StructTreeRoot" in data
    has_font = b"/Font" in data
    has_image = (b"/Image" in data or b"/DCTDecode" in data
                 or b"/JPXDecode" in data or b"/CCITTFaxDecode" in data)

    # Deliberately broken files, for crash/hang resistance only. Isartor's files violate
    # PDF/A rather than PDF syntax, but they are the same kind of input for our purposes:
    # something a conforming writer would never emit.
    if "Isartor test files" in relpath:
        return "malformed"
    if re.search(r"(^|/)(bad|damaged|broken)[^/]*\.pdf$", relpath, re.I):
        return "malformed"

    # A form is a form even when it is also tagged: the field tree is the harder shape
    # and the one we most want the batteries to meet. has_widget is NOT required here:
    # a modern, accessibility-tagged fillable PDF -- every real IRS/USCIS form this
    # source adds -- commonly stores its Widget annotations inside a compressed object
    # stream, invisible to this raw-byte scan, while /AcroForm itself sits in the
    # (uncompressed) catalog dictionary. Gating on has_widget as well as has_acroform
    # silently reclassified 185 of 186 real federal forms as tagged instead of form when
    # this source was added (#434 federal-forms extension) -- verified against a qpdf
    # --qdf decompress of the same files, which does expose /Widget for all of them.
    if has_acroform:
        return "form"
    if has_struct:
        return "tagged"
    if has_image and not has_font:
        return "scan"
    return "report"


def spread(items, limit):
    """Take `limit` items spread evenly across `items`, deterministically.

    Not the first N: the sources are laid out by specification clause, so the first N of
    a sorted list is every test for clause 6.1 and nothing after it. An even stride over
    the sorted list samples the whole tree, and sorting first makes it reproducible.
    """
    items = sorted(items)
    if limit is None or len(items) <= limit:
        return items
    step = len(items) / float(limit)
    return [items[int(i * step)] for i in range(limit)]


# --------------------------------------------------------------------------------------
# Checkouts
# --------------------------------------------------------------------------------------

def ensure_checkout(source, work_dir):
    """A shallow, blobless, sparse clone pinned to the source's commit."""
    dest = os.path.join(work_dir, source.key)
    if os.path.isdir(os.path.join(dest, ".git")):
        head = subprocess.run(["git", "-C", dest, "rev-parse", "HEAD"],
                              capture_output=True, text=True).stdout.strip()
        if head == source.commit:
            return dest
        sys.exit(f"{dest} is at {head}, not the pinned {source.commit}; "
                 f"move it aside or pass --checkout {source.key}=<path>")

    os.makedirs(dest, exist_ok=True)
    url = f"https://github.com/{source.repo}"
    run = lambda *a: subprocess.run(a, check=True, cwd=dest)
    run("git", "init", "-q")
    run("git", "remote", "add", "origin", url)
    if source.subdir:
        run("git", "config", "core.sparseCheckout", "true")
        with open(os.path.join(dest, ".git", "info", "sparse-checkout"), "w") as fh:
            fh.write(source.subdir + "/\n")
    # --filter=blob:none would save nothing here: we need every blob's bytes to hash it.
    run("git", "fetch", "-q", "--depth", "1", "origin", source.commit)
    run("git", "checkout", "-q", "FETCH_HEAD")
    return dest


def raw_url(source, relpath):
    quoted = urllib.parse.quote(relpath)
    return f"https://raw.githubusercontent.com/{source.repo}/{source.commit}/{quoted}"


def collect(source, checkout):
    """Every PDF in the source, with its hash, size and category."""
    base = os.path.join(checkout, source.subdir) if source.subdir else checkout
    found = []
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = [d for d in dirnames if d != ".git"]
        for name in sorted(filenames):
            if not name.lower().endswith(".pdf"):
                continue
            full = os.path.join(dirpath, name)
            relpath = os.path.relpath(full, checkout)
            try:
                with open(full, "rb") as fh:
                    data = fh.read()
            except OSError as exc:
                print(f"  skipped {relpath}: {exc}", file=sys.stderr)
                continue
            if not data.startswith(b"%PDF"):
                # qpdf's suite carries a few deliberately headerless files; they are
                # interesting, but a corpus row should be a PDF, so they are left out
                # rather than silently reclassified.
                continue
            found.append({
                "path": os.path.join(source.key, relpath).replace(os.sep, "/"),
                "url": raw_url(source, relpath),
                "sha256": hashlib.sha256(data).hexdigest(),
                "bytes": str(len(data)),
                "source": source.key,
                "licence": source.licence,
                "category": classify(data, relpath),
            })
    return found


# --------------------------------------------------------------------------------------
# Federal forms: no checkout, no commit -- fetched by URL, one at a time, one connection.
# --------------------------------------------------------------------------------------

class FederalHostUnreachable(Exception):
    """Every single request to the host failed at the transport level (DNS, connect,
    TLS, timeout) -- as opposed to the host answering with an HTTP error, which means it
    was reached. Raised only when the whole batch looks like a network refusal, so a
    handful of genuinely retired forms (a real HTTP 404) does not get misreported as the
    host being unreachable."""


def fetch_federal(source, cache_dir):
    """Fetch every form in `source.forms`, hash and classify each.

    Politeness: sequential, one request in flight, at least FEDERAL_REQUEST_GAP seconds
    apart -- this is manifest *generation*, run rarely by a maintainer, not the corpus
    *download* everyone else does (that is fetch.sh, which has its own --jobs-per-host).
    `cache_dir` holds the raw bytes across reruns so re-verifying or extending the list
    does not mean re-fetching everything: a byte already on disk is trusted (its sha256
    goes in the row either way) and never re-requested.

    Returns (rows, skipped) where `skipped` lists (relpath, reason) for forms that did
    not make it in -- e.g. a 404 for a form the agency has since retired. Raises
    FederalHostUnreachable if nothing at all could be reached (see above).
    """
    os.makedirs(cache_dir, exist_ok=True)
    rows = []
    skipped = []
    transport_failures = 0
    last_request = 0.0
    for relpath, _group in source.forms:
        cachefile = os.path.join(cache_dir, relpath)
        if os.path.exists(cachefile):
            with open(cachefile, "rb") as fh:
                data = fh.read()
        else:
            wait = FEDERAL_REQUEST_GAP - (time.monotonic() - last_request)
            if wait > 0:
                time.sleep(wait)
            url = source.base_url + relpath
            req = urllib.request.Request(url, headers={"User-Agent": FEDERAL_USER_AGENT})
            try:
                with urllib.request.urlopen(req, timeout=30) as resp:
                    data = resp.read()
            except urllib.error.HTTPError as exc:
                # The host answered -- reached, just not this form (retired, renamed).
                last_request = time.monotonic()
                skipped.append((relpath, f"HTTP {exc.code}"))
                print(f"  {source.key}: {relpath}: HTTP {exc.code}, skipping",
                      file=sys.stderr)
                continue
            except (urllib.error.URLError, TimeoutError, OSError) as exc:
                last_request = time.monotonic()
                transport_failures += 1
                skipped.append((relpath, str(exc)))
                continue
            last_request = time.monotonic()
            if not data.startswith(b"%PDF"):
                skipped.append((relpath, "not a PDF"))
                continue
            with open(cachefile, "wb") as fh:
                fh.write(data)
        rows.append({
            "path": f"{source.key}/{relpath}",
            "url": source.base_url + relpath,
            "sha256": hashlib.sha256(data).hexdigest(),
            "bytes": str(len(data)),
            "source": source.key,
            "licence": source.licence,
            "category": classify(data, relpath),
        })

    if not rows and transport_failures == len(source.forms):
        raise FederalHostUnreachable(
            f"every one of {len(source.forms)} requests to {source.key} failed at the "
            f"transport level; the host looks unreachable from here")
    return rows, skipped


COLUMNS = ["url", "sha256", "bytes", "source", "licence", "category", "path"]


def write_manifest(rows, out_path):
    rows = sorted(rows, key=lambda r: (r["category"], r["source"], r["path"]))
    with open(out_path, "w", encoding="utf-8") as fh:
        fh.write("# tools/stress/public-corpus/manifest.tsv -- generated by build-manifest.py (#434)\n")
        fh.write("# One row per document. Never edit by hand: rebuild, so every sha256 is one\n")
        fh.write("# that was computed from the bytes the URL actually served.\n")
        fh.write("\t".join(COLUMNS) + "\n")
        for row in rows:
            fh.write("\t".join(row[c] for c in COLUMNS) + "\n")


def read_manifest(path):
    rows = []
    with open(path, encoding="utf-8") as fh:
        header = None
        for line in fh:
            line = line.rstrip("\n")
            if not line or line.startswith("#"):
                continue
            fields = line.split("\t")
            if header is None:
                header = fields
                continue
            rows.append(dict(zip(header, fields)))
    return rows


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--work", default="/var/tmp/megapdf-public-corpus-build",
                    help="where to clone the sources (default: %(default)s)")
    ap.add_argument("--checkout", action="append", default=[], metavar="KEY=PATH",
                    help="use an existing checkout for KEY instead of cloning")
    ap.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                                  "manifest.tsv"))
    ap.add_argument("--add-source", metavar="KEY",
                    help="fetch and merge one federal-forms source into --out (%s); or, "
                         "for %s, refuse with why (no verified generator yet)"
                         % (", ".join(sorted(FEDERAL_SOURCES)),
                            ", ".join(sorted(SOURCES_BLOCKED))))
    args = ap.parse_args()

    if args.add_source:
        if args.add_source in SOURCES_BLOCKED:
            host = SOURCES_BLOCKED[args.add_source]
            sys.exit(
                f"--add-source {args.add_source} is not implemented as a committed generator.\n"
                f"See README.md, 'Network reality': {host} is unreachable from the sandbox this\n"
                f"manifest was built in, so no generator for it could be run, and a generator\n"
                f"that has never produced a verified row does not belong in the repository.\n"
                f"Add one from a machine that can reach {host}, following the shape of\n"
                f"FEDERAL_SOURCES (irs, uscis) in this file, which is real and verified.")
        source = FEDERAL_SOURCES.get(args.add_source)
        if source is None:
            sys.exit(f"unknown source {args.add_source!r}; known: "
                      f"{', '.join(sorted(list(FEDERAL_SOURCES) + list(SOURCES_BLOCKED)))}")

        cache_dir = os.path.join(args.work, f"{source.key}-fetch-cache")
        try:
            rows, skipped = fetch_federal(source, cache_dir)
        except FederalHostUnreachable as exc:
            sys.exit(
                f"--add-source {source.key}: {exc}.\n"
                f"That is a network problem here, not a design problem: this generator is\n"
                f"real and was verified against {source.base_url} on kdocker3, 2026-09-27\n"
                f"(#434 federal-forms extension). Run it from a machine that can reach\n"
                f"{source.base_url}.")

        existing = read_manifest(args.out) if os.path.exists(args.out) else []
        by_url = {r["url"]: r for r in existing}
        added = updated = 0
        for row in rows:
            if row["url"] in by_url and by_url[row["url"]]["sha256"] == row["sha256"]:
                continue
            added += (row["url"] not in by_url)
            updated += (row["url"] in by_url)
            by_url[row["url"]] = row
        write_manifest(list(by_url.values()), args.out)

        by_cat = collections.Counter(r["category"] for r in rows)
        print(f"{source.key}: {len(rows)} forms fetched and verified "
              f"({dict(sorted(by_cat.items()))}), {len(skipped)} skipped", file=sys.stderr)
        for relpath, reason in skipped:
            print(f"  skipped {relpath}: {reason}", file=sys.stderr)
        print(f"{args.out}: {added} new row(s), {updated} row(s) refreshed, "
              f"{len(by_url)} total", file=sys.stderr)
        return

    overrides = {}
    for item in args.checkout:
        key, _, path = item.partition("=")
        if key not in SOURCES_BY_KEY:
            sys.exit(f"unknown source {key!r}; known: {', '.join(SOURCES_BY_KEY)}")
        overrides[key] = path

    os.makedirs(args.work, exist_ok=True)
    pool = []
    for source in SOURCES:
        checkout = overrides.get(source.key) or ensure_checkout(source, args.work)
        rows = collect(source, checkout)
        print(f"{source.key}: {len(rows)} PDFs in {checkout}", file=sys.stderr)
        pool.extend(rows)

    by_category = collections.defaultdict(list)
    for row in pool:
        by_category[row["category"]].append(row)

    selected = []
    for category in CATEGORIES:
        rows = {r["path"]: r for r in by_category[category]}
        keep = spread(rows.keys(), QUOTAS[category])
        selected.extend(rows[p] for p in keep)
        print(f"  {category:10s} {len(keep):5d} of {len(rows):5d} found",
              file=sys.stderr)

    # A plain rebuild never touches the network for FEDERAL_SOURCES (that is what
    # --add-source is for, and what keeps this path reproducible from a sandbox that
    # cannot reach irs.gov/uscis.gov). But it must not silently DELETE federal rows a
    # previous --add-source run already put in --out, either -- so whatever is there
    # under a federal source key is carried over unchanged.
    if os.path.exists(args.out):
        federal_kept = [r for r in read_manifest(args.out) if r["source"] in FEDERAL_SOURCES]
        if federal_kept:
            print(f"  preserving {len(federal_kept)} federal rows already in {args.out} "
                  f"(source in {sorted(FEDERAL_SOURCES)}); re-run --add-source to refresh "
                  f"them from the network", file=sys.stderr)
            selected.extend(federal_kept)

    write_manifest(selected, args.out)
    total = sum(int(r["bytes"]) for r in selected)
    print(f"wrote {args.out}: {len(selected)} rows, {total/1048576:.1f} MB", file=sys.stderr)


if __name__ == "__main__":
    main()
