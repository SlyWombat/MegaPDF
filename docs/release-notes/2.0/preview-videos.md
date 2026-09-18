# The 2.0 App Store preview videos (#146 §3)

Nine clips — iPhone 6.9", iPad 13" and Mac, in `en`, `fr-CA` and `fr-FR` — cut
from 2.0.0 builds on 2026-09-18 and read frame by frame. This is the companion
to [`capture-gate-report.md`](capture-gate-report.md), which covers the stills.

**Nothing was uploaded anywhere.** Every clip is sitting on the machine that
shot it, and the release hold is untouched.

---

## 1. The verdict

| clip | frame | duration | fps | codec | audio | stills read | gate flags | **still open** |
|---|---|---:|---:|---|---|---:|---:|---:|
| iPhone 6.9" — en / fr-CA / fr-FR | 1320 × 2868 | 29.57 s | 30 cfr | H.264 yuv420p | silent AAC stereo | 90 | 22 | **0** |
| iPad 13" — en / fr-CA / fr-FR | 2064 × 2752 | 29.57 s | 30 cfr | H.264 yuv420p | silent AAC stereo | 90 | 3 | **0** |
| Mac — en / fr-CA / fr-FR | 1920 × 1080 | 29.57 s | 30 cfr | H.264 yuv420p | silent AAC stereo | 90 | 50 | **0** |

Every one of the 270 stills was opened. The seventy-five flags are five things,
each of them read and cleared by eye — §4 — and the six defects the eye found
that the tool could not are in §3, all fixed and all re-shot.

Every clip is inside App Store Connect's **15–30 s**, at a size the listing
takes, at a constant 30 fps, H.264, with the silent stereo track the upload is
refused without (`MOV_RESAVE_STEREO`). The sizes are the ones
`docs/app-store-listing.md` § App preview videos names for iOS and the Mac App
Store's app-preview frame for the Mac.

**No other platform is named or shown in any of the nine** (App Review 2.3.10).
The clips are the app and its own demo agreement — *Equipment Rental Agreement*
/ *Contrat de location d'équipement*, between "Sunrise Tool Rental" and the
customer — and nothing else. The iOS clips no longer open on the iOS home
screen; see §3.

## 2. What is in a clip

The same story on all three platforms, which is the story the listing
screenshots pose one frame of each: open the unfilled agreement, tick two of
the three options, place the saved signature on the line, type the printed
name under it, find every occurrence of *rental* / *location* and step through
the three matches, and finish on the completed page.

The demo person is **Jane Whitfield** (en), **Hélène Bélanger** (fr-CA) and
**Céline Lefèvre** (fr-FR), accents intact, in all nine — read off the last
frame of each clip at full size, not by OCR.

Each clip is **entirely in its own language**, toolbar, sheets, alerts, page
and search term together. The two French runs even differ in the keyboard: the
fr-FR simulator draws AZERTY where fr-CA draws QWERTY.

**Redaction is not in the clips.** The story the recording scripts drive is
fill–check–sign–find; redaction has its own listing screenshot and its own
review shot, and adding it to the choreography is a change to
`DemoFlowUITests.swift` and `macos-record-demo.sh` rather than a re-cut.

## 3. What the frames showed — the six defects

Every clip was cut into one still per second (`tools/preview-gate.py`) and each
still opened. Six things came out of that, all of them fixed and all of them in
clips that would otherwise have gone to the store:

