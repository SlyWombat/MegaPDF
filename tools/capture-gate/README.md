# The capture gate — a runbook

The last thing before a store submission is a person looking at every capture
(#146 §3). There are about a hundred of them across five platforms and three
languages, and looking at a hundred images is how the eye stops seeing: the
stale signature, the selection box left on a name, the English file name in a
French dialog, the spell-check underline and the Wi-Fi badge all reached a
review set with someone looking straight at them.

This is the tool that turns that review from a hunt into a confirmation. It
measures what a person is bad at measuring across a hundred images, prints the
result beside each one on a single page, and ends with a list of the images it
could **not** clear — which are the only ones anyone has to open.

It does not decide whether a set ships. It decides which images are worth
your eyes.

## Running it

```bash
python3 tools/capture-gate/gate.py --store mac artifacts/store/mac -o /tmp/gate
open /tmp/gate/sheet-mac.html
```

| store | the set it expects |
|---|---|
| `--store microsoft` | `tools/screenshots-windows` output, `artifacts/store/screenshots/<lang>/` |
| `--store mac` | `tools/macos-store-captures.sh` output, `<set>/<lang>/` |
| `--store ios` | `tools/ios-screenshots.sh` output — add `--only /listing` to leave the review set out |
| `--store play` | `android/scripts/capture-screenshots.sh` output, and the 27-cell QA matrix |
| `--store linux` | the Linux QA rig's `shots-gnome/` |
| `--store video` | the stills `tools/preview-gate.py` cuts out of an app preview, **one language at a time** |

Useful flags:

- `--only listing` / `--only cell-a,cell-b` — a shot folder usually holds more
  than one set; this picks the paths to walk.
- `--against <dir>` — compare every image with its counterpart in a set that
  was already signed off. This is the strongest check in here and the one to
  use for a re-shoot: it says what changed, and everything that changed had
  better be something you meant.
- `--live-clock` — the set was **not** shot with the clock posed, so only the
  right of the status bar is compared. The store sets pose the clock (9:41,
  SystemUI demo mode); the Android QA matrix does not.
- `--strict` — exit non-zero if anything is flagged, for CI.

It needs **ImageMagick** (`convert`, `identify`, `compare`), which the capture
tooling already depends on, and optionally **tesseract** — see below. No
Pillow, no numpy, nothing to install with pip.

## What comes out

`sheet-<store>.html` is self-contained: the thumbnails are inlined, so it can
be mailed or opened on a phone with no network, and each one links to the full
image beside it. One button hides everything the tool cleared. A verdict line
per language set says how many images, how many checks passed, how many could
not be run, and which images to look at.

`results.json` is the same thing for a script.

Nothing is written into the set itself, and **nothing is ever uploaded**.

## The checks

Each check answers one question, and says what it measured rather than only
whether it passed — a note you can disagree with beats a tick you cannot.

| check | what it means |
|---|---|
| `size` | the exact pixel size is one this store's slot takes, and the ratio is printed |
| `clipping` | ink is touching the frame in a pattern that looks like letters rather than like a border or a scrollbar |
| `toolbar` | the band at the top is the one-row 2.0 toolbar (#144), or the number of rows this pose is allowed |
| `accent` | brand-accent pixels, and whether this pose is one that draws in the accent at all |
| `squiggle` | a red mark that waves rather than rules — a spelling underline left on camera |
| `person` | the demo person matches the listing language, accents intact (needs OCR) |
| `zoom` | the zoom chip reads the pinned value (needs OCR) |
| `privacy` | no home-folder path is legible on the image (needs OCR) |
| `language` | no English UI word on a French capture (needs OCR) |
| `siblings` | the same pose in another language is the same layout, the same status bar and the same amount of accent |
| `chrome` | within one language, the status bar does not vary, and nothing modal is dimming one shot |
| `certified` | with `--against`, how much this image differs from the one that was signed off |

### Preview videos

`tools/preview-gate.py` checks the clip itself — the slot size, App Store
Connect's 15-30 s, the constant 30 fps, H.264, and the silent audio track the
upload is refused without — and then cuts it into one still per second, named
so that `--store video` reads them as a set. A clip is the one capture nobody
can read at a glance; twenty-odd stills on a contact sheet can be read.

Run it **one language at a time**. A clip is time-compressed to fit 30 s from
however long its own take ran, and the takes are not the same length, so still
12 of the English clip and still 12 of the French one are not the same moment.
Run per language and the cross-language checks stand down and say so.

The stills are H.264 frames, not screenshots, and that costs the status-bar
comparison its sensitivity: the quantiser smears the bar's flat grey over a
dozen values and the ringing along the clock reads as ink. On the 2.0 English
iPhone preview that moved up to 14.2 % of the band between two frames with
nothing in the bar, against 12.6 % for a planted badge — the check could not
tell them apart. The `video` profile therefore sets `ink_tolerance: 16`, which
widens each background tone: the same clean frames fall to 3.9-4.6 % and the
badge rises to 24.6 %. Every other profile leaves it at 0, so no set that was
already signed off is measured any differently.

**Three verdicts, and `skip` is the important one.** `pass` means measured and
right. `flag` means measured and questionable. `skip` means *not measured*, and
the note says why — a gate that quietly drops a check it could not run is
worse than no gate, because the sheet then reads clean.

### The sibling checks are the strong half

Everything in a set is shot by one script on one frame, so the same pose in
another language is a control: the status bar is the same pixels, the layout is
the same blocks, the accent is the same quantity. Anything that differs beyond
the words is something somebody left on screen. That is how the gate finds a
selection border, a notification or a dialog without being told what any of
them look like.

It is also why a **single-language set is a weaker gate**, and the sheet says
so: the checks that need a sibling stand down.

## What needs OCR

`person`, `zoom`, `privacy` and `language` read the screen, and for that they
need `tesseract` (`apt install tesseract-ocr tesseract-ocr-eng tesseract-ocr-fra`,
`brew install tesseract tesseract-lang`). Without it they report `skip` with
the reason, and the sheet's header says the text checks stood down.

Two things to know about the reader:

- **It drops accents.** It reads *Récents* as "Recents" and *Terminé* as
  "Termine". So `person` compares names unaccented and checks only that the
  accents are *there*, and `language` looks for English words whose French
  counterpart shares no letters with them.
- **It cannot read a toolbar.** Layout analysis throws away a row of icons with
  three-letter labels under them. `zoom` therefore cuts the toolbar into its
  separate controls and reads each one as a single line, which finds "100 %"
  every time.

## When something is flagged

The note says what was measured. Open the image, and:

- **`size`** — wrong frame or wrong scale. On Windows the display scale
  matters as much as the resolution (`tools/screenshots-windows/README.md`).
- **`clipping`** — look at that edge. A word cut off by the frame is a defect;
  a scrollbar and a window border are not, and the check tries to tell them
  apart by whether the marks cluster.
- **`accent` on a pose that should have none** — something is armed, selected
  or highlighted. This is the selection border that reached the Mac dry-run
  set through the French accents.
- **`squiggle`** — it reads the document as well as the app, and cannot know
  the difference. A PDF with red dimension dashes in it looks exactly like a
  spelling underline; two images in the 150-image Android QA matrix flag for
  that reason, and a glance clears them.
- **`siblings`** — a layout that moved, a status bar that gained something, a
  screen that did not translate.
- **`certified`** — with `--against`, *everything* that changed since the set
  was signed off, which is the only way to notice something plausible that is
  simply not what was agreed: a different signature in the library, a
  regenerated fixture, a theme that moved a shade.

## What it cannot tell you

- **Whether the set looks inviting.** Nothing here measures whether a capture
  makes someone want the app.
- **Whether the pose tells the story the listing copy promises.** The copy is
  in `docs/release-notes/2.0/`; the gate never reads it.
- **Whether the French reads like French.** `language` finds English words, not
  bad French. The francophone review is `docs/release-notes/2.0/french-review.md`.
- **Whether a signature is the demo one.** Any signature looks like a
  signature. `--against` a certified set is what catches a stale one, which is
  how the Android session proved its fix.
- **A notification on the left of an Android bar under `--live-clock`.** With a
  live clock the left of the bar is not comparable. Pose the clock — the store
  script does — and the whole bar is compared.

## Adding a store, or a device

`stores.py`, and nothing else. A profile holds what cannot be measured: the
slot sizes, the listing languages and their demo person, the order the images
appear in on the listing, the file-name shape, and the few facts about the
app's own chrome — how tall the toolbar is, what the zoom is pinned to, where
the status bar ends, and which poses draw in the accent on purpose.

Everything else is measured from the images.
