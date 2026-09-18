# 2.0 capture gate — the consolidated report (#146 §3)

Every store capture set that exists for 2.0.0, measured by `tools/capture-gate`
and set beside the human-eye review each of those sets already had. Built to be
read at speed: the verdict first, then the short list of images worth opening,
then where the tool and the eye disagreed.

**Nothing here was uploaded anywhere.** Every set is sitting on the machine that
shot it.

---

## 1. The verdict

| store | slot | images | checks passed | not run | **to look at** |
|---|---|---:|---:|---:|---:|
| **Mac App Store** | 1440 × 900 | 18 | 150 | 24 | **0** |
| **App Store (iOS)** — listing | 1320 × 2868, 2064 × 2752 | 36 | 258 | 114 | **0** |
| **App Store (iOS)** — review | the same, plus dark | 30 | 210 | 100 | **0** |
| **Google Play** — phone | 1080 × 2400 | 24 | 171 | 77 | **0** |
| **Google Play** — tablet | 1600 × 2560 | 24 | 171 | 77 | **0** |
| **Microsoft Store** | 2482 × 1541 | 15 | 137 | 8 | **0** — see §5 |
| Linux (not a store yet) | 480–1280 wide | 234 | 1 280 | 972 | 10 → **#237** |

All six store sets are **gate-clean**, and every one of them was also read
image by image by a person. The two reads agree.

The Linux set is the odd one out because it is a QA matrix rather than a
listing: its ten are all at the 480 × 360 minimum window, and both things they
show are real defects, filed as **#237**. Linux ships after the other four, so
neither blocks 2.0.

*"Not run" is a count, not a gap:* it is checks that stood down and said why —
`zoom` on a platform with no zoom control, `language` on an English set,
`person` on the shots that pose no name.

## 2. What a person should still look at

Short, and none of it is a defect the tool found:

1. **One image per device, properly.** `light-01-viewer.png` (Mac),
   `iphone-6_9-viewer.png` and `ipad-13-viewer.png` (iOS), `android-viewer.png`
   on each of phone and tablet (Play). Everything the gate does is a
   comparison, so anything identical in *every* image of a set is invisible to
   it — see §4. These five are where that lives.
2. **The two French listings, side by side.** fr-CA and fr-FR are **the same
   image** in 5 of 6 Mac poses, 4 of 12 iOS listing poses and 7 of 8 Play poses.
   That is correct — the demo person is the only string that differs between
   the two catalogues on any listing screen — but three listings go up looking
   alike, and whether that is wanted is a decision, not a defect.
3. **Whether each set sells the app.** Nothing here measures whether a capture
   makes someone want MegaPDF, whether the pose matches what the listing copy
   promises, or whether the French sounds like a person wrote it. Those are
   still yours; the difference is that they are now the *only* ones.

## 3. Where the tool and the eye disagreed

Both directions, which is the point of running both.

### What the eye caught that the tool missed

| | |
|---|---|
| **The iPad's date, after a run crossed midnight** | The English set read *Thu Sep 17* and both French sets *Fri Sep 18*: `simctl status_bar` overrides the time and the indicators, not the date. **The gate would have missed it** — and does not now. The status bar is compared whole across languages rather than only its right-hand end, and the band stops per device above the iPad's navigation bar. Planted back into a copy of the set, it lights up all six iPad poses in both French sets at ~37 % of the bar's ink, and leaves every iPhone pose alone. |
| **The launcher taskbar in 24 Play tablet captures** | Still missed, and structurally — §4. |
| **`Mega PDF` against `MegaPDF`** | The gate reads text but has no idea which spelling is meant. Decided 2026-09-18: **MegaPDF** everywhere. The Windows title bar and About said "Mega PDF"; the Microsoft set in §5 was shot after that change and reads MegaPDF. |

### What the tool caught that the eye missed

