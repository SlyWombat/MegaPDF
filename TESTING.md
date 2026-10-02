# Testing MegaPDF

Thanks for testing! MegaPDF is a deliberately simple PDF editor: it does four things
— edit text, check boxes, place signatures, save — and tries to do them with zero
learning curve. If you ever feel lost or surprised, that itself is a bug worth reporting.

## Requirements

- Windows 11 (or Windows 10 1809+), x64 or ARM64 PC.
- Nothing else — the package is self-contained.

## Install (one time, ~1 minute)

Get **Mega PDF** from the Microsoft Store:
https://apps.microsoft.com/detail/9PF4TRRH4M76 — the Store installs it, signs it,
and keeps it up to date. It appears in the **Start menu**.

If you installed an earlier test build from a zip (`Setup.exe`), **uninstall that
first** — it is a different package and cannot update itself into the Store version.

To uninstall completely: **Settings → Apps → Mega PDF → Uninstall**.
Note: uninstalling removes your signature library — signatures are stored locally,
per Windows user, and never leave your computer.

## What to test

Open any PDF (Open button, drag-and-drop onto the window, or right-click a PDF →
Open with → MegaPDF).

**Tabs** — open a second PDF any of those ways and it opens as a tab beside the first,
in the same window: the Open dialog takes several files at once, and a double-click in
File Explorer while MegaPDF is running lands in the window you already have rather
than starting another copy — as a new tab, or on the tab that already shows that file.
Each tab keeps its own undo history, find, zoom and unsaved dot; the toolbar acts on
the tab you are looking at. **Ctrl+W** closes a tab (**Close tab** and **New window**
are in the More menu; **Ctrl+Shift+N** opens another window). Close the window with
unsaved changes in two tabs and you should be asked about each; answer Cancel on the
second and the first must still be there, edits and all. Kill the app with three tabs
open and the next launch should offer to restore each of them, into its own tab.

**Save As → Markdown** — Save As offers **Markdown document** beside PDF document.
Pick it and you get the document's text as a `.md` file: headings, paragraphs, lists,
and the values you filled in. It is an export, not a save: the busy strip says
*Exporting…*, the confirmation says *Exported*, the PDF is untouched, and if the
document had unsaved changes the dot stays and closing still asks. A scanned page has no
text to give and is written as a one-line note. Open the `.md` in any text editor and
check it reads like the document.

**Text** — click any text and type. Enter or clicking elsewhere applies; Esc cancels.
Clearing all the text deletes it. Ctrl+Z / Ctrl+Y undo and redo everything.
If you see a note about fonts, that's expected on some documents — check the result
still looks reasonable.

**Checkboxes** — click a checkbox to check/uncheck it. This works both on real form
checkboxes and on plain drawn squares. The mark style (✗ / ✓ / ■) is in Settings (⚙).

**Form fields** — click a fillable field and type.

**Signatures** — the ✒ Signatures button: add one by typing, drawing, or importing a
photo/scan (white background is removed automatically). Then click the signature,
click the page to place it. Click a placed signature to select it: drag to move,
drag the round corner handle to resize, ✕ or Delete key to remove.

