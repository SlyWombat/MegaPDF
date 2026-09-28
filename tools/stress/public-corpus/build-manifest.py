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
from the cloud sandbox this file was first written in. `govinfo-signed` (#471 part 1,
kdocker3, 2026-09-28) is the same shape, against www.govinfo.gov: a curated set of
GPO-signed documents, forced into the `signed` category rather than classified. Run

    tools/stress/public-corpus/build-manifest.py --add-source irs
    tools/stress/public-corpus/build-manifest.py --add-source uscis
    tools/stress/public-corpus/build-manifest.py --add-source govinfo-signed

from a machine that can reach www.irs.gov / www.uscis.gov / www.govinfo.gov to (re)fetch
the federal rows and merge them into an existing --out manifest; a plain rebuild (no
--add-source) never touches them, so it stays exactly as reproducible from a blocked
sandbox as before. See FEDERAL_SOURCES below and README.md, "Extending".
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
#
# `govinfo` (the *bulk* generator #434 item 4 asks for, spanning the whole Federal
# Register/CFR archive for large-file coverage) stays blocked here: nobody has designed
# or verified that generator yet. `govinfo-signed` (#471 part 1, below) is a narrower,
# already-verified generator against the same host for a different purpose -- a curated
# set of GPO-signed documents -- and is intentionally a separate source key so the two
# do not collide when both are eventually implemented.
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
    "HttpSource", "key base_url licence attribution forms category",
    defaults=(None,))  # category: force a category rather than derive one with
                        # classify() -- see GOVINFO_SIGNED_DOCS below. irs/uscis leave
                        # this None, so their rows are classified exactly as before.

