# Testing MegaPDF 1.0

Thanks for testing! MegaPDF is a deliberately simple PDF editor: it does four things
— edit text, check boxes, place signatures, save — and tries to do them with zero
learning curve. If you ever feel lost or surprised, that itself is a bug worth reporting.

## Requirements

- Windows 11 (or Windows 10 1809+), **x64** PC (not ARM).
- Nothing else — the package is self-contained.

## Install (one time, ~1 minute)

1. Unzip the `MegaPDF-1.0` folder anywhere.
2. Right-click **`Install-MegaPDF.ps1`** → **Run with PowerShell**.
3. Accept the one **UAC prompt** — this trusts the test build's signing certificate.
   (Store releases won't need this step; it's a test-build-only thing.)
4. MegaPDF appears in the **Start menu**.

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

**Saving** — Ctrl+S overwrites; Save As makes a copy. Close with unsaved changes and
you should always be asked. After saving, open the file in Edge or Adobe to confirm
your edits look right there too.

**Sturdiness** — kill MegaPDF from Task Manager mid-editing; on next launch it should
offer to restore your unsaved changes. Zoom with the toolbar − / + or Ctrl+mouse-wheel.
Scroll through a long document; pages should appear as you reach them.

## Known limitations (not bugs)

- Text in scanned/photographed PDFs can't be edited (there's no OCR — by design).
- Editing text sometimes substitutes a similar font, with a notice — expected when
  the document doesn't embed a complete font.
- Password-protected PDFs show an error instead of a password prompt.
- No page add/remove/reorder, no merging — out of scope for 1.0.

## Reporting

For each issue: what you clicked, what you expected, what happened, and the PDF
(or a screenshot) if you can share it. "This felt confusing" is a valid report.
