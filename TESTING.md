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

## Linux (for contributors — there is nothing to install yet)

The Linux app (#158) builds and runs, but is not packaged, so there is no
download and nothing for a tester to install. Until Flathub happens, trying it
means building it — the README has the steps, and `install.sh` puts it in the
applications menu without root.

Everything above applies once it is open, with two differences worth knowing:

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

## Automated coverage (for contributors, not testers)

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
| 2 | the file could not be opened (missing, unreadable, not a PDF) |
| 3 | a password is required, or the one given is wrong |
| 4 | the document uses a security handler this build cannot open |
| 5 | none of the requested pages had a text layer |
| 6 | `--strict` was given and at least one requested page had no text layer |
| 7 | `--out` could not be written |
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

## Reporting

For each issue: what you clicked, what you expected, what happened, and the PDF
(or a screenshot) if you can share it. "This felt confusing" is a valid report.
