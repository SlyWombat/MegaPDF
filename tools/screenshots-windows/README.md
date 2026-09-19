# Windows Store screenshots — capture harness

Drives the installed MegaPDF package from WSL (via `powershell.exe`) with UI
Automation and synthetic input, and captures the four screenshots the Microsoft
Store listing uses. The iOS and Android equivalents run in CI
(`.github/workflows/ios-screenshots.yml`, `android/scripts/capture-screenshots.sh`);
this one needs a real Windows desktop, so it runs locally.

Output goes to `artifacts/store/screenshots/` (gitignored — these are upload
assets, not repo content). Override with `$env:MEGAPDF_SHOTDIR`.

## Driving a build that is not installed

Set `MEGAPDF_EXE` to an unpackaged `MegaPDF.exe` (the Release build under
`src\MegaPDF.App\bin\...\win-x64`) and `Start-App` launches that instead of the
Store package; `MEGAPDF_ARGS` adds launch arguments, and the first argument may
be a PDF path, which is how to open a document without the file picker: the
unpackaged build's `FileOpenPicker` never appears (no package identity), so
`Send-Path` finds nothing and `Shot-Shrink.ps1` cannot run against it. For the
Shrink shot use the installed package, driven by coordinates and keyboard:
UI Automation cannot see the packaged app's buttons from a WSL-launched
PowerShell, but `Click-InShot`, `^o` and the pickers work (2026-09-13).

## Run order

`Shoot-Set.ps1 -Lang <en-US|fr-CA|fr-FR> -Dir <en|fr-CA|fr-FR>` runs the whole
sequence below for one language: it refuses unless the installed package is the
version to be shot (2.0.0.0 by default), and afterwards it moves the probe and
in-between frames into `work/`, so the language folder holds only the six listing
images `tools/capture-gate` reads (`gate.py --store microsoft artifacts/store/screenshots`).
It produced the 2.0 set. The steps, one at a time:

