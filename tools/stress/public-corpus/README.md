# The public corpus (#434)

A redistributable test corpus, assembled rather than inherited: this directory holds a
**manifest**, never a PDF. `fetch.sh` reproduces the documents from it on any machine and
checks every byte against a sha256, so the structure, Markdown and pages batteries can run
in CI, in a cloud sandbox, and on anyone's laptop.

That is the whole point. The private corpus is 4,337 of the owner's real documents; it can
never leave his machines, so until now the gates could only ever be run by him. This one
anybody can reproduce.

    tools/stress/public-corpus/fetch.sh                  # → ~/megapdf-public-corpus
    tools/stress/public-corpus/fetch.sh /data/corpus --jobs 8
    tools/stress/public-corpus/fetch.sh --verify-only    # re-check what is on disk

The two corpora are different populations and are **expected to give different numbers**.
See TESTING.md, "Which corpus gates what".

## Network reality — two different machines, two different answers

The corpus was first built (#434) from the Anthropic cloud sandbox, which cannot reach any
`.gov` host at all. It was extended (this change) from **kdocker3**, a real machine with
ordinary internet access, specifically to add the one category the sandbox could never
reach: real US federal fillable forms.

**From the Anthropic cloud sandbox** — measured 2026-09-27, `403 CONNECT`: the container's
egress proxy refuses the tunnel outright, so the request never reaches the host.

| #434 source | host | status | in the corpus? |
|---|---|---|---|
| IRS fillable forms | `www.irs.gov` | **403 CONNECT** | now yes — added from kdocker3 |
| USCIS forms | `www.uscis.gov` | **403 CONNECT** | now yes — added from kdocker3 |
| UK gov forms (#471) | `assets.publishing.service.gov.uk` | **403 CONNECT** (untested from the sandbox; same egress proxy as every other blocked host here) | now yes — added from kdocker3 |
| govinfo large documents (#471) | `www.govinfo.gov` | **403 CONNECT** (untested from the sandbox) | now yes — added from kdocker3, scoped to a handful of large Federal Register issues (see below); the *bulk* generator #434 originally asked for remains unbuilt |
| SafeDocs / UNSAFE-DOCS | `digitalcorpora.org`, `downloads.digitalcorpora.org` | **403 CONNECT** | no — out of scope for now |
| Isartor suite | `pdfa.org` | **403 CONNECT** | yes, *via veraPDF* (below) |
| veraPDF corpus | `github.com` (git), `raw.githubusercontent.com` | **200 / 206** | yes |
| qpdf, PDFium fixtures | `github.com` (git), `raw.githubusercontent.com` | **200 / 206** | yes |

Two details worth knowing before you conclude a source is unreachable from the sandbox:

* Plain HTTPS to `github.com` is **scoped to the session's own repositories** and answers
  403 with a JSON body for anything else — but the **anonymous git lane** (`git clone`,
  `git ls-remote`) serves any public repository, and `raw.githubusercontent.com` serves
  individual files at a pinned commit with no scoping at all. That is the door every
  git-sourced row in this manifest goes through.
* Everything else tried was refused: `chromium.googlesource.com`, `pdfium.googlesource.com`,
  `www.gpo.gov`, `www.sec.gov`, `europa.eu`, `archive.org`.

**From kdocker3** — measured 2026-09-27, plain `curl`, one representative URL per host:

| host | status |
|---|---|
| `www.irs.gov` (`/pub/irs-pdf/f1040.pdf`) | **200** |
| `www.uscis.gov` (`/sites/default/files/document/forms/i-9.pdf`) | **200** |
| `assets.publishing.service.gov.uk` (a UK gov.uk form PDF) | **200** — re-checked 2026-09-28 for #471 part 3 |
| `www.govinfo.gov` | **200** (a specific Federal Register PDF); **206** on a ranged request; **302** on `/robots.txt` — re-checked 2026-09-28 for #471 part 4 |

All #434/#471 names are reachable from a real machine. `safedocs` is still **not** implemented
— nobody has designed or verified a generator for it. `govinfo` moved from blocked to
implemented in #471, but **scoped narrowly**: #434 originally asked for a *bulk* generator,
and this is a hand-picked set of large Federal Register issues (see "Very large documents"
below), not that bulk generator — a future extension, not a silent scope-creep of this one.
This change (#434) added the highest-value category the original corpus and its milestone
called out — real federal fillable forms, from IRS and USCIS; #471 adds a second
jurisdiction's forms (UK, OGL-licensed) and the corpus's first large-document population.

**One fetching wrinkle, because it cost real time to find.** This container's apt-installed
`curl` (8.5.0, OpenSSL 3.0.13) is refused outright by `www.uscis.gov` — `403`, `server:
AkamaiGHost` — even with a browser `User-Agent` string. It is a TLS/HTTP2 fingerprint match,
not an IP block or a robots rule: Python's `urllib` (same host, same network path, same
container) is **not** refused. `build-manifest.py`'s federal-forms fetcher therefore uses
`urllib`, not `curl`, for both IRS and USCIS. `fetch.sh` (bash + curl) is unaffected because
by the time anyone runs it the rows are already in the manifest with their own pinned
hashes — it just downloads and verifies bytes, it does not need to out-fingerprint Akamai.
If `fetch.sh` itself is ever refused this way on some other machine, that is the same
symptom and the same fix (a different HTTP client, or a newer curl/OpenSSL build).

## What is in it

1,464 documents, ~4.24 GB (dominated by the seven `large` documents; everything else is
~197 MB). Categories are assigned from each file's own bytes by
`build-manifest.py`'s `classify()`, not from the directory it arrived in, so a row says what
the battery will actually meet.

| category | count | what it is |
|---|---:|---|
| `form` | 605 | `/AcroForm` — fields, checkboxes, radio groups, signature fields (see below: `/Widget` alone is not required any more) |
| `tagged` | 338 | a `/StructTreeRoot`, for the tagged-PDF path (#358) |
| `report` | 264 | ordinary text documents, for extraction fidelity and reading order |
| `malformed` | 150 | deliberately broken or deliberately non-conforming — crash/hang resistance only |
| `scan` | 100 | image pages with no font resources |
| `large` | 7 | 48.4 MB - 2.07 GB — the paging-in/performance path (#471 part 4, see below) |

By source: veraPDF 860, qpdf 181, PDFium 122, **IRS 136**, **USCIS 50**, **HMRC 62**,
**Home Office 22**, **DWP 24**, **govinfo 7**.

`form` is the one category taken **whole** — every one the sources contain — because it is
what MegaPDF is for and the category #434 calls the private corpus thinnest on. The others
are sampled on an even stride across the sorted tree, which is reproducible and does not
hand back every test for one specification clause and nothing after it.

### A classification bug this extension found and fixed

`classify()` used to require **both** `/AcroForm` and `/Widget` to appear literally in a
file's raw bytes before calling it a `form`; without `/Widget` too, a file with a struct
tree fell through to `tagged` instead. That was fine for the small, usually-uncompressed git
fixtures already here, but every real IRS/USCIS form is generated by modern, accessibility-
tagged software that stores its `Widget` annotations inside a **compressed object stream**
— invisible to a raw-byte scan — while `/AcroForm` itself sits in the (uncompressed) catalog
dictionary. Left as-is, this would have silently filed 185 of the 186 new federal forms
under `tagged`, not `form` — exactly backwards for the category this whole extension exists
to fill. Verified with `qpdf --qdf` (which decompresses object streams for inspection): all
186 do carry real `/Widget` annotations once decompressed.

The fix drops the `/Widget` requirement — `has_acroform` alone now selects `form`, ahead of
the `tagged` check — matching the comment already on that code ("a form is a form even when
it is also tagged"), which the old `and has_widget` silently defeated. This also reclassified
126 **existing** git-sourced fixtures from `tagged` to `form` (237 → 363 before the federal
rows were added), the same shape appearing in some of veraPDF's and qpdf's own modern test
files. `tools/stress/public-corpus/build-manifest.py`'s `classify()` has the full comment;
this is a manifest-generation fix, not a change to the engine, a battery, or a gate.

## The federal forms (#434 federal-forms extension)

136 IRS forms and 50 USCIS forms, chosen deliberately rather than however many a scraper
happened to find, and grouped so the choice is legible:

| group | count | why |
|---|---:|---|
| IRS 1040-family | 21 | the most-filed federal form and its core schedules: filing-status radio group, checkboxes throughout, Schedule 8812/EIC repeating dependent rows, Schedule E's repeating property rows |
| IRS simple | 17 | near-one-page forms with no repeating structure — a baseline so "real forms are hard" doesn't overstate the case |
| IRS signature | 4 | Form 2848 (power of attorney, with its own repeating representative table), 8821, 8879, 8453 — forms whose entire purpose is a signature field |
| IRS credits | 28 | adjustments and credits with repeating rows (depreciation assets, capital-gain transactions, noncash-contribution items, monthly premium-credit tables) and checkbox/radio groups |
| IRS long | 34 | multi-page business/entity returns — 1120, 1065, 990, 706, 709, 1041, 5471, 8865, 3520(-A), the CIS 433 series — with deep `/Parent` hierarchies and repeating subforms (K-1 schedules, shareholder/partner tables): the shape #174's page-import refuses today, and the reason this source exists |
| IRS employment | 12 | checkbox-heavy quarterly/annual employment and excise returns |
| IRS info-returns | 20 | the 1098/1099 families: multi-copy layouts, void/corrected checkboxes — the shape a payroll or accounting integration actually feeds in |
| USCIS simple | 11 | short forms: I-9's checkbox-heavy attestation, G-28/G-325A/G-1145/G-639 administrative forms |
| USCIS long | 39 | long petitions and applications with deep hierarchical/repeating sections (family members, employment and address history) — the immigration-side counterpart of the IRS "long" group. Includes I-864's repeating household-member table and N-400, the longest and most heavily sectioned form either agency publishes |

The 1040 family, common schedules, and a spread of USCIS petitions are the backbone the
current milestone asked for by name. `f1099b.pdf` 404s as of 2026-09-27 (IRS's own
"about this form" page links to it, but the file is not there) and is skipped rather than
guessed at — one retired-or-broken link out of 187 candidates checked.

Every filename was resolved from the agency's own per-form page (IRS's `/forms-pubs/about-
form-*`, USCIS's `/sites/default/files/document/forms/*`) and fetched, hashed and confirmed
to start with `%PDF` before going in the manifest — see `build-manifest.py`'s `IRS_FORMS` /
`USCIS_FORMS` for the exact list and grouping, and "Extending" below for how to re-verify or
extend it.

**A real difference from the git sources, stated plainly.** veraPDF/qpdf/PDFium rows are
pinned to a **commit** — the sha256 is true forever, because the bytes at that commit never
change. IRS/USCIS rows are pinned to **today's bytes at a live URL** — there is no commit to
pin. If either agency revises a form, a future `--add-source` run will fetch different bytes
and the sha256 will change; `fetch.sh`'s existing "a wrong hash is a hard, named failure,
never silently re-fetched over" behaviour is exactly the right response to that, and needed
no changes to produce it. This is also why federal rows are **not** part of the default
`build-manifest.py` rebuild (see "Extending"): a plain rebuild stays fully reproducible from
a sandbox that cannot reach either host, exactly as it always has.

## UK government forms (#471 part 3)

108 forms — 62 HMRC, 22 Home Office, 24 DWP — chosen deliberately, one gov.uk publication
page at a time, never scraped:

| group | count | why |
|---|---:|---|
| HMRC self-assessment (core/supplementary/entity-returns/specialist/short-return) | 21 | SA100 and the schedule family that attaches to it; SA106 repeats per country, SA108 per disposal, SA800/SA900's partnership and trust statements repeat per partner/beneficiary — the two most deeply nested HMRC documents here |
| HMRC agent/repayment admin | 5 | 64-8, R43, P87, P53, P55 — short, mostly flat claim/authorisation forms; P87 is a confirmed 8pp AcroForm |
| HMRC PAYE P11D worksheets | 7 | WS1-WS6 benefit-calculation worksheets; WS4 (loans) and WS6 (mileage) are line-item/tabular |
| HMRC PAYE employer admin | 3 | BC539 App.1 + two Starter Checklist variants — flat, short |
| HMRC PAYE NIC settlement | 2 | NSR Appendix 7A/7B — each explicitly covers multiple employees, the strongest repeating-record evidence in the PAYE set |
| HMRC VAT (registration/group/refunds/schemes/option-to-tax/vehicles) | 16 | the VAT forms still published as static PDFs (several core VAT forms have moved to XFA `.xdp` interactive forms on a separate HMRC service — out of scope here, a candidate for a future source) |
| HMRC corporation tax | 9 | CT41G plus CT600 and seven supplementary pages (A/B/C/E/F/J/L) — CT600A/B/C/J each repeat a per-participator/CFC/group-member/scheme-reference row: the deepest `/Parent` hierarchies in the HMRC set after SA800/SA900 |
| Home Office asylum support | 5 | ASF1 (36pp, whole-household/dependants — the flagship Home Office repeating form) plus its Section 4 and integration-loan siblings |
| Home Office visa extension (humanitarian) | 3 | FLR(P) (27pp) and its 48pp fee-waiver form (an itemised income/expenditure/household breakdown), plus a 1pp payment slip |
| Home Office visa (settlement/forces/domestic-violence/detention) | 4 | the visa-route PDFs that survived the 2018 move to online-only applications |
| Home Office nationality (naturalisation/registration/admin) | 10 | Form AN (29pp, repeating employment/travel/address history) plus the postal registration routes (17-30pp each) and short admin forms |
| DWP disability benefits | 6 | PIP1/PIP1(AI)/PIP2/WCA50/AA1/DLA1-Child — PIP2 (~50pp) and WCA50 (24pp) iterate a fixed activity schema with per-activity sub-questions, the DWP reference case for repeating structure |
| DWP industrial injuries | 4 | BI100A/PD/OAE/OD — four near-sibling interactive claim forms whose history repeats per employer/incident |
| DWP carer's allowance / state pension / pension credit | 8 | DS700 pair; BR1 plus the living-abroad IPC BR1 variants (repeating country-by-country residence/work history); PC1H's explicit repeating table of accounts/investments |
| DWP bereavement/maternity / winter fuel | 6 | event-driven claims, mostly flat, plus one short one-pager as the simple-bucket anchor |

Every URL was resolved from the form's own gov.uk publication page and fetched, hashed and
confirmed to start with `%PDF` before going in the manifest — see `build-manifest.py`'s
`HMRC_FORMS` / `HOME_OFFICE_FORMS` / `DWP_FORMS` for the exact list and grouping.

**What no longer exists as a static PDF**, and is therefore left out rather than guessed at:
several core HMRC VAT/CT/payroll forms (VAT1, VAT7, VAT50/51, VAT600 series, CT2, P46(Car),
P350) have moved to XFA `.xdp` interactive forms on a separate HMRC service — a real, and
separately interesting, future source if MegaPDF wants `.xdp` coverage. HMRC's SA1, CWF1,
SA303, SA370/371, R40, P85 and P50 are online-only digital services with no blank PDF. Most
Home Office visa routes other than the ones listed (FLR(AF), SET(AF), SET(DV), FLR(M),
SET(M), SET(O) and others) are paper-withdrawn since 2018 — confirmed on their own gov.uk
pages, not assumed. DWP's ESA50 and UC50 were merged into WCA50 on 2026-05-05, and several
benefits (Universal Credit's general claim, Access to Work, New Style JSA, Cold Weather
Payment) are online-only with no separate paper form.

### The Open Government Licence, verified rather than assumed

Canadian Crown copyright is why CRA/IRCC forms are local-only (#456) — Crown copyright with
no clear permission for third-party redistribution. UK central-government material is
different, and this was checked from the licence's own text, not assumed:

> "You are free to: copy, publish, distribute and transmit the Information; adapt the
> Information; **exploit the Information commercially and non-commercially** for example, by
> combining it with other Information, or by including it in your own product or application."
> — [Open Government Licence v3.0](https://www.nationalarchives.gov.uk/doc/open-government-licence/version/3/), "Open Government Licence for public sector information", nationalarchives.gov.uk, verified 2026-09-28

Attribution is a **condition** of that permission, unlike the US federal forms above:

> "You must (where you do any of the above): acknowledge the source of the Information in
> your product or application by including or linking to any attribution statement specified
> by the Information Provider(s) and, where possible, provide a link to this licence."

None of the four gov.uk publication pages checked directly (one per agency, plus the Home
Office visa-forms collection page) specify their own attribution statement, so OGL's own
fallback applies, and is the credit this corpus carries (see "Licences and attribution"
below):

> "Contains public sector information licensed under the Open Government Licence v3.0."

Every one of those four pages carries the identical footer, verbatim: "All content is
available under the Open Government Licence v3.0, except where otherwise stated" plus a
"© Crown copyright" line — and on none of the four is "except where otherwise stated"
attached to anything specific (no per-item exclusion notice, no third-party carve-out), so
the forms linked from them read as OGL-covered.

**The one exception, checked and acted on rather than glossed over.** OGL v3.0's own
exemptions list "identity documents such as the British Passport" as outside its scope. A
blank *application form* for a passport is arguably different from the issued document
itself, but this could not be confirmed from HMPO's own Crown-copyright policy statement (it
would not render as extractable text during verification), so — following the same
"do not assume" standard that keeps Canada's forms out entirely — Home Office's
`passport-admin` group (a guidance booklet, PD1/PD2, LS01, the overseas application form and
its payment slip: 6 forms) is **excluded** from this manifest. Nothing else in the 108 rows
carries this ambiguity.

## Very large documents (#471 part 4)

#147-#151 did substantial work paging a 2.5 GB file in, and nothing in the public corpus
before this was remotely large enough to exercise that path — every existing document tops
out at 10.9 MB. Seven Federal Register issues from govinfo.gov close that gap, marked with a
new `large` category (`classify()`'s `LARGE_THRESHOLD_BYTES`, 20 MB: comfortably above every
prior document, comfortably below every one of these):

| document | date | size | why this one |
|---|---|---:|---|
| `FR-1974-01-03.pdf` | 1974-01-03 | 48.4 MB | just above the ~50 MB floor asked for |
| `FR-1976-10-01.pdf` | 1976-10-01 | 83.4 MB | |
| `FR-1978-12-29.pdf` | 1978-12-29 | 107.4 MB | |
| `FR-1980-01-02.pdf` | 1980-01-02 | 143.5 MB | Vol. 45 No. 1, 606pp |
| `FR-1983-04-25.pdf` | 1983-04-25 | 232.0 MB | Vol. 48 No. 80, 1031pp |
| `FR-1993-04-26.pdf` | 1993-04-26 | 1.57 GB | Vol. 58 No. 78, Spring Unified Agenda, 3648pp |
| `FR-1994-11-14.pdf` | 1994-11-14 | 2.07 GB | Vol. 59 No. 218, Fall Unified Agenda, 2006pp — the largest found |

**Why Federal Register and not CFR.** #434's original note named both Federal Register and
CFR annual volumes as govinfo's large-document candidates. CFR turned out not to have any:
checked by HTTP HEAD (`Content-Length`, no download) across the largest titles (7, 21, 26,
40, 48), every CFR title/volume PDF tops out around 4-9 MB — they are chunked per title
specifically to stay manageable. Federal Register issues are large for two reasons: pre-1995
issues are OCR'd scans of the printed page image rather than born-digital text, and two of
the seven above happen to be the day of the year the twice-yearly **Unified Agenda of Federal
Regulations** (a regulatory-plan compilation) was published as a section inside that day's
ordinary issue, running to 1,700-2,000+ pages in a single file. Several more Unified-Agenda
issues of similar size exist (1990, 1992) and were not needed to make the point.

**Public domain, verified rather than assumed.** From govinfo.gov's own policies page:

> "Copyright protection under this title is not available for any work of the United States
> Government" (17 U.S.C. § 105)

with one caveat noted on the same page and checked against these specific documents: a
government publication can incorporate copyrighted third-party material used with permission.
The Federal Register is the agencies' own regulatory text, not a compilation of outside
material, so this does not apply here.

**Timing and memory: a number for a future regression to be compared against.** Wall time and
peak RSS per document, running the shipped `megapdf-cli extract` (full text extraction — the
same open-and-page-through-the-whole-document shape #147-#151's paging-in work targets)
inside the same `--cpus=4 --memory=8g` container the rest of this extension used:

| document | size | wall time | peak RSS |
|---|---:|---:|---:|
| `FR-1974-01-03.pdf` | 48.4 MB | 1.42 s | 290 MB |
| `FR-1976-10-01.pdf` | 83.4 MB | 2.01 s | 423 MB |
| `FR-1978-12-29.pdf` | 107.4 MB | 2.47 s | 513 MB |
| `FR-1980-01-02.pdf` | 143.5 MB | 3.69 s | 703 MB |
| `FR-1983-04-25.pdf` | 232.0 MB | 6.83 s | 1.26 GB |
| `FR-1993-04-26.pdf` | 1.57 GB | 13.36 s | 2.08 GB |
| `FR-1994-11-14.pdf` | 2.07 GB | 14.75 s | 2.40 GB |

Both wall time and peak RSS scale roughly linearly with document size, with no sign of a
pathological blow-up at the top of the range — the 2.07 GB document, the largest in the
corpus, finishes in under 15 seconds and under 2.5 GB of RSS inside the `--cpus=4 --memory=8g`
container the rest of this extension used. This is `megapdf-cli extract` only (full text
extraction, the open-and-page-through-the-whole-document shape #147-#151's work targets);
the three batteries below exercise more operations per document (page rotate/delete/move,
the internal structure API, Markdown conversion) and are the fuller answer to the timeout
question just below.

**Battery timeouts and this category.** `structure-battery.sh` and `markdown-battery.sh`
default `TIMEOUT` to 120s per document; `pages-battery.sh` to 300s. #442/#445 fixed how a
battery *classifies* a timeout (bounded, counted separately, never silently stalling the
whole run) but did not give the `large` category its own, longer value. See the measured
times above and this PR's own text for whether the current default is enough headroom for a
2 GB document or needs a category-specific override — **raised as a request in the PR, not
changed here**: `tools/stress/*-battery.sh` is out of bounds for this change the same way it
was out of bounds for #442/#445 (see those issues).

## ⚠ The malformed set

The 150 documents under `malformed` are **deliberately broken by design**. They exist to prove
the engine does not crash or hang, and nothing else. Some may trip anti-malware on the way
past. Open them with the engine under test; do not open them with something you care about,
and do not treat a refusal to parse one as a defect — a refusal is the correct answer.

Note this is *not* the SafeDocs sample #434 asks for: `digitalcorpora.org` remains
unreachable from the sandbox this corpus's core is built in, and implementing a `safedocs`
generator is out of scope for this change. These are Isartor's PDF/A-violating files and
qpdf's damaged-file fixtures, which are adjacent but milder. They are spec violations and
structural damage, not the adversarial fuzzed population SafeDocs provides.

## Licences and attribution

Every document here is redistributable. That was settled in #434 and is not re-litigated by
adding a source: nothing goes in the manifest without a licence that lets a third party fetch
and keep the file.

| source | licence | attribution required |
|---|---|---|
| [veraPDF-corpus](https://github.com/veraPDF/veraPDF-corpus) @ `bb75f4f0` | **CC BY 4.0** | **yes** — see below |
| [qpdf](https://github.com/qpdf/qpdf) test suite @ `4eba9589` | Apache-2.0 | notice retained |
| [PDFium](https://github.com/chromium/pdfium) `testing/resources` @ `a8432342` | BSD-3-Clause | notice retained |
| IRS fillable forms (`www.irs.gov/pub/irs-pdf/`) | **public domain — 17 U.S.C. § 105** | no |
| USCIS forms (`www.uscis.gov/.../document/forms/`) | **public domain — 17 U.S.C. § 105** | no |
| UK HMRC/Home Office/DWP forms (`assets.publishing.service.gov.uk`) | **Open Government Licence v3.0** | **yes** — see below |
| govinfo.gov Federal Register volumes (`www.govinfo.gov`) | **public domain — 17 U.S.C. § 105** | no |

CC BY 4.0 requires attribution wherever these files or results derived from them are
published. The required credit:

> veraPDF test corpus © the veraPDF Consortium, used under
> [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). Not modified.

**Federal works: 17 U.S.C. § 105.** "Copyright protection under this title is not available
for any work of the United States Government." An IRS or USCIS form, or a govinfo.gov
Federal Register volume, is prepared by federal employees/agencies as part of their official
duties, so no copyright ever attaches to it in the first place — there is no licence to
comply with, and no attribution requirement, though the manifest records `source` (`irs` /
`uscis` / `govinfo`) and a plain-English attribution string in `build-manifest.py` anyway, as
a provenance trail rather than a legal obligation. This is a **stronger** position than any
of the git sources above: those are copyrighted works licensed to permit redistribution;
federal works are simply never copyrighted at all. (govinfo.gov's own policies page notes
one caveat, checked and not applicable here: a government publication can incorporate
copyrighted third-party material used with permission; the Federal Register issues fetched
for #471 part 4 are the agency's own regulatory text, not a reprint of someone else's work.)

**UK Crown copyright, under the Open Government Licence v3.0: attribution IS required**, the
one real difference from every other source in this corpus. See "UK government forms" above
for the licence text and verification; the required credit, since none of the four gov.uk
pages checked specifies its own attribution statement:

> Contains public sector information licensed under the Open Government Licence v3.0.

Every reader of this corpus, or of results derived from its `uk-hmrc` / `uk-homeoffice` /
`uk-dwp` rows, must carry that credit forward — the same obligation CC BY 4.0 places on the
veraPDF credit above, and the reason this paragraph exists rather than a bare licence name in
the table.

**One licence nuance, stated rather than buried.** 117 of the `malformed` rows are the
**Isartor test files**, which arrive inside the veraPDF repository and are therefore covered
by that repository's blanket CC BY 4.0 statement. Isartor's own terms (pdfa.org) are narrower:
free to use for checking validation software, and **must not be used to claim that MegaPDF is
certified or conformant**. Both constraints are treated as binding here. What was *not*
verified — `pdfa.org` being unreachable from the sandbox this corpus's core is built in — is
whether the veraPDF Consortium holds the right to relicense Isartor's files under CC BY 4.0.
Treat those 117 rows as Isartor-terms files that happen to be distributed by veraPDF, and
never cite a run over them as a conformance claim.

## Extending

`build-manifest.py` regenerates the **git-sourced** part of the manifest from scratch, so
that part of the corpus is a function of its sources rather than a pile someone once
assembled:

    tools/stress/public-corpus/build-manifest.py --work /var/tmp/pc-build

Each git source is pinned to a commit, never a branch — a branch would silently invalidate
every sha256 in the file. To add one: give it a licence that permits redistribution, add a
`Source` row, pin the commit, and rebuild. **A plain rebuild never touches the network for
any direct-URL source and never deletes their rows either** — it preserves whatever
`irs`/`uscis`/`uk-hmrc`/`uk-homeoffice`/`uk-dwp`/`govinfo` rows are already in `--out` (see
`NON_GIT_SOURCES` in `build-manifest.py`), so it stays exactly as reproducible from a blocked
sandbox as it always was.

To (re)fetch a direct-URL source, from a machine that can reach its host — none of these are
reachable from Anthropic's cloud sandbox, all reachable from an ordinary machine (see
"Network reality" above):

    tools/stress/public-corpus/build-manifest.py --add-source irs
    tools/stress/public-corpus/build-manifest.py --add-source uscis
    tools/stress/public-corpus/build-manifest.py --add-source uk-hmrc
    tools/stress/public-corpus/build-manifest.py --add-source uk-homeoffice
    tools/stress/public-corpus/build-manifest.py --add-source uk-dwp
    tools/stress/public-corpus/build-manifest.py --add-source govinfo

Each merges its rows into the existing `manifest.tsv` (by URL: a matching sha256 is left
alone, a changed one is refreshed, a new one is added) rather than rebuilding everything
from nothing. `IRS_FORMS` / `USCIS_FORMS` / `HMRC_FORMS` / `HOME_OFFICE_FORMS` / `DWP_FORMS`
/ `GOVINFO_DOCS` in `build-manifest.py` are the exact, grouped lists; add an entry to one
(verified fetchable and `%PDF`-starting first) to extend it, or add a new `HttpSource` to
`FEDERAL_SOURCES` (shared `base_url` + filename) or `DirectSource` to `DIRECT_SOURCES`
(each item its own full URL — what UK forms and govinfo need, since neither shares one
prefix) for a different agency or host under a licence that permits redistribution.
`--add-source safedocs` still refuses outright — see `SOURCES_BLOCKED` — because nobody has
yet written and verified a generator for it; that refusal is deliberate, the same way every
source above used to refuse before its own generator was written, and should be resolved the
same way: implement and verify it from a machine that can reach the host, don't just delete
the refusal.

**`govinfo`'s scope, stated precisely.** This is a hand-picked list of 7 large Federal
Register issues (`GOVINFO_DOCS`), not the *bulk* generator #434 originally named — CFR annual
title volumes, also named there, turned out to be structurally incapable of being large
(checked by HTTP HEAD across the biggest titles: every one tops out around 4-9 MB) and are
not fetched at all. A bulk generator over govinfo's full holdings remains unbuilt and would
be a separate, future extension.

## Files

| file | what it is |
|---|---|
| `manifest.tsv` | the corpus: one row per document. Generated — never edit by hand |
| `fetch.sh` | reproduces the documents and proves every byte |
| `build-manifest.py` | how the manifest was generated, so it can be regenerated and extended |
