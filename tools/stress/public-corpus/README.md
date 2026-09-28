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

## Staged permanently on kdocker3 (#470)

`~/megapdf-public-corpus` above is the *default* — right for a laptop, CI or a cloud
sandbox running once. A machine that runs batteries repeatedly should not re-download
1,300+ documents from `irs.gov`, `uscis.gov` and GitHub every time: **kdocker3 stages this
corpus permanently at `~/pdf-public`**, beside `~/pdf-test` and `~/pdf-test-ca` (the private
corpora — see TESTING.md, "Corpus staging on k3"). Point any battery at it directly:

    tools/stress/public-corpus/fetch.sh ~/pdf-public                # refresh: only new/changed rows are fetched
    tools/stress/public-corpus/fetch.sh --verify-only ~/pdf-public  # prove it intact, no network needed

`~/pdf-public` is chmod'd read-only (`dr-xr-sr-x` dirs, `r--r--r--` files) the way
`~/pdf-test` is, so a battery run cannot damage it by construction. `--verify-only` never
writes to the destination (#470: it used to try to refresh the `WHERE-THESE-CAME-FROM.txt`
marker even in this mode, which failed — harmlessly, since the shell does not run with
`-e`, but noisily — against a read-only directory).

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
| govinfo bulk | `www.govinfo.gov`, `api.govinfo.gov` | **403 CONNECT** | no — out of scope for now, see below |
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
| `www.govinfo.gov` | **200** (a specific Federal Register PDF); **302** on `/robots.txt` |

All three #434 names are reachable from a real machine. `govinfo` and `safedocs` are **not**
implemented here even though `govinfo` is reachable — #434 asks for a *bulk* generator for
it and nobody has designed or verified one yet; that stays a future extension, not a silent
scope-creep of this one. This change adds exactly the one category #434 and the current
milestone call "the highest-value" and "what a cloud sandbox could not reach": real federal
fillable forms, from IRS and USCIS.

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

**#471 part 1 (2026-09-28, kdocker3)** used the already-reachable `www.govinfo.gov` (see
the table above — reachable from kdocker3 since the #434 federal-forms extension, just not
yet used for anything) to add the `govinfo-signed` source: no new network finding, the same
host and the same `urllib`-vs-`curl` non-issue (plain `curl` was not refused by
`www.govinfo.gov`, unlike `www.uscis.gov`). This is **not** the bulk `govinfo` generator
#434 item 4 still asks for (a much larger, unimplemented scope covering the whole
Federal Register/CFR archive for large-file coverage) — it is a small, curated,
already-verified set for a different, narrower purpose, and deliberately kept under its own
source key (`govinfo-signed`, not `govinfo`) so the two do not collide when the bulk
generator is eventually written.

## What is in it

1,662 documents, 407.4 MB. Categories are assigned from each file's own bytes by
`build-manifest.py`'s `classify()`, not from the directory it arrived in, so a row says what
the battery will actually meet — except `signed` (below), whose category is forced rather
than derived, because the whole point of that row is the signature.