Each script drives one step against the already-running app, so you can inspect
the result before continuing. Paths must be **Windows** paths. The whole set is
shot once per listing language: `en-US`, `fr-CA`, `fr-FR`.

    # 0. staging documents, per language (regenerate before EVERY re-shoot — see below)
    python3 tools/screenshots-windows/gen_store_docs.py <repo>\artifacts\store\screenshots\fr-CA --lang fr-CA

    # 1. the machine's state: the app's language, and a signature library of exactly one
    .\Set-Language.ps1 -Lang fr-CA -Theme Light
    .\Reset-SignatureLibrary.ps1

    # 2. launch, size the window, open the agreement at 100%
    .\Setup-Frame.ps1 -W 2500 -T 1550 -Pdf "<repo>\artifacts\store\screenshots\fr-CA\blank-agreement.pdf" `
                      -Fit "ActualSizeItem" -ZoomIn 0 -Name probe-frame

    # 3. shot 1 — click the misspelled name, retype it (caret must be visible)
    .\Shot-TextEdit.ps1 -X 880 -Y 538 -Text "Nom : Helene Belanger"   # with the accents

    # 4. shot 2 — commit the edit, tick two of the three boxes
    .\Shot-Checkboxes.ps1 -X 787 -Y1 707 -Y2 759

    # 5. shot 3 — arm the signature, drop it on the line, let go of it
    .\Open-SignatureFlyout.ps1        # once, to locate the library item
    .\Arm-Signature.ps1 -Notches 0 -X 510 -Y 248
    .\Place-Signature.ps1 -X 1022 -Y 1400

    # 6. shot 5 — Add text with the size and face pickers showing (#43)
    .\Shot-AddText.ps1 -X 1390 -Y 1360 -Text "18 mars 2026"

    # 7. shot 4 — save, open the scan, Shrink for email (last: it replaces the document)
    .\Shot-Shrink.ps1 -Pdf "<repo>\...\fr-CA\scanned-agreement.pdf" -Lang fr-CA
    # 8. shot 6 — reopen the finished agreement, mark the name, save a redacted copy
    .\Setup-Frame.ps1 -W 2500 -T 1550 -Pdf "<repo>\...\fr-CA\blank-agreement.pdf" -Fit "ActualSizeItem" -ZoomIn 0 -Name probe-frame-redact
    .\Shot-Redact.ps1 -Lang fr-CA -Save

**Shoot at 100%, not at a fit.** "Fit page then one zoom in" landed on 109% in every
shot, and a listing image with a number like that in the toolbar reads like an
accident. At 100% on this frame the whole page fits anyway, signature line included,
so nothing has to be scrolled into view (#146, 2026-09-17).

**Shot 5 before shot 4.** Shrink opens the scan, which replaces the agreement in the
window, so Add text has to happen while the agreement is still open. `Shot-AddText.ps1`
commits its edit after the shot; leaving the editor open swallows the Ctrl+S that
Shrink starts with, and Shrink then walks into "Save changes?" with its toolbar
disabled behind the dialog.

**Type the accents from code points, not from this file.** Text piped from WSL through
`powershell.exe -Command` arrives in the OEM code page: `Hélène` becomes mojibake in
the document. Build the string in the driver script (`"Nom : H$([char]0xE9)l$([char]0xE8)ne..."`)
or put it in a `.ps1` saved with a BOM.

**The signature library is part of the frame.** `Reset-SignatureLibrary.ps1` replaces it
with one entry — the repo's own `tools/assets/megawoman-sig.png`, named "MegaWoman" —
and sets the machine's own library aside (`-Restore` puts it back). Seeding through the
UI instead leaves whatever was already there in the flyout and names the row after the
file it was imported from; both were in the 2026-09-17 dry run.

**The copy Shrink saves is named by the app, not by the harness.** Leave `-Out` off and
`Shot-Shrink.ps1` reads `SmallerFileName` out of the language's own `.resw`, so the
French shots say "scanned-agreement - réduit.pdf". Typing an English `-Out` is how
"- smaller.pdf" ended up in both French sets.

`Test-FullBreakpoint.ps1` captures toolbar strips right at the full-label
breakpoint with a document open and edited, so `Save ●` is showing — the widest
the bar ever gets, and the one state where clipping could survive the fix.
Verified 2026-08-13 at 1489 / 1494 / 1509 effective px: clean at all three.
Since #91 the breakpoint is measured from the labels rather than fixed at 1500
(about 1430 effective px in English, 1610 in French, bracketed 2026-09-10 with
this script at 1603 / 1619 effective), so pass `-Widths` straddling the right
value for the language being shot.

`Add-SignatureToLibrary.ps1` imports `tools/assets/megawoman-sig.jpg` through the app's
own "From photo" flow — how the fixture PNG was made, not how a capture run should set
the library up (use `Reset-SignatureLibrary.ps1`). `Test-ToolbarWidths.ps1` captures
toolbar strips across a list of widths. `Shot-Now.ps1` grabs the current state.

**The coordinates above are for a 2500x1550 window on a 2560x1600 display at 150%
scale** (GPD-DAVE, re-read 2026-09-17 against the one-row toolbar of #144 at 100%
zoom; the 2026-09-09 set was for the two-row bar and clicks the wrong things now). The set
before that was a 3060x2000 window on a 3240x2160 display at 200%.

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
  `FindButton`, `ZoomInButton`, `ZoomOutButton`, `ZoomMenuButton`, `SecurityButton`,
  `SettingsButton`, and in the signature flyout `AddSignatureFromImageButton`,
  `TypeSignatureButton`, `DrawSignatureButton`. `BtnByName` still exists for
  anything else.
- **The toolbar is one row since #144.** `FitPageButton`, `FitWidthButton` and
  `ViewMenuButton` are gone. Zoom is `ZoomOutButton`, the `ZoomMenuButton` level
  drop-down and `ZoomInButton`; the drop-down's menu holds `ActualSizeItem`,
  `FitWidthItem` and `FitPageItem`, which `Click-Zoom` opens and invokes (it still
  accepts the old `FitPageButton` name). Save as, Password (`SecurityButton`),
  Print, Shrink, Find and Settings live in the CommandBar's **More** overflow
  (`MoreButton`), and narrow windows push low-priority primary commands there too:
  `Click-Btn` opens More itself when a button is not on the row.
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

    python3 tools/screenshots-windows/gen_store_docs.py artifacts/store/screenshots/fr-CA --lang fr-CA
    $env:MEGAPDF_SHOTDIR = "<repo>\artifacts\store\screenshots\fr-CA"
    .\Setup-Frame.ps1 -W 2500 -T 1550 -Pdf "<shotdir>\blank-agreement.pdf" -Fit FitPageItem -ZoomIn 1 -Name probe-frame
    .\Shot-TextEdit.ps1 -X 887 -Y 563 -Text 'Nom : Hélène Bélanger'     # fr-FR: 'Nom : Céline Lefèvre'
    .\Shot-Checkboxes.ps1 -X 741 -Y1 750 -Y2 807
    .\Open-SignatureFlyout.ps1                      # MegaWoman is the second row
    .\Arm-Signature.ps1 -X 1440 -Y 292 -Notches 7
    .\Place-Signature.ps1 -X 960 -Y 1105            # the click is the signature's centre
    .\Shot-AddText.ps1 -X 1420 -Y 1090 -Text '18 mars 2026'   # press Esc first if the signature is still selected
    .\Shot-Shrink.ps1 -Pdf "<shotdir>\scanned-agreement.pdf" -Out "<shotdir>\scanned-agreement - reduit.pdf"

**The French customer is French, with accents (#146).** English is Jane
Whitfield with the "Whitfeld" typo, the name every other store's English set
poses (it was "Dana" on Windows alone until the 2.0 set). `gen_store_docs.py --lang fr-CA` writes
"Nom : Hélène Belanger" and `--lang fr-FR` "Nom : Céline Lefevre", and the on-camera
fix is the missing accent ("Bélanger", "Lefèvre"), which also shows accented
editing. The coordinates above were read off the 2026-09-10 frame; re-read them
before the 2.0 re-shoot. **Type the accents from a PowerShell prompt, or build the
string from char codes** (`"Nom : H" + [char]0xE9 + "l" + [char]0xE8 + "ne B" + [char]0xE9 + "langer"`):
text piped to `powershell.exe -Command -` from WSL arrives in the OEM code page and
types as "H├⌐l├¿ne" (seen 2026-09-17). The inline editor underlines "Bélanger" with a
spelling squiggle while it is open; commit with Enter before a shot that must not
show it.

Two more landmines paid for on that run: **arrow keys with nothing selected
scroll the page**, so nudge only while the signature shows its handles; and
**a click on the page while the signature is selected only deselects it** — the
Add-text click after `Place-Signature` needs an Esc in between, or it does
nothing and the placement banner stays up. Park the pointer *off the window*
(`SetCursorPos(2556, 900)` on this display) before a shot; parking inside the
window could leave a "Ctrl+F" accelerator tip painted over the page. That tip was
the app's bug, not the harness's: the window's shortcuts gave their host grid a
tooltip (#226, fixed in 2.0). Parking off the window is still the habit, because
it keeps any hover state out of the frame.

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
