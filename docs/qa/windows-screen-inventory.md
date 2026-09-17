# Windows app: screen inventory (#146 §1b)

Every window, dialog, flyout, menu, mode, notice, busy state and error the WinUI app
(`src/MegaPDF.App`) can show, as a checklist for the QA pass and the store captures.
It was built from the source on 2026-09-17 (main `ee2751d`). Keep it in step when a
surface is added or removed.

**How each one is reached:**
- **R** — the offscreen render: `MegaPDF.exe <pdf> --screenshot out.png --window WxH [--language tag] [--theme dark] [--screenshot-state s]`. It needs no desktop, but draws no popups. `D:\megapdf-qa\capture.ps1` batches it.
- **H** — the UI Automation harness in `tools/screenshots-windows` (a real window).
- **M** — by hand, or by a script written for that surface.

**Language and packaging:** strings with an `x:Uid` (the Find box placeholder, the signature panel's Draw / Type / From photo, Settings) follow the language only in the **packaged** build. The unpackaged dev build shows them in English whatever `--language` says (`AppLanguage.cs`). French checks therefore use the installed package, with `"Language"` set in `%LOCALAPPDATA%\MegaPDF\settings.json`.

Check every surface in en, fr-CA and fr-FR, light and dark, at effective widths 1280, 1000, 800 and 480 (the minimum), at 200% display scale.

## Windows
- [ ] **Splash** (`SplashWindow`, 2.5 s at launch). M
- [ ] **Main window, empty** (no document): toolbar, empty page area. R `empty`
- [ ] **Main window, document open**: page stack, page indicator pill ("Page 1 of N"), title with the file name, and the unsaved dot on Save. R `doc`
- [ ] **Windows print dialog** (system UI, opened by Print). M
- [ ] **File pickers:** Open, Save as, and the Shrink save picker "Save a smaller copy" (system UI, packaged build only). H

## Toolbar (one CommandBar row, #144)
- [ ] **Primary commands:** Open, Save (with the unsaved dot), Signatures, Add text, Whiteout, Undo, Redo, Zoom out, the zoom level drop-down, Zoom in. At each width, check the labels (right, collapsed) and what moves into More. R `doc` at each width
- [ ] **Font and size pickers** on the row while Add text is armed or a text box is selected. R `mode`, `textbox`
- [ ] **More (…) overflow menu:** Save as, Password…, Print, Shrink for email, Find, separator, Settings, plus any primary commands a narrow window pushed there. H (`more` render state + `--hold`, or `Open-More`)
- [ ] **Zoom menu:** Actual size, Fit width, Fit page, separator, preset levels. H (`Click-Zoom`)
- [ ] **Disabled states:** every editing command off on a restricted document (#131), and everything off while busy. M

## Flyouts and panels
- [ ] **Signatures flyout** (`SignatureLibraryPanel`): heading, "No signatures yet", signature cards, each card's "…" menu (Rename, Delete), and Draw / Type / From photo. R `sign`, H
- [ ] **Settings flyout:**
  - Check mark style (cross, check, filled square)
  - Theme (system, light, dark)
  - Language (system, English, Français (Canada), Français (France)) with the restart note
  - Reopen last file, and Flatten on save
  - About: version, license, copyright, thanks, GitHub link, Third-party notices link
  
  H
- [ ] **Find bar:** query box, previous, next, close, match count and highlighted hits. R `find`

## Modes and in-page states
- [ ] **Add text mode:** placement banner ("click where…") with Cancel, and the pickers on the row. R `mode`
- [ ] **Signature placement mode:** placement banner, the signature following the pointer, then the placed signature with its handles. H
- [ ] **Whiteout mode:** banner, dragging a rectangle, the covered area. H
- [ ] **Inline text editor** on the document's own text: the edit box, commit with Enter, Esc to cancel, the dashed underline on an edited run. H (`Shot-TextEdit.ps1`)
- [ ] **Added text box:** selected, editing, moved, with font and size. R `textbox`
- [ ] **Check marks:** ticking a printed box in each mark style. H
- [ ] **Form fields:** text entry, checkbox and radio on `forms.pdf` and `review-form.pdf`. H

## Busy states (#145)
- [ ] **Busy strip** under the toolbar (indeterminate bar with its label): opening, saving, checking the saved file, searching, shrinking, printing, restoring, applying, checking a page. R `busy`
- [ ] **Page-level spinner** over the page a change waits on. R `busy-page`

## Notices (InfoBars)
- [ ] **Font notice:** an edit used a substituted font. M
- [ ] **Scanned-page hint:** the page has no text to edit. M
- [ ] **Restricted notice** with an Unlock button (the owner doesn't allow changes, #131). M, `encrypted`/restricted fixtures
- [ ] **Recovery-off notice:** recovery is off for a document opened with a password. M
- [ ] **Security notice:** password set, changed or removed. M
- [ ] **Placement hint** while placing. H
- [ ] **Default-app card**, "Make MegaPDF your PDF app?", with its Choose default apps button: first run, once. M

## Dialogs (ContentDialog)
- [ ] **Save changes to "file"?** on close or when opening another file with unsaved changes. M
- [ ] **Restore unsaved changes?** after a crash or kill (`RestoreTitle`). M
- [ ] **Password dialogs:**
  - Password required, including the wrong-password retry text (`PasswordRequiredTitle`)
  - Unlock with the owner password (`UnlockTitle`)
  - Set password, with new and confirm fields, empty and mismatch errors (`SetPasswordTitle`)
  - Document password, choosing Change or Remove (`DocumentPasswordTitle`)
  - Change password (`ChangePasswordTitle`)
  
  M
- [ ] **Page rewrite warning** before an edit that rewrites the page (`PageRewriteWarningTitle`, #139). M
- [ ] **Signature dialogs:** Type signature, Draw signature, Rename signature, Delete signature "name"? M
- [ ] **Third-party notices** (scrolling text, or the notices-load-failed message). M
- [ ] **Messages** (`ShowErrorAsync`, with OK):
  - Could not open
  - Could not save (with the Save as hint)
  - Save first (before Shrink)
  - Nothing to shrink
  - Smaller copy saved (the before and after sizes)
  - Could not shrink
  - Could not edit
  - Can't edit this text: the layout guard refusals (`CannotEditTextTitle`)
  - Could not add, place or rename a signature
  - Could not restore
  - Could not change security
  - Could not print
  - "This file is too large for MegaPDF to open." (#147)
  
  M

## Flows walked in the QA pass
Open, scroll, zoom, find, tick, sign, add text, cover (whiteout), edit text, undo and redo, save and save as, set and remove protection, shrink, print (to Microsoft Print to PDF), close with unsaved changes, and recovery after the app is killed.

Use `review-form.pdf` (`tools/gen_review_form.py`), the fixtures (`tools/gen_test_fixtures.py`) and the large files in `D:\megapdf-large`. Also do a basic Narrator pass over the toolbar and More.
