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

`irs`, `uscis`, `govinfo-signed`, `uk-hmrc`, `uk-homeoffice`, `uk-dwp` and `govinfo-large`,
by contrast, ARE implemented and verified (`irs`/`uscis`: #434 federal-forms extension,
kdocker3, 2026-09-27; `govinfo-signed`: #471 part 1, kdocker3, 2026-09-28; the rest: #471
parts 3-4, kdocker3, 2026-09-28): every one of their hosts is reachable from a real
machine, just not from the cloud sandbox this file was first written in. Run

    tools/stress/public-corpus/build-manifest.py --add-source irs
    tools/stress/public-corpus/build-manifest.py --add-source uscis
    tools/stress/public-corpus/build-manifest.py --add-source govinfo-signed
    tools/stress/public-corpus/build-manifest.py --add-source uk-hmrc
    tools/stress/public-corpus/build-manifest.py --add-source uk-homeoffice
    tools/stress/public-corpus/build-manifest.py --add-source uk-dwp
    tools/stress/public-corpus/build-manifest.py --add-source govinfo-large

from a machine that can reach the relevant host (www.irs.gov / www.uscis.gov /
www.govinfo.gov / gov.uk's asset host) to (re)fetch that source's rows and merge them into
an existing --out manifest; a plain rebuild (no --add-source) never touches any of them, so
it stays exactly as reproducible from a blocked sandbox as before. `govinfo-signed` (a
curated set of GPO-signed documents, forced into the `signed` category) and
`govinfo-large` (a curated set of large Federal Register issues, forced into the `large`
category) are deliberately separate source keys against the same host for two different
purposes -- see SOURCES_BLOCKED below for why plain `govinfo` (the *bulk* generator #434
item 4 asks for) stays blocked regardless. See NON_GIT_SOURCES, FEDERAL_SOURCES and
DIRECT_SOURCES below, and README.md, "Extending".
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
# Register/CFR archive for large-file coverage) stays blocked here: nobody has designed or
# verified that generator yet. `govinfo-signed` (#471 part 1) and `govinfo-large` (#471
# part 4) are both narrower, already-verified generators against the same host for two
# different purposes -- a curated set of GPO-signed documents, and a curated set of large
# Federal Register issues -- and are each intentionally kept under their own source key, not
# `govinfo`, so neither collides with the other or with the bulk generator whenever that is
# eventually implemented.
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

# Selection (100-200 asked for; 108 chosen -- 62 HMRC + 22 Home Office + 24 DWP -- resolved
# one publication page at a time, never scraped, verified 2026-09-28):
#
#   self-assessment-core/supplementary/entity-returns/specialist/short-return (21)
#                       SA100 and the schedule family that attaches to it. The highest-value
#                       hierarchy cases: SA106 repeats per country, SA108 per disposal,
#                       SA800/SA900's partnership and trust statements repeat per partner/
#                       beneficiary -- the two most deeply nested HMRC documents here.
#   agent-repayment-admin (5)   64-8, R43, P87, P53, P55 -- short, mostly flat claim/
#                       authorisation forms; P87 is a confirmed 8pp AcroForm.
#   paye-p11d-worksheets (7)    P11D WS1-WS6 benefit-calculation worksheets; WS4 (loans)
#                       and WS6 (mileage) are line-item/tabular, the rest single-item.
#   paye-employer-admin (3)     BC539 App.1 + two Starter Checklist variants -- flat, short.
#   paye-nic-settlement (2)     NSR Appendix 7A/7B -- each covers multiple employees, the
#                       strongest explicit repeating-record evidence in the PAYE set.
#   vat-registration/group/refunds-certificates/schemes-payments/option-to-tax/
#   reliefs-vehicles (16)       the VAT forms that still exist as static PDFs (several core
#                       VAT forms -- VAT1, VAT7, VAT50/51, VAT600 series -- have moved to
#                       XFA .xdp interactive forms on a separate HMRC service and are out
#                       of scope for this manifest, which is PDF-only; worth its own future
#                       source if MegaPDF wants .xdp coverage). VAT2 and VAT1617A repeat rows.
#   corporation-tax-registration/return-family (9)   CT41G plus CT600 and seven of its
#                       supplementary pages (A/B/C/E/F/J/L) -- CT600A/B/C/J each repeat a
#                       per-participator/CFC/group-member/scheme-reference row: the deepest
#                       `/Parent` hierarchies in the HMRC set after SA800/SA900.
#
#   asylum-support (5)  ASF1 (36pp, whole-household/dependants -- the flagship Home Office
#                       repeating form) plus its Section 4 and integration-loan siblings.
#   visa-extension-humanitarian (3)   FLR(P) (27pp) and its 48pp fee-waiver form (an
#                       itemised income/expenditure/household breakdown -- the single
#                       longest, most repeating Home Office PDF found) plus a 1pp payment
#                       slip: a useful complexity spread from one publication page.
#   visa-settlement-other/forces-family/domestic-violence/detention-bail (4)
#                       the visa-route PDFs that survived the 2018 move to online-only
#                       applications (FLR(AF)/SET(AF)/SET(DV)/FLR(M)/SET(M)/SET(O) and most
#                       of the rest of that family are paper-withdrawn and no longer exist
#                       as PDFs -- confirmed on their own gov.uk pages, not guessed).
#   nationality-naturalisation/registration/admin (10)   Form AN (29pp, repeating
#                       employment/travel/address history) plus the postal registration
#                       routes (MN1/T/B(OTA)/UKF/MN3, 17-30pp each) and short admin forms.
#
#   Home Office also publishes a `passport-admin` group (a guidance booklet, PD1/PD2, LS01,
#   the overseas application form and its payment slip) that this manifest DELIBERATELY
#   EXCLUDES: OGL v3.0's own exemptions list "identity documents such as the British
#   Passport", and whether that carve-out reaches a blank application form or only the
#   issued document itself could not be confirmed from HMPO's own Crown-copyright policy
#   document (it would not render as extractable text during verification). "Do not assume"
#   is the standing rule for this whole extension (Canada's forms are excluded from the
#   public manifest for exactly the inverse reason -- Crown copyright with no clear
#   permission, #456) so the group stays out rather than being included on a guess. It costs
#   nothing structurally: every one of those forms is short and flat, the least valuable
#   property here, and 108 forms clear the 100-200 target without it.
#
#   disability-benefits-long (6)   PIP1/PIP1(AI)/PIP2/WCA50/AA1/DLA1-Child. PIP2 (~50pp)
#                       and WCA50 (24pp, replacing the former separate ESA50/UC50 as of
#                       2026-05-05) are the DWP reference cases for repeating structure:
#                       both iterate a fixed activity schema with per-activity sub-questions.
#   industrial-injuries (4)   BI100A/PD/OAE/OD -- four near-sibling interactive claim forms
#                       whose employment/exposure history repeats per employer or incident.
#   carers-allowance (2), state-pension (4), pension-credit (2)   DS700 pair; BR1 plus the
#                       living-abroad IPC BR1 / IPC BR1 NSP variants (repeating country-by-
#                       country residence/work history) and flat BR19; PC1 plus PC1H's
#                       explicit repeating table of accounts/investments over GBP 10,000.
#   bereavement-maternity (5), winter-fuel-payment (1)   event-driven claims, mostly flat,
#                       plus one short one-pager as the simple-bucket anchor.
#
# Every URL below was read off a fetched gov.uk publication page, the same discipline
# IRS_FORMS/USCIS_FORMS document for their own filenames -- none guessed. A number of forms
# named in earlier drafts of this list turned out to have no PDF at all any more (HMRC's
# SA1/CWF1/SA303/SA370/SA371/R40/P85/P50 and the VAT forms above are online-only or XFA
# .xdp now; several Home Office visa routes are paper-withdrawn; DWP's ESA50/UC50/AtW1/New
# Style JSA/Cold Weather Payment have no separate paper form) and are left out rather than
# guessed at, the same as IRS's f1099b.pdf in FEDERAL_SOURCES above.
HMRC_FORMS = [
    UkForm("https://assets.publishing.service.gov.uk/media/69c14d07cfa346b9d4704a8d/SA100-2026.pdf",
           "SA100-2026.pdf", "self-assessment-core"),
    UkForm("https://assets.publishing.service.gov.uk/media/69c25b9ab920af63be1c770c/SA101_2026.pdf",
           "SA101_2026.pdf", "self-assessment-supplementary"),
    UkForm("https://assets.publishing.service.gov.uk/media/69bd6a7a7e02b81c0d1c75ae/SA102-2026.pdf",
           "SA102-2026.pdf", "self-assessment-supplementary"),
    UkForm("https://assets.publishing.service.gov.uk/media/69c12ae013101e9908704a53/SA103S-2026.pdf",
           "SA103S-2026.pdf", "self-assessment-supplementary"),
    UkForm("https://assets.publishing.service.gov.uk/media/69c2635b13101e9908704b36/SA103F_2026.pdf",
           "SA103F_2026.pdf", "self-assessment-supplementary"),
    UkForm("https://assets.publishing.service.gov.uk/media/69c4e2c4471d520038d0f651/SA104S_2026.pdf",
           "SA104S_2026.pdf", "self-assessment-supplementary"),
    UkForm("https://assets.publishing.service.gov.uk/media/69c1434ecfa346b9d4704a7f/SA104F_2026.pdf",
           "SA104F_2026.pdf", "self-assessment-supplementary"),
    UkForm("https://assets.publishing.service.gov.uk/media/69cd19d6eafd66b876458ba9/SA105_2026_v0.1.pdf",
           "SA105_2026_v0.1.pdf", "self-assessment-supplementary"),
    UkForm("https://assets.publishing.service.gov.uk/media/69bbd5d34b1db2e4ba9b645f/SA106_2026.pdf",
           "SA106_2026.pdf", "self-assessment-supplementary"),
    UkForm("https://assets.publishing.service.gov.uk/media/69bd902913101e99087049e0/SA107-2026.pdf",
           "SA107-2026.pdf", "self-assessment-supplementary"),
    UkForm("https://assets.publishing.service.gov.uk/media/69bd8990cfa346b9d47049e4/SA108-2026.pdf",
           "SA108-2026.pdf", "self-assessment-supplementary"),
    UkForm("https://assets.publishing.service.gov.uk/media/69c2ab5fbd142c66ffe4437a/SA109-2026.pdf",
           "SA109-2026.pdf", "self-assessment-supplementary"),
    UkForm("https://assets.publishing.service.gov.uk/media/69c4fc1123fcbcd838a6f6e8/SA110-2026.pdf",
           "SA110-2026.pdf", "self-assessment-supplementary"),
    UkForm("https://assets.publishing.service.gov.uk/media/69c16433bb0dfe55b83e4bc8/sa700-2026.pdf",
           "sa700-2026.pdf", "self-assessment-entity-returns"),
    UkForm("https://assets.publishing.service.gov.uk/media/69c525cacdfd19de13d0f6d0/SA800man-2026.pdf",
           "SA800man-2026.pdf", "self-assessment-entity-returns"),
    UkForm("https://assets.publishing.service.gov.uk/media/69ba5b0626909a14239612e2/SA900man-2026.pdf",
           "SA900man-2026.pdf", "self-assessment-entity-returns"),
    UkForm("https://assets.publishing.service.gov.uk/media/69bbe2a38006048065f73cd3/SA970-2026.pdf",
           "SA970-2026.pdf", "self-assessment-entity-returns"),
    UkForm("https://assets.publishing.service.gov.uk/media/69bbd298f7b1c24d8e23ce0b/SA103L-2026.pdf",
           "SA103L-2026.pdf", "self-assessment-specialist"),
    UkForm("https://assets.publishing.service.gov.uk/media/69c14e217e02b81c0d1c7673/SA803-2026.pdf",
           "SA803-2026.pdf", "self-assessment-specialist"),
    UkForm("https://assets.publishing.service.gov.uk/media/69baa6162f28cf1b45bbe536/SA901-2026.pdf",
           "SA901-2026.pdf", "self-assessment-specialist"),
    UkForm("https://assets.publishing.service.gov.uk/media/69c3cddeb66ff902f454414b/SA211-Notes-2026.pdf",
           "SA211-Notes-2026.pdf", "self-assessment-short-return"),
    UkForm("https://assets.publishing.service.gov.uk/media/6852e9f02b367fdd44c15e8a/64-8.pdf",
           "64-8.pdf", "agent-repayment-admin"),
    UkForm("https://assets.publishing.service.gov.uk/media/6ab24f8cfceb6fb3a6500f75/R43_Manual.pdf",
           "R43_Manual.pdf", "agent-repayment-admin"),
    UkForm("https://assets.publishing.service.gov.uk/media/6a3a649433bc5beefd3c4772/P87.pdf",
           "P87.pdf", "agent-repayment-admin"),
    UkForm("https://assets.publishing.service.gov.uk/media/64ba514aef537100147af055/P53_0622.pdf",
           "P53_0622.pdf", "agent-repayment-admin"),
    UkForm("https://assets.publishing.service.gov.uk/media/67cab89cade26736dbf9ffe3/P55_2025.pdf",
           "P55_2025.pdf", "agent-repayment-admin"),
    UkForm("https://assets.publishing.service.gov.uk/media/69a991d02fd1694513b9b1d3/P11D-WS1-25-26.pdf",
           "P11D-WS1-25-26.pdf", "paye-p11d-worksheets"),
    UkForm("https://assets.publishing.service.gov.uk/media/69afd36fd620c14fa183ef25/P11D_WS2_25_26.pdf",
           "P11D_WS2_25_26.pdf", "paye-p11d-worksheets"),
    UkForm("https://assets.publishing.service.gov.uk/media/69afd395c78869bf8eb8a5a5/P11D_WS2b_25-26.pdf",
           "P11D_WS2b_25-26.pdf", "paye-p11d-worksheets"),
    UkForm("https://assets.publishing.service.gov.uk/media/69b019d3187a6dea78233103/P11D-WS3-25-26.pdf",
           "P11D-WS3-25-26.pdf", "paye-p11d-worksheets"),
    UkForm("https://assets.publishing.service.gov.uk/media/69aefb2c671a8a924c83eef1/P11D-WS4-25-26.pdf",
           "P11D-WS4-25-26.pdf", "paye-p11d-worksheets"),
    UkForm("https://assets.publishing.service.gov.uk/media/69afe920d620c14fa183ef31/P11D-WS5-25-26.pdf",
           "P11D-WS5-25-26.pdf", "paye-p11d-worksheets"),
    UkForm("https://assets.publishing.service.gov.uk/media/69aa876bac93547152b9b24e/P11D_WS6_25-26.pdf",
           "P11D_WS6_25-26.pdf", "paye-p11d-worksheets"),
    UkForm("https://assets.publishing.service.gov.uk/media/69428b6b8f4636fa2c547d64/emp-history-consent-app1.pdf",
           "emp-history-consent-app1.pdf", "paye-employer-admin"),
    UkForm("https://assets.publishing.service.gov.uk/media/69959214bfdab2546272bf04/Starter_checklist.pdf",
           "Starter_checklist.pdf", "paye-employer-admin"),
    UkForm("https://assets.publishing.service.gov.uk/media/6995922cb33a4db7ff889d4a/Expat-starter-checklist.pdf",
           "Expat-starter-checklist.pdf", "paye-employer-admin"),
    UkForm("https://assets.publishing.service.gov.uk/media/5b752c4be5274a0bb1f7d5c5/NSR_EP_Appendix_7A_.pdf",
           "NSR_EP_Appendix_7A_.pdf", "paye-nic-settlement"),
    UkForm("https://assets.publishing.service.gov.uk/media/5c389e8ded915d50bd133ac9/NSR_Appendix_7B_NICs_Settlement_Return.pdf",
           "NSR_Appendix_7B_NICs_Settlement_Return.pdf", "paye-nic-settlement"),
    UkForm("https://assets.publishing.service.gov.uk/media/5fe31eaed3bf7f089a7919c9/VAT1A-12-20.pdf",
           "VAT1A-12-20.pdf", "vat-registration"),
    UkForm("https://assets.publishing.service.gov.uk/media/5fe1bee18fa8f56af97b1e1a/VAT1B-12-20.pdf",
           "VAT1B-12-20.pdf", "vat-registration"),
    UkForm("https://assets.publishing.service.gov.uk/media/68dfb1bd750fcf90fa6ffd9f/VAT2.pdf",
           "VAT2.pdf", "vat-registration"),
    UkForm("https://assets.publishing.service.gov.uk/media/6a55f456c3d64d94cacab91b/VAT68.pdf",
           "VAT68.pdf", "vat-registration"),
    UkForm("https://assets.publishing.service.gov.uk/media/664c7386bd01f5ed32793f34/VAT1TR-05-24-English.pdf",
           "VAT1TR-05-24-English.pdf", "vat-registration"),
    UkForm("https://assets.publishing.service.gov.uk/media/681dbb72275cb67b18d87115/VAT56-11-22.pdf",
           "VAT56-11-22.pdf", "vat-group"),
    UkForm("https://assets.publishing.service.gov.uk/media/665ee6bf7b792ffff71a86a6/VAT65A.pdf",
           "VAT65A.pdf", "vat-refunds-certificates"),
    UkForm("https://assets.publishing.service.gov.uk/media/5a7dfba0e5274a2e8ab45142/vat66a.pdf",
           "vat66a.pdf", "vat-refunds-certificates"),
    UkForm("https://assets.publishing.service.gov.uk/media/610a7a30e90e0706c8fede44/VAT623.pdf",
           "VAT623.pdf", "vat-schemes-payments"),
    UkForm("https://assets.publishing.service.gov.uk/media/62fa5938e90e0779df0337cf/VAT1614C.pdf",
           "VAT1614C.pdf", "vat-option-to-tax"),
    UkForm("https://assets.publishing.service.gov.uk/media/5a7f1cdee5274a2e8ab4a310/vat1614d.pdf",
           "vat1614d.pdf", "vat-option-to-tax"),
    UkForm("https://assets.publishing.service.gov.uk/media/654102306de3b90012a7a6f2/VAT1614J.pdf",
           "VAT1614J.pdf", "vat-option-to-tax"),
    UkForm("https://assets.publishing.service.gov.uk/media/5a74dba9ed915d502d6cb8e3/VAT1617A.pdf",
           "VAT1617A.pdf", "vat-reliefs-vehicles"),
    UkForm("https://assets.publishing.service.gov.uk/media/600169fd8fa8f55f6a3414a0/VAT411.pdf",
           "VAT411.pdf", "vat-reliefs-vehicles"),
    UkForm("https://assets.publishing.service.gov.uk/media/5ff70524d3bf7f65cf03a513/VAT411A.pdf",
           "VAT411A.pdf", "vat-reliefs-vehicles"),
    UkForm("https://assets.publishing.service.gov.uk/media/5a7e320040f0b62305b81691/ct41g.pdf",
           "ct41g.pdf", "corporation-tax-registration"),
    UkForm("https://assets.publishing.service.gov.uk/media/69c543424a06660f085442bd/ct600.pdf",
           "ct600.pdf", "corporation-tax-return-family"),
    UkForm("https://assets.publishing.service.gov.uk/media/69cbe2a9a60a12ca3913c668/CT600A.pdf",
           "CT600A.pdf", "corporation-tax-return-family"),
    UkForm("https://assets.publishing.service.gov.uk/media/623de3b6e90e075f0e144710/CT600B_2022.pdf",
           "CT600B_2022.pdf", "corporation-tax-return-family"),
    UkForm("https://assets.publishing.service.gov.uk/media/5ad5dce3e5274a76be66c437/CT600C_2018.pdf",
           "CT600C_2018.pdf", "corporation-tax-return-family"),
    UkForm("https://assets.publishing.service.gov.uk/media/69ccf8bd5cf899414a0bc5dc/CT600E.pdf",
           "CT600E.pdf", "corporation-tax-return-family"),
    UkForm("https://assets.publishing.service.gov.uk/media/67c0159c72e83aab48866b5c/CT600F.pdf",
           "CT600F.pdf", "corporation-tax-return-family"),
    UkForm("https://assets.publishing.service.gov.uk/media/5a80293de5274a2e87db8360/CT600J_2015.pdf",
           "CT600J_2015.pdf", "corporation-tax-return-family"),
    UkForm("https://assets.publishing.service.gov.uk/media/69ce28cab5210036050bc6ce/CT600L.pdf",
           "CT600L.pdf", "corporation-tax-return-family"),
]

HOME_OFFICE_FORMS = [
    UkForm("https://assets.publishing.service.gov.uk/media/660bbb71f9ab419db2eea36d/appendix2-af-04-24.pdf",
           "appendix2-af-04-24.pdf", "visa-forces-family"),
    UkForm("https://assets.publishing.service.gov.uk/media/6997344c047739fe61889e67/MVDAC_02-26_-_reader_extended.pdf",
           "MVDAC_02-26_-_reader_extended.pdf", "visa-domestic-violence"),
    UkForm("https://assets.publishing.service.gov.uk/media/652e5016d86b1b00143a50c2/Asylum_Support_Application_Form_ASF1.pdf",
           "Asylum_Support_Application_Form_ASF1.pdf", "asylum-support"),
    UkForm("https://assets.publishing.service.gov.uk/media/68cc17e3a1e4472207995d68/Section+4_2_+Medical+Declaration.pdf",
           "Section+4_2_+Medical+Declaration.pdf", "asylum-support"),
    UkForm("https://assets.publishing.service.gov.uk/media/5a7d7069ed915d2d2ac08f85/section_4_service_users_2014.pdf",
           "section_4_service_users_2014.pdf", "asylum-support"),
    UkForm("https://assets.publishing.service.gov.uk/media/5e860120e90e0706ecc6942d/integration-loan-04-20-gov.uk.pdf",
           "integration-loan-04-20-gov.uk.pdf", "asylum-support"),
    UkForm("https://assets.publishing.service.gov.uk/media/5e86015286650c7439bb0625/loan-information-sheet-amended-02.04.2020a.pdf",
           "loan-information-sheet-amended-02.04.2020a.pdf", "asylum-support"),
    UkForm("https://assets.publishing.service.gov.uk/media/699831f9b33a4db7ff889ea3/FLR_P__02-26.pdf",
           "FLR_P__02-26.pdf", "visa-extension-humanitarian"),
    UkForm("https://assets.publishing.service.gov.uk/media/69972d8a9f2f510ba1d0b97e/FLR__P__Fee_Waiver_form_02-26_-_reader_extended.pdf",
           "FLR__P__Fee_Waiver_form_02-26_-_reader_extended.pdf", "visa-extension-humanitarian"),
    UkForm("https://assets.publishing.service.gov.uk/media/6895dda5a6eb81a3f9b2e2aa/Payment+slip+for+FLR_P_+form.pdf",
           "Payment+slip+for+FLR_P_+form.pdf", "visa-extension-humanitarian"),
    UkForm("https://assets.publishing.service.gov.uk/media/699ed6ce532c9ad91ebbcc7d/set_gt_-form-03-26.pdf",
           "set_gt_-form-03-26.pdf", "visa-settlement-other"),
    UkForm("https://assets.publishing.service.gov.uk/media/5fa26d298fa8f57899630c4f/application-for-sos-immigration-bail-_03-11-20___002_.pdf",
           "application-for-sos-immigration-bail-_03-11-20___002_.pdf", "visa-detention-bail"),
    UkForm("https://assets.publishing.service.gov.uk/media/699848a4339ee33f3ad0b9d1/form-an-03-26.pdf",
           "form-an-03-26.pdf", "nationality-naturalisation"),
    UkForm("https://assets.publishing.service.gov.uk/media/69982ebd9f2f510ba1d0b9ab/form-mn1-03-26.pdf",
           "form-mn1-03-26.pdf", "nationality-registration"),
    UkForm("https://assets.publishing.service.gov.uk/media/69972c35a58a315dbe72c040/form-t-02-26.pdf",
           "form-t-02-26.pdf", "nationality-registration"),
    UkForm("https://assets.publishing.service.gov.uk/media/6995a3c5b33a4db7ff889d65/Form_B_OTA__02-26.pdf",
           "Form_B_OTA__02-26.pdf", "nationality-registration"),
    UkForm("https://assets.publishing.service.gov.uk/media/6996ed7c339ee33f3ad0b919/form-ukf-02-26.pdf",
           "form-ukf-02-26.pdf", "nationality-registration"),
    UkForm("https://assets.publishing.service.gov.uk/media/699dd92c6457311dafbbcc29/Form_MN3_02-2026.pdf",
           "Form_MN3_02-2026.pdf", "nationality-registration"),
    UkForm("https://assets.publishing.service.gov.uk/media/5caf5da3ed915d6f205c3982/form-rn-04-2019.pdf",
           "form-rn-04-2019.pdf", "nationality-admin"),
    UkForm("https://assets.publishing.service.gov.uk/media/5caf607640f0b615d2a041ec/form-nc-04-19.pdf",
           "form-nc-04-19.pdf", "nationality-admin"),
    UkForm("https://assets.publishing.service.gov.uk/media/6995e632047739fe61889dba/form-roa-02-26.pdf",
           "form-roa-02-26.pdf", "nationality-admin"),
    UkForm("https://assets.publishing.service.gov.uk/media/69ea1a88ed93f72cf81632ed/Nationality_Forms_Guide_-_April_2026.pdf",
           "Nationality_Forms_Guide_-_April_2026.pdf", "nationality-admin"),
    # passport-admin group DELIBERATELY EXCLUDED -- see the module comment above this list.
]

DWP_FORMS = [
    UkForm("https://assets.publishing.service.gov.uk/media/6602ac7ca6c0f7699def9102/pip1-claim-form.pdf",
           "pip1-claim-form.pdf", "disability-benefits-long"),
    UkForm("https://assets.publishing.service.gov.uk/media/6602ace9f1d3a0666832ad65/pip1-additional-information.pdf",
           "pip1-additional-information.pdf", "disability-benefits-long"),
    UkForm("https://assets.publishing.service.gov.uk/media/6602af72f1d3a09b1f32ac81/pip2-form-and-information-booklet__1_.pdf",
           "pip2-form-and-information-booklet__1_.pdf", "disability-benefits-long"),
    UkForm("https://assets.publishing.service.gov.uk/media/69e8e6ee20a498c16734ae44/wca-50.pdf",
           "wca-50.pdf", "disability-benefits-long"),
    UkForm("https://assets.publishing.service.gov.uk/media/695e704d2a4a53b73d513838/aa1.pdf",
           "aa1.pdf", "disability-benefits-long"),
    UkForm("https://assets.publishing.service.gov.uk/media/69271ecfb3b9afff34e96074/dla-for-children-claim-form.pdf",
           "dla-for-children-claim-form.pdf", "disability-benefits-long"),
    UkForm("https://assets.publishing.service.gov.uk/media/6984b30d2df808759a7bd746/bi100a-interactive-claim-form.pdf",
           "bi100a-interactive-claim-form.pdf", "industrial-injuries"),
    UkForm("https://assets.publishing.service.gov.uk/media/69b18f1ccdd628b29e3496b8/Diseases-Industrial-Injuries-Disablement-Benefit-interactive-claim-form-BI100PD.pdf",
           "Diseases-Industrial-Injuries-Disablement-Benefit-interactive-claim-form-BI100PD.pdf",
           "industrial-injuries"),
    UkForm("https://assets.publishing.service.gov.uk/media/6942c1688f4636fa2c547dda/bi100oae-interactive-claim-form.pdf",
           "bi100oae-interactive-claim-form.pdf", "industrial-injuries"),
    UkForm("https://assets.publishing.service.gov.uk/media/6942c1858f4636fa2c547ddb/bi100od-interactive-claim-form.pdf",
           "bi100od-interactive-claim-form.pdf", "industrial-injuries"),
    UkForm("https://assets.publishing.service.gov.uk/media/69ca56b595a323ea3496ec93/ds700carers-allowance-claim-for-english.pdf",
           "ds700carers-allowance-claim-for-english.pdf", "carers-allowance"),
    UkForm("https://assets.publishing.service.gov.uk/media/69ca56dd76f83be521bb3ced/ds700-state-pension-claim-form.pdf",
           "ds700-state-pension-claim-form.pdf", "carers-allowance"),
    UkForm("https://assets.publishing.service.gov.uk/media/69835177afabc06c35d343e8/br1.pdf",
           "br1.pdf", "state-pension"),
    UkForm("https://assets.publishing.service.gov.uk/media/694953b872075a1d4a5089f0/ipc-br1.pdf",
           "ipc-br1.pdf", "state-pension"),
    UkForm("https://assets.publishing.service.gov.uk/media/696a24dc1c8a70fc0a3b0468/IPCBR1NSP_12_25_SECURE.pdf",
           "IPCBR1NSP_12_25_SECURE.pdf", "state-pension"),
    UkForm("https://assets.publishing.service.gov.uk/media/695f7ba2714f11cb49776587/br19.pdf",
           "br19.pdf", "state-pension"),
    UkForm("https://assets.publishing.service.gov.uk/media/69a59bf0a56a5482312c7180/pc1-interactive.pdf",
           "pc1-interactive.pdf", "pension-credit"),
    UkForm("https://assets.publishing.service.gov.uk/media/69a59fe3a56a5482312c7185/pc1h-money-savings-investments.pdf",
           "pc1h-money-savings-investments.pdf", "pension-credit"),
    UkForm("https://assets.publishing.service.gov.uk/media/698c58b7d7b51513fcd8a3b1/bereavement-support-payment-form.pdf",
           "bereavement-support-payment-form.pdf", "bereavement-maternity"),
    UkForm("https://assets.publishing.service.gov.uk/media/6aa2ce629f95f408139c664f/ma1-claim-form.pdf",
           "ma1-claim-form.pdf", "bereavement-maternity"),
    UkForm("https://assets.publishing.service.gov.uk/media/69c510bdcdfd19de13d0f69e/sure-start-maternity-grant-sf100.pdf",
           "sure-start-maternity-grant-sf100.pdf", "bereavement-maternity"),
    UkForm("https://assets.publishing.service.gov.uk/media/69d62bbf5f858b0a771d2a48/Funeral_expenses_payment_interactive_claim_form_adult.pdf",
           "Funeral_expenses_payment_interactive_claim_form_adult.pdf", "bereavement-maternity"),
    UkForm("https://assets.publishing.service.gov.uk/media/69c3f033cdfd19de13d0f5d3/SF200-funeral-expenses-payment-claim-form-child.pdf",
           "SF200-funeral-expenses-payment-claim-form-child.pdf", "bereavement-maternity"),
    UkForm("https://assets.publishing.service.gov.uk/media/6a998794f5b35599aec191ba/winter-fuel-payment-form-2026-to-2027.pdf",
           "winter-fuel-payment-form-2026-to-2027.pdf", "winter-fuel-payment"),
]

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
    "large": None,     # #471 part 4: none of the pinned git sources currently carries a
                       # file >= 20 MB (today's largest is ~10.9 MB), but classify() will
                       # call one "large" the moment one ever does. Take every one found,
                       # the same as "form" -- a category the selection loop below does not
                       # know about is a row that silently disappears from the manifest with
                       # no error and no line in the summary print (caught by code review
                       # before this ever happened in practice, since govinfo's own "large"
                       # rows go through the DIRECT_SOURCES/NON_GIT_SOURCES carry-over path
                       # below, not this loop, and so never exercised this bucket).
}

