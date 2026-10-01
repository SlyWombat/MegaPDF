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

As of 2026-10-01 (#609) it holds **1,494 of the manifest's 1,777 rows, 4.3 GB** — the UK
forms and the seven very large documents were staged on Dave's decision, 112 of the 115
arrived, and three `uk-hmrc` rows are #625. The remaining 280 are the `wiki-*` non-Latin
rows, deferred by decision.

## Saying what is staged, and what is deliberately not (#609)

A staged corpus is almost always a *subset* of the manifest, and for ten recorded battery
runs that subset was reported under the corpus's own name: every summary said "public
1,381" and nothing said what it was 1,381 of. `tools/stress/corpus_coverage.sh` closes
that — the four batteries call it at the end of their summaries, it reconciles the
battery's own count against this manifest, and it names the gap by `source`. It needs no
flag: the `WHERE-THESE-CAME-FROM.txt` marker `fetch.sh` leaves behind is what identifies a
corpus as this one, because the failure being fixed is that nobody remembered to ask.

It separates three numbers, because the gaps between them are different problems: manifest
rows (what the corpus is defined to be), rows present on disk (a **staging** gap), and rows
the battery's own `find -name '*.pdf'` reaches (a **tooling** gap — this is how the `.Pdf`
case-mismatched qpdf fixture, footnoted in run after run as "assumed / not verified",
finally became a counted number).