What this places is an *electronic signature* — a picture of a name. It is **not**
a *digital signature*, the cryptographic kind backed by a certificate, and nothing
MegaPDF says around this feature may imply otherwise (#602). If a string you meet
while testing suggests that placing a signature made the document verifiable,
tamper-evident, certified, legally binding or secure, that is a bug worth filing.
**A document that already has a digital signature** — open one of the genuinely
signed PDFs — `signed-approval.pdf` and `signed-certified.pdf`, written by
`python3 tools/gen_signature_fixtures.py <outdir>` — and press
Ctrl+S. MegaPDF must warn **at that point**, not on open, and must say *digital
signature* rather than just *signature*: *"This document has a digital signature"*,
or *"This document is certified against changes"* when the signature certifies the
document. **Save a copy** is the prominent choice and leaves the original untouched;
**Overwrite the original** is the deliberate, secondary one. After Save a copy, a
quiet line says the original's digital signature doesn't carry over. Nothing is ever
refused, and nothing here is about the signatures you place yourself (#481, #602).

**Whiteout** — the Whiteout button, then drag across anything (text, images, even a
scan) to cover it with white. Click a whiteout to select it; ✕ or Delete removes it.
Whiteout **covers**; it does not remove. What is underneath is still in the file, and
another app can copy or search it. That is what Redact is for, and the tooltip says so.

**Redact** — the Redact button, beside Whiteout, then drag across what you want *gone*,
or drag across text to mark the words. The marked areas show translucent with an outline,
so you can still read what you are about to remove. Click a mark to select it: drag it to
move it, drag a corner to resize it (a mark is an area, so a corner moves its edges on its
own rather than keeping a shape), ✕ or Delete removes it, and Ctrl+Z undoes it. Marking,
moving and removing leave the document clean — no unsaved dot, because a mark is not in
the file. **Clear all marks** in the More menu drops every mark on every page at once, and
one Ctrl+Z puts them all back. **Nothing is removed until you save.**

Ctrl+S or Save As then asks, and offers **Save as a copy** ("…-redacted.pdf") as the
default — redaction cannot be undone once saved. Afterwards a short summary says what
went: "1 area redacted: 13 characters".

The thing worth testing is the promise. Open the saved copy **in Edge or Acrobat**, press
Ctrl+F and search for a word you redacted: it should not be there. Select the black box
and copy: nothing should come out. Try it on a scan too — the pixels inside the box are
overwritten in the picture itself, not covered.

Sometimes it will refuse, say *"Nothing was removed"*, and tell you why. That is
deliberate: if MegaPDF cannot take some of what you marked apart safely, it removes
nothing rather than leaving some of it behind. A refusal leaves your marks in place and
your file untouched — if you hit one, the document is worth reporting.

**Add text** — the Add text button, then click anywhere (including on top of a
whiteout), type, press Enter. The new text behaves like any other text afterwards:
click it to edit, clear it to delete.

**Find** — press **Ctrl+F** and a small find bar appears at the top right. Type and it
searches the whole document as you go (capitals don't matter): every match on the page
lights up, the current one stands out from the rest, and the box next to the field reads
"3 of 17" — or "No results". **Enter** goes to the next match and **Shift+Enter** the
previous (while the find box still has focus; the arrow buttons always work), and both
wrap around from the end of the document back to the beginning. **Esc**
(or the ✕) closes the bar and clears the highlights. Try it on something long, and try a
phrase that runs across two lines.

**About** — the ⚙ Settings flyout ends with an About section: the version number, a link
to the source on GitHub, and **Third-party notices** — the licences of the open-source
pieces MegaPDF is built on. Check the notices dialog opens and scrolls.

**Print** — the Print button (or Ctrl+P) opens the normal Windows print dialog with a
preview; pick a printer and print. What you see, including your edits, is what prints.

**Shrink for email** — the Shrink button saves a *smaller copy* of the current file
(pictures reduced to email quality; the original is never touched). Try it on a
photo-heavy or scanned PDF and check the size difference — and that the copy still
looks fine.

**Saving** — Ctrl+S overwrites; Save As makes a copy. Close with unsaved changes and
you should always be asked. After saving, open the file in Edge or Adobe to confirm
your edits look right there too.

**Sturdiness** — kill MegaPDF from Task Manager mid-editing; on next launch it should
offer to restore your unsaved changes. Zoom with the toolbar − / + or Ctrl+mouse-wheel.
Scroll through a long document; pages should appear as you reach them.

## Known limitations (not bugs)

- Text in scanned/photographed PDFs can't be edited (there's no OCR — by design), and
  Ctrl+F can't find it either: to the app, a scan is a picture, not words.
- Find is plain text only — no wildcards, no "match case" or "whole word" options, and
  no replace. On a very long document the count appears when the whole sweep finishes,
  not progressively.
- Editing text sometimes substitutes a similar font, with a notice — expected when
  the document doesn't embed a complete font.
- Password-protected PDFs show an error instead of a password prompt.
- No page add/remove/reorder, no merging — out of scope for 1.0.
- **Redact refuses rather than half-finishes.** Some documents draw part of a page from a
  shared block MegaPDF cannot take apart safely; marking inside one gets "Nothing was
  removed" and an explanation. Removing nothing is the deliberate choice: a file that
  looks redacted and is not would be worse than no feature.
- **Redact removes whole letters.** Drag across half a word and the whole letter goes:
  you cannot remove half a glyph, and leaving half behind would leave it recoverable. On
  text set at an angle this reaches a little further than the box you drew.
- A document that does not allow changes cannot be redacted — the button is disabled,
  same as Whiteout and Add text.

## Linux

The Linux app ships as a `.deb`, a signed APT repository and a tarball —
https://electricrv.ca/megapdf/linux/ has the three ways in — and building it
yourself is in the README. Everything above applies once it is open, tabs
included: a second `megapdf file.pdf`, or a file manager's Open With while the
app is running, lands in the running window as a tab, and Ctrl+Page Up /
Ctrl+Page Down move between tabs. `megapdf-cli extract file.pdf` is on your
`PATH` beside it (below). Two differences worth knowing:

- **Print** opens MegaPDF's own small dialog — which printer, how many copies —
  rather than the system print panel, and then hands the job to CUPS. It needs
  `cups-client` installed; without it the status line says so.
- **Signatures you type** are drawn in whatever script face the machine has.
  `fonts-urw-base35` supplies one on most desktops; with none, they come out in
  the body font in italic, which is legible and does not look like a signature.

The app's own diagnostics answer most "is this my machine or the app" questions,
and none of them needs a document:

```
megapdf --desktop-check     # session, fonts, file dialogs, where saves are staged
megapdf --language-check    # which language it would run in, and why
megapdf --print-check       # the CUPS route
megapdf --self-test <dir>   # fill, check, sign, save, reopen — no window needed
```

Two known differences from Windows and the Mac, both filed: a save of a very
large document fails if `/tmp` is a tmpfs, as it is on Fedora
([#193](https://github.com/SlyWombat/MegaPDF/issues/193)), and the app has no
third-party notices file yet
([#194](https://github.com/SlyWombat/MegaPDF/issues/194)).

## Releasing (for contributors, not testers)

The manual pass above is also the release gate: before a build goes to any store it is
installed and clicked through on a real machine per platform, because CI cannot click and
every 2.1.1 show-stopper was found that way after CI was green. The order, the gates and
the commands are in [`docs/RELEASING.md`](docs/RELEASING.md).

## Automated coverage (for contributors, not testers)

**What is covered, and what is not: `docs/qa/test-matrix.md`.** Every feature against every
platform, with each pairing saying whether a test in CI drives that platform's own path, only
the shared engine is tested, nothing is, or the feature is not there — and the gap list first,
because that is the part worth reading. It is generated from `tests/matrix/coverage.toml`, and
the `qa-matrix` workflow resolves every claim in it against the tree on every push: a named
entry point or test that stops existing fails CI rather than quietly going stale here. It
answers "is anything looking at this?", never "does this work", and it does not stand in for
the by-hand pass in `docs/RELEASING.md` §2.3 (#330).

Search is covered by engine-level tests on all three platforms, so a parity break shows
up in CI rather than in your hands: `tests/MegaPDF.Core.Tests/PdfiumEngineTests.cs`
(five tests — case-insensitive matching with a plausible rect, every occurrence in
reading order, a match spanning text-object boundaries, no-match, empty term),
`ios/MegaPDFTests/SearchTests.swift`, and
`android/engine/src/androidTest/java/com/megapdf/engine/TextSearchTest.kt` — the last two
run against the same shared fixture PDFs. The About screen's notices formatting is
covered by `android/app/src/test/java/com/megapdf/android/NoticeParagraphsTest.kt`.
None of this replaces the manual pass above: nothing automated checks how the find bar
*feels*.

Redaction (#173) is checked by hunting for what it removed rather than by asking the
engine whether it removed it. `tools/leakcheck` searches a saved file for the removed
strings in PDFium's text extraction, in every stream `qpdf --qdf --decode-level=all`
writes out, in the raw bytes as ASCII, UTF-16 and PDF hex digits, in `/Info`, XMP, the
outline, every annotation and every link address, and in the pixels inside the areas. The
core test suite runs those searches over the fixtures
`tools/gen_redaction_fixtures.py` writes, the Mac app's `--self-test` runs the whole flow
through the view model the window binds to, and
`android/engine/src/androidTest/java/com/megapdf/engine/RedactionTest.kt` runs it on a
device. `tools/stress/redaction-battery.sh` runs the same searches across a whole corpus,
where the requirement is 0 leaks, 0 crashes, 0 hangs and no render change outside the
areas.

Large files (#147, #148, #267, #270) are checked in two tiers, because the defects only
show at sizes no repository can hold. `test_xref_entries_are_findable()` runs on every push:
it saves the ordinary fixtures in both of PDFium's cross-reference forms and insists every
in-use entry is a non-negative offset that lands on the `N 0 obj` it names, and that the
cross-reference stream is a conforming object — `/Type /XRef`, closed with `endobj`, an
entry for itself, and a `/Length` that is the length of the stream. `test_large_file_xref()`
and `test_large_xref_stream_opens_from_its_table()` make the same demands of files past 2
and 4 GiB, and run only where those files exist:

```bash
python3 tools/gen_large_fixtures.py ~/large --only huge-2_5gb,huge-4_5gb   # ~4 min, 7.5 GB
python3 tools/make_xref_stream.py ~/large/huge-4_5gb.pdf ~/large/huge-4_5gb-xrefstream.pdf
MEGAPDF_LARGE_FIXTURES=~/large MEGAPDF_LARGE_SCRATCH=/scratch ./megapdf_core_tests ...
```

Each case skips itself if its fixture is not there, so the 2.68 GB file alone is a useful run.

Both skip with a printed line when `MEGAPDF_LARGE_FIXTURES` is unset, and each case skips
itself when the scratch directory has less room than the copy it is about to write — a full
save is the size of its source and an incremental one is twice that. The saved copies are
deleted unless `MEGAPDF_LARGE_KEEP` is set. They are *not* in CI: generating a 4.5 GB
fixture and writing a 9.7 GB copy is not something to do on every push.

Document structure (#142, #353) — headings, paragraphs, lists, fields, furniture, reading
order over columns — is `megapdf_structure_load()`'s own contract, exercised by golden block
dumps (`core/tests/expected/structure/*.blocks`) over the fixtures
`tools/gen_structure_fixtures.py` builds (`columns.pdf`, `furniture.pdf`, `lists.pdf`,
`headings.pdf`, `xobject-text.pdf`, `scan.pdf`, `mixed.pdf`) plus the existing `demo`/`forms`/
`formtext`/`userunit` fixtures and the #98 schematic, and validated against the corpus by
`tools/stress/structure-battery.sh` (`megapdf_structure_check` driving contract 9 the way
`tools/leakcheck` drives redaction) — see that script's own header comment for the four
measures and their gates.

The tagged path (#358) reads a page's structure tree instead of inferring, when the tree
passes the trust rule (at least 90% of the page's characters inside a known-typed element or
an `/Artifact`, and no marked content referenced twice; otherwise the page falls back to the
heuristics with its confidence capped at 80). `tagged.pdf` (two pages: H1/H2, a two-MCID
paragraph, a nested list, a `/RoleMap`-mapped heading, a `/THead` table, a `/Figure` with
`/Alt`, a `/Link`, and a paragraph drawn above its heading but tagged after it) must come out
with `source = TAGGED` on both pages, its table as a pipe table in Markdown and the tree's
order; `tagged-wrong.pdf` (the same page with a tree covering 58% of it, scrambled) must fall
back. Both are dumped with and without `MEGAPDF_STRUCTURE_HEURISTIC_ONLY` (`--heuristic`),
`test_structure_tagged()` asserts every kind directly, and `test_structure_tagged_mutations()`
runs 300 same-length byte mutations of the tree's own syntax through the default path under
the Linux leg's ASan. `tools/stress/trust_threshold.py <battery-check.log>` bins the battery's
per-tagged-page coverage and each order's agreement with `pdftotext` to justify (or move) the
90% threshold; the battery summary reports how many tagged pages the tree served and how many
the rule rejected.

`tiny-font-size.pdf` (#382) pins the body size at the visible caption's 12 pt when most of a
page's characters report a near-zero `Tf` (an invisible OCR layer scaled through `Tm`), the
shape that used to round it to 0 and make every line a heading; `structure_check bodysizediag`
is the corpus measurement behind the 1 pt floor.

### Which corpus gates what (#434)

There are two corpora, and they answer different questions.

| | private | public |
|---|---|---|
| where | `GPD-DAVE`, `k2`, `k3` only | anywhere — CI, a cloud sandbox, a laptop |
| what | 4,337 of the owner's real documents | 1,497 fetched from a committed manifest |
| how | already on disk | `tools/stress/public-corpus/fetch.sh` |
| population | real producers, real typography, valid files | conformance fixtures, engine test suites, deliberately broken files, **186 real IRS/USCIS forms, 33 genuinely GPO-signed documents, 108 real UK OGL forms, and 7 very large documents** |
| depth | the deeper battery: real-world shapes nothing synthetic reproduces | the reproducible one: anyone can run it and get the same documents |

Neither replaces the other. The private corpus stays the deeper gate and stays local; the
public corpus is what makes the gates runnable by someone who is not the owner.

**They are different populations and they give different numbers. That is the point, not a
problem.** A public-corpus figure is never a substitute for the private one in a release
decision, and a gate must never be retuned to make the public corpus go green — doing that
would quietly weaken the gate for the corpus it was actually calibrated on (#354, #363).
Record the two separately.

    tools/stress/public-corpus/fetch.sh                  # → ~/megapdf-public-corpus, ~2 min
    bash tools/stress/pages-battery.sh    <cli> ~/megapdf-public-corpus <out> --jobs 3
    tools/stress/markdown-battery.sh      <cli> $(command -v cmark) ~/megapdf-public-corpus <out>
    tools/stress/structure-battery.sh     <structure_check> ~/megapdf-public-corpus <out> --reference --cli <cli>

**#525, 2026-09-30: the 280 `wiki-*` rows are now reachable from the Anthropic cloud sandbox,
and reproducible again.** Two separate problems, both fixed the same way. Wikipedia's PDF
export re-renders on demand, so those rows' pinned hashes used to drift without the article
changing (first found 2026-09-28, re-verifying this batch) — and, independently of that,
`*.wikipedia.org` is one of the hosts the sandbox's egress proxy refuses outright (with
`irs.gov`, `uscis.gov`, `assets.publishing.service.gov.uk` and `govinfo.gov` — see the Sixth
run below). Both are fixed by mirroring the 280 documents once to a GitHub release on this
repository, fetched the way `tools/fetch-pdfium-linux.sh` fetches PDFium (see
`tools/stress/public-corpus/README.md`, "Non-Latin scripts: hosted, not fetched live"). A
cloud-sandbox run after this change reaches 1,443 of 1,777 rows — the 1,163 git-sourced rows
the Sixth run measured, plus these 280 — leaving 334 (`irs`/`uscis`/`uk-*`/`govinfo-signed`/
`govinfo-large`) still behind hosts the sandbox cannot reach. The Sixth run's own 1,163
figure is left as written below: it is what that run, on that manifest revision, actually
measured, not a number to retrofit.

### Corpus staging on k3 (#470)

k3 (kdocker3) is where batteries run repeatedly, so all three corpora are staged there
permanently instead of being fetched or copied per run:

| corpus | path on k3 | size | refreshed by |
|---|---|---:|---|
| private | `~/pdf-test` | 4,337 documents | never re-fetched — it is the owner's own machine copy |
| private (Canadian forms) | `~/pdf-test-ca` | 135 documents | same |
| private (UN) | `~/pdf-test-un` | 90 documents | same |
| public | `~/pdf-public` | **1,494 documents, 4.3 GB** (#609, 2026-10-01) | `tools/stress/public-corpus/fetch.sh ~/pdf-public` |

Point any battery at `~/pdf-public` exactly as at `~/megapdf-public-corpus` above — it is
the same corpus, just not re-downloaded:

    tools/stress/public-corpus/fetch.sh ~/pdf-public                  # after a manifest change: skips what it already has
    tools/stress/public-corpus/fetch.sh --verify-only ~/pdf-public    # proves it byte-for-byte, no network at all
    tools/stress/structure-battery.sh <structure_check> ~/pdf-public <out> --reference --cli <cli>

`~/pdf-public` is chmod'd read-only the same way `~/pdf-test` is (`dr-xr-sr-x` directories,
`r--r--r--` files), so an ordinary battery run cannot write into it, move a file, or repair
a mismatch by accident — the same protection the private corpus has had all along.

**What is staged there, and what is not (#609).** Until 2026-10-01 this mount held 1,381 of
the manifest's 1,777 rows and every battery summary reported the figure as "the public
corpus" with nothing saying what it was 1,381 *of*. It now holds **1,494 rows**: the 108 UK
OGL forms and the 7 very large `govinfo-large` documents were staged on Dave's decision
(#609) — 112 of the 115 landed. The remaining 283 rows are **stated exclusions**, not a
silent hole: the **280 `wiki-*` non-Latin rows are excluded by decision**, and **3
`uk-hmrc` rows are excluded because HMRC has made them permanently unfetchable** (#625 —
P53_0622.pdf and P55_2025.pdf were retired for an online-only claim service, and VAT2.pdf's
asset URL now redirects to guidance instead of serving a PDF; re-pinning the manifest to
"whatever HMRC serves now" isn't available because nothing at those URLs is a PDF to pin
to). Both decisions are recorded the same way, in `EXCLUDED-FROM-THIS-CORPUS.tsv` inside the
corpus directory (`~/pdf-public` on k3), where `tools/stress/corpus_coverage.sh` reads it, so
every battery summary prints each exclusion by name instead of leaving a silent hole — see
`tools/stress/public-corpus/README.md` for the exact lines. One further row is on disk but
outside every number any battery prints: the qpdf fixture whose extension is `.Pdf`, which
`find -name '*.pdf'` does not match — long footnoted as "assumed / not verified", now
counted and named in the coverage block. Staging those 115 documents costs about 25 minutes
and 4.2 GB, nearly all of it the two multi-gigabyte rows, so it is a thing to do once per
machine and leave alone.

**The corpus arithmetic (#625), all 1,777 manifest rows:** 1,493 reachable (staged and
matched by a battery's own `*.pdf` walk) + 1 staged-but-unreached (the `.Pdf` case-mismatch
fixture) + 280 stated exclusion (`wiki-*`, non-Latin, deferred by decision) + 3 stated
exclusion (`uk-hmrc`, #625, permanently unfetchable) + 0 unexplained = 1,777.

**Scratch is disposable; corpora persist.** Every agent brief says "leave the machine as
you found it" and "clean up after yourself" — that means your own build directories,
containers and pulled images, named for your issue (`~/megapdf-<n>*`, `megapdf-<n>*`
containers/images). It does **not** mean deleting `~/pdf-test`, `~/pdf-test-ca` or
`~/pdf-public`: those are shared, permanent fixtures the next agent would otherwise have to
rebuild or re-download, and #470 exists precisely because three agents re-downloaded the
public corpus on 2026-09-27 rather than leaving it staged. If a task's brief does not name
one of these three paths as scratch, it is not scratch.

First full run, 2026-09-27 (1,036 documents visited, 1,533 pages — see the #434 PR for the
per-category breakdown). **Superseded by the run below**, kept for history:

| measure | public corpus | gate | |
|---|---|---|---|
| structure: aggregate token F1 | 0.998590 | >= 0.998 | pass |
| structure: F1 through `megapdf-cli` | 0.998915 | >= 0.998 | pass |
| structure: order agreement tau (median) | 1.000 | >= 0.9 | pass |
| structure: crashes / hangs | 0 / 0 | 0 / 0 | pass |
| structure: CLI bad exit codes | 2 | 0 | **fail** — #443 |
| markdown: cmark parse failures | 0 | 0 | pass |
| markdown: crashes / hangs | 0 / 0 | 0 / 0 | pass |
| markdown: bad exit codes | 2 | 0 | **fail** — #443 |
| pages: crashes / hangs | 0 / 0 | 0 / 0 | pass |
| pages: qpdf failures, count mismatches, other refusals | 6 / 3 / 5 | 0 / 0 / 0 | **fail** — #445, all harness or population |

That run's own text noted what it was missing: real US federal fillable forms. #434 calls
those the highest-value category, and `irs.gov`, `uscis.gov` and `govinfo.gov` were all
unreachable from the cloud sandbox that first built the corpus. Its 237 `form` documents were
synthetic single-feature fixtures, exercising field syntax but not the deep `/Parent`
hierarchies a real IRS form carries — "a green run here is not evidence that real government
forms work."

### Second run, 2026-09-27: 136 real IRS forms + 50 real USCIS forms added

**Its own gate numbers are superseded by the third run below**, which measured the identical
manifest (byte-identical, #455) against a moved main — kept here for the corpus population
history (the federal-forms extension) and because the fidelity-gate failure it recorded is
exactly what #453 and #455 are about.

`irs.gov` and `uscis.gov` are reachable from an ordinary machine (kdocker3) even though they
are not reachable from Anthropic's cloud sandbox — see
`tools/stress/public-corpus/README.md`, "Network reality", for the measured statuses and the
curl-vs-urllib TLS-fingerprint wrinkle this uncovered. The corpus is now **1,349 documents,
140.2 MB**; `classify()` also had a real bug fixed alongside this (README.md and
`build-manifest.py` have the detail — raw-byte scanning missed `/Widget` inside a compressed
object stream, which every modern government PDF uses), which is why the `form` category grew
beyond just the new federal rows.

    tools/stress/public-corpus/build-manifest.py --add-source irs      # from a machine that can reach it
    tools/stress/public-corpus/build-manifest.py --add-source uscis
    tools/stress/public-corpus/fetch.sh                                # → ~/megapdf-public-corpus
    bash tools/stress/pages-battery.sh    <cli> ~/megapdf-public-corpus <out> --jobs 3
    tools/stress/markdown-battery.sh      <cli> $(command -v cmark) ~/megapdf-public-corpus <out>
    tools/stress/structure-battery.sh     <structure_check> ~/megapdf-public-corpus <out> --reference --cli <cli>

1,348 documents visited (1,349 minus one qpdf fixture with a case-mismatched `.Pdf`
extension that `find -name '*.pdf'` still does not match, per #445's "assumed / not
verified" note — cosmetic, unchanged by this run):

| measure | public corpus | gate | |
|---|---|---|---|
| structure: aggregate token F1 | 0.996178 | >= 0.998 | **fail** — #453, not the federal forms (below) |
| structure: F1 through `megapdf-cli` | 0.996217 | >= 0.998 | **fail** — #453, same cause |
| structure: order agreement tau (median, 1,393 tagged pages) | 0.955 | >= 0.9 | pass |
| structure: poppler agreement tau (median, informational) | 0.998 | — | — |
| structure: crashes / hangs | 0 / 0 | 0 / 0 | pass |
| structure: CLI bad exit codes | 2 | 0 | **fail** — #443, same two pdfium fixtures as before |
| structure: pages F1 < 0.9 | 23 (was 5) | informational | → #453 (18 pages), #444 (rest) |
| markdown: cmark parse failures | 0 | 0 | pass |
| markdown: crashes / hangs | 0 / 0 | 0 / 0 | pass |
| markdown: bad exit codes | 2 | 0 | **fail** — #443, same two documents |
| pages: crashes / hangs | 0 / 0 | 0 / 0 | pass |
| pages: qpdf failures, count mismatches, other refusals | 6 / 3 / 5 | 0 / 0 / 0 | **fail** — #445, all harness or population |
| pages: extract refused (field `/Parent` hierarchy) | **209** (was 72) | informational | expected (#174) — see per-category table below |

**Per-category breakdown for `form`** (structure-battery measure 1, pages-battery `extract`):

| form source | documents | structure F1 | `extract` refused-fields |
|---|---:|---:|---:|
| **irs** (real IRS forms) | 136 | **0.998613** | **122 (90%)** |
| **uscis** (real USCIS forms) | 50 | **0.999991** | 14 (28%; 35/50 already permission-`restricted` before the field check) |
| veraPDF fixtures | 213 | 0.998879 | 4 |
| pdfium fixtures | 74 | 0.998749 | 7 |
| qpdf fixtures | 76 | 0.780229 | 61 |

**The real federal forms individually clear the fidelity gate** (0.998613 and 0.999991, both
>= 0.998) — they are not why the aggregate structure gate is red. That is `qpdf`'s own
overlay/annotation-copy fixtures over-counting tokens 2-2.7x against PDFium on two documents,
filed as #453, unrelated to this extension's federal-forms content. What the real forms *do*
confirm, at a scale no synthetic fixture showed: **90% of real IRS forms refuse a page
`extract`** because their fields sit in a `/Parent` hierarchy — exactly #174's documented,
currently-correct refusal (`MEGAPDF_ERR_FIELDS`, exit 9), now measured against Form 1040
itself and most of its schedules rather than a hand-built fixture. Filed as #452 for whoever
scopes #174's extract/import field-hierarchy behavior next.

#442 (the structure battery can stall forever on `pdftotext`) reproduced during this run too:
one qpdf fixture (`shared-unnamed-field.pdf`) hung `pdftotext` for the run's duration and had
to be killed by hand (an external watchdog, not a code change) to let the battery continue.
That document's measure 3 (poppler agreement, informational) is missing as a result; every
other measure above is unaffected.

Read #445 before treating a red pages battery on the public corpus as an engine defect: on
this run, as before, every qpdf-failure/count-mismatch/other-refusal was the harness or the
population, not the engine.

### Third run, 2026-09-28: proving the k3 staging (#470)

Structure battery only, run against **`~/pdf-public` on kdocker3** rather than a fresh
`~/megapdf-public-corpus` fetch, to prove the staged copy is a drop-in replacement — same
manifest revision as the second run above (`main` at `8b09807`, manifest unchanged since
`d182085`), engine built fresh from that same commit:

    tools/stress/public-corpus/fetch.sh --verify-only ~/pdf-public   # offline: 1,349 verified, 0 missing, 0 mismatched
    tools/stress/structure-battery.sh <structure_check> ~/pdf-public <out> --reference --cli <cli>

1,348 documents visited (the same case-mismatched `.Pdf` qpdf fixture noted above is still
skipped by `find -name '*.pdf'`):

| measure | public corpus (staged) | gate | |
|---|---|---|---|
| structure: aggregate token F1 | 0.999167 | >= 0.998 | pass |
| structure: F1 through `megapdf-cli` | 0.999206 | >= 0.998 | pass |
| structure: order agreement tau (median, 1,396 tagged pages) | 0.956 | >= 0.9 | pass |
| structure: poppler agreement tau (median, informational) | 0.998 | — | — |
| structure: crashes / hangs | 0 / 0 | 0 / 0 | pass |
| structure: CLI bad exit codes | 0 | 0 | pass |

Every gate is green on the engine as of `8b09807` — #443 and #453's CLI-exit-code and
aggregate-F1 failures from the second run above are both resolved by now (unrelated to
#470; recorded here only because this run would otherwise look inconsistent with the
history above). The point of this run is narrower than the numbers: **a battery pointed at
the permanently staged `~/pdf-public` produces a normal, gate-passing report, end to end,
with zero documents fetched over the network** — the staging in "Corpus staging on k3"
above is a transparent substitute for `~/megapdf-public-corpus`, not a different corpus.

### Third addition, 2026-09-28: 33 genuinely-signed documents (#471 part 1)

Not a battery run — a targeted measurement, per the task brief, of what `megapdf_save()`'s
full rewrite does to a digital signature already on a document. 33 documents from `www.govinfo.gov`
(GPO Federal Register, Public Law, Congressional Record, CFR and Statutes at Large PDFs),
every one independently verified `Signature is Valid` with poppler's `pdfsig` before any
MegaPDF code touched it. The corpus is now **1,382 documents, 298.1 MB**; the new `signed`
category and the `govinfo-signed` source are documented in
`tools/stress/public-corpus/README.md`, "The signed category".

    tools/stress/public-corpus/build-manifest.py --add-source govinfo-signed  # from a machine that can reach www.govinfo.gov
    tools/stress/public-corpus/fetch.sh --category signed                    # → ~/megapdf-public-corpus/govinfo-signed

**Result: the digital signature does not survive a MegaPDF save, with or without an edit.**

| stage | result (33/33) |
|---|---|
| before any MegaPDF processing | `Signature is Valid` |
| after `megapdf_save()`, no edit at all | `Digest Mismatch` |
| after `megapdf_save()` following one edit | `Digest Mismatch` |

The two after-save rows are identical: saving invalidates the digital signature regardless
of whether the content changed, because `FPDF_SaveAsCopy` re-serialises the whole file and
carries the original `/ByteRange` bounds over unchanged into a file whose length is now
different. Filed as **#476** — a behaviour finding per the task brief, `core/` untouched, no
gate adjusted. See the README section for the full detail (including the specific
byte-count evidence) and the harness used.

### Fourth addition, 2026-09-28: 280 non-Latin-script documents, per-script battery (#471 part 2)

`core/megapdf_structure.cpp`'s `BuildWords`, `BuildLines` and the XY-cut reading order
assume left-to-right, horizontal text (#444). The corpus had essentially no Arabic, Hebrew,
Han, Devanagari or Thai text to measure that assumption against. 280 rows were added, 40
real Wikipedia articles per script (`wiki-ar/he/zh/ja/ko/hi/th` sources, titles pinned in
`tools/stress/public-corpus/nonlatin_wiki_titles.py`); the corpus is now **1,662 documents,
407.4 MB**. See `tools/stress/public-corpus/README.md`, "Non-Latin scripts", for sourcing,
licence and a reproducibility caveat found while verifying the batch (Wikipedia's on-demand
PDF export is not always byte-stable minutes apart).

    tools/stress/public-corpus/build-manifest.py --add-source nonlatin-wiki
    tools/stress/structure-battery.sh <structure_check> <script-dir> <out> --reference --cli <cli>

All three batteries (structure, Markdown, pages) run per script, on a per-script corpus
directory rather than the pooled corpus, so the breakdown below is exact rather than
inferred from the `source` column after the fact:

| script | docs | pages | structure F1 (internal / CLI) | pages F1 < 0.9 | order tau (tree / poppler) | crashes / hangs |
|---|---:|---:|---|---:|---|---|
| Arabic | 40 | 84 | 0.982495 / 0.985033 | 3 | 0.823 / 0.964 | 0 / 0 |
| Hebrew | 40 | 118 | 0.998457 / 1.000000 | 5 | 0.863 / 0.957 | 0 / 0 |
| Han (Chinese) | 40 | 74 | 0.984626 / 0.997121 | 1 | 0.942 / 0.988 | 0 / 0 |
| Japanese (horizontal) | 40 | 123 | 0.997126 / 0.999669 | 0 | 0.956 / 0.993 | 0 / 0 |
| Korean | 40 | 119 | 0.998822 / 1.000000 | 1 | 0.942 / 1.000 | 0 / 0 |
| Devanagari (Hindi) | 40 | 89 | 0.998433 / 0.999060 | 1 | 0.799 / 0.996 | 0 / 0 |
| Thai | 40 | 174 | 0.988943 / 0.999758 | 0 | 0.974 / 0.987 | 0 / 0 |
| Japanese (vertical, bonus — not in manifest.tsv, see README) | 18 | 39 | 0.512555 / 0.512555 | 34 | 0.134 / 0.182 | 0 / 0 |

Markdown and pages batteries: 0 crashes, 0 hangs, 0 cmark parse failures, 0 qpdf failures,
0 page-count mismatches on every script, including the vertical-Japanese bonus sample.

**As expected, and as instructed, the gate was not tuned to fit these numbers.** Three of
seven real, horizontal, non-Latin scripts (Arabic, Han, Thai) fail the corpus-wide
`>= 0.998` structure fidelity gate outright at just 40 documents each; the rest (Hebrew,
Devanagari, Korean, and — marginally, on the internal measure only — Japanese-horizontal)
sit within 0.003 of it, against the general corpus's comfortable 0.998590–0.998915. Every
script under-segments slightly relative to PDFium's own tokenisation (engine/PDFium token
ratio 0.976–0.999, worst on Han and Thai); vertical Japanese under-segments drastically
(ratio 0.72) — the same family of bug #444 found, but the mirror image of its example
(there the engine *over*-produced tokens 10:2 against PDFium; here it under-produces).
Reading-order self-agreement (tree tau) is also markedly weaker for Arabic and Devanagari
(0.80–0.86) than for the CJK/Thai group (0.94–0.97) even though token fidelity is close to
passing for both — the RTL/Indic order risk `docs/reading-mode-plan.md` §7 named but had no
population to measure. Filed as **#482** (vertical Japanese, the #444-shaped failure),
**#483** (the horizontal-script fidelity-gate shortfall) and **#484** (Arabic/Devanagari
reading-order self-disagreement); `core/megapdf_structure.cpp` was not touched, since
another session is working there for #453.

#### What #482 turned out to be: the reference is wrong, and the real defect is order (2026-09-28)

**The vertical-Japanese row above measures PDFium, not the engine.** Measure 1 scores the
engine's tokens against PDFium's own `FPDFText_GetText`, and this sample is the population
where that reference stops being trustworthy. The sample is generated by
`tools/stress/public-corpus/gen-ja-vertical.py` from ja.wikipedia extracts, so the exact text
and the exact reading order are known in advance; scoring all three extractors against that
source text instead of against each other settles which of them is wrong:

| against the generated source text (18 docs, 39 pages) | CJK chars returned | ...in source order | Latin/digit token F1 |
|---|---:|---:|---:|
| **MegaPDF (the shipped, tagged path)** | 18,704 / 18,704 | **18,704 (1.0000)** | **0.9863** |
| PDFium `FPDFText_GetText` (measure 1's reference) | 18,704 / 18,704 | 16,440 (0.8790) | **0.5177** |
| poppler `pdftotext -layout` (measure 3's reference) | 18,704 / 18,704 | 1,586 (0.0848) | 1.0000 |

PDFium's 0.5177 is measure 1's 0.51 aggregate almost to the digit: it fragments the Latin and
numeric runs that vertical Japanese sets rotated inside its columns, returning 1,560 tokens
where the source has 1,125, and it also mis-orders 12% of the CJK. The engine returns the
source's text exactly, in the source's order. It is not under-segmenting — it is the only one
of the three that gets this population right, and the measure marks it down for it.
(poppler's 0.085 is not a defect either: `-layout` is asked to reproduce the *physical* page,
and a vertical page read physically is row-major across the columns.)

**The real defect the ground truth did expose is in reading order, and only on untagged
pages.** Forced down the heuristic path (`--heuristic`, what a vertical PDF with no structure
tree gets), the engine returned every character but only **24.4%** of them in source order:
#451 taught `BuildWords` to keep a vertical column whole, but `BuildLines` above it still
clustered words on a shared vertical centre and sorted them by left edge, so a `tb-rl` page
was read left-to-right — every column in exactly the wrong order, with nothing lost, which is
why a token-multiset measure cannot see it. Fixed by extending #472's layout frame to detect
vertical writing from the origin step between characters rather than from the glyph matrix
(`ReadsVertically`, `core/megapdf_structure.cpp`), with `vertical-tbrl.pdf` as the regression
fixture. Before and after, same sample, same binary:

| vertical Japanese (18 docs, 39 pages) | before | after |
|---|---:|---:|
| heuristic-path CJK order fidelity vs the source text | 0.2441 | **0.9225** |
| tagged-path (shipped) CJK order fidelity vs the source text | 1.0000 | 1.0000 |
| measure 2: order agreement tau (tree, median) | 0.154 | **0.976** |
| measure 1: aggregate token F1 | 0.522677 | 0.522677 |
| crashes / hangs | 0 / 0 | 0 / 0 |

Measure 1 does not move, and should not: the engine's tokens were already right and the gate
is measuring the reference's fragmentation. **The 0.998 gate was not touched, and the
vertical-Japanese sample is still below it** — that number stays red until measure 1 has a
reference that can read a vertical page, which is its own question and not one to settle by
moving a gate.

Nothing else moves either. The frame test was run over every page of every corpus on hand
before the change was measured, to size what it could possibly touch: **0 pages of 20,499 in
the public corpus outside one fixture set, 0 of 3,812 UN pages and 0 of 781 pages across all
seven horizontal non-Latin scripts** are seen as vertical, against 38 of the 39 vertical
pages. The seven horizontal scripts re-measured byte-identically on every column of the table
above, before and after.

### Fifth run, 2026-09-28: 108 UK OGL forms + 7 large govinfo documents (#471 parts 3-4)

Built on `main` including #452/#463's field-hierarchy relaxation, #457's XFA work, #470's
k3 staging, and #471 part 1's `signed` category — see the #471 parts-3/4 PR for the exact
commit and manifest revision this was actually run against. **The "whole corpus" row below
is a snapshot of 1,489 documents (pre-#471-part-2's non-Latin rows, which landed on `main`
while this run was already underway) — it does not include the 280 `wiki-*` rows the run
directly above added.** The UK-specific and `large`-specific findings that follow it do not
depend on total corpus composition (they are computed only from their own rows), so they
stand regardless; the aggregate figure is reported for context, not as this PR's own
authoritative "whole corpus" number — see the PR for a number against the true final
manifest if one was re-run. The corpus's final size, including every population added by
this point, is **1,777 documents, ~4.40 GB** (dominated by the seven `large` rows at
~4.25 GB) — see `tools/stress/public-corpus/README.md`, "UK government forms" and "Very
large documents", for how the UK/large sets were chosen and licensed.

    tools/stress/public-corpus/build-manifest.py --add-source uk-hmrc
    tools/stress/public-corpus/build-manifest.py --add-source uk-homeoffice
    tools/stress/public-corpus/build-manifest.py --add-source uk-dwp
    tools/stress/public-corpus/build-manifest.py --add-source govinfo-large
    tools/stress/public-corpus/fetch.sh                                # → ~/megapdf-public-corpus
    bash tools/stress/pages-battery.sh    <cli> ~/megapdf-public-corpus <out> --jobs 3
    tools/stress/markdown-battery.sh      <cli> $(command -v cmark) ~/megapdf-public-corpus <out>
    tools/stress/structure-battery.sh     <structure_check> ~/megapdf-public-corpus <out> --reference --cli <cli>

1,489 documents visited by `find -name '*.pdf'` for the non-`large` batteries (see the
snapshot caveat above), minus the same case-mismatched qpdf fixture #445 already noted,
minus the fact that `large` rows were battery-tested separately, below, rather than mixed
into the same run given their size:

| measure | whole corpus | gate | |
|---|---|---|---|
| structure: aggregate token F1 (internal API) | 0.998390 | >= 0.998 | pass — up from #453's 0.996178 now that #453/#463 have landed |
| structure: F1 through `megapdf-cli` | 0.999412 | >= 0.998 | pass |
| structure: order agreement tau (median, 2,403 tagged pages) | 0.945 | >= 0.9 | pass |
| structure: poppler agreement tau (median, informational) | 0.987 | — | — |
| structure: crashes / hangs | 0 / 0 | 0 / 0 | pass |
| structure: CLI bad exit codes | 0 | 0 | pass — the two pdfium fixtures #443 named no longer misfire |
| structure: pages F1 < 0.9 | 3 | informational | down from 23 |
| structure: poppler (`pdftotext`) timeouts | 1 | informational (#442) | bounded, did not stall the run |
| markdown: cmark parse failures | 0 | 0 | pass |
| markdown: crashes / hangs / bad exit codes | 0 / 0 / 0 | 0 / 0 / 0 | pass |
| pages: crashes / hangs | 0 / 0 | 0 / 0 | pass |

**Per-source breakdown, UK rows** (structure-battery measure 1, pages-battery `extract`):

| source | documents | structure F1 (internal) | structure F1 (cli) | `extract` refused-fields |
|---|---:|---:|---:|---:|
| uk-hmrc | 62 | 0.999535 | 0.999535 | **0** |
| uk-homeoffice | 22 | **0.991581** (below gate) | 0.999733 | **0** |
| uk-dwp | 24 | 0.999958 | 0.999984 | **0** |

**Zero field-`/Parent`-hierarchy refusals across all 108 UK forms** — including CT600A/B/C/J,
SA800 and SA900, the deepest hierarchies in the set. This is the evidence #471 part 3 asked
for: #463's relaxation holds on a second jurisdiction's forms, not just the US federal ones
it was built and measured against (compare the second run above: 90% of real IRS forms
refused `extract` before #463).

`uk-homeoffice`'s internal-API F1 (0.991581) is below the 0.998 gate even though the whole
corpus passes and `uk-homeoffice`'s own CLI-measured F1 (0.999733) does not — filed as #479,
a fidelity gap concentrated in the Home Office nationality-form family specifically (not
furniture volume, page count, or anything else common to every `uk-homeoffice` document).
Filed as the finding per #471's "never fix, never gate-adjust" instruction. #480 records the
addition and its overall results.

**`large` category (7 govinfo Federal Register documents, 48.4 MB - 2.07 GB), tested
separately given their size (`--jobs 1`):**

| measure | large category | gate | |
|---|---|---|---|
| pages: crashes / hangs | 0 / 0 (981s total, 28 operations) | 0 / 0 | pass |
| pages: qpdf timeouts | 0 | 0 | pass |
| structure: crashes / hangs | 0 / 0 (283s total) | 0 / 0 | pass |
| structure: aggregate token F1 (internal API) | **0.989043** | >= 0.998 | **fail** — see #488 |
| structure: F1 through `megapdf-cli` | 0.989950 | >= 0.998 | **fail** — same cause |
| structure: poppler (`pdftotext`) timeouts | 0 | informational (#442) | pass — completed even on the 2.07 GB document |
| markdown: crashes / hangs / bad exit codes | 0 / 0 / 0 (45s total) | 0 / 0 / 0 | pass |

Wall time / peak RSS per document (via `megapdf-cli extract`, outside the batteries, as a
baseline for a future regression to compare against): see
`tools/stress/public-corpus/README.md`, "Very large documents" — from 1.42s/290MB (48.4 MB
document) to 14.75s/2.40GB (the 2.07 GB document), scaling roughly linearly with size, no
sign of a blow-up at the top of the range.

The structure gate fails on this population — filed as **#488**: these are largely pre-1995
OCR'd scans (see README, "Very large documents"), and 0.989 is well within the range other
scan-heavy or non-standard populations have measured at elsewhere in this corpus. Not fixed
or gate-adjusted here, per #471's own instruction — a gate moving on a new population is the
information wanted.

**Battery timeouts and the `large` category: the current defaults were sufficient for every
document tested, with real margin.** Across all three batteries and all 7 documents up to
2.07 GB, **not one operation hit its timeout** — `pages-battery.sh`'s 300s (28 rotate/delete/
move/extract operations, 981s total, none individually timed out), `structure-battery.sh`'s
120s (283s total across internal-API, `pdftotext --reference`, and CLI calls per document;
poppler itself finished the 2.07 GB document inside 120s, where #442 found it can hang
indefinitely on a much smaller, adversarial fixture), and `markdown-battery.sh`'s 120s
(45s total for all 7). **No change requested for these sizes** — but this is 7 documents
topping out at 2.07 GB; a document meaningfully larger, or a slower host, could still reach
the existing limits, so this is worth re-checking rather than assumed permanently settled if
the category ever grows. Not changed here either way — `tools/stress/*-battery.sh` was out
of bounds for #442/#445 and stays out of bounds here.

### Android app UI tests (#346)

`android/app/src/androidTest/` drives the Android app's own screen on an emulator — the
menu rows, gestures, toolbar and pickers that the engine's tests and the core's parity
tests never touch, and that until #346 only a store capture had ever exercised. They are
Compose UI tests against the real `MainActivity` and its real view model and engine,
finding things by the labels the accessibility work gave them (#328, #347), so a label
that goes missing fails a test rather than a TalkBack pass. The fixture is the app's own
demo agreement, handed over as a `content://` uri through a FileProvider — the way another
app hands a PDF over (#376). (The app's own provider serves it, from a folder inside its
share grant: the test APK has a uid of its own, so a provider of its own would be out of
the app's reach.)

| test | what it drives |
|------|----------------|
| `EditingTest` | a tap ticks a drawn checkbox (Undo lights, the title gets its dot), Save writes it to the file; Add text → tap → type → Add puts a box where the tap landed, and tapping it again brings up its ✎ / ✕ chrome |
| `RedactionMarkLifecycleTest` | the ⋮ Redact row arms the tool and says so (#328); a drag along a line marks it; tap selects, drag moves, ✕ at its centre removes (#347); Undo takes back removal, move and placement one step each, Redo puts the placement back; Clear all marks is one step (#329) |
| `FileCommandsTest` | Export as Markdown asks for a `text/markdown` document named `.md` and writes real Markdown without touching the document (#409); Save a copy asks for a PDF, writes one that becomes the document; Share asks Save / Share without saving / Cancel when dirty, and Share without saving hands the chooser a `content://` grant on a copy (#378). The pickers and the chooser are answered by Espresso-Intents, so the test sees exactly what the app asked the system for |
| `OpenWithTest` | an `ACTION_VIEW` with a `content://` uri opens the document cold; a second one while the first has unsaved changes asks Save / Discard / Cancel, and Discard replaces it; a `file://` uri opens too (#376) |
| `PinchZoomTest` | two fingers apart widen the page past the screen; a double tap fits it again (#336) |

They run in `android-ci.yml`'s `instrumented-test` job after the engine's tests, on the
same API 30 emulator, a `pixel_5` frame (`:app:connectedDebugAndroidTest`). Locally, with
an emulator or a device attached:

```bash
cd android && ./gradlew :app:connectedDebugAndroidTest
```

A red test stays in the tree under `@Ignore` naming its issue, so the fix takes the
annotation off rather than writing the test: `undoAfterARemovalTakesBackTheMoveBeforeIt`
is #429, found by the harness's first run.

What they cannot reach: the system picker's own sheet (DocumentsUI is stubbed, not
driven), the OS share sheet, TalkBack itself, and how any of it *feels* — the manual pass
in this file still stands.

#### Three timing traps, and the numbers that settle them (#611)

Main went red for most of a day on `PageToolsProgressTest`, and none of it was a product
defect: it was three races the *tests* were losing, all of one family — a check that
samples something the app is in the middle of changing. They are written down here because
each was diagnosed from the test's name twice before anybody measured anything.

1. **An operation has to outlast the busy strip's 0.5 s show threshold**
   (`BusyState.SHOW_AFTER_MS`) before Stop is offered at all, and these fixtures were sized
   by eye rather than by measurement. Measured on the hosted CI emulator: extract over the
   old `q Q`-padded 800 x 100 KB fixture took **373 ms** — *under* the threshold, so most
   runs had nothing to press Stop on. The 80 MB document was a mirage:
   `megapdf_pages_extract` deflates what it copies and a no-op pair compresses to nothing,
   so the real work was a 332 KB write. With incompressible padding the same shape takes
   **1,893 ms**. The search sweep was the same story at 2,000 pages (**679 ms**, 1.4x the
   threshold, 3 failures in 24) and is 8,000 pages now (**1,904 ms**). Extract runs at
   ~42 MB/s of *output* and the sweep at ~0.24 ms a page, both near-linear, so bytes and
   pages are the only dials: **re-measure before shrinking either count.** Combine's own
   import of 5,000 pages is only **149 ms**; what keeps its token alive long enough is
   re-reading 5,002 page sizes afterwards, not the import.
2. **Undo is offered a moment before it will be obeyed.** `ViewerViewModel.undo` goes
   through `launchEdit`, which drops the request silently while `editingBlocked`
   (`editsInFlight > 0`) — by design (#145: a tap while one runs is ignored, never queued).
   The button greys out on `toolsDisabled` instead, which deliberately leaves
   `editsInFlight` out so quick page work does not make the toolbar flicker. An operation's
   effect lands *inside* `launchEdit`'s block and its `editsInFlight--` in the `finally`
   after it, so a test pressing Undo as soon as the previous change shows up is pressing
   into that gap. Every test presses Undo and Redo through `clickUndo()` / `clickRedo()`,
   which wait on `editingBlocked` — the question the view model actually answers. Do not
   click the button directly. (The product asymmetry this worked around is itself fixed
   now, see below, so the wait is belt and braces — but it is also what covers
   `pageRewriteDeciding`, and a test that asks the view model rather than the screen is the
   right habit regardless.)
3. **A one-shot status cannot be asserted on.** `ViewerViewModel.statusMessage` used to be
   erased by the toast that showed it, so "did the app say Stopped.?" was a race against a
   recomposition the test cannot see. It is kept now, and the toast de-duplicates on
   `statusSerial` instead.

Two habits came out of it. A wait that can time out uses `waitUntilOrExplain`, so the
report says what the app was doing rather than only a line number — that is what finally
told all three apart. And `.github/scripts/run-instrumented-tests.sh` runs `adb root`
*before* starting the logcat capture: `adb root` restarts adbd and tears down every
connection it was serving, which is why four of the six original #611 failures uploaded a
0-byte `instrumented-logcat.txt` — #545's diagnostics were missing exactly when needed.

#### The asymmetry behind trap 2 is fixed, and what the flicker actually costs

#611 left the product half of trap 2 alone and flagged it: the toolbar genuinely offered an
Undo the view model would discard, for the few hundred milliseconds after an edit too quick
to show a spinner. **Dave reversed the #145 trade-off for 2.2** — a person who taps Undo and
sees nothing happen reports it as lost work, which is worse than a blink — so there is now
one predicate instead of two. `ViewerViewModel.toolsDisabled` is gone;
`editingBlocked` is both what the actions refuse on and what the controls grey out on, and
`ToolbarGateTest` holds the invariant: *a command the toolbar offers is one the view model
will act on.* It fails 3 runs in 3 without the fix and passes 3 in 3 with it.

Two things worth keeping from that change. The swap was safe to make in one step because the
old condition was a strict **subset** of the new one — every `busy.page` spinner is started
inside a `launchEdit`/`launchPageEdit` block, so `page.isVisible` implies
`editsInFlight > 0` — which means unifying them could only ever disable something earlier,
never offer something new. And the blink #145 traded this away for was finally **measured**,
64 times over 16 full-suite runs on the CI emulator:

| edit | blocked, min–median–max |
|------|-------------------------|
| checkbox tap | 5.5 – 7.2 – 7.7 ms |
| rotate one page | 0 – 21.6 – 118.8 ms |
| undo a checkbox | 0 – 21.9 – 119.6 ms |
| undo a rotation | 0 – 30.5 – 122.8 ms |

A frame is 16.7 ms, so a checkbox tap's grey-out does not survive to be drawn at all, and
the worst case seen is about seven frames. Every single measurement showed **one** blocked
period or none — never two — so the toolbar dims once per edit and cannot strobe. Anything
long enough to be properly visible was already disabling the toolbar before this change, via
the page spinner.

**That is the whole justification for overriding #145, and it is worth being explicit about
why.** #145 did not weigh a 7 ms grey-out against a discarded tap and choose the tap; it
reasoned about "a flicker on every checkbox" without ever timing one, and traded away a real
correctness property for a cost that turns out not to exist at 60 Hz. The structural half of
the measurement is what settles it rather than taste: *one blocked period per edit, never
two* means there is no mechanism by which the toolbar can strobe, whatever the hardware — a
slower phone makes the single dim longer, not repeated. Opinion could have argued either way
about whether a blink is worse than losing a tap; the numbers mean nobody has to.

This is the same failure mode as trap 1 above, in the same file, found the same week: a
constant or a trade-off chosen by reasoning about a quantity instead of measuring it, and
wrong by enough to matter. The extract fixture was sized for a write that was assumed to take
"a real amount of wall time" and took 373 ms; this flicker was avoided as if it were visible
and it is 7 ms. **When a decision here turns on a duration, measure the duration.**

Measuring it needed three attempts, which is worth knowing before anyone tries to watch a
Compose state over time again: a thread calling `runOnMainSync` in a loop starves the looper
and times its own test out, and a `Runnable` reposting itself on the looper stops Espresso
ever seeing the app idle (`AppNotIdleException`). What works is a passive
`Snapshot.registerApplyObserver`, which timestamps every point the value can change and is
invisible to the idling policy.

### megapdf-cli (#142, #355, #357)

`megapdf-cli extract <file.pdf>` is a small, self-contained native binary — no .NET runtime,
no Store package — that turns a PDF's text into plain text (or, with `--format md`, CommonMark
Markdown) from a shell: `PowerShell`, `cmd`, `bash`, whatever the machine has. It is built as
part of the shared engine core, not as part of any app:

```bash
cmake -S core -B core/build/linux-x64 -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build core/build/linux-x64 --target megapdf_cli
core/build/linux-x64/megapdf-cli extract report.pdf > report.txt
```

(On Windows, `-A x64` in place of `-G Ninja`, and the output is `megapdf-cli.exe` under the
build's `Release\` folder; on macOS, the same as Linux. Building the core needs the pinned
PDFium prebuilt — `tools/fetch-pdfium-linux.sh` / `tools/fetch-pdfium-mac.sh`, or
`tools/pdfium/install-release.sh` on Windows — fetched first, same as `MEGAPDF_CORE_TESTS`.)

`--help` lists every option (`--format txt|md`, `--pages`, `--out`,
`--password-file`/`--password-stdin`, `--keep-lines`, `--page-marker`/`--no-page-breaks`,
`--keep-furniture`, `--all-fields`/`--no-fields`, `--heuristic`, `--strict`, `--quiet`). There
is deliberately no `--password` flag — the password is a file's first line or stdin's first
line, never a command-line argument, matching the rest of the repo's own practice
(`tools/gen_security_fixtures.sh`, `MEGAPDF_STRESS_UNLOCK_LIST`). Exit codes are part of the
contract, not an afterthought:

| exit | meaning |
|------|---------|
| 0 | text written |
| 1 | usage error (a bad option, a bad `--pages` range, both password options given) |
| 2 | the file could not be opened (missing, unreadable, not a PDF), or opened but a requested page in it could not be read (#443: a document PDFium's own page tree calls valid enough to open but not enough to load every page it counts) |
| 3 | a password is required, or the one given is wrong |
| 4 | the document uses a security handler this build cannot open |
| 5 | none of the requested pages had a text layer |
| 6 | `--strict` was given and at least one requested page had no text layer |
| 7 | the output could not be written (`--out`'s file, or stdout) |
| 130 | interrupted (Ctrl+C) |

A page with no text layer never silences the run — MegaPDF does not do OCR, and says so once
on stderr, plus one `page N: no text layer` line per such page (`--quiet` silences these two
notes; errors always print regardless). `core-tests.yml` runs the built binary against the
structure fixtures above on all three OSes and diffs its output against the same `.txt`/`.md`
goldens the internal API test compares against, plus an informational, Linux-only token
comparison against `pdftotext`. `tools/stress/structure-battery.sh --cli <megapdf-cli path>`
re-runs the corpus battery's token-fidelity measure through the real binary (`--page-marker`,
not the default form feed, because a real document's text can itself contain a stray form-feed
character from a bad font mapping) rather than only through the internal API call.

`--format md` (#357) renders the same contract-9 blocks as CommonMark: `#`-headings (capped at
level 6), unwrapped paragraphs with bold/italic/monospace spans, `-`/`N.`-prefixed list items
(a lettered or roman marker becomes `1. <marker> text`, since CommonMark has no lettered
lists), form fields as GitHub task-list items or `**name:** value`, and a page with no text
layer as `*[Page N has no text layer]*`. It infers nothing itself — every block came from the
same structure load the plain-text writer uses, so the two formats can never disagree about
what a heading is. `core_tests.cpp` additionally renders each golden `.md` fixture through an
independent CommonMark implementation (`cmark`, `apt install cmark` on Linux) and asserts its
heading and list-item counts match contract 9's own block counts — proof that escaping never
breaks a real block out of its own markup, skipped rather than failed when `cmark` is not on
`PATH`. `tools/stress/markdown-battery.sh <megapdf-cli> <cmark> <corpus> <out-dir>` is the
corpus-scale counterpart: every document's `--format md` output parses cleanly through `cmark`,
gating on crashes, hangs and parse failures (never on heading *accuracy* — there is no
corpus-scale ground truth for that; #357's own hand-checked sample of 30 documents is where
that bar is judged, by a person, off `--dump` output on the corpus machine).

**Where the binary comes from, per platform (#142, #356).** Not a separate build: on every
platform `megapdf-cli` is the `megapdf_cli` CMake target, built alongside `libmegapdf_core`
by the same core build every app already needed.

| platform | where it ships |
|---|---|
| Linux | `tools/build-linux-app.sh` builds it and copies it into the app tree's `bin/`, beside `libmegapdf_core.so` and `libpdfium.so`. From there: the tarball and `install.sh` (`~/.local/bin/megapdf-cli`, a symlink into `~/.local/lib/megapdf`), the `.deb` (`/usr/bin/megapdf-cli`, a symlink into `/opt/MegaPDF`), the Flatpak (`flatpak run --command=megapdf-cli ca.electricrv.MegaPDF file.pdf`), and the snap (`snap run megapdf.cli extract file.pdf`). `tools/Linux-Packaging.md` has the exact invocations and the packaging changes behind each. |
| Windows | `megapdf-cli.exe`, x64 and arm64, in `megapdf-cli-windows-<arch>-<version>.zip`, attached to a draft GitHub release by `.github/workflows/windows-cli-release.yml` on a `windows-cli-v*` tag, alongside `megapdf_core.dll`, `pdfium.dll`, `LICENSE` and `THIRD-PARTY-NOTICES.txt`. **Unsigned** — MegaPDF carries no Authenticode certificate; the only Windows signing that exists is the Store's own re-signing of the MSIX on ingestion, which cannot sign a loose `.exe`. The release notes say so. Not in the MSIX Store package — a Store app cannot put a binary on `PATH`. |
| macOS | `megapdf-cli`, a universal (arm64 + x86_64) binary, in `megapdf-cli-macos-universal-<version>.zip`, attached to a draft GitHub release by the `cli-release`/`cli-release-publish` jobs in `.github/workflows/macos-app.yml` on a `macos-cli-v*` tag. Signed with the app's Developer ID and notarized the way `notarize-macos-app.sh` notarizes the bundle, but not stapled — Apple's stapler only attaches to a bundle, `.pkg` or `.dmg`, not a loose-file zip, so Gatekeeper checks the ticket online on first run instead. Not in the Mac App Store package, for the same reason as Windows. |

The zips are a separate release artifact from the app itself, on their own tag series
(`windows-cli-v*`, `macos-cli-v*`, alongside `linux-v*`, `android-v*`, `ios-v*`) — nothing
about the MSIX or the Mac App Store submission changes.

**Verifying the archives needs Windows/macOS hardware this repository's CI does not have
outside the tag build itself** — download, unzip and run on a clean Windows 11 machine and a
clean Mac (Gatekeeper's verdict on the unstapled zip in particular) is a manual check.

### Sixth run, 2026-09-28: the sample pinned, baseline tied to a named revision (#455)

The first baseline recorded against a **manifest revision** rather than a date, which is what
#455 asks for: a gate number without the manifest it was measured on cannot tell you whether
it moved because the engine changed or because the corpus reshuffled.

**Measured before #519, so the internal-API figures below read very slightly low.** That fix
(#479/#495) taught the fidelity measure to count a list item's marker, which the engine keeps
in its own field and the CLI's writer puts back when it renders — so every alphanumeric list
marker had been counting as a token the engine lost. The correction is small and always in
the same direction: on a 300-document sample the internal aggregate moved from 0.971414 to
0.971436, matching the CLI leg exactly. Compare a later run's internal-API numbers against a
re-measurement, not against these, and note it the way #455 asks: a gate number names both
the manifest it was measured on **and** the tool that measured it.

    manifest revision: sha256:d1c084b1cedb7583dfbef03e3b8fb59c9eac55c39b1dbed0fdd132cde7dd079e
    1,777 rows  (363 form, 300 tagged, 250 report, 150 malformed, 100 scan, 0 large,
                 614 opt-in: 136 irs, 50 uscis, 108 uk-*, 33 govinfo-signed, 7 govinfo-large,
                 280 wiki-*)

**Measured over 1,163 of those 1,777 rows.** Only `raw.githubusercontent.com` is reachable
from Anthropic's cloud sandbox; the 614 opt-in rows sit behind hosts that refuse the proxy's
CONNECT outright (`irs.gov`, `uscis.gov`, `assets.publishing.service.gov.uk`, `govinfo.gov`,
`*.wikipedia.org` — the same block #434 recorded). This is therefore the baseline for the
**git-sourced subset** of that revision, not the whole corpus; a full-corpus number needs a
machine that can reach the other five hosts.

Run against a clean tree holding **only** this revision's rows. That matters and is itself a
#455 point: the fetch directory on this machine still held 101 documents selected by *earlier*
manifest revisions, and a battery walks the directory, not the manifest — so measuring it
as-is would have reported 1,264 documents against a 1,163-row manifest and reproduced the
exact ambiguity this issue exists to remove. `fetch.sh --verify-only` over the subset:
`verified 1163, missing 0, mismatched 0`. Built against the pinned 33-patch PDFium
(`pdfium-7934-megapdf-2b3b415e86b1`).

| measure | this run | gate | |
|---|---|---|---|
| structure: aggregate token F1 | **0.999450** | >= 0.998 | pass |
| structure: F1 through `megapdf-cli` | **0.999670** | >= 0.998 | pass |
| structure: crashes / hangs | 0 / 0 | 0 / 0 | pass |
| structure: CLI bad exit codes | **0** | 0 | pass |
| structure: order agreement tau, median | 1.000 | >= 0.9 | pass |
| structure: pages F1 < 0.9 | 2 | informational | |
| markdown: cmark parse failures | **0** | 0 | pass |
| markdown: crashes / hangs / bad exits | 0 / 0 / 0 | 0 / 0 / 0 | pass |
| pages: crashes / hangs | 0 / 0 | 0 / 0 | pass |
| pages: qpdf failures / count mismatches / write failures | 0 / 0 / 0 | 0 / 0 / 0 | pass |
| pages: **refused, other** | **5** | 0 | **fail — #445** |

1,162 documents seen by the structure and markdown batteries rather than 1,163: their `find`
is case-sensitive `-name '*.pdf'` and one qpdf fixture is `split-exp-04.Pdf`. Cosmetic, noted
so the counts reconcile.

The one red gate is the same one TESTING.md already tells you how to read. All five `refused,
other` come from **two** documents — `pdfium/testing/resources/bug_216.pdf` and
`document_aactions.pdf` — refusing `rotate`/`move`/`extract` with exit 9, the pair #453
identified and #445 filed. #450 since split out the buckets that were miscounted (`usage
error`, `refused, damaged input`, `qpdf, carried damage`), and those now read 2 / 0 / 3 and are
correctly **not** gated; what is left is #445's still-open point that a page the engine cannot
load is a third legitimate refusal the contract does not list. Not re-filed, and no threshold
touched.

One poppler timeout (`pdftotext` bounded at 120s by #442, on qpdf's `shared-unnamed-field.pdf`);
that document is simply absent from measure 3's counts and does not touch the fidelity gate.

### Seventh run, 2026-09-28: all three batteries over all four corpora, main at 88117b7

The first measurement of every corpus on hand against one commit, and the first since the three
changes that could have moved these numbers: **#521** (the layout frame now detects vertical
writing), **#519** (the fidelity measure now counts a list item's marker) and **#452/#463**
(PDFium at 33 patches, release `pdfium-7934-megapdf-2b3b415e86b1`). Run on kdocker3 in a
container, `structure-battery.sh --reference --cli`, then `markdown-battery.sh`, then
`pages-battery.sh`.

**Zero crashes and zero hangs, in every battery, on every corpus. 5,763 documents.**

Nothing was silently narrowed, which is the first thing to check in a run this size: each
battery's own sweep visited exactly the document count on disk.

| corpus | on disk | visited | opened | the rest |
|---|---:|---:|---:|---|
| private | 4,158 | 4,158 | 4,084 | 12 encrypted, 62 unreadable format |
| public | 1,381 | 1,381 | 1,368 | 5 encrypted, 8 unreadable format |
| Canadian | 134 | 134 | 134 | — |
| UN | 90 | 90 | 90 | — |

Structure, against F1 >= 0.998 and order agreement tau >= 0.9:

| corpus | token F1, internal | token F1, CLI | tau median | CLI bad exits |
|---|---:|---:|---:|---:|
| private | **0.998880** pass | 0.998880 pass | 0.935 pass | 0 |
| public | **0.971952** fail | 0.971952 fail | 0.956 pass | 0 |
| Canadian | **0.999907** pass | 0.999907 pass | 0.959 pass | 0 |
| UN | **0.996348** fail | 0.996348 fail | 0.967 pass | 0 |

Markdown: every gate passes on all four corpora. Pages: all gates pass except public's five
`refused, other`, which are the same two documents recorded above, tracked as #445's open point.
The private corpus now shows **0 refused-fields**, down from 33, which is #452/#463 landing.

**Both failures are already-open issues, measured again rather than newly found.** Neither is
an artifact of #519's correction, whose effect is about 0.00002.

- **Public** resolves to one category. The 33 `govinfo-signed` documents carry 96% of the
  corpus's tokens and score 0.9707; every other category present scores 0.999 to 1.000. #498
  already recorded 0.970684 for exactly that population.
- **UN** resolves to one document. No document scores below 0.96, and a single 190-page one at
  0.9662 carries the whole shortfall. Excluding it, the corpus reads **0.99960**. That document
  is #496's, root-caused since as **#524**: `BuildLines` scales its split threshold off the raw
  `Tf` operand, so a producer that puts its scale in the text matrix gets one block per word.

Read the private corpus's 0.998880 as a first figure, not a comparison: no private baseline is
committed, by design, because the corpus is personal.

### Eighth run, 2026-09-30: page tools (#174) corpus battery, `pages-battery.sh`, main at 5483095

The outstanding item #174 itself named: three platforms now ship a page-tools interface
(Android #554, Mac/Linux #556, the shared .NET layer Windows builds on), the engine side
(#430, contract 10) and rotation-aware coordinates (#439) had landed, but no pass had run the
corpus through `rotate`/`delete`/`move`/`extract` since #430's engine-only pass over the
private corpus alone. This run adds the other three corpora and measures against `main` at
`5483095`, on top of PDFium at 33 patches (`pdfium-7934-megapdf-2b3b415e86b1`), the drawn-em
font-size fix and the vertical-writing frame (#521) the seventh run already covered. Run on
kdocker3 in a container, all four corpora through `tools/stress/pages-battery.sh` (the private
and public corpora concurrently, `--jobs 8` / `--jobs 6` on 16 cores; Canadian and UN
sequentially first, `--jobs 8`).

**Zero crashes and zero hangs, on every operation, on every corpus. 5,763 documents — the same
population size as the seventh run.** As in that run, nothing was silently narrowed:

| corpus | on disk | visited | opened | the rest |
|---|---:|---:|---:|---|
| private | 4,158 | 4,158 | 4,084 | 12 encrypted, 62 unreadable format |
| public | 1,381 | 1,381 | 1,374 | 4 encrypted, 3 unreadable format |
| Canadian | 134 | 134 | 134 | — |
| UN | 90 | 90 | 90 | — |

Gates (0 crashes, 0 hangs, 0 qpdf failures, 0 page-count mismatches, 0 write failures, 0
refusals outside the two the contract documents — a security-forbidden operation, exit 8, and
the field-`/Parent`-hierarchy refusal, exit 9 with that message):

| corpus | crashes/hangs | QPDF FAILED | COUNT MISMATCH | WRITE FAILED | REFUSED, other | |
|---|---:|---:|---:|---:|---:|---|
| private | 0/0 | 0 | 0 | 0 | 0 | pass |
| public | 0/0 | 0 | 0 | 0 | **5** | **fail — #445** |
| Canadian | 0/0 | 0 | 0 | 0 | 0 | pass |
| UN | 0/0 | 0 | 0 | 0 | 0 | pass |

**Public's failure is the confirmation the task brief said to expect, not a new finding.** The
five `refused, other` (2 `rotate`, 1 `move`, 2 `extract`) resolve to exactly two documents (checked
by their path-hash in the log, not their names) — the same pair #445 already tracks as pages the
engine cannot load. Not re-filed, no threshold moved.

**The field-hierarchy refusal (`refused-fields`, the other named-in-advance result) did not
reproduce at the rate expected.** `extract`'s field-`/Parent`-hierarchy refusal came back **0
on every operation, on every one of the four corpora** — not the roughly 0.8% quoted going in.
This is not an instrument or engine defect: the sixth and seventh runs already recorded the
private corpus at 0 refused-fields, "down from 33," once #452/#463's relaxation landed, and
this run's corpus composition (document counts identical to the seventh run on every corpus)
is consistent with that fix simply still holding rather than a new regression to chase. The
~0.8% figure appears to predate #452/#463 landing; flagged here rather than restated as
confirmed, per the instruction to check the instrument before concluding.

Permission-restricted documents (exit 8, the security itself forbidding the operation — expected,
not gated) were common on the Canadian and public corpora specifically: Canadian `rotate`
52/134, `delete`/`move` 46/134 (plus 51 single-page documents skipped), `extract` 67/134 — the
Canada Revenue Agency's fillable-form family carries assembly restrictions; public corpus
`restricted` ran 36–47 per operation out of 1,374 opened. Private and UN corpora carried far
fewer (private 37–51 of 4,084; UN 0 of 90).

**Coverage gap worth stating plainly: this run, like #430's, only exercises `rotate`, `delete`,
`move` and `extract`.** `megapdf-cli pages --help` lists two more contract-10 operations,
`--blank` (insert) and `--import` (pages from another document), that `pages-battery.sh` never
calls. #174's own scope names insert and import alongside the other four; **they remain
untested at corpus scale** — the harness would need extending to drive them (`--import` in
particular needs a second document per call) before that half of #174's ask is answered. No
issue filed for this here since it is a coverage gap in the harness rather than a battery
result, but it should not be read as answered by this run.

No new issue was filed from this run: the one failing gate is #445, already open and explicitly
not to be re-filed; nothing else was red.

### Ninth run, 2026-09-30: page tools (#174, #567) — insert and import added, `pages-battery.sh`, main at 7786c7f

The eighth run's own coverage-gap note is what this run closes: `pages-battery.sh` drove
`rotate`, `delete`, `move` and `extract` but never `--blank` (insert) or `--import`, the other
two operations `megapdf-cli pages --help` and #174's own scope both name. #567 extended the
harness to drive both, on the same gates as the rest, and this is that run — the same four
corpora, on `main` at `7786c7f` (unchanged since the eighth run), PDFium still at 33 patches.

**Blank** appends one page after the document's last, sized to match it (`pdfinfo`'s own
"Page size", already rotation-adjusted — the same rule `DocumentViewModel.Pages.cs`'s
`BlankPageSize` uses on the desktop apps) rather than the CLI's own Letter default, so the
corpus's mixed and unusual page sizes are what actually gets exercised.

**Import** is the operation #567 named as mattering most, and the one this run has the most to
say about. Two calls per document, both population-wide, neither a sub-sample of the corpus:

- **importself** — every page of the document, imported into a fresh copy of *itself*,
  appended at the end. Expected page count 2n, always known.
- **importpair** — every page of a *second*, different document imported the same way: the
  document standing half the corpus's length further along the same sorted file listing,
  wrapping around. One pairing per document (N calls, not N² — a full pairwise sweep was
  considered and rejected as quadratically more expensive for a return this run's numbers
  don't show it earning; see the pairing-rule discussion below).

Every document that opens is visited by **both** — importself is exhaustive by construction,
and importpair's fixed offset means every document appears exactly once as an import target
and (for an even-sized corpus) very close to exactly once as another document's import source.
Neither call's page-count expectation, or damaged/clean classification, is taken from the
primary document alone: the *other* document in each call is independently `qpdf --check`'d
and `qpdf --show-npages`'d, and damage or an unknown count on either side is handled the same
way #445 already handles it for the primary document (folded into the expected-count skip and
the damaged-input carve-out, never blamed on the side that was actually clean).

**Population — nothing quietly narrowed. 5,763 documents, the same size as the seventh and
eighth runs:**

| corpus | on disk | visited | opened | the rest |
|---|---:|---:|---:|---|
| private | 4,158 | 4,158 | 4,084 | 12 encrypted, 62 unreadable format |
| public | 1,381 | 1,381 | 1,374 | 4 encrypted, 3 unreadable format |
| Canadian | 134 | 134 | 134 | — |
| UN | 90 | 90 | 90 | — |

Identical to the eighth run's own table on every corpus — the two runs are the same
population, extended with two more operations, not a different sample.

**Gates** (0 crashes, 0 hangs, 0 qpdf failures, 0 page-count mismatches, 0 write failures, 0
refusals outside the two the contract documents — a security-forbidden operation, exit 8, and
the field-`/Parent`-hierarchy refusal, exit 9 with that message, now reachable from `import` as
well as `extract`):

| corpus | crashes/hangs | QPDF FAILED | COUNT MISMATCH | WRITE FAILED | REFUSED, other | |
|---|---:|---:|---:|---:|---:|---|
| private | 0/0 | 0 | 0 | 0 | 0 | pass |
| public | 0/0 | 0 | 0 | 0 | **9** | **fail — #445, extended** |
| Canadian | 0/0 | 0 | 0 | 0 | 0 | pass |
| UN | 0/0 | 0 | 0 | 0 | 0 | pass |

Private, Canadian and UN are clean across all seven operations, blank and both imports
included. Public's own gate is red for the same reason the eighth run's was.

**Public's failure is #445, confirmed rather than rediscovered, and now visible through two
more operations.** The same two documents #445 already tracks — pages the engine cannot load,
not a `pages-battery` defect — account for every one of the nine `REFUSED, other` instances:
`rotate` (2), `move` (1) and `extract` (2) exactly as the eighth run counted them, plus, now,
`importself` (2: each of the two documents refusing to import a copy of its own unloadable
pages into itself) and `importpair` (2: two *different*, otherwise-clean documents whose fixed
partner happens to be one of the two #445 documents, so importing pages from it fails the same
way). Four distinct documents in total (checked by path-hash, not by name) — the original pair,
plus the two documents that happen to sit opposite them in the pairing. Not re-filed, no
threshold moved.

**A second, smaller and genuinely new observation, filed as #574 rather than folded into
#445.** Fixing an accounting gap this run's own per-op breakdown had — `protected` /
`unsupported security` / `did not open` were previously summed only at the top level (the
*primary* document failing before any operation ran), never per operation — surfaced two
*more* public-corpus documents where `qpdf` reports a page count fine but `megapdf-cli`'s own
open fails outright on every operation run against them, `rotate` and `extract` included. This
is a different failure shape from #445 (which opens fine and fails on specific pages) and was
sitting in every prior run's own log, uncounted in the printed table. Not gated (an open
failure was never part of this battery's crash/hang/refusal gate, in this run or any before
it), 2 of 1,381 public documents, not seen on the other three corpora. #574 has the detail;
#567's PR carries the reporting fix that found it.

**The genuine finding, and the reason #567 was filed the way it was: the field-hierarchy
refusal is not gone — `extract` on this PDFium (33 patches) simply cannot reach it any more,
and `import` can.** The eighth run (and the seventh) measured `extract`'s own refusal at
**zero** on every corpus, against roughly 0.8% expected, and flagged rather than confirmed the
gap: `core/megapdf_core.h`'s own comment on the patched copy explains why — extracting pages
into a brand-new document has no pre-existing field name for the collision check to trip over,
so past patch 33 `extract` structurally cannot produce `MEGAPDF_ERR_FIELDS` at all. Importing
pages *into an existing document* still can, and `importself` guarantees the collision (every
field name in the copy already exists, verbatim, in the target), so it measures the population
that actually carries hierarchical form fields, not a sampled fraction of it:

| corpus | importself refused-fields | rate | importpair refused-fields | rate |
|---|---:|---:|---:|---:|
| private | 37 / 4,084 | **0.91%** | 0 / 4,084 | 0% |
| public | 212 / 1,374 | **15.4%** | 0 / 1,374 | 0% |
| Canadian | 5 / 134 | 3.7% | 4 / 134 | 3.0% |
| UN | 0 / 90 | 0% | 0 / 90 | 0% |

**The private corpus's own rate — 0.91% — lands right where the eighth run said it expected
extract's to be (roughly 0.8%), on the same corpus composition (identical document counts
since the seventh run).** That is exactly the confirmation #567 asked for: the zero the last
two runs measured was the absence of a working probe, not the absence of the problem, and the
problem is still there, at essentially the rate it was always expected at. Public's much higher
rate (15.4%) is not a surprise once the corpus is considered rather than compared directly to
private's: the public corpus is disproportionately government fillable forms, and an earlier
run over this same corpus already found 90% of real IRS forms alone hit this exact hierarchy
before #452/#463 relaxed it (see the second public-corpus run, above) — `importself` measuring
a corpus-wide 15.4% where forms are a large minority of 1,374 documents is consistent with
that, not a contradiction of it.

**Cross-document import (`importpair`) almost never collides, and that is itself informative,
not a null result.** A field-name clash needs the same top-level name on both sides, and two
arbitrary documents essentially never share one — except on the Canadian corpus, where four
documents do (3.0%, against zero on private and public despite far larger populations). The
Canadian corpus is entirely Canada Revenue Agency fillable forms sharing a generated
field-naming convention across different forms, exactly the condition that makes a collision
between two *different* documents plausible. This is the shape a self-import can never produce
(there is only one document in it) and the reason the pairing rule keeps a real cross-document
case in the run at all, rather than relying on self-import alone.

**The pairing rule, stated as the issue asked: every document once as a target, every document
once as a source, no full pairwise sweep.** `importself` is exhaustive over the corpus by
construction. `importpair` pairs document *i* with the document standing at *i* + half the
(limited) file count, wrapping around — a fixed, population-wide sample rather than a
sub-sample of it, and, for an even-sized listing, a perfect one-to-one matching (every document
is some other document's partner exactly once). A full O(n²) sweep was considered and rejected:
it is quadratically more expensive against a corpus where this run's own numbers (0% collision
rate on two of the four corpora, and the Canadian corpus's real collisions already found by the
cheap version) do not show it buying more than a fixed offset already reaches. The next person
extending this should read that as "no evidence a denser sample would find more," not as "a
denser sample was ruled out on principle" — the corpus composition could change that.

**Blank is clean everywhere.** Zero `REFUSED, other`, zero count mismatches, zero crashes or
hangs on any corpus. `dims-unknown` (the page `pdfinfo` could not size, so the harness declined
to guess rather than sending a request that was never really formed) was 0 on private, Canadian
and UN, and 2 on public — one of them the #574 document that needs a password `qpdf` does not,
the other a clean document `pdfinfo` simply could not report a size for. Neither is gated.

No issue was re-filed for #445 (confirmed, not rediscovered). #574 is new, small, not gated and
not #567's own finding, but disclosed rather than folded silently into this run's numbers.
With insert and import now both run at corpus scale, #174 has exercised its full contract-10
surface: rotate, delete, move, extract, blank and import all ran clean except for #445's
already-known, already-excused pair (and #574's two, also unrelated to #567).

### Tenth run, 2026-10-01: all three batteries, all four corpora, main at c3cc720 (#537)

A #537 release-candidate condition and a regression check, not an investigation: a day's worth
of engine changes had each been tested on its own but never together at corpus scale --
**#578** rewrote lifetime rules across 47 entry points behind the dead-handle contract, **#595**
normalises a masked hyphen in the structure path, **#587** landed two new contract functions,
and **#532** changed how line splitting scales off the drawn em rather than the raw `Tf`
operand. Run on kdocker3 in a container (`megapdf:base`, PDFium still at 33 patches,
`pdfium-7934-megapdf-2b3b415e86b1`), all four corpora through `structure-battery.sh --reference
--cli`, then `markdown-battery.sh`, then `pages-battery.sh` (#567's full seven-operation shape:
rotate, delete, move, extract, blank, importself, importpair) -- the same battery order the
seventh, eighth and ninth runs used, each stage run with all four corpora concurrent (pages kept
the eighth/ninth run's own split: private+public together at `--jobs 8`/`--jobs 6`, then
Canadian+UN together at `--jobs 8`). Total wall time for all three batteries, all four corpora:
about 30 minutes -- far short of "hours," worth noting for planning the next one.

**Zero crashes and zero hangs, in every battery, on every corpus. 5,763 documents, nothing
silently narrowed:**

| corpus | on disk | visited | opened (structure/markdown) | the rest |
|---|---:|---:|---:|---|
| private | 4,158 | 4,158 | 4,084 | 12 encrypted, 62 unreadable format |
| public | 1,381 | 1,381 | 1,368 | 5 encrypted, 8 unreadable format |
| Canadian | 134 | 134 | 134 | — |
| UN | 90 | 90 | 90 | — |

Identical to the seventh run's own table on every corpus. `pages-battery.sh` opens through a
different path and so counts differently, as every prior run has noted: private 4,084 opened (12
protected, 62 did-not-open, matching the table above exactly), public 1,374 opened (4 protected,
3 did-not-open, plus 5 of those 1,374 separately flagged `input already damaged` -- failing
their own `qpdf --check` before `megapdf-cli` ever touched them, a #445-point-4 case, not a
smaller opened count), Canadian and UN both 134/90 with nothing flagged.

**Structure, against F1 >= 0.998 and order agreement tau >= 0.9:**

| corpus | token F1, internal | token F1, CLI | tau median | CLI bad exits | |
|---|---:|---:|---:|---:|---|
| private | **0.998997** | 0.998997 | 0.951 | 0 | pass |
| public | **0.974441** | 0.974441 | 0.977 | 0 | **fail — #498, confirmed** |
| Canadian | **0.999932** | 0.999932 | 0.994 | 0 | pass |
| UN | **0.999136** | 0.999136 | 0.967 | 0 | **pass — moved, see below** |

Markdown: every gate passes on all four corpora (0 crashes, 0 hangs, 0 other bad exit codes, 0
cmark parse failures, 0 cmark timeouts).

**Pages** (0 crashes/hangs, 0 qpdf failures, 0 count mismatches, 0 write failures, 0 refusals
outside the contract's own two: a security-forbidden operation, exit 8, and the field-`/Parent`-
hierarchy refusal, exit 9 with that message, reachable from both `extract` and `import`):

| corpus | crashes/hangs | QPDF FAILED | COUNT MISMATCH | WRITE FAILED | REFUSED, other | |
|---|---:|---:|---:|---:|---:|---|
| private | 0/0 | 0 | 0 | 0 | 0 | pass |
| public | 0/0 | 0 | 0 | 0 | **9** | **fail — #445, confirmed** |
| Canadian | 0/0 | 0 | 0 | 0 | 0 | pass |
| UN | 0/0 | 0 | 0 | 0 | 0 | pass |

Field-`/Parent`-hierarchy refusal, through `import` (the probe #567 established; `extract`
structurally cannot reach it on this PDFium, per the ninth run):

| corpus | importself refused-fields | rate | importpair refused-fields | rate |
|---|---:|---:|---:|---:|
| private | 37 / 4,084 | **0.91%** | 0 / 4,084 | 0% |
| public | 212 / 1,374 | **15.43%** | 0 / 1,374 | 0% |
| Canadian | 5 / 134 | 3.7% | 4 / 134 | 3.0% |
| UN | 0 / 90 | 0% | 0 / 90 | 0% |

Every one of these numbers matches the ninth run exactly, document-for-document count, not just
in aggregate -- this is the instrument confirming the same known state, not coincidence.

**Public's structure failure resolves to the same category #498 already named, confirmed rather
than rediscovered.** The 33 `govinfo-signed` documents carry 95.6% of the corpus's tokens (the
brief's "96%" holds) and score **0.973305** on their own (#498 recorded 0.970684); the remaining
documents score 0.999206. Public's overall figure (0.974441) is up from the seventh run's
0.971952 by almost exactly the same margin the signed category itself moved by (+0.0026 signed,
+0.0025 overall) -- one population moving, not two. Not fixed or gate-adjusted, per #471's own
instruction. A plausible contributor is #595's masked-hyphen normalisation (fewer spurious token
splits would read as a small, broad fidelity gain of exactly this shape), but that is offered as
a plausible direction, not confirmed root cause -- nothing here isolates it from #532 or #587.

**Public's nine `REFUSED, other` are #445's known pair, confirmed exactly, not re-filed.** This
run used `pages-battery.sh`'s full seven-operation shape (#567), the same as the ninth run, not
the five-operation shape the eighth run and the task brief's own "known results" both describe
-- so nine, not five, is the right number to expect here. Resolved by path-hash (never by name)
to the same shape the ninth run found: the same two primary documents refuse `rotate`/`move`/
`extract`/`importself` on their own unloadable pages (7 instances between them), and two
unrelated, otherwise-clean documents refuse `importpair` only because their fixed partner
happens to be one of those two (2 instances) -- four distinct documents, nine refusals, matching
the ninth run's own accounting of this exact pair down to the operation.

**UN's gate moved from fail to pass, and it is #524 fixed, not a new result.** The seventh run
measured UN at 0.996348 (fail); this run measures **0.999136** (pass). Re-deriving each
document's own F1 from the battery log's per-document token counts (`fid_a`/`fid_b`/`fid_match`,
keyed by the same path-hash id the log already uses) finds the previously-worst document -- the
190-page one the seventh run's own text named as #524's and #496's -- improved from F1 0.9662 to
**0.9935**; it is no longer even the corpus's worst (an unrelated 148-page document is, at
0.9853, nowhere near failing the gate by itself). Excluding that formerly-worst document, UN
reads 0.999295, itself slightly above the seventh run's own exclusion figure of 0.99960 -- a
small, general improvement on top of the specific one. This is not a mystery: `git log` shows PR
#532 (`wip/524-drawn-em-font-size`, merge commit `6e67296`) landed after the seventh run's commit
(`88117b7`) and is present at this run's `c3cc720`; its own fix commit's message is `structure:
lock the size-in-Tf/size-in-Tm invariant, and retire #496's caveat (#524)`. The brief's own
"known results to confirm" section still named UN as failing at ~0.9963 because that is what the
last full-corpus measurement (the seventh run) found, before #532 landed -- this run is simply
the first full-corpus measurement taken after the fix, and it confirms the fix rather than
finding a new problem. Worth Dave's attention as a candidate to close #524, not done here.

**Large documents (#488) are not present in this run's public corpus and nothing here can move
or reconcile against the other agent's figure.** This corpus's public-corpus mount is the
1,381-document, seven-category state (`govinfo-signed`, `irs`, `nonlatin`, `pdfium`, `qpdf`,
`uscis`, `verapdf`) that predates the `large`/`govinfo-large` addition TESTING.md records
separately; there is no `large` population mounted here to measure. Noted so the absence is not
mistaken for a silent pass on #488's own figure.

**Dead-handle contract rewrite (#578, 47 entry points): no lifetime regression at corpus
scale.** Zero crashes and zero hangs across all three batteries, all four corpora, 5,763
documents, every `pages-battery.sh` operation including `blank` and both `import` variants --
the broadest exercise of entry-point lifetimes this repository's battery has driven since #578
landed.

**#587's two new contract functions are not directly exercised by name in any of these three
batteries** (no battery script calls either one), so this run speaks to the absence of a
corpus-wide regression elsewhere after they landed, not to their own correctness -- that remains
whatever #587's own tests already cover.

No issue filed from this run: the one genuinely new finding (#524, via #532) is a fix confirmed,
not a regression, and everything else measured is #445 or #498 confirmed exactly rather than
rediscovered.

### Eleventh run, 2026-10-01: all three batteries, all four corpora, over the staged corpus, `wip/609-stage-and-measure` at 993ba98 (#609, #612)

**The first run over a public corpus that says what it is a count of, and the first over the
two populations that were added to the manifest because they found defects.** Dave's decision
on #609 was to stage the missing documents rather than redefine the condition, so this run is
the measurement after staging: #612 fixed the fetch, 112 of the 115 missing documents were
staged on k3's `~/pdf-public`, and all three batteries ran over the result. Run on kdocker3 in
a container (`megapdf:base`, PDFium still at 33 patches,
`pdfium-7934-megapdf-2b3b415e86b1`), all four corpora through `structure-battery.sh
--reference --cli`, then `markdown-battery.sh`, then `pages-battery.sh` (#567's full
seven-operation shape), in the tenth run's own order and concurrency: structure and markdown
with all four corpora at once, pages as private+public (`--jobs 8`/`--jobs 6`) then
Canadian+UN (`--jobs 8`). About 55 minutes for everything, against the tenth run's 30 -- the
whole of the difference is the seven large documents.

**Zero crashes and zero hangs, in every battery, on every corpus. 5,875 documents:**

| corpus | on disk | visited | opened (structure/markdown) | the rest |
|---|---:|---:|---:|---|
| private | 4,158 | 4,158 | 4,084 | 12 encrypted, 62 unreadable format |
| public | **1,493** | **1,493** | **1,480** | 5 encrypted, 8 unreadable format |
| Canadian | 134 | 134 | 134 | — |
| UN | 90 | 90 | 90 | — |

Private, Canadian and UN are identical to the tenth run on every line. Public is +112
documents and +112 opened: **every one of the 112 newly staged documents opened**, on both
paths, with nothing newly encrypted, unreadable, crashing or hanging. `pages-battery.sh`
counts differently as always: private 4,084 opened (12 protected, 62 did-not-open), public
**1,486** opened (4 protected, 3 did-not-open, 5 separately flagged `input already damaged`),
Canadian and UN 134/90 with nothing flagged.

**What the corpus actually was, printed by the run itself (#609's cheap half, and the part
that stops this recurring).** Every battery summary now ends with a coverage block from
`tools/stress/corpus_coverage.sh`. Public's, verbatim in substance:

| | |
|---|---:|
| manifest rows | 1,777 |
| rows present on disk | 1,494 |
| rows the `*.pdf` walk reaches — **the population every number below is over** | **1,493** |
| staged but not reached (`.Pdf`, qpdf) | 1 |
| stated exclusion: `wiki-*` non-Latin, deferred by Dave decision | 280 |
| not accounted for: `uk-hmrc`, source re-served different bytes (#625) | 3 |

Coverage **1,493 of 1,777 (84.0%), PARTIAL** -- against the tenth run's unstated 1,381 of
1,777 (77.7%). The three corpora with no manifest print one line saying so rather than
nothing, because an absent line reads as "nobody checked". Nothing in the block gates: a
partial corpus is a fact about the machine, not a regression in the code under test.

**Structure, against F1 >= 0.998 and order agreement tau >= 0.9:**

| corpus | token F1, internal | token F1, CLI | tau median | CLI bad exits | |
|---|---:|---:|---:|---:|---|
| private | **0.998997** | 0.998997 | 0.951 | 0 | pass |
| public | **0.980019** | 0.980019 | 0.978 | 0 | **fail — #498, confirmed** |
| Canadian | **0.999932** | 0.999932 | 0.994 | 0 | pass |
| UN | **0.999136** | 0.999136 | 0.967 | 0 | pass |

Markdown: every gate passes on all four corpora (0 crashes, 0 hangs, 0 other bad exit codes,
0 cmark parse failures, 0 cmark timeouts), public now over 1,493 documents rather than 1,381.

**Pages** (0 crashes/hangs, 0 qpdf failures, 0 qpdf timeouts, 0 count mismatches, 0 write
failures, 0 refusals outside the contract's own two):

| corpus | crashes/hangs | QPDF FAILED | COUNT MISMATCH | WRITE FAILED | REFUSED, other | |
|---|---:|---:|---:|---:|---:|---|
| private | 0/0 | 0 | 0 | 0 | 0 | pass |
| public | 0/0 | 0 | 0 | 0 | **9** | **fail — #445, confirmed** |
| Canadian | 0/0 | 0 | 0 | 0 | 0 | pass |
| UN | 0/0 | 0 | 0 | 0 | 0 | pass |

**Public's structure failure is #498 unchanged, and the aggregate's move from 0.974441 to
0.980019 is the staging, attributed to the token.** Resolved by manifest `source` from the
battery log's own per-document `fid_match`/`fid_a`/`fid_b`:

| source | docs | tokens | token F1 |
|---|---:|---:|---:|
| `govinfo-signed` | 33 | 10,041,571 | **0.973305** |
| `govinfo-large` | 7 | 5,331,327 | **0.989955** |
| `irs` | 136 | 266,159 | 0.998664 |
| `uscis` | 50 | 172,827 | 0.999991 |
| `uk-dwp` | 24 | 95,980 | **0.999984** |
| `uk-homeoffice` | 22 | 89,862 | **0.999900** |
| `uk-hmrc` | 59 | 82,508 | **0.999558** |
| `verapdf` | 860 | 10,400 | 0.999567 |
| `qpdf` | 174 | 6,053 | 1.000000 |
| `pdfium` | 115 | 1,730 | 0.999133 |

`govinfo-signed` measures **0.973305** -- the tenth run's figure to six decimal places, on the
same 33 documents, which is #498 confirmed and not rediscovered. **Restricting to the tenth
run's own population (dropping `govinfo-large` and the three `uk-*` sources) reproduces
0.974441 exactly, over 1,368 documents and 10,498,740 tokens -- the tenth run's public figure,
token for token.** So the aggregate did not move because anything changed in the engine; it
moved because 5.33 M tokens scoring 0.989955 joined a population of 10.5 M scoring 0.974441.
`govinfo-large` measures **0.989955**, which is #488's own recorded figure to six decimal
places: that category is understood (97.1% of its deficit is line-wrap hyphens that never
join, because line building merges lines across the column gutter), #488 is out of the
milestone with the bar for a future attempt written down, and **nothing was attempted here**.
Public still fails the gate, by less, for exactly the reason it failed before.

**The UK forms had never been measured, and this is what they produced.** All three sources
pass the fidelity gate with room to spare -- 0.999984, 0.999900, 0.999558, the best scores of
any real-document source in the corpus and comfortably above `irs` (0.998664). They
contributed **zero** crashes, hangs, qpdf failures, count mismatches, write failures and zero
`REFUSED, other`. The one thing they did produce is field-hierarchy refusals, below. #479 (the
divergence between the internal path and the command line that this population originally
found) does not reappear: internal and CLI F1 are identical to six decimals on every corpus,
public included.

**The seven large documents are clean on every operation.** 7 seen, 7 opened, zero refusals of
any kind, zero timeouts -- `pages-battery.sh`'s 300s bound was not approached by any operation
on a 2.07 GB document, which is what `tools/stress/public-corpus/README.md`, "Very large
documents", already recorded from a separate run and is now true in a battery that measured
them alongside everything else.

**Public's nine `REFUSED, other` are #445's known pair, confirmed exactly.** Resolved by
manifest source rather than by name: `pdfium` 7 (rotate 2, move 1, extract 2, importself 2 --
the two primary documents whose own pages the engine cannot load) and `verapdf` 2 (importpair
only -- two otherwise-clean documents whose fixed pairing partner happens to be one of those
two). Four distinct documents, nine refusals, the ninth and tenth runs' accounting down to the
operation. None of the 112 newly staged documents is among them.

**Field-`/Parent`-hierarchy refusal, through `import` only -- and the UK forms moved it, as
#609 predicted they would:**

| corpus | importself refused-fields | rate | importpair | rate |
|---|---:|---:|---:|---:|
| private | 37 / 4,084 | **0.91%** | 0 / 4,084 | 0% |
| public | **230 / 1,486** | **15.48%** | 0 / 1,486 | 0% |
| Canadian | 5 / 134 | 3.7% | 4 / 134 | 3.0% |
| UN | 0 / 90 | 0% | 0 / 90 | 0% |

Private (0.91%), Canadian (5 and 4) and UN (0) are the tenth run's numbers exactly. Public
rose from 212 to 230, and **all 18 of the increase are UK forms**: `uk-dwp` **11 of 24
(45.8%)**, `uk-homeoffice` **5 of 22 (22.7%)**, `uk-hmrc` **2 of 59 (3.4%)**. The seven large
documents produced none. The rate order is worth noting rather than burying: DWP's 45.8% is
the second-highest of any source in the corpus after IRS's 100% (136 of 136), and these are
numbered-question benefit forms -- the shape #609 said was "exactly the population that
exposed the list-marker bug in the measure". It is contract behaviour (exit 9, a refusal
rather than a corruption, since #452/#463) and so not a gate failure and not re-filed, but
somebody should decide whether a one-in-two refusal rate on real UK benefit forms is
acceptable product behaviour, which is a question for Dave and not for this run.
`extract`-side refusals remain **zero on all four corpora**, which is the ninth run's finding
re-confirmed over a 112-document-larger population: past PDFium patch 33, extracting pages
into a brand-new document has no pre-existing field name for the collision check to trip
over, so `extract` structurally cannot reach this refusal and `import` can. The zero is the
contract, not a dead probe.

**`fetch.sh` was fixed first, because #609 could not be done without it (#612).** `--max-time
300` aborted the two multi-gigabyte rows at around 875 MB on four attempts out of four, so the
command the manifest's README names could not fetch the manifest's own corpus. Replaced with a
low-speed abort and resumption. Proved on the real rows, not only in the test: the 1.57 GB row
was carried past 1.53 GB, the run was killed there deliberately, and the re-run resumed from
exactly that offset and transferred only the remaining 34,122,108 bytes before its manifest
sha256 verified; the 2.07 GB row's first single `curl` invocation ran unbroken past 1.07 GB
over about ten minutes -- more than three times the old wall clock, at a size the old wall
clock could never have reached. All 7 landed and verified. 112 of the 115 missing documents
staged; the other three are #625, three `uk-hmrc` rows whose source now serves different
bytes, each refused on its own by the rule that a pinned hash is never re-fetched over (#525).

**One issue filed: #625.** Everything else is confirmation. #498 is confirmed to six decimals
on the same 33 documents; #445 is confirmed to the operation on the same four; #488's figure
is confirmed to six decimals and explicitly not attempted; #567's refusal measure is
confirmed on private, Canadian and UN and extended on public; #479 does not reappear. The
release-candidate condition in #537 that a full corpus battery be green still cannot be
claimed -- but for the first time the reason is written in the run's own output rather than
inferred from a count nobody could interpret.

### Twelfth run, 2026-10-01: pre-flight for 2.2.0, all three batteries, all four corpora, main at c108240

**Not the §2.2 release gate.** The 2.2.0 version bump and two feature PRs were still landing
at the time of this run, so this is not a run against a release commit and the checklist
issue should not cite it as the gate. It is an early-warning pass, asked for so a regression
would be found now rather than after the release commit exists; the gate run against the
actual release commit is separate and still to come.

**Run on kdocker3, not kdocker2.** The task brief named kdocker2, but the three non-private
corpora (`~/pdf-public`, `~/pdf-test-ca`, `~/pdf-test-un`) are staged on kdocker3, not
kdocker2 -- kdocker2 holds only the private corpus mirror and has no `/data/megapdf-work`
role in corpus batteries at all (`CLAUDE.md`'s own machine table: kdocker3 is "general
builds, corpus batteries"; kdocker2 is Android only). This looks like the same k2/k3
mix-up noted elsewhere. Run in `megapdf:base` (PDFium still at 33 patches,
`pdfium-7934-megapdf-2b3b415e86b1`, the same image the tenth and eleventh runs used), built
fresh from `origin/main` at `c108240` (`megapdf-cli --version` reports `2.1.1`, confirming
the version bump has not landed). Scratch workspace and container removed afterward; the four
corpora were only read.

**Compared directly against the eleventh run (993ba98 -> 77 commits -> c108240), not just
against the gates**, per the brief: every structure, markdown and pages number below is the
eleventh run's own number, to six decimal places on every F1 and exactly on every count. 77
commits landed on `main` between the two runs (iOS whiteout/signature tools, the Windows/
Android screenshot slots, the scroll-position indicator, the NDK pin, the French review,
this file's own `agent-guide`), none of which touched anything this battery measures.

**Zero crashes and zero hangs, in every battery, on every corpus. 5,875 documents, identical
to the eleventh run on every corpus:**

| corpus | on disk | visited | opened (structure/markdown) | the rest |
|---|---:|---:|---:|---|
| private | 4,158 | 4,158 | 4,084 | 12 encrypted, 62 unreadable format |
| public | 1,493 | 1,493 | 1,480 | 5 encrypted, 8 unreadable format |
| Canadian | 134 | 134 | 134 | — |
| UN | 90 | 90 | 90 | — |

**Structure, against F1 >= 0.998 and order agreement tau >= 0.9 -- matches the eleventh run to
six decimal places on every corpus:**

| corpus | token F1, internal | token F1, CLI | tau median | CLI bad exits | |
|---|---:|---:|---:|---:|---|
| private | **0.998997** | 0.998997 | 0.951 | 0 | pass |
| public | **0.980019** | 0.980019 | 0.978 | 0 | **fail — #498, confirmed, unmoved** |
| Canadian | **0.999932** | 0.999932 | 0.994 | 0 | pass |
| UN | **0.999136** | 0.999136 | 0.967 | 0 | pass |

**Markdown: every gate passes on all four corpora** (0 crashes, 0 hangs, 0 other bad exit
codes, 0 cmark parse failures, 0 cmark timeouts) -- private 4,158 visited/2,982 extracted/
1,102 textless/12 password-gated; public 1,493/937/543/5; Canadian 134/134/0/0; UN 90/83/7/0.
All four match the eleventh run's own figures exactly.

**Pages** (0 crashes/hangs, 0 qpdf failures, 0 count mismatches, 0 write failures, 0 refusals
outside the contract's own two), matching the eleventh run exactly on every cell:

| corpus | crashes/hangs | QPDF FAILED | COUNT MISMATCH | WRITE FAILED | REFUSED, other | |
|---|---:|---:|---:|---:|---:|---|
| private | 0/0 | 0 | 0 | 0 | 0 | pass |
| public | 0/0 | 0 | 0 | 0 | **9** | **fail — #445, confirmed, unmoved** |
| Canadian | 0/0 | 0 | 0 | 0 | 0 | pass |
| UN | 0/0 | 0 | 0 | 0 | 0 | pass |

Public's opened count (the pages path counts differently, as every prior run notes): 1,486
opened (4 protected, 3 did-not-open, 5 of those separately flagged `input already damaged`).
Private: 4,084 opened (12 protected, 62 did-not-open). Canadian/UN: 134/90, nothing flagged.
The nine public `REFUSED, other` are #445's known pair (`pdfium` 7: rotate 2, move 1,
extract 2, importself 2; `verapdf` 2: importpair only) -- the same four documents, nine
refusals, the ninth/tenth/eleventh runs' own accounting.

Field-`/Parent`-hierarchy refusal, through `import` only:

| corpus | importself refused-fields | rate | importpair refused-fields | rate |
|---|---:|---:|---:|---:|
| private | 37 / 4,084 | **0.91%** | 0 / 4,084 | 0% |
| public | 230 / 1,486 | **15.48%** | 0 / 1,486 | 0% |
| Canadian | 5 / 134 | 3.7% | 4 / 134 | 3.0% |
| UN | 0 / 90 | 0% | 0 / 90 | 0% |

Every one of these numbers is the eleventh run's own, unchanged.

**Coverage arithmetic reconciles exactly as stated in the brief, and matches what the eleventh
run's own block plus #625 together predict:** 1,493 reachable + 1 staged-but-unreached (the
`.Pdf` case-mismatch qpdf fixture) + 283 stated exclusion (280 `wiki-*`, deferred by Dave
decision, plus 3 `uk-hmrc` rows resolved as permanently unfetchable by #625) + 0 unexplained
= 1,777 manifest rows. `corpus_coverage.sh`'s own block (printed identically by the structure,
markdown and pages summaries) lists every exclusion by name; nothing is silently absent.

**Nothing moved since the eleventh run.** Every gate verdict, every F1 to six decimals, every
crash/hang/refusal count and the coverage arithmetic are identical to the eleventh run across
all three batteries and all four corpora, over 77 intervening commits. The two known,
tracked failures (#498 on the public structure gate, #445's nine `REFUSED, other` on the
public pages battery) are confirmed unchanged and are not new findings. No issue filed from
this run. This is a pre-flight only: `docs/RELEASING.md` §2.2's gate run still has to happen
against the actual release commit once the version bump and the two in-flight feature PRs
land.

### Thirteenth run, 2026-10-02: the §2.2 release gate for 2.2.0, all three batteries, all four corpora, `main` at 983fcdf

**This is the gate, not another pre-flight.** The release commit is `main` at `983fcdf`
(merge of #646, `wip/version-2-2-0` — "Version 2.2.0 everywhere"), the commit Dave has called
the 2.2.0 release candidate. `megapdf-cli --version` in the built tree reports `megapdf-cli
2.2.0`, confirming the bump is live on the commit under test.

**Run on kdocker3, not kdocker2, same as the twelfth run and for the same reason.**
`docs/RELEASING.md` §2.2 still names kdocker2; `CLAUDE.md`'s own machine table still says
kdocker3 is "general builds, corpus batteries" and kdocker2 is Android-only with no
`/data/megapdf-work` role in a corpus battery at all. `~/pdf-public`, `~/pdf-test-ca` and
`~/pdf-test-un` exist only on kdocker3. Noted here rather than silently switched, as the
twelfth run's own entry did; the runbook's §2.2 heading and body text could be corrected to
say kdocker3 in one small edit, but that is a documentation fix for its own change, not a
side effect of a gate run, so it is named here and left alone.

Built fresh in a throwaway `megapdf:base` container (Ubuntu 24.04, build-essential/cmake/
ninja/qpdf/poppler-utils/cmark) from a clean clone of `983fcdf`, repo and all four corpora
mounted read-only except the repo's own build tree: `tools/fetch-pdfium-linux.sh` pulled the
same pinned release the eleventh and twelfth runs used (`pdfium-7934-megapdf-2b3b415e86b1`,
33 patches, confirmed by the configure log), then `cmake -S core -B core/build/linux-x64 -G
Ninja -DCMAKE_BUILD_TYPE=Release -DMEGAPDF_CORE_TESTS=ON -DMEGAPDF_FIXTURES_DIR=...` and
`cmake --build ... --target megapdf_cli megapdf_structure_check`. All four corpora read
1,493/134/90/4,158 documents on disk, matching the eleventh/twelfth runs exactly before a
single document was opened. Container and workspace removed afterward (the build tree was
root-owned inside the container; a throwaway `alpine` container did the final `rm -rf` after
`docker rm` left files behind); the corpora were only read.

**`tools/stress/compare_runs.py` does not apply to this battery shape, and the git diff between
the two commits confirms why a bit-for-bit match was the only sane expectation.**
`compare_runs.py` reads a `MegaPDF.Stress run --out .../edits` directory's `results.jsonl`
(the edit-battery harness); `structure-battery.sh`, `markdown-battery.sh` and
`pages-battery.sh` each write their own plain-text `.log`, as they have since the seventh run
established "all three batteries" as structure/markdown/pages, not structure/markdown/edits.
So the comparison against the twelfth run below is done the way the twelfth run compared
itself against the eleventh: number for number, against this file's own recorded figures.
`git diff --stat c108240 983fcdf` touches 69 files over 23 commits (the signature-strip parity
#641, the iOS cold-start deadline #599/#643, the scroll-position indicator, Fable's French
review, the MS Store certification notes #647, and the version bump itself) and exactly one
line inside `core/` or `libs/pdfium/`: `core/cli/megapdf_cli.cpp`'s version string,
`"megapdf-cli 2.1.1"` to `"megapdf-cli 2.2.0"`. `tools/stress/` itself is byte-identical
between the two commits. Nothing in the diff touches engine behaviour, so an exact match on
every figure below is the expected result, not a coincidence to be explained.

**Zero crashes and zero hangs, in every battery, on every corpus. 5,875 documents, identical
to the eleventh and twelfth runs on every corpus:**

| corpus | on disk | visited | opened (structure/markdown) | the rest |
|---|---:|---:|---:|---|
| private | 4,158 | 4,158 | 4,084 | 12 encrypted, 62 unreadable format |
| public | 1,493 | 1,493 | 1,480 | 5 encrypted, 8 unreadable format |
| Canadian | 134 | 134 | 134 | — |
| UN | 90 | 90 | 90 | — |

**Structure, against F1 >= 0.998 and order agreement tau >= 0.9 — matches the twelfth run to
six decimal places on every corpus:**

| corpus | token F1, internal | token F1, CLI | tau median | CLI bad exits | |
|---|---:|---:|---:|---:|---|
| private | **0.998997** | 0.998997 | 0.951 | 0 | pass |
| public | **0.980019** | 0.980019 | 0.978 | 0 | **fail — #498, confirmed, unmoved** |
| Canadian | **0.999932** | 0.999932 | 0.994 | 0 | pass |
| UN | **0.999136** | 0.999136 | 0.967 | 0 | pass |

**Markdown: every gate passes on all four corpora** (0 crashes, 0 hangs, 0 other bad exit
codes, 0 cmark parse failures, 0 cmark timeouts) — private 4,158 visited/2,982 extracted/
1,102 textless/12 password-gated; public 1,493/937/543/5; Canadian 134/134/0/0; UN 90/83/7/0.
All four match the eleventh and twelfth runs' own figures exactly.

**Pages** (0 crashes/hangs, 0 qpdf failures, 0 count mismatches, 0 write failures, 0 refusals
outside the contract's own two), matching the twelfth run exactly on every cell:

| corpus | crashes/hangs | QPDF FAILED | COUNT MISMATCH | WRITE FAILED | REFUSED, other | |
|---|---:|---:|---:|---:|---:|---|
| private | 0/0 | 0 | 0 | 0 | 0 | pass |
| public | 0/0 | 0 | 0 | 0 | **9** | **fail — #445, confirmed, unmoved** |
| Canadian | 0/0 | 0 | 0 | 0 | 0 | pass |
| UN | 0/0 | 0 | 0 | 0 | 0 | pass |

Public's opened count (the pages path counts differently, as every prior run notes): 1,486
opened (4 protected, 3 did-not-open, 5 of those separately flagged `input already damaged`).
Private: 4,084 opened (12 protected, 62 did-not-open). Canadian/UN: 134/90, nothing flagged.
The nine public `REFUSED, other` are #445's known pair (`pdfium` 7: rotate 2, move 1,
extract 2, importself 2; `verapdf` 2: importpair only) — the same four documents, nine
refusals, the ninth/tenth/eleventh/twelfth runs' own accounting.

Field-`/Parent`-hierarchy refusal, through `import` only:

| corpus | importself refused-fields | rate | importpair refused-fields | rate |
|---|---:|---:|---:|---:|
| private | 37 / 4,084 | **0.91%** | 0 / 4,084 | 0% |
| public | 230 / 1,486 | **15.48%** | 0 / 1,486 | 0% |
| Canadian | 5 / 134 | 3.7% | 4 / 134 | 3.0% |
| UN | 0 / 90 | 0% | 0 / 90 | 0% |

Every one of these numbers is the eleventh and twelfth runs' own, unchanged.

**Coverage arithmetic reconciles exactly as before, printed identically by all three battery
summaries:** 1,493 reachable + 1 staged-but-unreached (the `.Pdf` case-mismatch qpdf fixture)
+ 283 stated exclusion (280 `wiki-*`, deferred by Dave decision, plus 3 `uk-hmrc` rows
resolved as permanently unfetchable by #625) + 0 unexplained = 1,777 manifest rows.
`corpus_coverage.sh`'s own block lists every exclusion by name; nothing is silently absent.

**Gates, per `docs/RELEASING.md` §2.2 and this task's own checklist — pass on every corpus
except the two already-tracked, out-of-milestone exceptions, unmoved from the twelfth run:**
structure F1 >= 0.998 (private/Canadian/UN pass; public fails at 0.980019, #498); order tau
>= 0.9 (all four pass); zero crashes (pass, all batteries, all corpora); zero hangs (pass, all
batteries, all corpora); zero out-of-contract refusals (private/Canadian/UN pass; public has
the same nine #445 `REFUSED, other`, none outside the known pair); the markdown battery's own
checks (pass on all four corpora); the pages battery's own checks other than the refusal count
above (0 qpdf failures, 0 count mismatches, 0 write failures on all four corpora).

**Nothing moved since the twelfth run.** Every gate verdict, every F1 to six decimal places,
every crash/hang/refusal count and the coverage arithmetic are identical to the twelfth run
(itself identical to the eleventh), across all three batteries and all four corpora, over the
23 commits that separate `c108240` from `983fcdf` — which the `core`/`libs/pdfium` diff above
shows touched no engine code but one version string. The two known, tracked failures (#498 on
the public structure gate, #445's nine `REFUSED, other` on the public pages battery) are
confirmed unchanged for the fourth consecutive full-corpus run and are not new findings. No
issue filed from this run; no regression found. This is the §2.2 release gate against the
actual 2.2.0 release commit, and it is green except for the two conditions #498 and #445
already carry outside milestone 2.2.

## Reporting

For each issue: what you clicked, what you expected, what happened, and the PDF
(or a screenshot) if you can share it. "This felt confusing" is a valid report.