# --------------------------------------------------------------------------------------
# #471 part 1: documents that already carry a valid digital signature, to measure what
# megapdf_save()'s full-rewrite does to it (see tools/stress/public-corpus/README.md,
# "The signed category", for the measurement and its result). All from
# www.govinfo.gov (U.S. Government Publishing Office): every GPO-published Federal
# Register issue, Statutes at Large volume, Public Law, Congressional Record issue and
# CFR title/volume is served as a PDF bearing GPO's own digital signature
# (Signature Field Name "USGPOSignature", Signer CN "Government Publishing Office" or
# "U.S. Government Publishing Office") -- verified with poppler's `pdfsig` against every
# row below, kdocker3, 2026-09-28: 33/33 "Signature is Valid" before any MegaPDF
# processing touches them. Same licence bucket as IRS/USCIS (a GPO publication is a
# federal government work, 17 U.S.C. Sec 105 -- no copyright, so no licence to comply
# with), with its own attribution string recording the signing fact as provenance.
#
# Grouped by GPO collection, matching govinfo's own package-ID prefixes:
#   fr        Federal Register daily issues, 2019-2024, a spread of months so the
#             sample is not one season's rulemaking.
#   plaw      Public Laws -- enacted legislation, one PDF per law, chosen as a spread of
#             well-known acts across four Congresses rather than sequential numbers.
#   crec      Congressional Record daily issues, one per year 2019-2024.
#   cfr       Code of Federal Regulations, one title/volume per year 2019-2023, a range
#             of titles (agencies) and volume sizes (2.8-23 MB).
#   statute   United States Statutes at Large, individual public-law excerpts by
#             volume/page citation.
# category is forced to "signed" (not derived from classify()) so these rows do not
# fall under "form" just because a /Sig field is technically inside an /AcroForm --
# the whole point of the row is the signature, and #471 asks for a `signed` category.
GOVINFO_SIGNED_DOCS = [
    ("FR-2019-03-15/pdf/FR-2019-03-15.pdf", "fr"),
    ("FR-2020-06-10/pdf/FR-2020-06-10.pdf", "fr"),
    ("FR-2021-09-01/pdf/FR-2021-09-01.pdf", "fr"),
    ("FR-2021-09-08/pdf/FR-2021-09-08.pdf", "fr"),
    ("FR-2022-04-06/pdf/FR-2022-04-06.pdf", "fr"),
    ("FR-2022-08-16/pdf/FR-2022-08-16.pdf", "fr"),
    ("FR-2023-01-12/pdf/FR-2023-01-12.pdf", "fr"),
    ("FR-2023-07-05/pdf/FR-2023-07-05.pdf", "fr"),
    ("FR-2024-01-02/pdf/FR-2024-01-02.pdf", "fr"),
    ("FR-2024-05-14/pdf/FR-2024-05-14.pdf", "fr"),
    ("FR-2024-10-01/pdf/FR-2024-10-01.pdf", "fr"),

    ("PLAW-107publ56/pdf/PLAW-107publ56.pdf", "plaw"),
    ("PLAW-109publ171/pdf/PLAW-109publ171.pdf", "plaw"),
    ("PLAW-111publ148/pdf/PLAW-111publ148.pdf", "plaw"),
    ("PLAW-113publ235/pdf/PLAW-113publ235.pdf", "plaw"),
    ("PLAW-115publ97/pdf/PLAW-115publ97.pdf", "plaw"),
    ("PLAW-116publ136/pdf/PLAW-116publ136.pdf", "plaw"),
    ("PLAW-117publ58/pdf/PLAW-117publ58.pdf", "plaw"),

    ("CREC-2019-07-17/pdf/CREC-2019-07-17.pdf", "crec"),
    ("CREC-2020-02-12/pdf/CREC-2020-02-12.pdf", "crec"),
    ("CREC-2021-10-05/pdf/CREC-2021-10-05.pdf", "crec"),
    ("CREC-2022-03-08/pdf/CREC-2022-03-08.pdf", "crec"),
    ("CREC-2023-06-13/pdf/CREC-2023-06-13.pdf", "crec"),
    ("CREC-2024-01-02/pdf/CREC-2024-01-02.pdf", "crec"),

    ("CFR-2019-title21-vol1/pdf/CFR-2019-title21-vol1.pdf", "cfr"),
    ("CFR-2020-title29-vol5/pdf/CFR-2020-title29-vol5.pdf", "cfr"),
    ("CFR-2021-title17-vol3/pdf/CFR-2021-title17-vol3.pdf", "cfr"),
    ("CFR-2022-title26-vol1/pdf/CFR-2022-title26-vol1.pdf", "cfr"),
    ("CFR-2023-title40-vol1/pdf/CFR-2023-title40-vol1.pdf", "cfr"),
    ("CFR-2023-title47-vol1/pdf/CFR-2023-title47-vol1.pdf", "cfr"),

    ("STATUTE-115/pdf/STATUTE-115-Pg272.pdf", "statute"),
    ("STATUTE-124/pdf/STATUTE-124-Pg119.pdf", "statute"),
    ("STATUTE-131/pdf/STATUTE-131-Pg2054.pdf", "statute"),
]

FEDERAL_ATTRIBUTION_GOVINFO_SIGNED = (
    "U.S. Government Publishing Office -- a U.S. Government work, no copyright "
    "(17 U.S.C. Sec 105); also bears GPO's own digital signature "
    "(Signature Field Name \"USGPOSignature\") attesting the authenticity of the "
    "version published at govinfo.gov")