**A deliberate absence belongs on the record, not in the gaps.** Put a
`EXCLUDED-FROM-THIS-CORPUS.tsv` in the corpus directory, one `<source>` TAB `<reason>` line
per source that is meant to be missing:

    # one <manifest source> TAB <reason> per line; '#' comments
    wiki-ar	non-Latin (Arabic): deferred by Dave decision, 2026-10-01 (#609)
    wiki-he	non-Latin (Hebrew): deferred by Dave decision, 2026-10-01 (#609)

Those rows are then reported as a recorded decision, with the reason, in every battery
summary; everything else absent is reported as "NOT accounted for", by source. Nothing
about coverage gates a battery — a partial corpus is a fact about the machine, not a
regression in the code under test, and a battery that refused to run on a partial corpus
would simply stop being run, which is how a reporting problem turns into a testing one.

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

All #434/#471 names are reachable from a real machine. `safedocs` and the *bulk* `govinfo`
generator #434 item 4 asks for are both still **not** implemented — nobody has designed or
verified a generator for either. #471 instead added two narrow, curated generators against
`www.govinfo.gov`, each kept under its own source key rather than `govinfo` so neither
collides with the bulk generator whenever it is eventually written: `govinfo-signed` (33
already-signed GPO documents, part 1) and `govinfo-large` (7 large Federal Register issues,
part 4 — see "Very large documents" below). #434 added the highest-value category the
original corpus and its milestone called out — real federal fillable forms, from IRS and
USCIS; #471 adds a second jurisdiction's forms (UK, OGL-licensed) and the corpus's first
large-document and genuinely-signed-document populations.

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

1,777 documents, ~4.40 GB (dominated by the seven `large` documents at ~4.25 GB; everything
else is ~456 MB). Categories are assigned from each file's own bytes by
`build-manifest.py`'s `classify()`, not from the directory it arrived in, so a row says what
the battery will actually meet — except `signed` and `large` (both below), whose category is
forced rather than derived: for `signed`, because the whole point of that row is the
signature; for `large`, because the row is chosen to be large before it is ever fetched (see
"Very large documents" below — a byte-size check in `classify()` would agree regardless).

| category | count | what it is |
|---|---:|---|
| `form` | 605 | `/AcroForm` — fields, checkboxes, radio groups, signature fields (see below: `/Widget` alone is not required any more) |
| `tagged` | 618 | a `/StructTreeRoot`, for the tagged-PDF path (#358) — includes the 280 non-Latin-script rows below: Wikipedia's own PDF export tags its output |
| `report` | 264 | ordinary text documents, for extraction fidelity and reading order |
| `malformed` | 150 | deliberately broken or deliberately non-conforming — crash/hang resistance only |
| `scan` | 100 | image pages with no font resources |
| `signed` | 33 | already carries a valid digital signature — for #471 part 1's measurement of what `megapdf_save()`'s full rewrite does to it |
| `large` | 7 | 48.4 MB - 2.07 GB — the paging-in/performance path (#471 part 4, see below) |

By source: veraPDF 860, qpdf 181, PDFium 122, **IRS 136**, **USCIS 50**, **govinfo-signed 33**,
**non-Latin wiki 280**, **HMRC 62**, **Home Office 22**, **DWP 24**, **govinfo-large 7**.

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

**A reproducibility limitation found while re-verifying this batch (2026-09-28, kdocker3),
and fixed by #525 below: Wikipedia's PDF export is rendered on demand, and is not always
byte-stable even minutes apart.** Two sequential fetches of the same article URL, seconds
apart, matched each other; a `fetch.sh` run later the same day, fetching the same 280 URLs
concurrently, produced different bytes for roughly a third of them (`MISMATCH-ON-FETCH`, a
hard failure per `fetch.sh`'s own design). This is a real difference from every other
source in this manifest: the git-pinned sources are true forever, and even the federal
forms (pinned to "today's bytes", not a commit) are stable within a day. **This was fixed,
not worked around** — see "Non-Latin scripts: hosted, not fetched live" immediately below.

**Licence.** Wikipedia article text is dual-licensed CC BY-SA 4.0 / GFDL (confirmed against
`Wikipedia:Reusing Wikipedia content`, 2026-09-28). CC BY-SA requires attribution (a
hyperlink or URL to the article, or a list of authors), a licence notice, and — for a
further-modified copy — an indication that changes were made; recorded per row as licence
`CC-BY-SA-4.0`. The human-readable article behind each hosted file (replace
`/api/rest_v1/page/pdf/` with `/wiki/` in its original fetch URL) is now recorded in
`nonlatin-wiki-attribution.tsv` rather than in the manifest's own `url` column — see
"Non-Latin scripts: hosted, not fetched live" for why. See `NONLATIN_ATTRIBUTION` in
`build-manifest.py` for the full notice text.

### Non-Latin scripts: hosted, not fetched live (#525)

The limitation stated above — Wikipedia's PDF export re-rendering, so a pinned hash for a
`wiki-*` row stops verifying without the article itself changing — is a hole in what #455
set out to guarantee: a row's identity is its URL plus a hash, and for a re-rendering source
the hash cannot be relied on, so the row could not be refetched on a clean machine at all.
**Dave's decision, 2026-09-30: fetch each of the 280 rows once and host the bytes
ourselves**, on a GitHub release attached to this repository, fetched the exact way
`tools/fetch-pdfium-linux.sh` already fetches MegaPDF's own patched PDFium build. Every
`wiki-*` row's `url` column is now a `github.com/.../releases/download/...` URL pointing at
that mirror, not `*.wikipedia.org`; its `sha256` is pinned to the exact bytes mirrored, which
never re-render, so the row is reproducible again the same way every other source in this
manifest already was. `fetch.sh` needed **no changes** for this — a `wiki-*` row is now an
ordinary url+sha256 row like any other, fetched and verified the same way.

**A consequence worth stating, because it is better than the problem it solved.** Only
`raw.githubusercontent.com`-style GitHub asset URLs clear the Anthropic cloud sandbox's
egress proxy; `irs.gov`, `uscis.gov`, `assets.publishing.service.gov.uk`, `govinfo.gov` and
`*.wikipedia.org` all refuse it outright (see "Network reality" above, and TESTING.md's
"Which corpus gates what"). Mirroring these 280 rows to this repository's own storage makes
them reachable from that sandbox for the first time — 280 of the 614 opt-in rows that used
to be unreachable from there, leaving 334 (`irs`/`uscis`/`uk-*`/`govinfo-*`) still behind
hosts the sandbox cannot reach.

**What this does and does not license.** Wikipedia article text is CC BY-SA 4.0 (dual
GFDL), which permits redistribution provided attribution and share-alike travel with the
copy — see "Licence" above and "Licences and attribution" below. A Wikipedia PDF export
already carries its own licence and contributor statement inside the document, which is
normally how that obligation is met; `nonlatin-wiki-attribution.tsv` (written by
`mirror-nonlatin-wiki.py`, see "Files" below) additionally records, per hosted file, the
exact article it came from and that article's own CC BY-SA 4.0 licence, so the attribution
link survives even though the manifest's `url` column no longer points at Wikipedia itself.
**This is not a licence to host anything else.** It applies to this one source because its
licence explicitly permits redistribution. The Canadian Crown-copyright forms (#456) and the
UN documents (investigated and declined above, "UN parallel-language documents") are local
validation only and are **never** redistributed — Crown copyright and the UN's own terms of
use do not grant third parties that permission, and nothing about hosting the Wikipedia
mirror changes that. Do not read this section as precedent for either.

**These are test fixtures, not a product release.** The GitHub release holding the mirror is
marked a pre-release, named and described as a corpus mirror for MegaPDF's own test
tooling (#434/#525), and carries no app binary — the same way the PDFium prebuilt releases
this mechanism was borrowed from are not MegaPDF product releases either.

**Mirrored once, not kept in sync.** `mirror-nonlatin-wiki.py` is a one-time migration: once
a `wiki-*` row's `url` points at the mirror, a plain `build-manifest.py --add-source
nonlatin-wiki` refuses rather than silently fetching live Wikipedia again and doubling the
category (see that refusal's own message, and the comment above it in `build-manifest.py`,
for the exact reasoning). Extending the non-Latin sample with new titles or languages after
this point means adding them to `nonlatin_wiki_titles.py`, running `mirror-nonlatin-wiki.py`
again (it only touches rows still pointed at live Wikipedia, so already-mirrored rows are
left alone), and uploading the new assets to the release the same way.

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

* **A new opt-in HTTP source** (the `irs`/`uscis`/`govinfo-signed`/`uk-*`/`govinfo-large`/
  `nonlatin-wiki` shape, added via `--add-source` and merged by `merge_and_report()`) never
  goes through `CATEGORIES`/`spread()`/`pin_selection()` at all — it is excluded from the
  pinning machinery by construction (`main()` filters on `OPT_IN_SOURCE_VALUES`, the full
  `NON_GIT_SOURCES` ∪ `wiki-*` set) and carried over unconditionally, the same as the
  federal-forms rows always were. Its rows are simply **appended**: they compete for no
  quota, so they cannot reshuffle anything and nothing can crowd them out. That exclusion
  must be the *whole* opt-in set: scoped to `FEDERAL_SOURCES` alone it silently mis-reads
  all 148 `uk-*`/`govinfo-*` rows as pinned rows that vanished, and every plain rebuild
  refuses until each is named to `--allow-removed`.
* **A new git-cloned source** (the `verapdf`/`qpdf`/`pdfium` shape, added to `SOURCES`) *does*
  go through the normal pipeline: its files are `classify()`d into ordinary categories and
  become `pool_by_path` candidates like any other source's. `pin_selection()` treats "a
  brand-new source's files" exactly the same as "an existing source's newly-added files" —
  there is no special case, because there does not need to be one: every previously-selected
  row from *any* source is pinned regardless of which source it came from, and a new source's
  rows are `spread()`-selected into whatever quota room is left (none, today, for `tagged`/
  `malformed`/`scan`/`report`, all four already at capacity; unlimited for `form` and
  `large`). A future git source that should guarantee itself real representation needs either
  its own quota bump (a visible, deliberate `QUOTAS` edit) or, like `govinfo-signed`, a forced
  `category` that sidesteps competition entirely — not a change to `pin_selection()` itself.

  Measured, on the 1,777-row manifest, by adding a fourth git source carrying four documents
  (two that `classify()` calls `form`, two it calls `report`) and rebuilding:

  | | rows lost | rows added | net |
  |---|---:|---:|---:|
  | unpinned (the behaviour #455 is about) | **189** | **191** | +2 |
  | pinned (this design) | **0** | **2** | +2 |

  Both land on the same net `+2`. Unpinned, that `+2` hides 380 changed rows — 189
  previously-measured documents swapped out for 191 others, none of it visible in a row
  count. Pinned, the diff is literally two added lines and nothing else: the two new `form`
  documents are appended (that category has no quota), the two new `report` documents are
  declined (that category is at 250/250), and not one of the 1,777 existing rows moves.

**Lowering a `QUOTAS` entry keeps every pinned row — and now says so (#500).** The pin is
one-directional by design: `pin_selection()` never evicts. So editing a quota *down* below
what a category already holds changes nothing — `tagged` at 300 rows with its quota edited to
100 rebuilds to 300 — and until #500 it changed nothing *silently*, with no message and no
diff to tell a maintainer the edit had not taken. The asymmetry stays, because eviction on a
quota edit is precisely the silent reshuffling this whole section exists to prevent; what was
missing was the feedback. A rebuild whose pinned count for a category exceeds that category's
quota now prints one line on stderr:

    note: tagged keeps 300 pinned row(s), over QUOTAS['tagged'] = 100 -- #455 never evicts a
    pinned row on a quota edit alone, so lowering a quota changes nothing by itself;
    shrinking a category means retiring rows the deliberate, visible way, with
    --allow-removed <path> (#508: this retires PATH even while its source still carries it)

It is a note, not a refusal: the rebuild proceeds and keeps all the pinned rows. Raising a
quota needs no note (the extra room is filled from new candidates only, and no existing pick
moves), and a quota left alone, a quota met exactly, an unlimited category (`form`, `large`,
quota `None`) and a first build with nothing pinned yet all print nothing new. Shrinking a
category for real is the `--allow-removed` route above — deliberate and visible, one named
path at a time.

**`--allow-removed` also retires a row the sources still carry (#508).** It used to take
effect only on a row that had already left the rebuild's pool — naming a still-carried path
did nothing at all, not even the "not actually missing" note, because that note lived inside
the same `if` block as the refusal it is paired with, and that block only ever ran when
something had, in fact, gone missing. So there was no supported way to shrink a category on
purpose: lowering a quota is a no-op (#500 above), `--allow-removed` was inert while the
source still carried the file, and `manifest.tsv`'s own first line says never to edit it by
hand. Now naming a path to `--allow-removed` evicts it from the selection regardless of
whether its source still carries it, and the room that frees is handed to `spread()` like any
other vacancy — so a category at quota can genuinely shrink, or swap one specific row for
another the pool already had room to add. This is the cheapest of the options #508 weighed:
naming a path explicitly on the command line is already as deliberate and visible as #455
asks a removal to be, so the row's own manifest diff line is the record of the decision. A
rebuild reports what it retired this way:

    note: --allow-removed retired 1 row(s) still carried by their source (#508):
      verapdf/ISO 32000-1/veraPDF test suite 6-8-3-3-t01-fail-a.pdf  (tagged)

A path named to `--allow-removed` is not remembered between runs — it is a per-invocation
acknowledgement, the same as the "row already left the pool" case above, not a permanent
blacklist. Leave it off a later rebuild and a retired document is just an ordinary pool
candidate again, eligible to be `spread()`-selected back in if there is room.

## UN parallel-language documents — investigated, not added (#471)

#471's non-Latin-script sample above is 40 *unrelated* Wikipedia articles per script, so a
fidelity gap between two scripts (#483: Arabic/Han/Thai fail the gate; #484: Arabic/
Devanagari reading order disagrees with the structure tree) could be the script or could be
the content. The UN publishes the same document in all six official languages (Arabic,
Chinese, English, French, Russian, Spanish), which would hold the content fixed and vary
only the script — exactly the instrument those two findings need. Investigated 2026-09-28;
**no documents were fetched and no `un-*` rows exist in `manifest.tsv`**, because the
licence check that #471 asks to run *before anything else* did not clear.

**Two primary sources conflict, and neither wins outright.**

1. `www.un.org/en/about-us/terms-of-use` — the terms that govern using any un.org-family
   site, including `documents.un.org`, which is what actually serves the PDF bytes:

   > The United Nations grants permission to Users to visit the Site and to download and
   > copy the information, documents and materials … from the Site for the User's
   > personal, non-commercial use, without any right to resell or redistribute them or to
   > compile or create derivative works therefrom

   `www.un.org/en/about-us/copyright` reinforces this ("Copyright © United Nations. All
   rights reserved."), and points to `shop.un.org/rights-permissions`, which confirms there
   is no blanket exception for research, testing, or non-resale redistribution — anything
   beyond narrow excerpt limits requires prior written permission. Read alone, this rules
   MegaPDF out immediately: MegaPDF is a commercial product, and a public, redistributable
   manifest is not "personal" use by construction — the same reasoning that kept Canadian
   Crown-copyright forms local-only (#456, #434's "Do NOT use" list).

2. `ST/AI/189/Add.9/Rev.2` (17 September 1987), the UN Secretariat's own administrative
   instruction on copyright practice — fetched and read directly (see "How the PDF URLs
   were found" below). Paragraph 2(b) lists "United Nations documents: written material
   officially issued under a United Nations document symbol" among the categories the UN
   "will not seek copyright" for; paragraph 7: "The general rule for Official Records,
   United Nations documents and public information material is that these publications
   will be in the public domain." A resolution or Secretary-General report (symbol
   `A/RES/…`, `A/74/…`, etc.) is exactly this. Read alone, this would clear the
   commercial-use bar the same way 17 U.S.C. § 105 does for the IRS/USCIS/govinfo-signed
   rows above, and it is the basis Wikimedia Commons currently cites for its
   `{{PD-UN-doc}}` licence tag (still in active, non-deprecated use — some evidence it is
   still treated as operative). But it is a 39-year-old internal staff instruction whose
   own text frames itself as "experimental … until the end of 1989", and nothing reachable
   from here confirms how the Organization reconciles it with (1) for material served
   *today*: `digitallibrary.un.org`'s per-record rights metadata returned only an empty,
   bot-challenge response (HTTP 202, zero-byte body) to both a plain fetch and to `curl`
   with a browser user agent.

Per #471: "If the terms are unclear or restrict commercial use, stop and report rather than
adding the rows; a corpus whose licence column is guesswork is worth less than a smaller
certain one." A live, specific, currently-displayed restriction against an old,
unconfirmed-as-still-controlling permission is exactly that kind of unclear — unlike
IRS/USCIS/govinfo-signed (17 U.S.C. § 105, no conflicting source found) or Wikipedia
(CC BY-SA 4.0, no conflicting source found), both of which are clean. **Outcome: stop.**
`build-manifest.py --add-source un-parallel` refuses with this reasoning
(`SOURCES_BLOCKED_LICENCE`) rather than silently doing nothing — the same shape as
`SOURCES_BLOCKED` for `govinfo`/`safedocs`, except the refusal is licence, not
reachability or a missing generator.

**How the PDF URLs were found, recorded for whoever eventually gets a written answer.**
`docs.un.org` and `undocs.org` serve a JS-*looking* symbol-select landing page, but it is
plain, un-rendered HTML that 302-redirects per language to a page whose `<iframe src=…>`
is already the real, static, no-JS-needed URL:

    https://documents.un.org/api/symbol/access?s=<SYMBOL>&l=<ar|zh|en|fr|ru|es>&t=pdf

which itself 302s to a stable-looking direct path (e.g.
`https://documents.un.org/doc/undoc/gen/ns0/000/81/img/ns000081.pdf` for
`ST/AI/189/Add.9/Rev.2`'s English copy) — verified 2026-09-28 with plain `curl`, no
Playwright needed for *this* part of the pipeline after all. Playwright would still be the
right tool for the part not attempted here: discovering which ~10 document symbols exist
in all six languages (`docs.un.org`'s own search UI is genuinely JS-driven), and confirming
each direct URL's stability across repeated fetches the way #455/`fetch.sh` requires before
anything is pinned by sha256.

**If this is picked up again:** get a written answer from `permissions@un.org`, or from the
Secretary of the Publications Board per `ST/AI/189/Add.9/Rev.2` paragraph 20, confirming
that (a) documents bearing a UN document symbol remain in the public domain for material
served today, and (b) a commercial company redistributing them (even indirectly, via a
public URL+sha256 manifest rather than rehosting bytes) is within that permission. Only
then resolve `un-parallel` in `SOURCES_BLOCKED_LICENCE` the way `irs`/`uscis`/
`govinfo-signed` were resolved out of `SOURCES_BLOCKED`.
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

**Battery results.** All three batteries were run against the `large` category separately
(see TESTING.md, "Fourth run", for the full table): 0 crashes and 0 hangs across all three,
on all 7 documents. `structure-battery`'s aggregate token-fidelity F1 (0.989, both through
the internal API and through `megapdf-cli`) misses the corpus-wide 0.998 gate — these are
largely pre-1995 OCR'd scans, and a scan-heavy population measuring differently from the
mostly-born-digital rest of the corpus is exactly the kind of population-specific movement
#471 expects rather than treats as a regression. Filed as #488; not fixed or gate-adjusted
here.

**Battery timeouts and this category: sufficient for every document tested, with real
margin.** `structure-battery.sh` and `markdown-battery.sh` default `TIMEOUT` to 120s per
document; `pages-battery.sh` to 300s. #442/#445 fixed how a battery *classifies* a timeout
(bounded, counted separately, never silently stalling the whole run) but did not give the
`large` category its own, longer value. Measured directly: **not one operation, across all
three batteries and all 7 documents up to 2.07 GB, hit its timeout** — `pages-battery.sh`
completed all 28 rotate/delete/move/extract operations (981s total, none individually
timed out); `structure-battery.sh`'s `pdftotext --reference` call finished the 2.07 GB
document inside 120s (the same call #442 found could hang indefinitely on a 2.5 KB
adversarial fixture); `markdown-battery.sh` took 45s total for all 7. **No change requested
for these sizes** — raised in the PR only as a note that this is 7 documents topping out at
2.07 GB, and a meaningfully larger document or a slower host could still reach the existing
limits, worth re-checking rather than assumed permanently settled if the category grows.
Not changed here either way: `tools/stress/*-battery.sh` is out of bounds for this change
the same way it was out of bounds for #442/#445 (see those issues).

**Re-confirmed 2026-10-01 (#609), this time inside a full-corpus run rather than against the
category alone:** all 7 documents opened, zero refusals of any kind, zero timeouts on any
battery, and `structure-battery`'s aggregate for the category measured **0.989955** — #488's
own figure to six decimal places, with the fix deliberately not attempted. The category's
5.33 M tokens do move the *public aggregate* (0.974441 → 0.980019) simply by joining it;
that is arithmetic, not a change in anything, and TESTING.md's eleventh run shows the
restriction back to the previous population reproducing 0.974441 exactly.

**Fetching them is the hard part, not running them (#612).** `fetch.sh` used to abort both
multi-gigabyte rows at around 875 MB every time, because it bounded every transfer with
`--max-time 300` — a fixed wall clock on a fetch whose rows span five orders of magnitude of
size. It now aborts on sustained low speed instead (under 1 KB/s for two minutes, which
catches a dead connection at any size) and resumes rather than restarts, keeping the partial
in place between runs under a `*.part` name. Budget about **25 minutes and 4.2 GB** for the
category on an ordinary link, and if a run is interrupted just run the same command again:
it picks up where it stopped and the manifest sha256 is still checked before anything is
promoted into the corpus. `fetch-resume-test.sh` covers the transfer's behaviour against a
local server built to misbehave; it is not in CI because it spends two minutes in sleeps.

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
| Wikipedia (mirrored from `ar/he/zh/ja/ko/hi/th.wikipedia.org` — #525, see below) | **CC BY-SA 4.0** (dual GFDL) | **yes** — see below |
| UK HMRC/Home Office/DWP forms (`assets.publishing.service.gov.uk`) | **Open Government Licence v3.0** | **yes** — see below |
| govinfo.gov Federal Register volumes (`www.govinfo.gov`) | **public domain — 17 U.S.C. § 105** | no |

CC BY 4.0 requires attribution wherever these files or results derived from them are
published. The required credit:

> veraPDF test corpus © the veraPDF Consortium, used under
> [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). Not modified.

**Federal works: 17 U.S.C. § 105.** "Copyright protection under this title is not available
for any work of the United States Government." An IRS or USCIS form, a GPO publication
(signed or not), or a govinfo.gov Federal Register volume, is prepared by federal
employees/agencies as part of their official duties, so no copyright ever attaches to it in
the first place — there is no licence to comply with, and no attribution requirement, though
the manifest records `source` (`irs` / `uscis` / `govinfo-signed` / `govinfo-large`) and a
plain-English attribution string in `build-manifest.py` anyway, as a provenance trail rather
than a legal obligation. This is a **stronger** position than any of the git sources above:
those are copyrighted works licensed to permit redistribution; federal works are simply
never copyrighted at all. (govinfo.gov's own policies page notes one caveat, checked and not
applicable here: a government publication can incorporate copyrighted third-party material
used with permission; the Federal Register issues fetched for #471 part 4 are the agency's
own regulatory text, not a reprint of someone else's work.)

**Wikipedia, CC BY-SA 4.0: attribution and share-alike travel with the mirrored copy** the
same way they would with a live fetch — hosting the bytes ourselves (#525, "Non-Latin
scripts: hosted, not fetched live" above) changes *where* the file comes from, not what its
licence requires. The required credit, per `NONLATIN_ATTRIBUTION` in `build-manifest.py`:

> Wikipedia contributors — article text is CC BY-SA 4.0
> (https://creativecommons.org/licenses/by-sa/4.0/), dual-licensed GFDL; see
> `nonlatin-wiki-attribution.tsv` for each file's source article. PDF rendering is the
> wiki's own REST export, unmodified further here.

Each hosted file also carries Wikipedia's own licence and contributor statement inside the
document itself (the usual way this requirement is met for a wiki PDF export), and
`nonlatin-wiki-attribution.tsv` records the specific article URL behind every row, since the
manifest's own `url` column now names the mirror rather than Wikipedia. **This licence
covers Wikipedia content alone** — see "Non-Latin scripts: hosted, not fetched live" above
for why it is not a precedent for hosting the Canadian or UN sources discussed elsewhere in
this file.

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

`build-manifest.py` regenerates the **git-sourced** part of the manifest by re-collecting
every source and re-deriving each file's category, so that part of the corpus is a function
of its sources rather than a pile someone once assembled — but it does not re-*sample* from
scratch (see "Stable under addition" above): whatever `--out` already has selected stays
selected, and only a category's unfilled quota room is drawn from new candidates.

    tools/stress/public-corpus/build-manifest.py --work /var/tmp/pc-build

Each git source is pinned to a commit, never a branch — a branch would silently invalidate
every sha256 in the file. To add one: give it a licence that permits redistribution, add a
`Source` row, pin the commit, and rebuild. **A plain rebuild never touches the network for
any direct-URL source and never deletes their rows either** — it preserves whatever
`irs`/`uscis`/`govinfo-signed`/`uk-hmrc`/`uk-homeoffice`/`uk-dwp`/`govinfo-large` rows are
already in `--out` (see `NON_GIT_SOURCES` in `build-manifest.py`), so it stays exactly as
reproducible from a blocked sandbox as it always was. If a rebuild would drop an existing row
(a pinned commit's tree lost a file a previous manifest selected, or `classify()`'s logic
changed and now files it under a different category), it refuses and names the row; pass
`--allow-removed <path>` once you have confirmed why (#455).

To (re)fetch a direct-URL source, from a machine that can reach its host — none of these are
reachable from Anthropic's cloud sandbox, all reachable from an ordinary machine (see
"Network reality" above):

    tools/stress/public-corpus/build-manifest.py --add-source irs
    tools/stress/public-corpus/build-manifest.py --add-source uscis
    tools/stress/public-corpus/build-manifest.py --add-source govinfo-signed
    tools/stress/public-corpus/build-manifest.py --add-source uk-hmrc
    tools/stress/public-corpus/build-manifest.py --add-source uk-homeoffice
    tools/stress/public-corpus/build-manifest.py --add-source uk-dwp
    tools/stress/public-corpus/build-manifest.py --add-source govinfo-large

`nonlatin-wiki` is **not** in this list on purpose, even though it is a real, verified
`--add-source` generator (see "Non-Latin scripts" above): since #525 it only runs against a
manifest that still has live-Wikipedia `wiki-*` rows left to mirror, and refuses outright
once they are all hosted — see "Non-Latin scripts: hosted, not fetched live" for why, and
`mirror-nonlatin-wiki.py` for the actual extension path.

Each merges its rows into the existing `manifest.tsv` (by URL: a matching sha256 is left
alone, a changed one is refreshed, a new one is added) rather than rebuilding everything
from nothing. `IRS_FORMS` / `USCIS_FORMS` / `GOVINFO_SIGNED_DOCS` / `HMRC_FORMS` /
`HOME_OFFICE_FORMS` / `DWP_FORMS` / `GOVINFO_DOCS` in `build-manifest.py` are the exact,
grouped lists; add an entry to one (verified fetchable and `%PDF`-starting first, and — for
`GOVINFO_SIGNED_DOCS` specifically — verified as genuinely signed with `pdfsig` before it
goes in) to extend it, or add a new `HttpSource` to `FEDERAL_SOURCES` (shared `base_url` +
filename; give it a `category=` only when its category should be forced rather than derived
from `classify()`, as `govinfo-signed` does — leave it unset, as `irs`/`uscis` do, to
classify normally) or `DirectSource` to `DIRECT_SOURCES` (each item its own full URL — what
UK forms and govinfo need, since neither shares one prefix) for a different agency or host
under a licence that permits redistribution. `--add-source safedocs` still refuses
outright — see `SOURCES_BLOCKED` — because nobody has yet written and verified a generator
for it; that refusal is deliberate, the same way every source above used to refuse before its
own generator was written, and should be resolved the same way: implement and verify it from
a machine that can reach the host, don't just delete the refusal.

**`govinfo-signed` and `govinfo-large`'s scope, stated precisely.** Neither is the *bulk*
generator #434 item 4 originally named (`--add-source govinfo` itself still refuses — see
`SOURCES_BLOCKED`). `govinfo-signed` is a hand-picked set of 33 already-signed GPO documents
(`GOVINFO_SIGNED_DOCS`) for the `signed` category (#471 part 1). `govinfo-large` is a
hand-picked list of 7 large Federal Register issues (`GOVINFO_DOCS`) for the `large` category
(#471 part 4) — CFR annual title volumes, also named in #434's original note, turned out to be
structurally incapable of being large (checked by HTTP HEAD across the biggest titles: every
one tops out around 4-9 MB) and are not fetched at all. A bulk generator over govinfo's full
holdings remains unbuilt and would be a separate, future extension, under its own `govinfo`
key once someone writes it.

## Files

| file | what it is |
|---|---|
| `manifest.tsv` | the corpus: one row per document. Generated — never edit by hand |
| `fetch.sh` | reproduces the documents and proves every byte |
| `build-manifest.py` | how the manifest was generated, so it can be regenerated and extended |
| `nonlatin_wiki_titles.py` | the pinned per-language article titles for the `wiki-*` sources (#471 part 2) |
| `mirror-nonlatin-wiki.py` | one-time migration that fetches the pinned wiki titles and points their manifest rows at MegaPDF's own release storage instead of live Wikipedia (#525) — see "Non-Latin scripts: hosted, not fetched live" |
| `nonlatin-wiki-attribution.tsv` | CC BY-SA 4.0 attribution record for the hosted `wiki-*` files: which article each one came from, since the manifest's own `url` column no longer says (#525) |
| `gen-ja-vertical.py` | bonus vertical-Japanese recipe, NOT part of manifest.tsv — see "Non-Latin scripts" above |
