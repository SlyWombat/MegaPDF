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
so you can still read what you are about to remove. Click a mark to select it; ✕ or
Delete removes it; Ctrl+Z undoes it. **Nothing is removed until you save.**

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

Large files (#147, #148, #267) are checked in two tiers, because the defects only
show at sizes no repository can hold. `test_xref_entries_are_findable()` runs on every push:
it saves the ordinary fixtures in both of PDFium's cross-reference forms and insists every
in-use entry is a non-negative offset that lands on the `N 0 obj` it names, and that the
cross-reference stream is a conforming object — `/Type /XRef`, closed with `endobj`, an
entry for itself, and a `/Length` that is the length of the stream. `test_large_file_xref()`
makes the same demands of files past 2 and 4 GiB, and runs only where those files exist:

```bash
python3 tools/gen_large_fixtures.py ~/large --only huge-2_5gb        # ~2 min, 2.7 GB
python3 tools/make_xref_stream.py ~/large/huge-4_5gb.pdf ~/large/huge-4_5gb-xrefstream.pdf
MEGAPDF_LARGE_FIXTURES=~/large MEGAPDF_LARGE_SCRATCH=/scratch ./megapdf_core_tests ...
```

It skips with a printed line when `MEGAPDF_LARGE_FIXTURES` is unset, and each case skips
itself when the scratch directory has less room than the copy it is about to write — a full
save is the size of its source and an incremental one is twice that. The saved copies are
deleted unless `MEGAPDF_LARGE_KEEP` is set. They are *not* in CI: generating a 4.5 GB
fixture and writing a 9.7 GB copy is not something to do on every push.

## Reporting

For each issue: what you clicked, what you expected, what happened, and the PDF
(or a screenshot) if you can share it. "This felt confusing" is a valid report.
