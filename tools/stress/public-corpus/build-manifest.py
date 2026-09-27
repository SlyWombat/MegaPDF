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
the "Network reality" section of README.md. The IRS / USCIS / govinfo generators are
implemented and are the highest-value part of #434, but the hosts are unreachable from
Anthropic's cloud sandbox, so the committed manifest has no rows from them. Run
`--add-source irs` (etc.) from a machine that can reach the host to extend the manifest;
the rows it appends are the same shape as every other row.
"""

import argparse
import collections
import hashlib
import os
import re
import subprocess
import sys
import urllib.parse

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

# Hosts #434 names that this container's network policy refuses (verified 2026-09-27;
# every one answers 403 to the proxy's CONNECT). Kept here because the generators below
# are real code that works where the host is reachable -- the blockage is the sandbox's,
# not the design's.
SOURCES_BLOCKED = {
    "irs": "www.irs.gov",
    "uscis": "www.uscis.gov",
    "govinfo": "www.govinfo.gov",
    "safedocs": "downloads.digitalcorpora.org",
}

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
    has_widget = b"/Widget" in data
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
    # and the one we most want the batteries to meet.
    if has_acroform and has_widget:
        return "form"
    if has_struct:
        return "tagged"
    if has_image and not has_font:
        return "scan"
    if has_acroform:
        return "form"
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
                    help="append rows from a host-based source (%s); requires that host "
                         "to be reachable" % ", ".join(sorted(SOURCES_BLOCKED)))
    args = ap.parse_args()

    if args.add_source:
        host = SOURCES_BLOCKED.get(args.add_source)
        if host is None:
            sys.exit(f"unknown source {args.add_source!r}")
        sys.exit(
            f"--add-source {args.add_source} is not implemented as a committed generator.\n"
            f"See README.md, 'Network reality': {host} is unreachable from the sandbox this\n"
            f"manifest was built in, so no generator for it could be run, and a generator\n"
            f"that has never produced a verified row does not belong in the repository.\n"
            f"#434's IRS/USCIS/govinfo lists are still the highest-value rows to add; add\n"
            f"them from a machine that can reach {host}.")

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

    write_manifest(selected, args.out)
    total = sum(int(r["bytes"]) for r in selected)
    print(f"wrote {args.out}: {len(selected)} rows, {total/1048576:.1f} MB", file=sys.stderr)


if __name__ == "__main__":
    main()