| category | count | what it is |
|---|---:|---|
| `form` | 549 | `/AcroForm` — fields, checkboxes, radio groups, signature fields (see below: `/Widget` alone is not required any more) |
| `tagged` | 580 | a `/StructTreeRoot`, for the tagged-PDF path (#358) — includes the 280 non-Latin-script rows below: Wikipedia's own PDF export tags its output |
| `report` | 250 | ordinary text documents, for extraction fidelity and reading order |
| `malformed` | 150 | deliberately broken or deliberately non-conforming — crash/hang resistance only |
| `scan` | 100 | image pages with no font resources |
| `signed` | 33 | already carries a valid digital signature — for #471 part 1's measurement of what `megapdf_save()`'s full rewrite does to it (see below) |

By source: veraPDF 860, qpdf 181, PDFium 122, **IRS 136**, **USCIS 50**, **govinfo-signed 33**,
**non-Latin wiki 280**.

## Non-Latin scripts (#471 part 2)

`core/megapdf_structure.cpp`'s `BuildWords`, `BuildLines` and the XY-cut reading order
assume left-to-right, horizontal text (#444, #471). Before this addition the corpus had
essentially no Arabic, Hebrew, Han, Devanagari or Thai text to measure that assumption
against. 280 rows were added, 40 real articles from each of seven Wikipedia language
editions, fetched from that wiki's own REST `page/pdf` export endpoint:

| `source` | script | count |
|---|---|---:|
| `wiki-ar` | Arabic | 40 |
| `wiki-he` | Hebrew | 40 |
| `wiki-zh` | Han (Chinese) | 40 |
| `wiki-ja` | Japanese (horizontal) | 40 |
| `wiki-ko` | Hangul (Korean) | 40 |
| `wiki-hi` | Devanagari (Hindi) | 40 |
| `wiki-th` | Thai | 40 |

Titles were chosen by MediaWiki's own `list=random` (mainspace only, so an unbiased sample
of real body text, not hand-picked) and pinned in `nonlatin_wiki_titles.py` so a rebuild
fetches the same 280 articles rather than a fresh random sample each time (#455 asks that
adding documents not reshuffle what is already measured). Regenerate or extend with:

    tools/stress/public-corpus/build-manifest.py --add-source nonlatin-wiki

**A reproducibility limitation worth stating plainly, found while re-verifying this batch
(2026-09-28, kdocker3): Wikipedia's PDF export is rendered on demand, and is not always
byte-stable even minutes apart.** Two sequential fetches of the same article URL, seconds
apart, matched each other; a `fetch.sh` run later the same day, fetching the same 280 URLs
concurrently, produced different bytes for roughly a third of them (`MISMATCH-ON-FETCH`, a
hard failure per `fetch.sh`'s own design). This is a real difference from every other
source in this manifest: the git-pinned sources are true forever, and even the federal
forms (pinned to "today's bytes", not a commit) are stable within a day. A `wiki-*`
mismatch is disclosed here as an expected property of the source, not corruption or a
network fault — re-run `--add-source nonlatin-wiki` to refresh a row's hash from whatever
the wiki serves now, the same remedy a federal-forms revision gets.

**Licence.** Wikipedia article text is dual-licensed CC BY-SA 4.0 / GFDL (confirmed against
`Wikipedia:Reusing Wikipedia content`, 2026-09-28). CC BY-SA requires attribution (a
hyperlink or URL to the article, or a list of authors), a licence notice, and — for a
further-modified copy — an indication that changes were made; recorded per row as licence
`CC-BY-SA-4.0`. Each row's own URL doubles as its attribution link (replace
`/api/rest_v1/page/pdf/` with `/wiki/` for the human-readable article). See
`NONLATIN_ATTRIBUTION` in `build-manifest.py` for the full notice text.

**Vertical Japanese is not in this table, and not in manifest.tsv at all.** #444 found its
bug on a *vertical*-writing CMap, and none of the seven sources above are vertical — a
plain Wikipedia PDF export is always horizontal. `gen-ja-vertical.py` generates a bonus,
best-effort sample instead: 18 ja.wikipedia.org article extracts (same licence as `wiki-ja`
above), laid out with a genuine `tb-rl` paragraph direction via LibreOffice, so the
resulting PDF carries real downward glyph-advance geometry. It is a **recipe, not a
manifest row**, because neither the wiki-render step nor the local LibreOffice-render step
is byte-stable enough to pin a sha256 to (see `gen-ja-vertical.py`'s own docstring for the
full reasoning, including the one real caveat: the embedded font is a simple TrueType
subset, not a composite Type0/Identity-V CMap font). Run it yourself with:

    tools/stress/public-corpus/gen-ja-vertical.py /tmp/ja-vertical

The battery results this bonus sample produced are in the #471 part 2 PR and its filed
issues, not repeated here, because the bytes that produced them cannot be reproduced from
this repository alone.
`form` is the one category taken **whole** — every one the sources contain — because it is
what MegaPDF is for and the category #434 calls the private corpus thinnest on. The others
are sampled on an even stride across the sorted tree, which is reproducible and does not
hand back every test for one specification clause and nothing after it.

### Stable under addition, not just deterministic (#455)

Deterministic (rebuild twice, get the same bytes) is not the same property as **stable under
addition** (add documents, keep the same existing selection). #453 found the gap the hard
way: adding 186 real federal forms didn't touch the `tagged`/`malformed`/`scan`/`report`
categories' *content* directly, but it changed the total pool each category's even stride was
computed over, so the stride landed on a different subset — including two qpdf fixtures that
happened to over-count tokens against PDFium, dragging the aggregate fidelity gate down for a
reason that had nothing to do with the new forms. A gate number is not evidence if you cannot
tell whether it moved because the engine changed or because the corpus reshuffled.

`build-manifest.py` now pins the sample (`pin_selection()`): every rebuild reads whatever
`--out` already has selected for a category and keeps every one of those rows that is still
in the freshly-collected pool, unconditionally — a document's seat is never taken by a stride
recomputed over a larger or smaller pool. Only the quota room left over after pinning (if any)
is handed to the even-stride `spread()`, and only over candidates that were never previously
selected, so `spread()` can never reshuffle an existing pick. `form` (quota `None`) already
took every match unconditionally, so it needed no change — it was never the reshuffling
category. Chosen over the other two options #455 listed (sampling per source, or hash-based
selection `sha256(path) mod N < k`) because it builds on what the manifest already is: an
explicit, committed list of rows, so "the sample" has a literal, auditable home rather than
being implicit in a formula that a reviewer has to re-derive to trust.

