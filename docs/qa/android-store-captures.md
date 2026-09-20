# Android store captures — the runbook

The Play phone screenshots, and what has to be true of them before they go on the
listing. The point of writing it down is that the shoot should be a re-run, not a
debugging session (#146).

**Nothing here uploads anything.** Putting images on the listing is a separate,
deliberate act in the Play Console.

## How the set is produced

| | |
|---|---|
| Workflow | **Android Screenshots** (`.github/workflows/android-screenshots.yml`), `workflow_dispatch` |
| Script | `android/scripts/capture-screenshots.sh` — the same file runs locally |
| Device | `profile: pixel_6`, `api-level: 33` → **1080 × 2400 at 420 dpi** (411 × 914 dp). `tools/android-qa/make-store-avds.sh` builds that geometry and a 1600 × 2560 tablet on the same image. |
| Languages | `en`, `fr-CA`, `fr-FR`, one artifact each (`MEGAPDF_LANG`) |
| States | `home viewer search sign draw text text-edit redact` — 8 images per language, 24 in all |

The app poses itself: `--es screenshot <state>` on the launch intent puts the view
model straight into the state (`ViewerViewModel.applyScreenshotMode`). Nothing is
driven by tapping, so nothing depends on where a tap lands.

To shoot the same set on any machine with an emulator on that geometry:

```bash
cd android
./gradlew :app:assembleDebug
for L in en fr-CA fr-FR; do MEGAPDF_LANG=$L bash scripts/capture-screenshots.sh; done
# images land in /tmp/shots/<language>/
```

## What makes the set deterministic

Each of these has been a defect at least once, so each is named:

- **A fresh process per image.** The script force-stops the app between states, so
  no state carries over — including the zoom.
- **The zoom.** Android has no window to land on and no "100%" to choose: the page
  is laid out at the container's width, and the viewer opens at `1f`, which is both
  that width and the floor (`MIN_ZOOM`). Windows standardises its captures at 100%;
  1f is the same intent expressed in the units this platform has.
- **The signature library.** `applyScreenshotMode` **replaces** the library with the
  bundled `demo-signature.png` ("Mega W.") on every launch. It used to seed only when
  the library was empty, which made the `sign` and `draw` shots a function of whatever
  the device already held — that is how a stale signature reached a review set. What
  was there is moved to `files/signatures.before-capture`, not deleted, so the launch
  extra cannot cost anyone their signatures; Windows does the same in
  `tools/screenshots-windows/Reset-SignatureLibrary.ps1`. Note the two platforms label
  the row differently — "Mega W." here, "MegaWoman" there.
- **The launcher's taskbar.** On a large screen the launcher draws a taskbar across
  the bottom of every app, carrying whatever the device has pinned, with the
  navigation buttons beside it — it was in all 24 tablet captures. The script
  disables the launcher for the run and puts it back on the way out. No effect on a
  phone: the captures come out byte-identical either way.
- **The status bar.** SystemUI demo mode, re-asserted before every capture, not once
  at the start: clock 9:41, battery 100 % unplugged, Wi-Fi full **with `fully true`**
  (without it SystemUI draws the "no internet" badge over the icon, because the
  emulator has no validated connection), mobile hidden, notifications hidden.
- **The demo person.** `screenshot_text` per language — Jane Whitfield, Hélène
  Bélanger (fr-CA), Céline Lefèvre (fr-FR). It is the one string that differs
  between the two Frenches in this set; everything else is identical by design.
- **The pose's word on itself.** `applyScreenshotMode` logs `::error::` under the tag
  `megapdf-screenshot` when a state cannot do what it promises — the `redact` pose
  finding no line to mark, or asking for a mark twice and getting none, or any state
  throwing while it poses. The script clears logcat before each launch and reads it
  after the capture, so a line it finds can only belong to that state, and the step
  goes red naming it. The images are still uploaded (`if: always()`) — a failed run's
  shots are what say how it failed — but the step is red, so a bad set cannot be taken
  for a finished one. This exists because of 2026-09-20: the `fr-CA` `redact` image
  came out with no mark on the page at all, byte-identical across two runs, and both
  runs were green. A pose that does not fire has nothing to say on its own.

## The gate

Look at every image. A set ships only when all of this holds:

1. **1080 × 2400**, all 24.
2. **Nothing clipped or overlapping**, in any language. The title bar is the tight
   one: French labels are longer, so `Enregistrer` leaves the title less room than
   `Save` does, and a demo file name over about 20 characters ellipsises there.
3. **Current UI**: the one-row #144 bottom bar, brand-blue chips and signature
   buttons (#180), the ellipsising title (#181). No pre-#144 toolbar.
4. **The right demo person per language**, accents rendering.
5. **A clean status bar**: 9:41, full battery, no "no internet" badge, no
   notification icons, nothing of the emulator's own. And a clean *bottom*: no
   taskbar, no navigation buttons.
6. **The version the app reports.** About shows it, so a set shot from the wrong
   tree is a set that has to be shot again — which is what happened when the 2.0
   bump landed after the first gate set. Confirm `versionName` in the built APK
   (`aapt2 dump badging`) before shooting, not in the gradle file.
7. **No stray dialogs** — only the one each state is posing.
8. **Identical poses across the three languages.** `fr-CA` and `fr-FR` should differ
   only in `text`; `en` should differ from them everywhere but agree in layout.
9. **No state missed its pose.** The step reports this itself, and names the states; a
   red run still uploaded its images, so the ones it named are the ones not to read.

`compare -metric AE` between the language sets is a quick way to check 8, and
between two runs of the same language a quick way to check the set is reproducible
at all: English is byte-identical run to run.

## Two things this runbook cannot settle from here

**The aspect ratio.** `pixel_6` is 1080 × 2400, which is 9:20. Play's help text
for phone screenshots asks for 9:16, and 2400 ÷ 1080 = 2.22 is past a 2:1 limit if
that is the one being enforced. Whether the console accepts it has to be checked in
the console. If it does not, the fix is the AVD and not the script: the script
captures whatever geometry it is pointed at, so `hw.lcd.height=1920` (16:9) or
`2160` (2:1) re-shoots the whole set unchanged in every other respect.

**Whether the listing has a tablet slot.** The workflow shoots phone only. A tablet
set has been taken once as a look-ahead and read fine, but nothing in this repo
records the listing's slots — that is in the console too.

## What each image is for

| State | Shows |
|---|---|
| `home` | the recents list with where each file lives (#165) — no account, no cloud |
| `viewer` | the demo agreement, ticked and signed |
| `search` | a term found, `1 of 3`, matches highlighted |
| `sign` | the signature library sheet, with Draw / Type / Photo |
| `draw` | the draw-a-signature dialog |
| `text` | Add text, with the size and face pickers (#43) |
| `text-edit` | the body-text editor mid-correction (#114) |
| `redact` | a line marked for redaction, with the mark selected (#173, #329) |

`redact` is a listing shot now that #173 is finished. It poses a marked line and
the mark's own selection chrome — the drag handles and the ✕ that take it off
(#329) — so the shot shows both halves of the feature: the area about to be
removed, and that nothing is removed until Save is answered. It waits for the edit
gate to open before asking for the mark (the same call refuses silently while an
edit is in flight), waits for the mark to land rather than guessing at a delay, and
asks a second time before it gives up — and says so when it does.

**Save is live (brand blue) in it** and grey in the seven others. That is not a
dirty document, which is why the title carries no bullet and closing asks nothing:
a mark leaves the document exactly as it was. Save is live because a mark is
something Save has to *finish* — `enabled = (isDirty || hasMarks)`, same as #173's
Windows and Mac passes found.

It used to pose the tool **armed** as well. Since #328 the tool is a row in the ⋯
menu, so an armed tool draws nothing on the page to photograph — the pose selects
its mark instead. The whole certified set is re-shot either way: moving Redact out
of the bottom bar changes every viewer capture.