Nothing, in these five sets — which is the right answer when five people have
already read them image by image. Where it *has* earned its keep is the set
nobody re-read: **234 Linux captures, already reviewed by hand for #158, held
two real defects at the minimum window size** (#237).

### Where the eye was right and the tool was wrong

| | |
|---|---|
| **"Hélène Bélanger without her accents"** on a 420 dpi Android capture | The screen magnified reads *Hélène Bélanger* with all three accents; tesseract returned them flat. The check now refuses to testify about accents on an image where the reader resolved none anywhere — it says so and stands down. (On this machine's container the same image reads correctly, so the reader, not the image, is the variable.) |
| **`size` stood down 48 times on Play** | It was looking for a slot named `desktop` that the Play profile does not define. It now finds the slot by size, names it, and prints the ratio — which is what the open Play Console question needs: **1080 × 2400 is 9:20, ratio 2.222**. |

### What both reads agree on, independently

- The Mac's fr-CA and fr-FR sets are the same image in five of six poses.
- iOS's captures are not pixel-deterministic and the Mac's are: the gate's
  layout comparison tolerates the one-to-two pixel drift of a presented sheet,
  and its status-bar comparison does not touch it.
- Play's status bars are **byte-identical** across the three languages on both
  devices — the posed 9:41, the full battery, no badge.

## 4. What the gate cannot see, and why

**Anything that is the same in every image of a set.** Every check is a
comparison — against a slot size, against the same pose in another language,
against a set already signed off. Device chrome has no sibling to disagree with
it, so:

- the **launcher taskbar** across all 24 Play tablet captures is cleared by this
  tool, today, knowing it is there;
- the **Wi-Fi badge** of the September dry run was the same shape of thing.

Two rules were tried against the data and neither survived it:

| rule tried | why it was dropped |
|---|---|
| *foreign colour in the top and bottom bands* — the taskbar's icons are green, blue and yellow, and MegaPDF's chrome is grey, black, white and one blue | the **fixed** tablet set carries more of it than the broken one: 4 947 px against 4 528, and the clean phone 2 911. The app's own bar and Android's battery are colourful too. |
| *the bottom band should be painted in a tone the app uses elsewhere* | the clean tablet's bottom band tone covers 4.3 % of the rest of its image and the broken one's 2.5 %. Too close to separate, and the ordering is not even stable between poses. |

So the sheet names the constants instead: one image per device and appearance,
with a line saying why that one needs an eye. A check that agreed with the
answer would have been tuned to it; this is the honest version.

## 5. The Microsoft Store set

Shot 2026-09-18 07:14–07:22 EDT on GPD-DAVE, from the installed
**ElectricRV.MegaPDF 2.0.0.0** package (checked before each run; About reads
2.0.0), built from the tree that renamed the product to MegaPDF. English, fr-CA
and fr-FR, five poses each, 2500 × 1550 window at 150 % scale (1667 effective px,
full-label toolbar), 100 % zoom, one-entry MegaWoman signature library. The
driver is `tools/screenshots-windows/Shoot-Set.ps1`. The set is in
`artifacts/store/screenshots/{en,fr-CA,fr-FR}/` and its sheet is beside it in
`artifacts/store/gate-microsoft/`. The pre-2.0 set is kept aside in
`artifacts/store/screenshots-pre2.0/`.

| language | images | checks passed | not run | **to look at** |
|---|---:|---:|---:|---:|
| en | 5 | 39 | 6 | **0** |
| fr-CA | 5 | 49 | 1 | **0** |
| fr-FR | 5 | 49 | 1 | **0** |

`person` read *Jane Whitfield*, *Hélène Bélanger* and *Céline Lefèvre* with
their accents intact in the four poses that show the name. `zoom` read 100 % in
all 15. `toolbar` found one row. `language` found no English on either French
set, and `privacy` found no home-folder path. The shrink pose poses no name, so
`person` stands down there, and `language` does the same on English.

**The English person is now Jane Whitfield on Windows too.** Windows alone still
used "Dana Whitfield" (`gen_store_docs.py`), which the gate's `person` check
would have failed and the listing would have shown against every other store's
Jane.

### The `microsoft` profile, the first time it met a real set

It had never met a real set, and the first run flagged all 15 images. None of
those flags was a defect in an image. All of them came from the profile being
written from a README rather than measured. Each fix below is a measurement, and
each fix was then tested by planting the defect it could have hidden, in a copy
of the set:

| flag | what it really was | fix | planted to prove it still bites |
|---|---|---|---|
| `size`: 2482 × 1541 is not a slot | The Store has no fixed desktop slot. Its only rule is **at least 1366 × 768** (docs/microsoft-store-listing.md § Screenshots), and the 1.7 listing went up at this exact non-16:9 size. The profile listed 16:9 sizes and a 2500 × 1550 frame the harness never produces, because the DWM bounds of a 2500 × 1550 window are 2482 × 1541. | The frame is 2482 × 1541. Any size at or above the floor passes. | — |
| `clipping` on three edges of every image | **Real, but not clipped text.** The DWM bounds take in Windows 11's 1 px border, and the capture reads the screen, so the outer **two pixels on every side are border over the wallpaper**. Windows Spotlight had just put a textured photo behind the window, and those lines read as 70–700 "strokes". Two pixels in, nothing. | `edge_inset: 2`. The check starts inside the border. | A word pasted against the right edge is flagged (`right: 3 strokes in 14 px`). |
| `accent` in every image | **The app's own icon** in the title bar: a blue 24 px square at (13, 10), about 400 accent px in every shot. In the two editor poses, the inline editor's **focus underline** is drawn in the accent, which is the edit being shown. | `accent_ignore` for the icon's corner; `edit-text` and `add-text` are poses that draw the accent. | A 2 px accent box around the name in the checkbox pose is flagged (1,112 px). |
| `toolbar`: two rows (after the size fix) | Windows paints the title bar, the command bar and the canvas in one Mica backdrop. So the band had no edge to stop at, ran on to 200 px, and counted **the page's top edge** (row 153) as a second row of buttons. And `zoom`, reading "the first row of ink", read **the title bar**. | The band is fixed at 150 (`fixed`). The title bar, rows 0–45, is its own strip, and `toolbar` and `zoom` start below it. Only gaps of 4 rows or less are closed, because the labels sit beside the icons. | — (no two-row frame exists at this size; a pre-#144 bar would sit a few pixels under the first and stay separate at `gap: 4`) |
| — | The listing order was spelled `textedit`/`checkboxes`/`addtext`, which match no file. | The order is the file numbers: edit-text, checkbox, signature, shrink, add-text. | — |

A red squiggle drawn under the name in the add-text pose is flagged as well
(`squiggle`, 98 px). The fixes left that check as it was.

### Where the tool and the eye disagreed, this set

**What the tool caught that the eye missed.** The two-pixel wallpaper fringe
above: invisible at listing scale, but real, and on every Windows capture the
harness has ever taken. It is not worth a re-shoot. The rounded corners show
the desktop too, which is simply how a Windows 11 window looks.

**What the eye caught that the tool did not look for**, and worth a glance, none
of it a defect:
- **The run was spoiled once, by a person, not by the app.** Partway through the
  first fr-FR run someone started sharing the desktop, and a "Your desktop is
  currently shared with …" banner with an email address landed over the page.
  The later clicks missed, so no box was ticked. Those images were deleted and the
  language was re-shot. The gate would have flagged the missing ticks through
  `siblings`, but a banner like that on an otherwise correct frame is exactly the
  one-image problem §4 describes.
- **The backdrop changed between runs.** Spotlight rotated the wallpaper at about
  07:05, and Mica took on its colour: one French set came out beige while the
  other two were pink and blue. All three were re-shot on the new wallpaper, so
  the set agrees. A capture machine on Spotlight will do this again. A fixed
  wallpaper for the shoot would stop it.
- **Keyboard focus shows in two places:** a dark focus square around the second
  checkbox in every `02-checkbox` (all three languages alike), and a heavier
  outline on the dialog's OK in fr-CA's `04-shrink` only. The accent check can't
  see either one, because neither is drawn in the accent.
- **The inline editor sits over the next line.** In `01-edit-text`, the open
  editor's underline runs through the top of the "Rental period" line under it.
  That is what the app does, so it is a pose choice, not a capture fault.

## 6. Running it again

The sheets are self-contained HTML — thumbnails inlined, full images linked
beside them — so one can be mailed or opened on a phone with no network.

| set | where its sheet is | the command |
|---|---|---|
| Mac | `kdocker2:~/megapdf-157-work/cg-gate/final/mac/` | `gate.py --store mac ~/gate-macos -o out` |
| iOS listing | `.../final/ios-listing/` | `gate.py --store ios ~/gate-ios --only /listing -o out` |
| iOS review | `.../final/ios-review/` | `gate.py --store ios ~/gate-ios --only /review -o out` |
| Play phone | `.../final/play-phone/` | `gate.py --store play <set>/phone -o out` |
| Play tablet | `.../final/play-tablet/` | `gate.py --store play <set>/tablet -o out` |
| Linux | `kdocker2:~/megapdf-157-work/cg-out2/linux/` | `gate.py --store linux <shots> -o out` |
| Microsoft | GPD-DAVE `artifacts/store/gate-microsoft/` | `gate.py --store microsoft artifacts/store/screenshots -o out` (tesseract in a container: the WSL box has no sudo) |

Add `--against <a set you already signed off>` for a re-shoot: it reports
everything that changed, and everything that changed had better be something
you meant. The Android session's `--against` run over the tablet re-shoot lit
up all 24 images at 18–25 % of pixels, which was the taskbar coming out.

Four checks need `tesseract`; the numbers in §1 were measured with it
installed. Without it they stand down and the sheet's header says so.

---

*Measured against `tools/capture-gate` at the commit this file arrived in,
over sets built from main `476ce157` (Mac, iOS), `4200f1c`'s tree (Play),
`c8b7a7e` (Linux) and the MegaPDF rename's tree (Windows, 2.0.0.0).*