# --------------------------------------------------------------------------------------
# Non-Latin scripts (#471 part 2). core/megapdf_structure.cpp's BuildWords, BuildLines and
# the XY-cut reading order assume left-to-right, horizontal text -- #444 found this the
# hard way when a vertical-writing CMap turned "Hello world" into ten one-letter words. The
# public corpus otherwise has almost no Arabic, Hebrew, Han, Devanagari or Thai text, so
# there was no population to measure that risk against.
#
# Source: real Wikipedia articles, one PDF per article, fetched from that wiki's own REST
# "page/pdf" export endpoint (the same mechanism #434's README names under "good sources").
# Titles were chosen by MediaWiki's own list=random (mainspace only), then pinned here --
# exactly the IRS_FORMS/USCIS_FORMS shape above -- so a rebuild fetches the same articles
# rather than a fresh random sample each time (#455 asks that adding documents not reshuffle
# what is already measured). 40 titles per language, chosen 2026-09-28 on kdocker3;
# NONLATIN_WIKI_TITLES is the exact, pinned list.
#
# Licence: Wikipedia article text is dual CC BY-SA 4.0 / GFDL (enwiki's own "Reusing
# Wikipedia content" page, checked 2026-09-28). CC BY-SA requires attribution (a hyperlink
# or URL to the article, or a list of authors), a licence notice, and -- because the PDF
# rendering is the wiki's own, not further modified here -- no "changes made" notice is
# owed beyond noting the export mechanism, which this comment and the README do. Recorded
# per row as licence CC-BY-SA-4.0; NONLATIN_ATTRIBUTION carries the notice text.
#
# Scripts: Arabic, Hebrew, Han (Chinese, both flavours as the sources happen to serve them),
# Devanagari (Hindi) and Thai are each a plain horizontal-text wiki export -- real body
# text, real fonts, real ToUnicode/CMap data, but not vertical. Korean and Japanese add
# Hangul and a second horizontal CJK sample. None of these are vertical writing, which is
# the one axis #444 actually broke on -- see gen-ja-vertical.py alongside this file for
# that gap, which is NOT part of manifest.tsv (its bytes are not reproducible enough for a
# checksum-pinned row; see that file's own docstring for why).
NONLATIN_WIKI_SCRIPTS = {
    "ar": "arabic",
    "he": "hebrew",
    "zh": "han-chinese",
    "ja": "japanese-horizontal",
    "ko": "hangul-korean",
    "hi": "devanagari",
    "th": "thai",
}

NONLATIN_LICENCE = "CC-BY-SA-4.0"
NONLATIN_ATTRIBUTION = (
    "Wikipedia contributors -- article text is CC BY-SA 4.0 "
    "(https://creativecommons.org/licenses/by-sa/4.0/), dual-licensed GFDL; each row's "
    "own URL is the article's canonical attribution link (replace '/api/rest_v1/page/pdf/' "
    "with '/wiki/' for the human-readable page). PDF rendering is the wiki's own REST "
    "export, unmodified further here.")

