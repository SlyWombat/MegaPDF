#!/usr/bin/env python3
"""One-time migration (#525): fetch the 280 pinned non-Latin-script Wikipedia articles and
point their manifest.tsv rows at MegaPDF's own storage instead of live *.wikipedia.org.

The problem this fixes: Wikipedia's PDF export is rendered on demand and is not always
byte-stable even minutes apart (README.md, "Non-Latin scripts" -- found 2026-09-28 while
re-verifying this batch, roughly a third of the 280 rows failed MISMATCH-ON-FETCH on a later
run). A row's identity in this manifest is its URL plus a sha256; for a re-rendering source
the hash cannot be relied on, so the row is not reproducible no matter how well the selection
is pinned (#455). Dave's decision, 2026-09-30 (#525): fetch each of the 280 documents once
and host the bytes ourselves -- a release asset on this repository, fetched the same way
tools/fetch-pdfium-linux.sh already fetches our patched PDFium -- so the pinned hash holds
forever and the corpus is reproducible again. It also makes these 280 rows reachable from
the Anthropic cloud sandbox for the first time: only raw.githubusercontent.com-style GitHub
asset URLs clear its egress proxy, and *.wikipedia.org (like irs.gov, uscis.gov, gov.uk and
govinfo.gov) does not.

This script does NOT change the licence: Wikipedia article text is CC BY-SA 4.0 (dual GFDL),
and redistributing the rendered PDF carries that obligation forward exactly as it did when
the corpus fetched it live -- see README.md, "Non-Latin scripts: hosted, not fetched live"
and "Licences and attribution". It is NOT precedent for hosting any other source: the
Canadian Crown-copyright forms and the (never added) UN documents are local validation only
and are never redistributed, a distinction the README states plainly for exactly this reason.

What it does, run from a machine that can reach *.wikipedia.org (kdocker3; not the cloud
sandbox, not CI):

  1. Fetches the current bytes for every title in NONLATIN_WIKI_TITLES (build-manifest.py's
     fetch_nonlatin_wiki -- the same generator --add-source nonlatin-wiki uses, reused here
     rather than duplicated so there is exactly one implementation of "fetch a wiki PDF").
  2. Matches each fresh fetch back to its existing manifest.tsv row by the *.wikipedia.org
     URL both still share at this point (the pinned title makes that URL stable even though
     the bytes behind it are not).
  3. Writes each matched file's bytes to --assets-out under a flat, self-describing name
     (`wiki-<lang>-<sha16>.pdf`) ready for `gh release upload`.
  4. Rewrites manifest.tsv in place: the matched rows' sha256/bytes/path/category are
     refreshed to the new fetch, and their url becomes the release asset URL this script was
     told to use (--release-base-url) -- not the live wiki any more.
  5. Writes --attribution-out, a small TSV recording each hosted file's source article (the
     human-readable /wiki/ URL) and the original REST API URL it was fetched from, because
     CC BY-SA's attribution requirement travels with the bytes even once the manifest's own
     `url` column stops pointing at Wikipedia. Not committed PDF content -- just URLs and
     hashes, the same kind of provenance data manifest.tsv itself already carries.

This is a one-time migration, not a generator you run routinely: once mirrored, a plain
`--add-source nonlatin-wiki` refuses (see build-manifest.py's #525 comment) rather than
silently re-adding a live-Wikipedia duplicate of what this script already froze. Extending
the non-Latin corpus with new titles or languages after this point means fetching and
mirroring the new rows the same way -- re-run this script against a manifest that still has
*some* unmigrated wiki-* rows (a fresh language, say) and it will only touch those; it
refuses outright if it finds none left to migrate.

Usage:

    tools/stress/public-corpus/mirror-nonlatin-wiki.py \\
        --release-base-url https://github.com/SlyWombat/MegaPDF/releases/download/<tag> \\
        --assets-out /var/tmp/nonlatin-wiki-assets
"""
import argparse
import hashlib
import importlib.util
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)  # build-manifest.py does `from nonlatin_wiki_titles import ...`

