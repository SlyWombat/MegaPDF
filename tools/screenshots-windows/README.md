# Windows Store screenshots — capture harness

Drives the installed MegaPDF package from WSL (via `powershell.exe`) with UI
Automation and synthetic input, and captures the four screenshots the Microsoft
Store listing uses. The iOS and Android equivalents run in CI
(`.github/workflows/ios-screenshots.yml`, `android/scripts/capture-screenshots.sh`);
this one needs a real Windows desktop, so it runs locally.

Output goes to `artifacts/store/screenshots/` (gitignored — these are upload
assets, not repo content). Override with `$env:MEGAPDF_SHOTDIR`.

## Run order

Each script drives one step against the already-running app, so you can inspect
the result before continuing. Paths must be **Windows** paths.

    # 0. staging documents (regenerate before EVERY re-shoot — see below)
    python3 tools/screenshots-windows/gen_store_docs.py

    # 1. launch, size the window, open the agreement
    .\Setup-Frame.ps1 -W 3060 -T 2000 -Pdf "<repo>\artifacts\store\screenshots\blank-agreement.pdf" `
                      -Fit "FitPageButton" -ZoomIn 1 -Name probe-frame

    # 2. shot 1 — click the misspelled name, retype it (caret must be visible)
    .\Shot-TextEdit.ps1 -X 1013 -Y 735

    # 3. shot 2 — commit the edit, tick two of the three boxes
    .\Shot-Checkboxes.ps1

    # 4. shot 3 — arm the signature, scroll down, drop it on the line
    .\Open-SignatureFlyout.ps1        # once, to locate the library item
    .\Arm-Signature.ps1 -Notches 7
    .\Place-Signature.ps1 -X 1203 -Y 1401

    # 5. shot 4 — save, open the scan, Shrink for email
    .\Shot-Shrink.ps1 -Pdf "<repo>\...\scanned-agreement.pdf" -Out "<repo>\...\scanned-agreement - smaller.pdf"

    # 6. shot 5 — Add text with the size and face pickers showing (#43)
    .\Shot-AddText.ps1 -X 1430 -Y 1080

`Test-FullBreakpoint.ps1` captures toolbar strips right at the full-label
breakpoint with a document open and edited, so `Save ●` is showing — the widest
the bar ever gets, and the one state where clipping could survive the fix.
Verified 2026-08-13 at 1489 / 1494 / 1509 effective px: clean at all three.
Since #91 the breakpoint is measured from the labels rather than fixed at 1500
(about 1430 effective px in English, 1610 in French, bracketed 2026-09-10 with
this script at 1603 / 1619 effective), so pass `-Widths` straddling the right
value for the language being shot.

`Add-SignatureToLibrary.ps1` seeds `tools/assets/megawoman-sig.jpg` into the
signature library (needed once per machine). `Test-ToolbarWidths.ps1` captures
toolbar strips across a list of widths. `Shot-Now.ps1` grabs the current state.

**The coordinates above are for a 2500x1550 window on a 2560x1600 display at 150%
scale** (GPD-DAVE, 2026-09-09); the previous set was a 3060x2000 window on a
3240x2160 display at 200%.

**Set the display scale, not just the resolution.** `ApplyToolbarLayout` switches
on *effective* pixels, so a 2500 px window at 200% is 1250 effective and drops the
toolbar labels; at 150% it is 1667 and keeps them. Getting this wrong produces
shots that look fine until you notice the toolbar is icon-only. `Set-Scale.ps1 150`
switches the primary display without a sign-out (`Set-Scale.ps1` alone prints the
current value; put it back to 200 afterwards). Run it in its **own** PowerShell
process, then start the capture scripts in a fresh one: a process that changes
the scale keeps measuring windows at the old DPI, and every width comes out wrong. They are read off the previous screenshot, not computed: `Click-InShot`
maps image coordinates to screen because the shot *is* the DWM frame rect. On any
other frame, take a shot first and re-read them.

## Shot 5 — Add text, with the pickers (#43)

Written 2026-09-09; `Shot-AddText.ps1`. It shows the inline editor open on the
Date line with the size box and font box above it — the state `ShowInlineEditor`
produces when handed a non-null `style`, which happens only for MegaPDF's own
text boxes.

Run it after `Place-Signature.ps1`, so the signature is already on its line and
the date reads as the next thing you would fill in.

**Do not click the size box open before shooting**, which an earlier version of
this file advised. Clicking it commits the inline edit, dismisses both pickers,
and can leave an access-key tooltip painted over the page. Both boxes are legible
closed, which is all the caption promises.

The editor's top-left lands ON the click, so aim about 60 px above the rule you
want the text to sit on, at that rule's left end.

It cannot be done from CI: unlike iOS and Android, whose screenshots come from the
Actions workflows, this harness drives a real installed package on a real desktop.

## Why this frame

2500x1550 at 150% is 1667 effective px, above the toolbar's Full breakpoint
(measured from the labels — about 1430 in English and 1475 in French; see
`ApplyToolbarLayout` in `MainWindow.xaml.cs`), so the toolbar shows icon + label. Shoot narrower and the listing's screenshots show a different
toolbar than its description. Narrower than ~980 the zoom cluster folds into the
View flyout.

## Regenerate staging documents before every re-shoot

`blank-agreement.pdf` carries a deliberate "Whitfeld" typo that shot 1 fixes on
camera, and empty checkboxes shots 2-3 fill in. Shot 4 **saves the open document**
on its way past Shrink's "save first" guard, so a second pass without regenerating
starts with the typo already corrected and the boxes already ticked.

`scanned-agreement.pdf` is the same page rendered as one 400 DPI JPEG (~3 MB) so
Shrink has real work to do — the repo corpus PDFs contain no images at all, and
Shrink reports "Nothing to shrink" on them. It spells the name correctly; the typo
belongs only to shot 1's story.

## Landmines, each one paid for

- **The desktop must be unlocked.** `CopyFromScreen` on a locked session returns
  pure black. `Shot` prints a mean pixel value — near 0 means you captured nothing.
- **Never use ALT to take foreground.** It puts WinUI into access-key mode and
  stamps "O"/"S" badges on the toolbar buttons. `Front` clicks the title bar
  instead; `Clear-Badges` sends ESC if a stray ALT already armed them.
- **Capture the DWM extended frame bounds** (`DwmGetWindowAttribute`, attribute 9),
  not `GetWindowRect` — the latter includes the invisible shadow margin and bleeds
  the desktop into the edges of the frame.
- **The file picker is invisible to UI Automation.** The packaged `FileOpenPicker`
  does not appear under `RootElement` as `#32770`, so `Send-Path` finds it by
  window title through `EnumWindows`, takes foreground with a title-bar click, then
  types the path. Typing without that click sends the path to whatever window
  actually had focus.