| | what it was |
|---|---|
| **Every iOS clip opened on the iOS home screen** | The first six frames were the springboard — the wallpaper, other apps' icons, and the UI-test runner's icon among them — then half a second of the app's black launch screen. The trim looked for the first frame whose average saturation was under 10, which the *black launch screen* satisfies, and then backed up 0.2 s, which is squarely in the springboard. It takes saturation **and** luma now: the springboard is Y 158–168, the launch screen Y 16, the app Y 103–217. Neither end is padded. |
| **The Mac clip ran at four times speed** | `screencapture -v` records for the whole of its `-V` and cannot be stopped — SIGINT it ignores, SIGTERM kills it and takes the unfinalised file, both measured. The story is 34.6 s; the recorder ran 120; the time compression fitted two minutes into thirty. `-V` is a ceiling now and the cut keeps the wall-clock story. The `-t` that does the cutting also had to move **after** `-i`: screencapture writes a variable-rate movie, 253 frames in ninety seconds, and before `-i` the cut landed 3.7 s late. |
| **The French Mac clips typed the names without their accents** | *Hlne Blanger* and *Cline Lefvre*. `cliclick t:` drops every non-ASCII character, so the one thing this capture set exists to prove was the one thing it disproved. AppleScript's `keystroke` is worse — it types the base letter and drops the mark on a neighbour, *Halane Balanger*. The name goes through the pasteboard now, which lands it exactly. |
| **The Mac toolbar coordinates were the pre-#144 ones** | Sign, Add text and a fit-page button that no longer exists were clicked at positions that in 2.0 are empty bar. They are measured off a probe shot now (`tools/macos-measure-toolbar.py`) and taken by index, because the order is the same in every language and the widths are not: French puts Sign at 172 and Add text at 250 where English has 136 and 190. |
| **A notification banner sat in the right of the Mac frame** | macOS puts banners in the top-right of the screen; a 1920-wide window at x=320 on the 2560-wide capture display ends at 2240, inside the banner. The window sits at x=0 now, 640 px clear of it, and no setting on the machine was changed to get that. |
| **The iOS recording drove the whole UI-test bundle** | `-only-testing:MegaPDFUITests` is four classes, the App Review walkthrough among them, and every one of them was driven on camera. The one test is named in full now, and the simulator's container is wiped first so the signature library holds one card rather than whatever the last run drew. |

## 4. What the gate said, and what the eye said back

`gate.py --store video` over all 270 stills, one language at a time — a clip is
time-compressed from however long its own take ran, so still 12 of two
languages is not the same moment and the cross-language checks stand down.

**Seventy-one of the 270 stills carry a flag, and none of them is a defect.**
Seventy-five flags in all, and they are five things rather than seventy-five:

| flags | where | what it is |
|---:|---|---|
| **40** `clipping` | every Mac still from the moment the page is scrolled | The scrollbar's chevron at the right edge, at row 1046, byte-identical in all forty. The check's own notes say a scrollbar is not a defect. |
| **17** `chrome` | iPhone stills | H.264 ringing along the clock, and the status bar crossing between its dark and light states as the find bar opens. See below, and §5.2. |
| **10** `person` | fr-FR Mac stills | *"'Céline Lefèvre' is on screen without any of its accents."* **The tool is wrong and the eye is right** — see below. |
| **6** `clipping` | iPhone and iPad, bottom | A sheet caught mid-presentation, or the keyboard. Both genuinely touch the bottom of the frame; that is what they look like while they move. |
| **2** `clipping` | iPhone, right | The device's rounded corner mask read as strokes at the edge — the same shape of thing as the two-pixel Windows fringe in the stills report §5. |
| **2** `squiggle` | iPhone, on the just-placed signature | The red ⊗ that sits on a selected signature. Not a spelling underline. |

**The accents are confirmed twice over, and the one disagreement is the
reader's.** `person` reads the demo name off the image and checks that the
accents are *there*:

| set | `person` |
|---|---|
| iOS — en / fr-CA / fr-FR | **19 / 20 / 20 pass**, 0 flag |
| Mac — en | 30 skip (English poses no accent to find) |
| Mac — fr-CA | **10 pass**, 0 flag |
| Mac — fr-FR | 4 pass, **10 flag** |

So *Hélène Bélanger* and *Céline Lefèvre* are read correctly off fifty-four
stills, including four of the very fr-FR Mac clip whose other ten are flagged.
Opened at full size, t019 through t029 of that clip all read *Céline Lefèvre*
with both accents. It is the reader, not the image — the same thing that
happened to Hélène Bélanger on a 420 dpi Android capture (stills report §3),
and the check is right to say so out loud rather than pass quietly.

**The `chrome` check had to be retuned for video, and the retuning is measured.**
A still out of an H.264 clip has no flat areas: the quantiser smears the status
bar's grey over a dozen values and the ringing along the clock reads as ink. On
the English iPhone preview that moved up to **14.2 %** of the band between two
frames with nothing in the bar, against **12.6 %** for a planted badge — the
check could not tell them apart at all. Widening each background tone by 16
(`ink_tolerance`, set on the `video` profile and nowhere else) puts the same
clean frames at 3.9–4.6 % and the badge at 24.6 %; planted back into the set,
`gate.py` flags it at 25.2 %.

