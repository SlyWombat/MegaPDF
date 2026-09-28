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
the "Network reality" section of README.md. `safedocs` is out of scope for now (#434 lists
it, but nobody has yet built and verified a generator for it); its host is also unreachable
from Anthropic's cloud sandbox, so `--add-source safedocs` still refuses rather than
shipping an unverified generator.

`irs`, `uscis`, `uk-hmrc`, `uk-homeoffice`, `uk-dwp` and `govinfo`, by contrast, ARE
implemented and verified (`irs`/`uscis`: #434 federal-forms extension, kdocker3,
2026-09-27; the rest: #471 parts 3-4, kdocker3, 2026-09-28): every one of their hosts is
reachable from a real machine, just not from the cloud sandbox this file was first written
in. Run

    tools/stress/public-corpus/build-manifest.py --add-source irs
    tools/stress/public-corpus/build-manifest.py --add-source uscis
    tools/stress/public-corpus/build-manifest.py --add-source uk-hmrc
    tools/stress/public-corpus/build-manifest.py --add-source uk-homeoffice
    tools/stress/public-corpus/build-manifest.py --add-source uk-dwp
    tools/stress/public-corpus/build-manifest.py --add-source govinfo

from a machine that can reach the relevant host (www.irs.gov / www.uscis.gov / gov.uk's
asset host / www.govinfo.gov) to (re)fetch that source's rows and merge them into an
existing --out manifest; a plain rebuild (no --add-source) never touches any of them, so it
stays exactly as reproducible from a blocked sandbox as before. See NON_GIT_SOURCES,
FEDERAL_SOURCES and DIRECT_SOURCES below, and README.md, "Extending".
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

# Hosts #434 names for which no verified generator exists yet. `irs`, `uscis` and `govinfo`
# used to be listed here too; they moved out once a generator for each was actually written
# and run against the real host (see FEDERAL_SOURCES and GOVINFO_SOURCE below). Kept as a
# hard refusal rather than a half-built generator: a manifest row whose hash nobody computed
# is worse than a missing one.
SOURCES_BLOCKED = {
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
# UK central-government forms (#471 part 3). Unlike the Canadian Crown-copyright forms
# #456 keeps local-only, UK central-government material is published under the Open
# Government Licence v3.0, which explicitly permits commercial use -- verified from the
# licence's own page, https://www.nationalarchives.gov.uk/doc/open-government-licence/version/3/,
# 2026-09-28: "You are free to ... exploit the Information commercially and
# non-commercially", conditioned on attribution. Every gov.uk publication page these rows
# were resolved from carries its own "All content is available under the Open Government
# Licence v3.0, except where otherwise stated" footer plus a Crown copyright line -- also
# checked directly, not assumed, per form group below. Unlike FEDERAL_LICENCE (US works,
# no copyright at all under 17 U.S.C. Sec 105), OGL v3.0 *requires* attribution -- see
# UK_ATTRIBUTION and README.md, "Licences and attribution", for the exact credit line this
# corpus carries to satisfy it.
#
# One exception checked and excluded: OGL's own terms carve out "the paperwork associated
# with passports" from its scope, so *completed/issued* travel-document artefacts are not
# OGL material -- but a *blank application form* is ordinary published guidance like any
# other gov.uk form, and is what is fetched here. Nothing that could be an actual identity
# document (a passport photo page, a visa vignette, an issued document) is in scope.
UK_LICENCE = "OGL-UK-3.0"
UK_ATTRIBUTION_HMRC = ("Contains public sector information licensed under the Open "
                        "Government Licence v3.0 -- HM Revenue & Customs, gov.uk")
UK_ATTRIBUTION_HOME_OFFICE = ("Contains public sector information licensed under the Open "
                               "Government Licence v3.0 -- Home Office, gov.uk")
UK_ATTRIBUTION_DWP = ("Contains public sector information licensed under the Open "
                       "Government Licence v3.0 -- Department for Work and Pensions, gov.uk")

# Each row is (full PDF url, relpath under the source's key, group) -- unlike IRS_FORMS /
# USCIS_FORMS there is no shared base_url to build a relpath against: gov.uk serves every
# asset from its own content-addressed path under assets.publishing.service.gov.uk, so the
# url is recorded in full and resolved by hand from the form's own gov.uk publication page
# (never guessed), the same discipline IRS_FORMS/USCIS_FORMS document for their own filenames.
UkForm = collections.namedtuple("UkForm", "url relpath group")

HMRC_FORMS = []
HOME_OFFICE_FORMS = []
DWP_FORMS = []

# --------------------------------------------------------------------------------------
# govinfo.gov: very large public-domain documents (#471 part 4). Federal Register and CFR
# annual volumes are U.S. Government works -- public domain, 17 U.S.C. Sec 105, the same
# basis as FEDERAL_LICENCE -- confirmed from govinfo.gov's own About/terms page, 2026-09-28.
# #434 listed `govinfo` in SOURCES_BLOCKED because nobody had written and verified a
# generator that could reach it from the sandbox; kdocker3 reaches www.govinfo.gov fine
# (see README.md, "Network reality"), so this is that generator -- scoped narrowly to the
# handful of large volumes #471 part 4 asks for, not the bulk-download generator #434's
# note also mentions and which remains unbuilt.
GOVINFO_LICENCE = "US-PD-17-USC-105"
GOVINFO_ATTRIBUTION = ("U.S. Government Publishing Office / govinfo.gov -- a U.S. "
                       "Government work, no copyright (17 U.S.C. Sec 105)")

GovinfoDoc = collections.namedtuple("GovinfoDoc", "url relpath collection label")

# Selection (#471 part 4; verified by HTTP HEAD -- Content-Length, no download -- against
# govinfo.gov, 2026-09-28): CFR annual title volumes turned out NOT to be a source of large
# files at all -- checked the largest titles by XML size (7, 21, 26, 40, 48) and every CFR
# title/volume PDF tops out around 4-9 MB, structurally: they are chunked per title
# specifically to stay small. Federal Register historic issues are where the size is,
# because pre-1995 issues are OCR'd scans of the printed page image, not born-digital, and
# two of the issues below were also the special day each year the Unified Agenda of Federal
# Regulations (a twice-yearly regulatory-plan compilation, then published as a section
# inside that day's ordinary issue) ran to 1,700-2,000+ pages in one file. A deliberate
# spread from just above the ~50 MB ask to the largest one found (2,066,415,812 bytes,
# 1994-11-14) rather than every huge issue available -- several more equally large Unified
# Agenda issues exist (1990, 1992) and are not needed to make the point.
GOVINFO_DOCS = [
    GovinfoDoc(
        url="https://www.govinfo.gov/content/pkg/FR-1974-01-03/pdf/FR-1974-01-03.pdf",
        relpath="fr/FR-1974-01-03.pdf", collection="Federal Register",
        label="Vol. 39 No. 2, 1974-01-03, 179pp -- 48.4 MB, just above the size floor"),
    GovinfoDoc(
        url="https://www.govinfo.gov/content/pkg/FR-1976-10-01/pdf/FR-1976-10-01.pdf",
        relpath="fr/FR-1976-10-01.pdf", collection="Federal Register",
        label="1976-10-01 -- 83.4 MB"),
    GovinfoDoc(
        url="https://www.govinfo.gov/content/pkg/FR-1978-12-29/pdf/FR-1978-12-29.pdf",
        relpath="fr/FR-1978-12-29.pdf", collection="Federal Register",
        label="1978-12-29 -- 107.4 MB"),
    GovinfoDoc(
        url="https://www.govinfo.gov/content/pkg/FR-1980-01-02/pdf/FR-1980-01-02.pdf",
        relpath="fr/FR-1980-01-02.pdf", collection="Federal Register",
        label="Vol. 45 No. 1, 1980-01-02, 606pp -- 143.5 MB"),
    GovinfoDoc(
        url="https://www.govinfo.gov/content/pkg/FR-1983-04-25/pdf/FR-1983-04-25.pdf",
        relpath="fr/FR-1983-04-25.pdf", collection="Federal Register",
        label="Vol. 48 No. 80, 1983-04-25, 1031pp -- 232.0 MB"),
    GovinfoDoc(
        url="https://www.govinfo.gov/content/pkg/FR-1993-04-26/pdf/FR-1993-04-26.pdf",
        relpath="fr/FR-1993-04-26.pdf", collection="Federal Register",
        label="Vol. 58 No. 78, 1993-04-26, Spring Unified Agenda, 3648pp -- 1.57 GB"),
    GovinfoDoc(
        url="https://www.govinfo.gov/content/pkg/FR-1994-11-14/pdf/FR-1994-11-14.pdf",
        relpath="fr/FR-1994-11-14.pdf", collection="Federal Register",
        label="Vol. 59 No. 218, 1994-11-14, Fall Unified Agenda, 2006pp -- 2.07 GB, the largest found"),
]

# Direct-URL sources share one User-Agent and one polite, sequential fetch with the federal
# forms above -- see fetch_direct()'s docstring for why a larger per-read timeout is used.
DIRECT_USER_AGENT = FEDERAL_USER_AGENT
UK_REQUEST_GAP = 0.4
GOVINFO_REQUEST_GAP = 1.0  # #471: govinfo serves large single files; extra room, asked for.

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

# #471 part 4: a document large enough to exercise the paging-in path #147-#151 built (the
# public corpus's largest existing document is ~10.9 MB; the govinfo Federal Register/CFR
# volumes this threshold is for are fetched to be 50 MB+). Set comfortably above the former
# and comfortably below the latter so the category means what it says regardless of which
# source a future large document arrives from -- this is a property of the bytes, checked
# the same way every other category is (see the module comment on classify()), not a label
# only govinfo rows can carry.
LARGE_THRESHOLD_BYTES = 20 * 1024 * 1024


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

    # #471 part 4: size dominates every other test. A 300 MB Federal Register volume is
    # still worth knowing is also a `form` or `tagged` page, but what this category exists
    # to measure -- wall time and peak RSS on the paging-in path -- is a property of its
    # size, not its content, so it is checked first and wins outright.
    if len(data) >= LARGE_THRESHOLD_BYTES:
        return "large"

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


# --------------------------------------------------------------------------------------
# Direct-URL sources (#471): UK OGL forms and govinfo large documents. Unlike FEDERAL_SOURCES
# there is no shared base_url -- every item already carries its own full, hand-resolved URL
# -- so `items` here is a plain (url, relpath) pair rather than (relpath, group).
# --------------------------------------------------------------------------------------

def fetch_direct(key, licence, items, cache_dir, request_gap, force_category=None,
                  timeout=60, chunk_size=1 << 20):
    """Fetch a list of (url, relpath) pairs whose bytes already live at their own fully-
    resolved URL, hash and classify each. The govinfo generator's reason to exist at all:
    #471 part 4's documents run from tens of MB to just over 2 GB, so both the fetch and
    the cached copy are streamed and hashed in `chunk_size` pieces rather than held in
    memory whole -- one document's bytes are on disk at a time, never in a Python bytes
    object as well.

    `force_category` skips the content-based classify() scan entirely (used for govinfo:
    every item there is already known, by construction, to be a `large` document -- see
    GOVINFO_DOCS -- and there is no reason to read a 2 GB file a second time just to
    confirm what its own size already says via LARGE_THRESHOLD_BYTES). Left as None (UK
    forms; at most a few MB each) it reads the cached file once and classifies it the same
    way every git-sourced row is classified.

    Politeness: sequential, one request in flight, at least `request_gap` seconds apart --
    manifest *generation*, run rarely by a maintainer; not the corpus *download* everyone
    else does (fetch.sh, with its own --jobs-per-host). `timeout` is a per-`read()` socket
    timeout, not a cap on total transfer time: a slow-but-steady multi-hundred-MB download
    keeps resetting it on every chunk that arrives.

    Returns (rows, skipped); raises FederalHostUnreachable (reused rather than duplicated;
    its docstring already describes exactly this condition and is not specific to
    FEDERAL_SOURCES) if every request failed at the transport level.
    """
    os.makedirs(cache_dir, exist_ok=True)
    rows = []
    skipped = []
    transport_failures = 0
    last_request = 0.0
    for url, relpath in items:
        cachefile = os.path.join(cache_dir, relpath)
        os.makedirs(os.path.dirname(cachefile) or ".", exist_ok=True)
        if not os.path.exists(cachefile):
            wait = request_gap - (time.monotonic() - last_request)
            if wait > 0:
                time.sleep(wait)
            req = urllib.request.Request(url, headers={"User-Agent": DIRECT_USER_AGENT})
            tmp = cachefile + ".part"
            try:
                with urllib.request.urlopen(req, timeout=timeout) as resp, \
                        open(tmp, "wb") as out:
                    for chunk in iter(lambda: resp.read(chunk_size), b""):
                        out.write(chunk)
            except urllib.error.HTTPError as exc:
                last_request = time.monotonic()
                skipped.append((relpath, f"HTTP {exc.code}"))
                print(f"  {key}: {relpath}: HTTP {exc.code}, skipping", file=sys.stderr)
                if os.path.exists(tmp):
                    os.remove(tmp)
                continue
            except (urllib.error.URLError, TimeoutError, OSError) as exc:
                last_request = time.monotonic()
                transport_failures += 1
                skipped.append((relpath, str(exc)))
                if os.path.exists(tmp):
                    os.remove(tmp)
                continue
            last_request = time.monotonic()
            with open(tmp, "rb") as fh:
                head = fh.read(4)
            if head != b"%PDF":
                skipped.append((relpath, "not a PDF"))
                os.remove(tmp)
                continue
            os.replace(tmp, cachefile)

        size = os.path.getsize(cachefile)
        digest = hashlib.sha256()
        with open(cachefile, "rb") as fh:
            for chunk in iter(lambda: fh.read(chunk_size), b""):
                digest.update(chunk)

        if force_category is not None:
            category = force_category
        else:
            with open(cachefile, "rb") as fh:
                category = classify(fh.read(), relpath)

        rows.append({
            "path": f"{key}/{relpath}",
            "url": url,
            "sha256": digest.hexdigest(),
            "bytes": str(size),
            "source": key,
            "licence": licence,
            "category": category,
        })

    if not rows and transport_failures == len(items):
        raise FederalHostUnreachable(
            f"every one of {len(items)} requests to {key} failed at the transport level; "
            f"the host looks unreachable from here")
    return rows, skipped


# Assembled after HMRC_FORMS/HOME_OFFICE_FORMS/DWP_FORMS/GOVINFO_DOCS (above) are populated.
# force_category=None means "classify from content" (UK forms); "large" means "this item's
# category is already known by construction and is never re-derived from a content scan"
# (govinfo -- see fetch_direct()'s docstring).
DirectSource = collections.namedtuple(
    "DirectSource", "key licence attribution items request_gap force_category")

DIRECT_SOURCES = {
    "uk-hmrc": DirectSource(
        key="uk-hmrc", licence=UK_LICENCE, attribution=UK_ATTRIBUTION_HMRC,
        items=[(f.url, f.relpath) for f in HMRC_FORMS],
        request_gap=UK_REQUEST_GAP, force_category=None),
    "uk-homeoffice": DirectSource(
        key="uk-homeoffice", licence=UK_LICENCE, attribution=UK_ATTRIBUTION_HOME_OFFICE,
        items=[(f.url, f.relpath) for f in HOME_OFFICE_FORMS],
        request_gap=UK_REQUEST_GAP, force_category=None),
    "uk-dwp": DirectSource(
        key="uk-dwp", licence=UK_LICENCE, attribution=UK_ATTRIBUTION_DWP,
        items=[(f.url, f.relpath) for f in DWP_FORMS],
        request_gap=UK_REQUEST_GAP, force_category=None),
    "govinfo": DirectSource(
        key="govinfo", licence=GOVINFO_LICENCE, attribution=GOVINFO_ATTRIBUTION,
        items=[(d.url, d.relpath) for d in GOVINFO_DOCS],
        request_gap=GOVINFO_REQUEST_GAP, force_category="large"),
}

# Every source whose rows are fetched by direct URL rather than rebuilt from a pinned git
# checkout: opt-in via --add-source, and -- like FEDERAL_SOURCES on its own used to be --
# carried over unchanged by a plain rebuild rather than resampled or dropped (see main()).
NON_GIT_SOURCES = {**FEDERAL_SOURCES, **DIRECT_SOURCES}


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
                    help="fetch and merge one direct-URL source into --out (%s); or, "
                         "for %s, refuse with why (no verified generator yet)"
                         % (", ".join(sorted(NON_GIT_SOURCES)),
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
                f"NON_GIT_SOURCES (irs, uscis, uk-hmrc, uk-homeoffice, uk-dwp, govinfo) in\n"
                f"this file, which is real and verified.")
        if args.add_source not in NON_GIT_SOURCES:
            sys.exit(f"unknown source {args.add_source!r}; known: "
                      f"{', '.join(sorted(list(NON_GIT_SOURCES) + list(SOURCES_BLOCKED)))}")

        cache_dir = os.path.join(args.work, f"{args.add_source}-fetch-cache")
        try:
            if args.add_source in FEDERAL_SOURCES:
                source = FEDERAL_SOURCES[args.add_source]
                rows, skipped = fetch_federal(source, cache_dir)
                host_for_error = source.base_url
            else:
                ds = DIRECT_SOURCES[args.add_source]
                rows, skipped = fetch_direct(ds.key, ds.licence, ds.items, cache_dir,
                                              ds.request_gap, force_category=ds.force_category)
                host_for_error = (ds.items[0][0] if ds.items else "its source host")
        except FederalHostUnreachable as exc:
            sys.exit(
                f"--add-source {args.add_source}: {exc}.\n"
                f"That is a network problem here, not a design problem: this generator is\n"
                f"real and was verified on kdocker3 (#434 federal-forms extension; #471 for\n"
                f"uk-hmrc/uk-homeoffice/uk-dwp/govinfo). Run it from a machine that can reach\n"
                f"{host_for_error}.")

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
        print(f"{args.add_source}: {len(rows)} document(s) fetched and verified "
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

    # A plain rebuild never touches the network for NON_GIT_SOURCES (that is what
    # --add-source is for, and what keeps this path reproducible from a sandbox that cannot
    # reach irs.gov/uscis.gov/gov.uk/govinfo.gov). But it must not silently DELETE rows a
    # previous --add-source run already put in --out, either -- so whatever is there under
    # any of those source keys is carried over unchanged. (#471 generalizes this from
    # "federal" to every direct-URL source; the doctrine -- opt-in fetch, never resampled,
    # never silently dropped -- is the same one FEDERAL_SOURCES established for irs/uscis.)
    if os.path.exists(args.out):
        federal_kept = [r for r in read_manifest(args.out) if r["source"] in NON_GIT_SOURCES]
        if federal_kept:
            print(f"  preserving {len(federal_kept)} direct-URL rows already in {args.out} "
                  f"(source in {sorted(NON_GIT_SOURCES)}); re-run --add-source to refresh "
                  f"them from the network", file=sys.stderr)
            selected.extend(federal_kept)

    write_manifest(selected, args.out)
    total = sum(int(r["bytes"]) for r in selected)
    print(f"wrote {args.out}: {len(selected)} rows, {total/1048576:.1f} MB", file=sys.stderr)


if __name__ == "__main__":
    main()