# See "Extending" below and NONLATIN_WIKI_TITLES's own comment for the pinned title list
# (kept in a companion module to keep this file's line count sane).
from nonlatin_wiki_titles import NONLATIN_WIKI_TITLES  # noqa: E402

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
    "govinfo-signed": HttpSource(
        key="govinfo-signed",
        base_url="https://www.govinfo.gov/content/pkg/",
        licence=FEDERAL_LICENCE,
        attribution=FEDERAL_ATTRIBUTION_GOVINFO_SIGNED,
        forms=GOVINFO_SIGNED_DOCS,
        category="signed",
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
            # govinfo-signed's relpaths nest a "/pdf/" component (govinfo's own package
            # layout); irs/uscis relpaths never did, so this was never needed before.
            os.makedirs(os.path.dirname(cachefile), exist_ok=True)
            with open(cachefile, "wb") as fh:
                fh.write(data)
        rows.append({
            "path": f"{source.key}/{relpath}",
            "url": source.base_url + relpath,
            "sha256": hashlib.sha256(data).hexdigest(),
            "bytes": str(len(data)),
            "source": source.key,
            "licence": source.licence,
            "category": source.category or classify(data, relpath),
        })

    if not rows and transport_failures == len(source.forms):
        raise FederalHostUnreachable(
            f"every one of {len(source.forms)} requests to {source.key} failed at the "
            f"transport level; the host looks unreachable from here")
    return rows, skipped


def fetch_nonlatin_wiki(cache_dir):
    """Fetch every title in NONLATIN_WIKI_TITLES, one language at a time (#471 part 2).

    Same politeness and caching shape as fetch_federal above (this reuses
    FEDERAL_REQUEST_GAP / FEDERAL_USER_AGENT rather than duplicating them under a new
    name -- one wiki host is exactly as polite a target as one government host). Not
    folded into fetch_federal/HttpSource itself: the row `path` federal forms use is
    the filename an agency already gave it (`f1040.pdf`), which is a bad fit here --
    percent-encoding an Arabic or Thai title into a filename is legal but ugly and, for
    a long title, can overflow a filesystem's 255-byte name limit. Rows here use the
    content hash for `path` instead, which every corpus row could in principle do but
    federal doesn't need to.

    Each `source` value is `wiki-<lang>` (not a single `wikipedia` source): the point
    of this extension is a per-script breakdown, and the manifest's existing `source`
    column is already what the battery tables key on, so a language-per-source-value
    reproduces the breakdown with no new column and no change to any battery script.
    """
    os.makedirs(cache_dir, exist_ok=True)
    rows = []
    skipped = []
    transport_failures = 0
    total_requests = 0
    last_request = 0.0
    for lang in NONLATIN_WIKI_SCRIPTS:
        titles = NONLATIN_WIKI_TITLES[lang]
        base_url = f"https://{lang}.wikipedia.org/api/rest_v1/page/pdf/"
        for i, title in enumerate(titles):
            total_requests += 1
            quoted = urllib.parse.quote(title.replace(" ", "_"), safe="")
            tag = f"{lang}/{title}"
            cachefile = os.path.join(cache_dir, f"{lang}-{i:03d}.pdf")
            if os.path.exists(cachefile):
                with open(cachefile, "rb") as fh:
                    data = fh.read()
            else:
                wait = FEDERAL_REQUEST_GAP - (time.monotonic() - last_request)
                if wait > 0:
                    time.sleep(wait)
                url = base_url + quoted
                req = urllib.request.Request(url, headers={"User-Agent": FEDERAL_USER_AGENT})
                try:
                    with urllib.request.urlopen(req, timeout=60) as resp:
                        data = resp.read()
                except urllib.error.HTTPError as exc:
                    last_request = time.monotonic()
                    skipped.append((tag, f"HTTP {exc.code}"))
                    print(f"  nonlatin-wiki: {tag}: HTTP {exc.code}, skipping", file=sys.stderr)
                    continue
                except (urllib.error.URLError, TimeoutError, OSError) as exc:
                    last_request = time.monotonic()
                    transport_failures += 1
                    skipped.append((tag, str(exc)))
                    continue
                last_request = time.monotonic()
                # A stub-length rendering (a redirect, a disambiguation page, a page the
                # renderer gave up on) is not useful corpus material -- 15 KB is comfortably
                # below every real article seen in the 2026-09-28 sample and comfortably
                # above an almost-empty render.
                if not data.startswith(b"%PDF") or len(data) < 15000:
                    skipped.append((tag, "not a usable PDF (missing header or under 15 KB)"))
                    continue
                with open(cachefile, "wb") as fh:
                    fh.write(data)
            sha = hashlib.sha256(data).hexdigest()
            rows.append({
                "path": f"nonlatin/wiki-{lang}/{sha[:16]}.pdf",
                "url": base_url + quoted,
                "sha256": sha,
                "bytes": str(len(data)),
                "source": f"wiki-{lang}",
                "licence": NONLATIN_LICENCE,
                "category": classify(data, f"nonlatin/wiki-{lang}/{sha[:16]}.pdf"),
            })

    if not rows and transport_failures == total_requests:
        raise FederalHostUnreachable(
            f"every one of {total_requests} requests to *.wikipedia.org failed at the "
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


def merge_and_report(key, rows, skipped, out_path):
    """Merge `rows` (by URL: a matching sha256 is left alone, a changed one refreshed,
    a new one added) into the manifest at `out_path`, and print the same shape of
    summary every --add-source generator has printed since the federal-forms extension.
    Shared by fetch_federal and fetch_nonlatin_wiki so the two opt-in HTTP generators
    report identically rather than drifting apart.
    """
    existing = read_manifest(out_path) if os.path.exists(out_path) else []
    by_url = {r["url"]: r for r in existing}
    added = updated = 0
    for row in rows:
        if row["url"] in by_url and by_url[row["url"]]["sha256"] == row["sha256"]:
            continue
        added += (row["url"] not in by_url)
        updated += (row["url"] in by_url)
        by_url[row["url"]] = row
    write_manifest(list(by_url.values()), out_path)

    by_cat = collections.Counter(r["category"] for r in rows)
    print(f"{key}: {len(rows)} documents fetched and verified "
          f"({dict(sorted(by_cat.items()))}), {len(skipped)} skipped", file=sys.stderr)
    for tag, reason in skipped:
        print(f"  skipped {tag}: {reason}", file=sys.stderr)
    print(f"{out_path}: {added} new row(s), {updated} row(s) refreshed, "
          f"{len(by_url)} total", file=sys.stderr)


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
                    help="fetch and merge one opt-in HTTP source into --out (%s); or, "
                         "for %s, refuse with why (no verified generator yet)"
                         % (", ".join(sorted(list(FEDERAL_SOURCES) + ["nonlatin-wiki"])),
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

        if args.add_source == "nonlatin-wiki":
            cache_dir = os.path.join(args.work, "nonlatin-wiki-fetch-cache")
            try:
                rows, skipped = fetch_nonlatin_wiki(cache_dir)
            except FederalHostUnreachable as exc:
                sys.exit(
                    f"--add-source nonlatin-wiki: {exc}.\n"
                    f"That is a network problem here, not a design problem: this generator is\n"
                    f"real and was verified against *.wikipedia.org on kdocker3, 2026-09-28\n"
                    f"(#471 part 2). Run it from a machine that can reach the *.wikipedia.org\n"
                    f"hosts named in NONLATIN_WIKI_SCRIPTS.")
            merge_and_report("nonlatin-wiki", rows, skipped, args.out)
            return

        source = FEDERAL_SOURCES.get(args.add_source)
        if source is None:
            sys.exit(f"unknown source {args.add_source!r}; known: "
                      f"{', '.join(sorted(list(FEDERAL_SOURCES) + ['nonlatin-wiki'] + list(SOURCES_BLOCKED)))}")

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

        merge_and_report(source.key, rows, skipped, args.out)
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

    # A plain rebuild never touches the network for FEDERAL_SOURCES or the nonlatin-wiki
    # rows (that is what --add-source is for, and what keeps this path reproducible from a
    # sandbox that cannot reach irs.gov/uscis.gov/*.wikipedia.org). But it must not
    # silently DELETE opt-in rows a previous --add-source run already put in --out,
    # either -- so whatever is there under one of those source keys is carried over
    # unchanged.
    opt_in_source_keys = set(FEDERAL_SOURCES) | {f"wiki-{lang}" for lang in NONLATIN_WIKI_SCRIPTS}
    if os.path.exists(args.out):
        opt_in_kept = [r for r in read_manifest(args.out) if r["source"] in opt_in_source_keys]
        if opt_in_kept:
            print(f"  preserving {len(opt_in_kept)} opt-in rows already in {args.out} "
                  f"(source in {sorted(opt_in_source_keys)}); re-run --add-source to refresh "
                  f"them from the network", file=sys.stderr)
            selected.extend(opt_in_kept)

    write_manifest(selected, args.out)
    total = sum(int(r["bytes"]) for r in selected)
    print(f"wrote {args.out}: {len(selected)} rows, {total/1048576:.1f} MB", file=sys.stderr)


if __name__ == "__main__":
    main()