**No set that was already signed off is measured any differently**, and that is
run rather than argued: `gate.py --store ios` over the 48-image App Store set,
once from `origin/main` and once from this branch, with tesseract in both,
comes back **identical — 48 images, 496 checks, every status and every note the
same.** `ink_tolerance` defaults to 0 and only the `video` profile sets it.

## 5. What no check can see here, and the four things for Dave

The gate is a comparison, so anything the same in every still of a clip is
invisible to it (stills report §4). Four of those, found by eye, and all four
are judgement rather than defect — none of them is wrong, and each of them is
something someone might want different:

1. **The Mac frames carry the desktop in their bottom two corners.** The
   recording is the window's content rect and the window's bottom corners are
   rounded, so the wallpaper shows through: about **70 saturated pixels in the
   bottom-left 40 × 40 and 38 in the bottom-right**, in every frame of all three
   Mac clips. The top corners are clean. This is the same judgement the stills
   report made about the two-pixel Windows fringe, and the same answer is
   available — it is real, it is tiny, and the fixes are either retouching a
   store asset or hanging the window off the edge of the screen. **Left as
   shot; say the word and it goes.**
2. **About two seconds of each iPhone clip has a pale status bar.** As the find
   bar opens, the glyphs go white while the background behind them is light
   grey: contrast 0.21 where every other frame reads 0.87. It is the transition
   only — the signed-off `search` still has black glyphs on that same light bar
   — and it is 2.0 s of 29.6 s on the iPhone, 0.2 s on one iPad clip and not at
   all on fr-FR.
3. **A sixth of each iOS clip is a modal alert.** *Tap the page where the
   signature should go* and *Tap the page where the text should go* hold for
   about five seconds between them. That is what the app really does, and it is
   also five of the thirty seconds a shopper watches.

4. **The Mac page is small in its frame.** The page is drawn **817 px wide in the clip and 817 px wide in the
   listing still** — the same 100 % zoom — but that is 57 % of a 1440 × 900
   still and only **43 % of a 1920 × 1080 preview**, so the clip carries half a
   frame of grey margin the stills do not. Zooming in would fill it, and would
   stop matching the stills.

## 6. Where they are, and how to run it again

Not in git: they are 0.4–4 MB each and the stills' convention is that a capture
set lives on the machine that shot it (`tools/mac-mini.md`).

| | |
|---|---|
| all nine clips | capture Mac, `~/captures/video-2.0/{ios/<lang>,macos}/` |
| the stills and sheets | kdocker2, `~/megapdf-video-work/{preview-out,mac-out}/` |
| contact sheets, committed | [`preview-contact-sheets/`](preview-contact-sheets/) |

```bash
# on the capture Mac
tools/ios-demo-video.sh   en 'iPhone .*Pro Max' iphone-6_9 ~/captures/video-2.0/ios/en
tools/ios-demo-video.sh   en 'iPad Pro 13'      ipad-13    ~/captures/video-2.0/ios/en
tools/macos-record-demo.sh ~/app-macos/MegaPDF.app ~/captures/video-2.0/macos light en

# anywhere with ffmpeg, ImageMagick and tesseract — not the Mac, which has neither
python3 tools/preview-gate.py -o out <clip>...
python3 tools/capture-gate/gate.py --store video out/frames/<lang> -o out/gate/<lang>
```

`TRIM_ONLY=1` re-cuts an iOS clip from the raw without recording it again,
which is how the six iOS clips were re-cut after the springboard fix.

---

*The version was read out of each built artefact before recording, not out of
the source: `com.megapdf.ios` **2.0.0** from `~/dd-ios` for the six iOS clips
(the script prints it), and `CFBundleShortVersionString` **2.0.0** in
`~/app-macos/MegaPDF.app` for the three Mac ones. Both built on the capture Mac
from main `167afe3`'s tree, with the PDFium pin that tree carried,
`pdfium-7934-megapdf-1a09f6c61f3b`.*

*main moved while these were being cut — #241 and #246 landed, and with them a
new PDFium pin, `…-a02dc04f63e3`. **Nothing in it is in shot.** Both new
patches are save-with-security ones (0029 drops a removed encryption
dictionary, 0030 names the dictionary written), the clips never save, and the
document they show is not protected. The same is true of the #237 work that
landed beside it, which is the Linux window at its 480 px minimum. If something
that **is** in shot changes before submission, these are two commands per clip
to re-cut — §6.*
