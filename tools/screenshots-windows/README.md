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
                      -Fit "Fit page" -ZoomIn 1 -Name probe-frame

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

`Add-SignatureToLibrary.ps1` seeds `tools/assets/megawoman-sig.jpg` into the
signature library (needed once per machine). `Test-ToolbarWidths.ps1` captures
toolbar strips across a list of widths. `Shot-Now.ps1` grabs the current state.

**The coordinates above are for a 3060x2000 window on a 3240x2160 display at 200%
scale.** They are read off the previous screenshot, not computed: `Click-InShot`
maps image coordinates to screen because the shot *is* the DWM frame rect. On any
other frame, take a shot first and re-read them.

## Why 3060x2000

That is 1530 effective px, above the toolbar's `ToolbarFullWidth` breakpoint
(1500, see `ApplyToolbarLayout` in `MainWindow.xaml.cs`), so the toolbar shows
icon + label. Shoot narrower and the listing's screenshots show a different
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
- **Flyout contents are not in the main window's UIA tree** (separate popup HWND),
  so flyout items are clicked by coordinate, not found by name.
- **Flyout button names end in a real ellipsis (U+2026)** and PowerShell 5.1 reads
  `.ps1` as ANSI, so match by prefix (`BtnLike`) rather than embedding the char.
- **Shrink refuses a dirty document** ("Save first"), and its before/after dialog
  only appears after the save-picker round trip.
- **The app restores its remembered window size** a moment after launch, so size
  the window *after* that settles, and again after opening a document.
- **Mouse wheel deltas are unsigned**: scrolling down is `[uint32]4294967176`.

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
