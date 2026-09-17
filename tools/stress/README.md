# Corpus stress harness

Drives `MegaPDF.Core` the way the apps do over every PDF under a directory:
open, size pass, render every page at the viewport scale plus the interaction
regions, whole-document search for a list of terms, zoom extremes, save and
reopen, shrink-candidate decode. One worker process per slot, so a native crash
or hang inside PDFium costs one file, not the run. Written for #92; the first
run's numbers and the issues it produced are recorded there.

    MegaPDF.Stress run --root <dir> --out <private-dir> [--workers 4] [--scale 1.5]
    python3 tools/stress/report.py <run-dir> [<run-dir>] --md report.md
    python3 tools/stress/reference_check.py <run-dir> <corpus-root>

## Large files

A run drives each document from a copy on local disk, so `open_ms` measures parsing
rather than the network. That copy is streamed and the document is opened from its
file (`megapdf_open_file`, #147/#148), read on demand, exactly as the apps open one:
nothing here holds a document's bytes, so a file above the ~2 GB ceiling of a .NET
array is an ordinary file to the harness and the memory figures are the engine's
rather than the harness's own (#157).

`--phases` also accepts `edit` (not in the default set, because it changes the
document): it retypes the first text line of page 1 through the tiered body-text
edit, saves, reopens and checks the new text reads back (#112). The report counts
edits tried, edits that needed a substituted font, edits the engine declined
because PDFium would have disturbed the page's layout (#118; counted as skipped,
not failed), and failures.

`--phases edits` (#127) runs an edit battery instead of one retype: up to nine lines
sampled from the first, middle and last pages, each edited from a fresh copy of the
document through the app's own `LineEditOperation` / `DeleteLineOperation`. The kinds
rotate: the same text, longer, one word, digits and a date, accented, CJK (which must
be refused) and deleting the line. Each item records the guard's verdict, the outcome,
whether the edit reads back after save and reopen, and how far any other line on the
page moved. Kinds, positions and numbers only; no document text.

Protected documents (#131) are their own outcome, never a failure: `encrypted` when a
password is needed, `unsupported-security` when the document uses a security handler
PDFium cannot open. To exercise protected documents instead of only counting them,
point `MEGAPDF_STRESS_UNLOCK_LIST` at a private file with one line per document: its
path relative to `--root`, a tab, then its password. Workers inherit the variable from
`run`; a document opened that way records `"unlocked": true` and goes through every
phase. Nothing from the list is logged or reported. The side-by-side table counts
protected, unlocked and unsupported documents.

One-file diagnostics: `find`, `open-bench`, `inspect`, `flags-bench`.

`dump-text --files a.pdf,b.pdf` prints every page's text runs and visual lines in
the format `core/tests/core_tests.cpp` compares against
(`core/tests/expected/text_runs.txt`). That file is the desktop engine's answer
captured before #106 moved the contract into the shared core; regenerate it only
when the contract is meant to change, from the generated fixtures plus the
micro:bit schematic, on Windows (the fixtures use non-embedded fonts, so glyph
boxes and family names are the Windows substitutes').

Everything written to `--out` contains file names and stays private (the corpus is
the owner's own documents). The report prints indices and numbers only.

## The shared engine core

Since #38 the desktop engine forwards contracts to `core/` (ADR-003), so a run
exercises the shared core on 4,337 real documents through the `IPdfEngine`
adapter with no changes here. As more contracts migrate (#105–#112) the same run
covers them; a before/after comparison is `report.py <old-run> <new-run>`, whose
side-by-side section lists any document whose outcome, page count or hit count
changed.
