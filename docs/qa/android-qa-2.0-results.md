# Android §1b QA pass for 2.0 — what it found (#146)

Run on a Linux server, 2026-09-17, against `main` ee2751d plus the fixes in this
folder's branch. The rig is `tools/android-qa/`; the checklist it walks is
[`android-screen-inventory.md`](android-screen-inventory.md).

## Set-up

Everything ran in one container — JDK 17, the Android SDK, the NDK, CMake and the
emulator inside the image, talking to the host's `/dev/kvm`. Nothing was installed
on the host. Four AVDs, API 36 (`google_apis`, x86_64), written by hand so their
dp sizes are exactly the ones this pass claims to cover:

| AVD | Pixels | Density | dp | Stands for |
|---|---|---|---|---|
| `megapdf-small` | 720 × 1440 | 320 | **360 × 720** | the smallest phone still sold |
| `megapdf-large` | 1440 × 3200 | 480 | **480 × 1067** | the largest phone |
| `megapdf-tablet` | 1600 × 2560 | 320 | **800 × 1280** | a 10" tablet |
| `megapdf-flows` | 1440 × 3200 | 480 | 480 × 1067 | the flow walk and the large files |

The driver works the real UI — dump the view hierarchy, find a node by the label
the app actually drew, tap the middle of it — so the flows go through the system
document and photo pickers rather than round them.

**Captures: 27 cells × ~50 screens = 1,341 images.** A cell is
*device × language × theme*, plus a largest-text pass per language:
3 devices × (en, fr-CA, fr-FR) × (light, dark), then the same three languages at
`font_scale 2.0`.

## What the pass found

Five defects, four fixed on this branch.

| | | Fixed |
|---|---|---|
| **#178** | Setting, changing or removing document protection, and the owner unlock, left the viewer showing **blank pages**. Every command that reopens the file in place. The file was always fine; only the viewer was wrong. | yes |
| **#180** | Material's baseline **purple** on the Add text size and font chips and the signature sheet's Draw / Type / Photo — four colour roles `LightColours` left to the baseline tonal palette. Two of those screens are Google Play capture poses. | yes |
| **#183** | At the largest text size, **dialog buttons behind the keyboard** — including the pair that made protecting a document impossible at that text size. | yes |
| **#181** | The viewer **title clipped at a word boundary** with no ellipsis, and showed nothing but the bullet once the document was dirty. | yes |
| **#186** | At the largest text size the find bar's query is vertically clipped by the top bar's fixed height, and the three-button unsaved-changes prompt breaks "Abandonner" across two lines. Both are design calls on the find bar and the button row, so they are reported rather than fixed. | no |

Two smaller ones fixed in passing (4cc591c): the signature sheet's Draw button
read **"Dessin"** — a different French word, not a visibly shortened one, because
`maxLines = 1` clipped it without an ellipsis; and the find bar's match counter
had no width of its own, so at the largest text size "Aucun résultat" left the
query field a few pixels wide.

### Observations, not filed

- **A small added text box disappears under its own selection chrome.** At 1× zoom
  on a phone, a 12 pt text box is about 30 px wide and its ✕ and ✎ badges are
  about 40 px each, so selecting it hides it. Zooming in fixes it.
- **Back with a stamp selected closes the document** rather than dropping the
  selection. Arguable either way — Back exiting a mode is as much an Android
  convention as Back leaving a screen — but it means a stray Back during
  placement puts up the unsaved-changes prompt.
- **`signature_card_a11y` reads "Mega W.. Tap to place…"** — the format string ends
  the name with its own full stop, so a name that already ends in one doubles it.
  Only affects what TalkBack says.
- **The find bar's query field is narrow on a small phone even at the default text
  size**, because the counter and two chevrons share the bar. Standard
  single-line behaviour, no text lost.

## Dark mode, and the language sweep

Both checked across the whole matrix by `tools/android-qa/compare.py`, so the eye
went where it was needed.

