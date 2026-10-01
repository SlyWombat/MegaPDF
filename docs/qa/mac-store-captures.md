# Mac and iOS store captures — the runbook

What produces the Apple listing images, what makes the set the same every time it
is taken, and what to check each image against before anything is uploaded.

Nothing here uploads. Publishing is `tools/asc_publish.py`, run deliberately and
separately; this runbook stops at a folder of reviewed images.

The Android equivalent is `docs/qa/android-store-captures.md`, and Windows' is in
the `#146` §3 comments. The three sets are shot by different harnesses because the
platforms are, but the gate below is the same gate.

## How the set is produced

Both from the in-house Mac (`tools/mac-mini.md`), from a checkout of the commit
being shipped:

```
# Mac: seven listing images per language, 1440x900 (#613)
tools/build-macos-app.sh osx-arm64 ~/app-store
tools/macos-store-captures.sh en    ~/store/macos-screenshots/en
tools/macos-store-captures.sh fr-CA ~/store/macos-screenshots/fr-CA
tools/macos-store-captures.sh fr    ~/store/macos-screenshots/fr

# iOS: six listing images and five review shots per language, per device
tools/ios-screenshots.sh en    ~/store/ios-screenshots/en
tools/ios-screenshots.sh fr-CA ~/store/ios-screenshots/fr-CA
tools/ios-screenshots.sh fr    ~/store/ios-screenshots/fr
```

The iOS set is also an Actions workflow (**iOS Screenshots**), which takes the
same shots on a runner. The Mac set has no workflow: it needs a real display, and
a runner has none.

## What makes the set deterministic

Each of these has been a defect once. They are the reason the harness is a script
rather than a sequence of commands somebody remembers.

| | Why |
|---|---|
| **A fresh process per image** | A state left over from the shot before is the commonest way a capture lies. Windows was bitten twice by it. Each Mac shot is its own launch; each iOS shot is `simctl launch` after a `terminate`. |
| **The run owns its fixtures** | `gen_test_fixtures.py` into a new temp directory, copied to the demo document's display name. Nothing on the machine is read. |
| **The run owns its signature library** | `--signature tools/assets/megawoman-sig.jpg`, into a throwaway library directory the `sign` state makes for itself. The flyout shows exactly one card, "Mega W.". iOS installs into a freshly uninstalled container for the same reason. |
| **The recents are a fixture** | `--screenshot-state home` fills the list from `DemoContent.Recents` (iOS: `DemoContent.demoRecents`). The machine's own list is whatever it last opened — on the capture Mac that was a path through the sandbox container. |
| **The zoom is 100%** | The fixture path is new each run, so the recents store has no zoom to restore for it. A round number reads as chosen; Windows shipped a dry run at 109%. |
| **The document has one name** | `DemoContent.DocumentFileName`, so the status line and the recents list agree. The fixture is `demo-fr.pdf` (fr-CA) or `demo-fr-FR.pdf` (fr, France's own text since #310) on disk and "Contrat de location.pdf" in every shot. |
| **The language is an argument** | `--language fr-CA` on the Mac, `-AppleLanguages`/`-AppleLocale` on iOS. The machine's own language is never changed, so a run cannot pick up half of it. |
| **The binary is named** | Each Mac run writes `RUN.txt` with the bundle path and the binary's sha256. This Mac has ~20 stale `com.megapdf.ios` bundles registered, and everything is launched by absolute path rather than through LaunchServices. |
| **The pointer is off the window** | The Mac shots are the real window on the capture Mac's display, so a pointer resting over it paints hover. A 2026-09-19 re-shot `home` came out with the first recent highlighted and its location tooltip up. Move the pointer to a corner first (it is the one input the script doesn't pose), and read `home` before keeping it. |
| **The iPhone status bar is fixed** | `simctl status_bar override`: 9:41, full bars, charged. The iPad's date cannot be overridden — see below. |

## The gate

Every image, every language. The script checks the first two itself and fails the
run; the rest are read by eye.

1. **The size is exact.** Mac 1440×900; iPhone 6.9" 1320×2868; iPad 13" 2064×2752.
2. **Every listing slot is present** — seven for the Mac since #613, the eight
   `docs/app-store-listing.md` maps for iOS — and review shots are not among them.
3. **The UI language matches the set.** No English in a French shot, no French
   caption on an English one, including the status line and the window title.
4. **The demo person is right**: Jane Whitfield (en), Hélène Bélanger (fr-CA),
   Céline Lefèvre (fr). Accents whole — nothing drawn across them.
5. **The toolbar is the current one**: Open / Save / Sign / Add text / Cover /
   Redact / Undo / Redo / zoom / More. If Redact is missing, the set predates 2.0.
6. **Nothing transient**: nothing *selected* in the wrong place, no first-boot
   banner, no accelerator badge, no stray dialog, no spinner. Two exceptions, and
   in both the chrome is the point of the image. Slot 4's selection — two rows of
   the Pages sidebar picked out, and the toolbar's Pages button toggled on — is
   the feature, not a selection left behind. And slot 6's: the mark, its box, its four
   corner handles, and the ✕ at the mark's own top-right corner — see the note
   under the slot table for what changed in 2.1.
7. **Nothing from this machine**: no container path, no real file name, no library
   entry that is not "Mega W.".
8. **The zoom reads 100%** in the six slots that have a toolbar to read it off.
   Slot 1 has none: reading mode takes the toolbar away, so there is no zoom chip
   in that frame and the gate records the check as skipped rather than passed.
