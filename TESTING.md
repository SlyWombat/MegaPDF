# Testing MegaPDF 1.0

Thanks for testing! MegaPDF is a deliberately simple PDF editor: it does four things
— edit text, check boxes, place signatures, save — and tries to do them with zero
learning curve. If you ever feel lost or surprised, that itself is a bug worth reporting.

## Requirements

- Windows 11 (or Windows 10 1809+), **x64** PC (not ARM).
- Nothing else — the package is self-contained.

## Install (one time, ~1 minute)

1. Unzip the `MegaPDF-1.0` folder anywhere.
2. Double-click **`Setup.exe`** and follow the prompts.
3. If Windows shows a blue "Windows protected your PC" screen, click
   **More info → Run anyway** (this test build isn't Store-signed yet).
4. Choose **Yes** on the one Windows security prompt.
5. MegaPDF appears in the **Start menu**.

(If Setup.exe is blocked by company policy, the same install is available by
right-clicking `Install-MegaPDF.ps1` → Run with PowerShell.)

To uninstall completely: **Settings → Apps → MegaPDF → Uninstall**.
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

**Updates** — if a newer version is out, a green bar appears at startup offering Update;
click it and MegaPDF downloads and installs the new version in the background, then uses
it next time you open the app (or click Restart now). Turn this off in Settings if you
prefer. (Only works in the installed app, not a dev build.)

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

## Reporting

For each issue: what you clicked, what you expected, what happened, and the PDF
(or a screenshot) if you can share it. "This felt confusing" is a valid report.