# build-manifest.py has a hyphen in its name, so it cannot be `import`ed by that name --
# load it by path instead. This keeps fetch_nonlatin_wiki (and classify, read_manifest,
# write_manifest, NONLATIN_*) to exactly one implementation rather than a second copy here
# that could drift from it.
_spec = importlib.util.spec_from_file_location("build_manifest", os.path.join(HERE, "build-manifest.py"))
bm = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(bm)


def human_article_url(api_url):
    """The REST export URL doubles as the attribution link once '/api/rest_v1/page/pdf/' is
    swapped for '/wiki/' (README.md, "Non-Latin scripts" -- the same transform already
    documented there, applied here so the attribution record carries the readable link
    rather than making a future reader re-derive it).
    """
    return api_url.replace("/api/rest_v1/page/pdf/", "/wiki/")


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                  formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--manifest", default=os.path.join(HERE, "manifest.tsv"))
    ap.add_argument("--work", default="/var/tmp/megapdf-nonlatin-wiki-mirror",
                     help="scratch dir for the fetch cache (default: %(default)s)")
    ap.add_argument("--assets-out", required=True,
                     help="directory to write the fetched PDFs, named ready for "
                          "'gh release upload' (one file per migrated row)")
    ap.add_argument("--release-base-url", required=True,
                     help="e.g. https://github.com/SlyWombat/MegaPDF/releases/download/<tag> "
                          "-- each asset's final URL is this plus '/' plus its filename")
    ap.add_argument("--attribution-out",
                     default=os.path.join(HERE, "nonlatin-wiki-attribution.tsv"))
    args = ap.parse_args()

    if not os.path.exists(args.manifest):
        sys.exit(f"no manifest at {args.manifest}")

    existing_rows = bm.read_manifest(args.manifest)
    wiki_rows = [r for r in existing_rows if r.get("source") in bm.NONLATIN_SOURCE_KEYS]
    if not wiki_rows:
        sys.exit(f"no wiki-* rows found in {args.manifest} -- nothing to mirror")

    already_mirrored = [r for r in wiki_rows if ".wikipedia.org" not in r.get("url", "")]
    still_live = [r for r in wiki_rows if ".wikipedia.org" in r.get("url", "")]
    if not still_live:
        sys.exit(f"all {len(wiki_rows)} wiki-* rows in {args.manifest} are already mirrored "
                  f"(#525) -- nothing left to do. To extend with new titles or languages, add "
                  f"them to nonlatin_wiki_titles.py first, so there is something live to find.")
    print(f"{len(still_live)} wiki-* row(s) still point at live *.wikipedia.org; "
          f"{len(already_mirrored)} already mirrored and will be left alone.", file=sys.stderr)

    by_url = {r["url"]: r for r in still_live}

    cache_dir = os.path.join(args.work, "nonlatin-wiki-fetch-cache")
    try:
        fresh_rows, skipped = bm.fetch_nonlatin_wiki(cache_dir)
    except bm.FederalHostUnreachable as exc:
        sys.exit(f"mirror-nonlatin-wiki.py: {exc}.\n"
                  f"Run this from a machine that can reach the *.wikipedia.org hosts named in "
                  f"NONLATIN_WIKI_SCRIPTS (kdocker3, not the cloud sandbox or CI).")
    for tag, reason in skipped:
        print(f"  skipped (fetch): {tag}: {reason}", file=sys.stderr)

    # Recover each fresh row's raw bytes from the cache fetch_nonlatin_wiki just populated --
    # it returns manifest-shaped dicts, not the bytes themselves, and this script needs the
    # bytes both to write the release asset and to prove the sha256 it is about to pin.
    fresh_by_source_sha = {(r["source"], r["sha256"]): r for r in fresh_rows}
    os.makedirs(args.assets_out, exist_ok=True)

    migrated = 0
    unmatched_fresh = []   # fetched fine, but no existing manifest row shares its URL
    unavailable = []       # an existing row whose title no fresh fetch could confirm
    attribution_rows = []

    for lang in bm.NONLATIN_WIKI_SCRIPTS:
        titles = bm.NONLATIN_WIKI_TITLES[lang]
        source = f"wiki-{lang}"
        for i in range(len(titles)):
            cachefile = os.path.join(cache_dir, f"{lang}-{i:03d}.pdf")
            if not os.path.exists(cachefile):
                continue  # this title was skipped above; already logged
            with open(cachefile, "rb") as fh:
                data = fh.read()
            sha = hashlib.sha256(data).hexdigest()
            fresh = fresh_by_source_sha.get((source, sha))
            if fresh is None:
                continue  # should not happen: fetch_nonlatin_wiki computed this same sha
            orig = by_url.get(fresh["url"])
            if orig is None:
                unmatched_fresh.append(fresh["url"])
                continue

            sha16 = sha[:16]
            asset_name = f"{source}-{sha16}.pdf"
            new_path = f"nonlatin/{source}/{sha16}.pdf"
            release_url = f"{args.release_base_url}/{asset_name}"

            with open(os.path.join(args.assets_out, asset_name), "wb") as fh:
                fh.write(data)

            attribution_rows.append({
                "path": new_path,
                "source": source,
                "sha256": sha,
                "bytes": str(len(data)),
                "article_url": human_article_url(fresh["url"]),
                "fetched_from": fresh["url"],
            })

            orig["sha256"] = sha
            orig["bytes"] = str(len(data))
            orig["path"] = new_path
            orig["category"] = fresh["category"]
            orig["url"] = release_url
            migrated += 1
            del by_url[fresh["url"]]

    unavailable = list(by_url.values())  # whatever is left in by_url was never matched

    bm.write_manifest(existing_rows, args.manifest)

    with open(args.attribution_out, "w", encoding="utf-8") as fh:
        fh.write("# tools/stress/public-corpus/nonlatin-wiki-attribution.tsv -- generated by\n"
                 "# mirror-nonlatin-wiki.py (#525). Not part of the fetch/verify contract --\n"
                 "# manifest.tsv's url/sha256/bytes columns are what fetch.sh reads. This file\n"
                 "# exists solely to carry CC BY-SA 4.0 attribution forward once manifest.tsv's\n"
                 "# own url column stops pointing at the source article: each row here names\n"
                 "# the hosted file (by its manifest `path`) and the Wikipedia article and\n"
                 "# licence it came from. See README.md, 'Licences and attribution'.\n")
        fh.write("path\tsource\tsha256\tbytes\tarticle_url\tfetched_from\n")
        for row in sorted(attribution_rows, key=lambda r: r["path"]):
            fh.write("\t".join(row[c] for c in
                     ["path", "source", "sha256", "bytes", "article_url", "fetched_from"]) + "\n")

    print(f"migrated {migrated} row(s) to {args.release_base_url}", file=sys.stderr)
    print(f"assets written to {args.assets_out} ({migrated} file(s))", file=sys.stderr)
    print(f"attribution record: {args.attribution_out}", file=sys.stderr)
    if unmatched_fresh:
        print(f"note: {len(unmatched_fresh)} fresh fetch(es) matched no existing manifest row "
              f"(unexpected -- a title pinned in NONLATIN_WIKI_TITLES should already be in "
              f"manifest.tsv)", file=sys.stderr)
    if unavailable:
        print(f"could not migrate {len(unavailable)} row(s) -- their title's current fetch "
              f"did not come back (see 'skipped (fetch)' lines above); left pointed at live "
              f"*.wikipedia.org, unchanged, same as before this script ran:", file=sys.stderr)
        for r in unavailable:
            print(f"  {r['path']}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