CATEGORIES = ["form", "tagged", "malformed", "scan", "report", "large"]

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


# --------------------------------------------------------------------------------------
# Direct-URL sources (#471): UK OGL forms and govinfo large documents. Unlike FEDERAL_SOURCES
# there is no shared base_url -- every item already carries its own full, hand-resolved URL
# -- so `items` here is a plain (url, relpath) pair rather than (relpath, group).
#
# fetch_direct() below duplicates fetch_federal()'s request-gap throttle and HTTP-error
# handling rather than factoring them into one shared helper -- a deliberate call, not an
# oversight: three other agents are editing this same file concurrently (#455's sampling
# rewrite among them) as this is written, and fetch_federal() is exactly the kind of
# already-working, already-tested code a refactor-for-its-own-sake risks conflicting with
# mid-flight for no behavioural gain. Worth doing once the concurrent work has landed and
# this file is quiet again.
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
    way every git-sourced row is classified. Because `force_category` bypasses that read,
    it does NOT re-derive any confidence that the bytes on disk are complete -- that is
    what the Content-Length check below is for; a category forced without a size check
    would happily label a silently-truncated download "large" just because it was told to.

    A server that closes the connection early (a clean EOF, not a reset) raises nothing in
    `urllib` -- `resp.read()` just returns less data and then `b""`, and a truncated PDF can
    easily still start with `%PDF`. So when the response gives a `Content-Length`, the
    bytes actually written are checked against it before the file is accepted into the
    cache; a mismatch is treated exactly like an HTTP error (skipped, temp file removed),
    not silently kept. A server that omits `Content-Length` gets no such check -- the same
    trust fetch_federal() already places in a completed, non-raising `resp.read()`.

    The sha256 is computed once, incrementally, while a fresh download is written (never a
    second full read of a file that can be multiple GB); a cache hit -- already on disk
    from an earlier run -- is read once here to hash it, since nothing persists the digest
    across runs (the same cost fetch_federal() accepts for its own, much smaller, cached
    federal forms).

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
        fresh_digest = None
        if not os.path.exists(cachefile):
            wait = request_gap - (time.monotonic() - last_request)
            if wait > 0:
                time.sleep(wait)
            req = urllib.request.Request(url, headers={"User-Agent": DIRECT_USER_AGENT})
            tmp = cachefile + ".part"
            try:
                with urllib.request.urlopen(req, timeout=timeout) as resp, \
                        open(tmp, "wb") as out:
                    want_bytes = resp.headers.get("Content-Length")
                    want_bytes = int(want_bytes) if want_bytes is not None else None
                    got_bytes = 0
                    digest = hashlib.sha256()
                    for chunk in iter(lambda: resp.read(chunk_size), b""):
                        out.write(chunk)
                        digest.update(chunk)
                        got_bytes += len(chunk)
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
            if want_bytes is not None and got_bytes != want_bytes:
                skipped.append((relpath,
                                 f"truncated: got {got_bytes} bytes, "
                                 f"server said Content-Length {want_bytes}"))
                print(f"  {key}: {relpath}: truncated download ({got_bytes} of "
                      f"{want_bytes} bytes), skipping", file=sys.stderr)
                os.remove(tmp)
                continue
            with open(tmp, "rb") as fh:
                head = fh.read(4)
            if head != b"%PDF":
                skipped.append((relpath, "not a PDF"))
                os.remove(tmp)
                continue
            os.replace(tmp, cachefile)
            fresh_digest = digest

        size = os.path.getsize(cachefile)
        if fresh_digest is not None:
            digest = fresh_digest
        else:
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
#
# `attribution` here is not read by any code path, on purpose and consistently with
# FEDERAL_ATTRIBUTION_IRS/USCIS above: `manifest.tsv`'s COLUMNS has no attribution field,
# so it was never going to be. The manifest schema exists to make a source, licence and
# hash reproducible; the human-readable attribution text a reader must actually carry is
# licence-specific prose that lives in README.md ("Licences and attribution"), keyed by
# the `licence` column each row already carries (`OGL-UK-3.0` here). Recorded on the
# `DirectSource` anyway, the same way FEDERAL_ATTRIBUTION_* is recorded on `HttpSource`, as
# a provenance trail next to the code that fetches each source -- not as an enforcement
# mechanism, which OGL's real, binding requirement (unlike the federal forms') deserves to
# be stated plainly rather than implied by a namedtuple field nothing reads.
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
    # Named "govinfo-large", not "govinfo": #471 part 1's `govinfo-signed` (FEDERAL_SOURCES
    # above) already established the convention of keeping a narrow, curated govinfo.gov
    # generator under its own specific key, precisely so a future *bulk* `govinfo` generator
    # (still blocked -- see SOURCES_BLOCKED) never collides with either.
    "govinfo-large": DirectSource(
        key="govinfo-large", licence=GOVINFO_LICENCE, attribution=GOVINFO_ATTRIBUTION,
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
                f"NON_GIT_SOURCES (irs, uscis, govinfo-signed, uk-hmrc, uk-homeoffice,\n"
                f"uk-dwp, govinfo-large) in this file, which is real and verified.")
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
                host_for_error = (urllib.parse.urlparse(ds.items[0][0]).netloc
                                   if ds.items else "its source host")
        except FederalHostUnreachable as exc:
            sys.exit(
                f"--add-source {args.add_source}: {exc}.\n"
                f"That is a network problem here, not a design problem: this generator is\n"
                f"real and was verified on kdocker3 (#434 federal-forms extension; #471 for\n"
                f"govinfo-signed/uk-hmrc/uk-homeoffice/uk-dwp/govinfo-large). Run it from a\n"
                f"machine that can reach {host_for_error}.")

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