- **Buttons are found by `AutomationId`, never by name** (#91). The UIA Name is
  localised — "Shrink for email" is "Réduire pour courriel" under the French
  setting — so `Click-Btn` takes the ids set in `MainWindow.xaml`: `OpenButton`,
  `SaveButton`, `SaveAsButton`, `ShrinkButton`, `PrintButton`, `UndoButton`,
  `RedoButton`, `SignaturesButton`, `WhiteoutButton`, `AddTextButton`,
  `FindButton`, `ZoomInButton`, `ZoomOutButton`, `FitWidthButton`,
  `FitPageButton`, `ViewMenuButton`, `SettingsButton`, and in the signature
  flyout `AddSignatureFromImageButton`, `TypeSignatureButton`,
  `DrawSignatureButton`. `BtnByName` still exists for anything else.
- **Flyout contents are not in the main window's UIA tree** (separate popup HWND),
  so flyout items are clicked by coordinate, not found by name.
- **Flyout button names end in a real ellipsis (U+2026)** and PowerShell 5.1 reads
  `.ps1` as ANSI, so match by prefix (`BtnLike`) rather than embedding the char.
- **Shrink refuses a dirty document** ("Save first"), and its before/after dialog
  only appears after the save-picker round trip.
- **The app restores its remembered window size** a moment after launch, so size
  the window *after* that settles, and again after opening a document.
- **Mouse wheel deltas are unsigned**: scrolling down is `[uint32]4294967176`.

## French screenshots (#91)

Shot 2026-09-10 for fr-CA and fr-FR; the sets live in
`artifacts/store/screenshots/fr-CA/` and `fr-FR/`. The installed package has no
command line, so the language comes from the Language setting: before launching,
set `"Language": "fr-CA"` (or `"fr-FR"`) in `%LOCALAPPDATA%\MegaPDF\settings.json`,
and set it back to `""` afterwards. Everything else is the same run — the
scripts find buttons by `AutomationId`, and `Send-Path` knows the French picker
titles. The recipe that produced both sets, with the coordinates read off the
French probe frame (the layout is the English one, translated):

    python3 tools/screenshots-windows/gen_store_docs.py artifacts/store/screenshots/fr-CA --lang fr
    $env:MEGAPDF_SHOTDIR = "<repo>\artifacts\store\screenshots\fr-CA"
    .\Setup-Frame.ps1 -W 2500 -T 1550 -Pdf "<shotdir>\blank-agreement.pdf" -Fit FitPageButton -ZoomIn 1 -Name probe-frame
    .\Shot-TextEdit.ps1 -X 887 -Y 563 -Text 'Nom : Dana Whitfield'
    .\Shot-Checkboxes.ps1 -X 741 -Y1 750 -Y2 807
    .\Open-SignatureFlyout.ps1                      # MegaWoman is the second row
    .\Arm-Signature.ps1 -X 1440 -Y 292 -Notches 7
    .\Place-Signature.ps1 -X 960 -Y 1105            # the click is the signature's centre
    .\Shot-AddText.ps1 -X 1420 -Y 1090 -Text '18 mars 2026'   # press Esc first if the signature is still selected
    .\Shot-Shrink.ps1 -Pdf "<shotdir>\scanned-agreement.pdf" -Out "<shotdir>\scanned-agreement - reduit.pdf"

Two more landmines paid for on that run: **arrow keys with nothing selected
scroll the page**, so nudge only while the signature shows its handles; and
**a click on the page while the signature is selected only deselects it** — the
Add-text click after `Place-Signature` needs an Esc in between, or it does
nothing and the placement banner stays up. Park the pointer *off the window*
(`SetCursorPos(2556, 900)` on this display) before a shot; parking inside the
window can leave a "Ctrl+F" accelerator tip painted over the page.

## Privacy — this is not optional

Screenshots must contain no real user data:

- The **empty state lists the user's own recent documents by filename**, so never
  shoot it. Open a document first. Width testing uses `ShotStrip`, which crops to
  the toolbar and never photographs the page area at all.
- The **signature library holds the user's real signature.** Place the
  `megawoman-sig` demo instead, and don't put the library flyout on camera.
- The **file picker shows the user's folder tree.** Never call `Shot` with one open.
- Delete every intermediate frame that catches any of the above; ship only the four.

What a session leaves behind, all under `%LOCALAPPDATA%\MegaPDF\`: `recent.json`
(write `[]` to clear), `Signatures\` (including anything added for the shoot), and
`Recovery\` (crash-recovery journals of unsaved edits).