**Dark:** the app is light-only on purpose (`ui/Brand.kt`, #40). Every screen that
differs between a light cell and its dark twin is one where the *system* is
drawing — the document picker, the create-document picker, the soft keyboard over
a dialog or the find bar. **No surface of the app's own changes in dark mode**, in
any cell. The light-only decision holds with no leakage.

**Language:** no screen of the app's own is pixel-identical to its English twin.
The only identical captures are the two system pickers, which stay in the device
language because only the *app's* locale is overridden — correct, and what the
Play listing crops out.

The comparison is by absolute changed-pixel count, not by fraction: one swapped
toolbar word is ~2,000 pixels, which is 0.4 % of a phone screen but 0.05 % of a
tablet's, and a fraction threshold called nine translated tablet screens
untranslated.

## The flow walk

Every flow #146 lists, on the review form (`tools/gen_review_form.py`) with real
taps. 18 of 18 steps pass.

| Flow | Result |
|---|---|
| open | 20 s including the picker; the title is the file name |
| scroll | down twice and back |
| zoom | double-tap 1× → 2× → 1×, and pinch. Both need **real touch events** — see the note below |
| find | "insurance" → 1 of 4, Next twice → 3 of 4 |
| tick | 3 drawn squares and 2 AcroForm widgets, all marked, document dirty |
| sign | typed a name, placed it above the rule |
| move a signature | dragged, committed |
| add text | typed and placed |
| edit the document's own text | retyped the subtitle line |
| undo and redo | 3 each |
| save | back over the opened file; the dirty marker clears |
| save as | a copy through the system create-document picker |
| set protection | written and saved; the document reopens protected |
| reopen protected | the wrong one is rejected, the right one opens it |
| remove protection | written and saved |
| reopen unprotected | no prompt |
| close with unsaved changes | Save · Cancel · Discard; Cancel keeps the change; Discard returns to Home |
| killed mid-edit | see below |

**Not applicable on Android.** Whiteout, Shrink for email and Print have no
Android screen — the tool set is Sign · Add text · Search · Undo · Redo — so
three of §1b's flows do not exist here. They are reported as not applicable
rather than passed.

**Recovery after a kill.** There is none on Android by design: `EditHistory` is
single-session and keeps no journal, unlike the desktops. Walked anyway to see
what *does* happen: after `am force-stop` mid-edit the app comes back to Home,
the unsaved tick is gone, there is no restore offer, and **the file on disk is
byte-for-byte what it was** (4,367 bytes before and after). Nothing is corrupted;
the edit is simply lost. Worth deciding for 2.1 whether that is the intended
contract on a platform that reclaims backgrounded processes routinely.

### A note on driving zoom

`adb shell input tap` starts a JVM per call, so two of them never land inside the
300 ms double-tap window, and `input` has a single pointer, so it cannot pinch at
all. Driven that way, **both zoom gestures look broken and are not**. The rig
writes to the kernel input device instead (`tools/android-qa/touch.py`), which is
how the gestures above were actually exercised. Anyone repeating this pass should
not report a zoom bug found with `input tap`.

## Large files

All 13 fixtures from `tools/gen_large_fixtures.py`, on the 480 dp phone with a
512 MB Dalvik heap. Peak is `VmHWM` of the app process — the high-water mark of
its resident set, read from `/proc` with a rooted adbd. Open is from the tap on
the file to the first page drawn. **11 of 11 walked: open, scroll, whole-document
search, add text, save a copy, all clean.**

| File | Pages | Open | Peak after open | Peak | Search | Save a copy |
|---|---:|---:|---:|---:|---|---:|
| big-scan-250mb | 100 | 2.0 s | 316 MB | **422 MB** | 39 of 39 | +0.4 s |
| big-1gb | 400 | 4.6 s | 289 MB | **345 MB** | 32 of 32 | +7.4 s |
| huge-2_5gb | 1000 | 4.7 s | 275 MB | **353 MB** | 74 of 74 | +15.6 s |
| huge-image-page | 1 | 4.4 s | 356 MB | 371 MB | 1 of 1 | +1.1 s |
| deep-10000 | 10000 | 4.5 s | 285 MB | 363 MB | 426 of 426 | +1.3 s |
| wide-poster (200 in) | 1 | 2.1 s | 211 MB | 215 MB | 15 of 15 | +0.7 s |
| wide-userunit (10 m) | 1 | 2.1 s | 202 MB | 214 MB | 4 of 4 | +0.4 s |
| tall-receipt | 1 | 2.1 s | 211 MB | 215 MB | 12 of 12 | +0.9 s |
| mixed-sizes | 40 | 2.1 s | 233 MB | 331 MB | 3 of 3 | +0.5 s |
| many-objects | 4 | 2.1 s | 322 MB | 330 MB | 11 of 11 | +0.7 s |
| many-fields | 201 | 2.1 s | 271 MB | 373 MB | 30 of 30 | +0.9 s |

Every search count is the fixture's stated canary count, and matches the figures
the Mac and iOS legs reported on #146. "Save a copy" is given as the time over
the ~23 s the create-document picker takes on its own, which is what the rest of
the number is.

**The two the issue asks for by name.** The 1 GB file peaks at **345 MB** and the
2.5 GB file at **353 MB**. The app with a 3 KB document already sits at about
208 MB, so the documents themselves account for roughly 137 MB and 145 MB — in
line with the ~110 MB the iOS leg measured on the same 2.5 GB file, and nowhere
near a phone's limit. Neither number tracks file size, which is the point of
#147: the 250 MB scan is the most expensive file in the set, because 100 pages of
300–600 dpi images cost more to *render* than 2.5 GB of text costs to *read*.

## §3, the demo name

`accents.py` drives the app's own screenshot flow in each language, stamps the
name onto the demo page, saves a copy, pulls it and reads the text back out with
`pdftotext`. **3 of 3 languages.**

| Language | `screenshot_text` | In the dialog | In the saved PDF |
|---|---|---|---|
| en | Jane Whitfield | yes | yes |
| fr-CA | **Hélène Bélanger** | yes | yes |
| fr-FR | **Céline Lefèvre** | yes | yes |

Matched after NFC normalisation as well, so the accents are real characters and
not a rendering coincidence.

This is checked against the file rather than against a screenshot on purpose: a
capture shows the glyphs drawn, and what §3 needs is the glyphs *in the document*.

## Reproducing any of it

```
docker build -t megapdf-146-android:base tools/android-qa
# then tools/android-qa/README.md
```

`compare.py` writes `compare.json` beside a capture set; `flows.py` writes
`flows.json`; `accents.py` writes `accents.json`. None of the output is committed —
it is a few gigabytes of PNGs — and none of it touches the PDF corpus.