9. **The document's name is the same in every shot of the set.**

## What this runbook cannot settle from here

- **200% / Retina.** The capture Mac's only display is an HDMI capture at 1×, and
  the offscreen `--scale 2` path is not a substitute: it draws every page overlay
  at twice its offset and twice its size, so the find highlights float in the grey
  beside the page. The app refuses `--scale 2` when anything is drawn over the
  page rather than write a wrong image. 1440×900 is an accepted Mac App Store
  size, so the listing is not blocked; a Retina set needs a Retina display.
- **The iPad set is the phone UI** (#172). The 13" shots show a phone-width
  toolbar pinned bottom-left. Whatever is decided there, these images are the
  evidence.
- **The iPad's status bar carries a real date.** `simctl status_bar` overrides the
  time and the indicators but not the date, so an iPad shot dates itself.
- **Window chrome.** The Mac app renders its own window, so the image is the
  client area — no title bar, no traffic lights. App Store Connect accepts it
  (the size is exact); Mac listings conventionally show the window. Changing that
  means `screencapture` on a real screen, which needs privacy permissions an SSH
  session cannot be granted.
- **A real device.** Everything here is a simulator and an offscreen Mac window.

## What each image is for

### Mac App Store — 1440×900, `light-NN-<state>.png`

Seven slots since #613, in the order Dave settled on 2026-10-01: *"Redaction and
whiteout are minor features that move to the back, signing, editing and reading
are common features."*

| Slot | File | What it shows |
|---|---|---|
| 1 | `light-01-reading.png` | The agreement with the chrome gone and the floating bar up (#505). |
| 2 | `light-02-text.png` | A typed name on the blank line, nothing selected. |
| 3 | `light-03-sign.png` | The signature library flyout, one card. |
| 4 | `light-04-pages.png` | The Pages sidebar beside the document, two pages picked out (#174). |
| 5 | `light-05-search.png` | The find bar with a term typed and "1 of 3". |
| 6 | `light-06-redact.png` | A line marked and selected, with the chrome that takes it off — 2.1's headline (#329). Changed for 2.1: 2.0's pose armed the tool instead. |
| 7 | `light-07-home.png` | The empty window and its recents list. |

**`viewer` left the set.** It was slot 1 — the filled, signed agreement — and
**reading replaced it** rather than joining it: both are a picture of a page, and
the reading one says something as well. The state still exists in the app and in
`docs/qa/mac-screen-inventory.md`; it is simply not a listing slot.

**Slot 1 is the hardest image in the set, for the reason the feature exists.**
Reading mode takes the toolbar, the tab strip, the sidebar and the status line off
the screen, so the frame is a page on a canvas and the floating bar is the only
thing in it that names the application — and the bar fades after two seconds of
idle, which is when the window is rendered. The pose therefore **pins the bar**
(`MainWindow.PinReadingPillForCapture`, added by the Linux pass of #613 and shared
by both desktops, because they are the same Avalonia app). Read slot 1 for the
bar: if it is not there, the picture is a page and nothing else.

**Page colour is left at Normal in slot 1.** Sepia and Night belong to reading
mode too and would make the image unmistakable, but a set whose first image is the
only tinted one reads as a different app from the six behind it, and the tint is in
the description, where a reader meets it as a choice.

**Slots 1 and 4 open a different document from the other five.** A Pages sidebar
with one thumbnail is a picture of nothing and the floating bar would read "Page 1
of 1", so both open `demo-pages.pdf` (`demo-fr-pages.pdf` / `demo-fr-FR-pages.pdf`
in French): the same filled, ticked, signed page 1, followed by the agreement's
four sections and a landscape rate schedule. It is opened under the same document
name as the one-page one, so the set is still one document from end to end — check
that the status line says the same name in all seven.

**Slot 4 is deliberately not the phone's picture.** The Mac puts the page tools in
a thumbnail sidebar beside the document that stays open while you work; Android
puts them on a screen of their own with a contextual selection bar. Both are the
platform's own idiom (#165) and the difference is worth showing rather than
normalising.

**Slot 6 changed twice on 2026-09-20, and the set has to be shot after both
(#338).** Before them, 2.1 wrote no image for this state in any language — the
pose asked for a mark and read it before the placement had run, so the run failed
the slot on `::error::`. (2.0 did write one: `docs/qa/linux-fr/redact.png`, the
armed tool over a marked line and no chrome, which is what 2.0's pose posed.)
And the ✕ that takes a mark off was drawn at the *page's* top-right corner, half
of it cut off above the page, instead of at the mark's. Both are fixed on the
desktops branch and were verified by shooting all six slots with the Avalonia
build on a Windows machine, in en and fr-CA; the images are the three things to
read in slot 6 — the translucent band across the line, the four corner handles,
and the ✕ on the mark's top-right corner, with the status line under the page
reading "Drag to move, corners to resize, Delete to remove" in the set's
language.

### iOS — `listing/` and `review/`

`listing/` holds the eight slots `docs/app-store-listing.md` maps, per device
(`iphone-6_9-*`, `ipad-13-*`): viewer, text-edit, redact, text, search, sign,
draw, home — in that order, which is the order they are uploaded in.

`review/` holds everything else — `text-edit`, `redact`, and the dark-mode
`search`, `sign` and `redact`. They are for looking at, not for uploading. They
are in their own folder because a folder of eleven images beside a table of six
slots is how a review shot ends up on a store listing.