A rebuild that would lose an existing row — a source checkout changed, or `classify()` now
files a document under a different category — refuses outright rather than dropping or
silently re-filing it:

    refusing to rebuild manifest.tsv: 1 row(s) already selected there are missing from this
    rebuild's pool -- a source checkout changed, or classify() now files them elsewhere.
    #455: a removal must be deliberate and visible, never silent. If this is expected,
    re-run with --allow-removed <path> for each (repeatable):
      verapdf/ISO 32000-1/veraPDF test suite 6-8-3-3-t01-fail-a.pdf  (was tagged)

`--allow-removed <path>` (repeatable) is how a maintainer makes that removal deliberate; the
manifest diff then shows exactly one row disappearing for a reason a PR description can state,
never a silent reshuffle buried in a hundred other changed rows.

Each rebuild's stderr also prints the manifest's own **revision**: the sha256 of the written
`manifest.tsv`, e.g. `revision sha256:1e7a27ca...`. TESTING.md's public-corpus baselines are
recorded against this hash, not just a date, so a gate number always names the exact manifest
it was measured against.

**What happens when a brand-new source shows up — a source the pin was written before —
stated explicitly, because it is the question that matters most.** Two shapes exist, and
they are handled differently on purpose:

* **A new opt-in HTTP source** (the `irs`/`uscis`/`govinfo-signed`/`nonlatin-wiki` shape,
  added via `--add-source` and merged by `merge_and_report()`) never goes through
  `CATEGORIES`/`spread()`/`pin_selection()` at all — it is excluded from the pinning
  machinery by construction (see `main()`'s `opt_in_source_keys`) and carried over
  unconditionally, the same as the federal-forms rows always were. Its rows are simply
  **appended**: they compete with nothing, so they cannot reshuffle anything and nothing
  can crowd them out. This is proven live in TESTING.md's fifth run: pin against a manifest
  from *before* `nonlatin-wiki` existed, run `--add-source nonlatin-wiki` for real, and the
  1,382 previously-selected rows come back **byte-identical**, with exactly 280 new rows
  added.
* **A new git-cloned source** (the `verapdf`/`qpdf`/`pdfium` shape, added to `SOURCES`) *does*
  go through the normal pipeline: its files are `classify()`d into ordinary categories and
  become `pool_by_path` candidates like any other source's. `pin_selection()` treats "a
  brand-new source's files" exactly the same as "an existing source's newly-added files" —
  there is no special case, because there does not need to be one: every previously-selected
  row from *any* source is pinned regardless of which source it came from, and a new source's
  rows are `spread()`-selected into whatever quota room is left (none, today, for `tagged`/
  `malformed`/`scan`/`report`, all four already at capacity; unlimited for `form`). A future
  git source that should guarantee itself real representation needs either its own quota
  bump (a visible, deliberate `QUOTAS` edit) or, like `govinfo-signed`, a forced `category`
  that sidesteps competition entirely — not a change to `pin_selection()` itself.

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

## The signed category (#471 part 1) — and what it found

33 documents from `www.govinfo.gov` (the U.S. Government Publishing Office), every one
already carrying a valid digital signature: 11 Federal Register daily issues (2019-2024),
7 Public Laws, 6 Congressional Record issues, 6 Code of Federal Regulations title/volumes,
and 3 Statutes at Large excerpts. GPO signs essentially everything it publishes —
Signature Field Name `USGPOSignature`, Signer CN `Government Publishing Office` or
`U.S. Government Publishing Office` — so this is a real, independently-verifiable sample,
not a synthetic fixture: **33/33 verified `Signature is Valid` with poppler's `pdfsig`**
(kdocker3, 2026-09-28) before any MegaPDF code touched them. Same licence as IRS/USCIS —
`US-PD-17-USC-105`, a federal government work, no copyright, no attribution required —
recorded per row in `build-manifest.py`'s `GOVINFO_SIGNED_DOCS`.

**Why this category exists.** MegaPDF saves by full rewrite: `megapdf_save()` calls
PDFium's `FPDF_SaveAsCopy`, which re-serialises the entire document rather than patching
it in place. #471 part 1 asked whether that silently invalidates a signature already on a
document a user opens, edits and saves — the same silent-failure shape as the XFA wall
(#456): a document that still *looks* signed and is not.

**The measurement, run against all 33 (kdocker3, 2026-09-28, `pdfsig` as the independent
verifier — never MegaPDF's own code):**

| stage | result |
|---|---|
| before any MegaPDF processing | 33/33 `Signature is Valid` |
| after `megapdf_save()` with **no edit calls at all** (open, save immediately) | 33/33 `Digest Mismatch` |
| after `megapdf_save()` following one edit (see note) | 33/33 `Digest Mismatch` |

None of the 33 has a fillable checkbox field to click — GPO's publications carry only the
`USGPOSignature` field, no interactive AcroForm beyond it — so the "edit" row used
`megapdf_page_rotate(doc, 0, 0)`, a rotation by zero degrees: a real edit call, visually a
no-op. The result is identical either way, which is itself the finding: **the signature is
invalidated by the act of saving, regardless of whether anything changed.** Inspecting one
resave with `pdfsig` in detail shows why: the `/ByteRange` array is carried into the
rewritten file with its original numeric bounds unchanged, but the file's own length
changes (13,257,131 → 13,276,302 bytes for `FR-2024-01-02.pdf`, +19,171 bytes with no
content edit), so those bounds no longer describe the whole file — `pdfsig` reports "Not
total document signed" where the original said "Total document signed", on top of the
digest mismatch.

Filed as **#476** (behaviour only; `core/` was not touched, per the task brief — a gate
corpus documents what is true, it does not get to also decide what should be done about
it).

The measurement harness (`sig_harness`, ad hoc, not part of the product or this repository)
opened each document with `megapdf_open_file`, optionally called `megapdf_form_click` on
the first `MEGAPDF_FIELD_CHECKBOX` found via `megapdf_form_fields_load` (falling back to
the identity rotate when none exists, as above), then called `megapdf_save` and wrote the
result to a temporary file for `pdfsig` to check — the same open/edit/save shape the apps
use, with signature verification done entirely outside MegaPDF's own code.

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
| GPO-signed documents (`www.govinfo.gov/content/pkg/`) | **public domain — 17 U.S.C. § 105** | no |
| Wikipedia (`ar/he/zh/ja/ko/hi/th.wikipedia.org`) | **CC BY-SA 4.0** (dual GFDL) | **yes** — see below |

CC BY 4.0 requires attribution wherever these files or results derived from them are
published. The required credit:

> veraPDF test corpus © the veraPDF Consortium, used under
> [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). Not modified.

**Federal works: 17 U.S.C. § 105.** "Copyright protection under this title is not available
for any work of the United States Government." An IRS or USCIS form, or a GPO publication,
is prepared by federal employees as part of their official duties, so no copyright ever
attaches to it in the first place — there is no licence to comply with, and no attribution
requirement, though the manifest records `source` (`irs` / `uscis` / `govinfo-signed`) and a
plain-English attribution string in `build-manifest.py` anyway, as a provenance trail rather
than a legal obligation. This is a **stronger** position than any of the git sources above:
those are copyrighted works licensed to permit redistribution; federal works are simply
never copyrighted at all.

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

`build-manifest.py` regenerates the **git-sourced** part of the manifest by re-collecting
every source and re-deriving each file's category, so that part of the corpus is a function
of its sources rather than a pile someone once assembled — but it does not re-*sample* from
scratch (see "Stable under addition" above): whatever `--out` already has selected stays
selected, and only a category's unfilled quota room is drawn from new candidates.

    tools/stress/public-corpus/build-manifest.py --work /var/tmp/pc-build

Each git source is pinned to a commit, never a branch — a branch would silently invalidate
every sha256 in the file. To add one: give it a licence that permits redistribution, add a
`Source` row, pin the commit, and rebuild. **A plain rebuild never touches the network for
the federal-forms rows and never deletes them either** — it preserves whatever `irs`/`uscis`/
`govinfo-signed` rows are already in `--out`, so it stays exactly as reproducible from a
blocked sandbox as it always was. If a rebuild would drop an existing row (a pinned commit's
tree lost a file that a previous manifest selected, or `classify()`'s logic changed and now
files it under a different category), it refuses and names the row; pass
`--allow-removed <path>` once you have confirmed why.

To (re)fetch the federal-sourced rows, from a machine that can reach the host — `www.irs.gov`,
`www.uscis.gov` and `www.govinfo.gov` are all unreachable from Anthropic's cloud sandbox,
reachable from an ordinary machine (see "Network reality" above):

    tools/stress/public-corpus/build-manifest.py --add-source irs
    tools/stress/public-corpus/build-manifest.py --add-source uscis
    tools/stress/public-corpus/build-manifest.py --add-source govinfo-signed

Each merges its rows into the existing `manifest.tsv` (by URL: a matching sha256 is left
alone, a changed one is refreshed, a new one is added) rather than rebuilding everything
from nothing. `IRS_FORMS` / `USCIS_FORMS` / `GOVINFO_SIGNED_DOCS` in `build-manifest.py` are
the exact, grouped lists; add an entry there (verified as `%PDF`-starting and fetchable
first, and — for `GOVINFO_SIGNED_DOCS` specifically — verified as genuinely signed with
`pdfsig` before it goes in) to extend any of the three, or add a new `HttpSource` to
`FEDERAL_SOURCES` for a different agency under the same public-domain licence. Give an
`HttpSource` a `category=` only when its category should be forced rather than derived from
`classify()`, as `govinfo-signed` does — leave it unset (as `irs`/`uscis` do) to classify
normally. `--add-source govinfo` and `--add-source safedocs` still refuse outright — see
`SOURCES_BLOCKED` — because nobody has yet written and verified a generator for either (the
*bulk* `govinfo` generator #434 item 4 asks for is a different, larger scope than
`govinfo-signed`'s curated set, and is why the two are separate keys); that refusal is
deliberate, the same way `irs`/`uscis`/`govinfo-signed` used to refuse before each was
implemented, and should be resolved the same way: implement and verify it from a machine
that can reach the host, don't just delete the refusal.

## Files

| file | what it is |
|---|---|
| `manifest.tsv` | the corpus: one row per document. Generated — never edit by hand |
| `fetch.sh` | reproduces the documents and proves every byte |
| `build-manifest.py` | how the manifest was generated, so it can be regenerated and extended |
| `nonlatin_wiki_titles.py` | the pinned per-language article titles for the `wiki-*` sources (#471 part 2) |
| `gen-ja-vertical.py` | bonus vertical-Japanese recipe, NOT part of manifest.tsv — see "Non-Latin scripts" above |
