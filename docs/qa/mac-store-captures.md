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
# Mac: six listing images per language, 1440x900
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
| **The document has one name** | `DemoContent.DocumentFileName`, so the status line and the recents list agree. The fixture is `demo-fr.pdf` on disk and "Contrat de location.pdf" in every shot. |
| **The language is an argument** | `--language fr-CA` on the Mac, `-AppleLanguages`/`-AppleLocale` on iOS. The machine's own language is never changed, so a run cannot pick up half of it. |
| **The binary is named** | Each Mac run writes `RUN.txt` with the bundle path and the binary's sha256. This Mac has ~20 stale `com.megapdf.ios` bundles registered, and everything is launched by absolute path rather than through LaunchServices. |
| **The iPhone status bar is fixed** | `simctl status_bar override`: 9:41, full bars, charged. The iPad's date cannot be overridden — see below. |

## The gate

Every image, every language. The script checks the first two itself and fails the
run; the rest are read by eye.

1. **The size is exact.** Mac 1440×900; iPhone 6.9" 1320×2868; iPad 13" 2064×2752.
2. **Six listing slots, all present**, and review shots are not among them.
3. **The UI language matches the set.** No English in a French shot, no French
   caption on an English one, including the status line and the window title.
4. **The demo person is right**: Jane Whitfield (en), Hélène Bélanger (fr-CA),
   Céline Lefèvre (fr). Accents whole — nothing drawn across them.
5. **The toolbar is the current one**: Open / Save / Sign / Add text / Cover /
   Redact / Undo / Redo / zoom / More. If Redact is missing, the set predates 2.0.
6. **Nothing transient**: no selection chrome, no first-boot banner, no accelerator
   badge, no stray dialog, no spinner.
7. **Nothing from this machine**: no container path, no real file name, no library
   entry that is not "Mega W.".
8. **The zoom reads 100%.**
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

| Slot | File | What it shows |
|---|---|---|
| 1 | `light-01-viewer.png` | The filled, signed agreement. The shot that leads. |
| 2 | `light-02-text.png` | A typed name on the blank line, nothing selected. |
| 3 | `light-03-search.png` | The find bar with a term typed and "1 of 3". |
| 4 | `light-04-sign.png` | The signature library flyout, one card. |
| 5 | `light-05-redact.png` | Redact armed with an area marked — 2.0's headline feature. |
| 6 | `light-06-home.png` | The empty window and its recents list. |

### iOS — `listing/` and `review/`

`listing/` holds the six slots `docs/app-store-listing.md` maps, per device
(`iphone-6_9-*`, `ipad-13-*`): viewer, text, search, sign, draw, home.

`review/` holds everything else — `text-edit`, `redact`, and the dark-mode
`search`, `sign` and `redact`. They are for looking at, not for uploading. They
are in their own folder because a folder of eleven images beside a table of six
slots is how a review shot ends up on a store listing.
