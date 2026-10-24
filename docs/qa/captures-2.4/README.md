# The 2.4 listing captures, as shot and as read

The reviewed store images for 2.4 in five locales (`en`, `fr-CA`, `fr-FR`, `es-419`, `es-ES`), filed
the way `docs/qa/captures-2.2/` is, so the set that was looked at is the set that goes up.

**Nothing here is published.** Putting images on a listing is a separate, deliberate act in each
console (App Store Connect, the Play Console, Partner Center) and is Dave's call. The website is not
deployed either: `website/megapdf/screenshots/linux/` has no `es-419` or `es-ES` folder yet, and the
Linux metainfo points at them.

Shot on 2026-10-23 from `main` after the Spanish rigs landed (#789, d065597). The apps are the 2.3.0
version number plus everything merged since; `main` still reports 2.3.0 in `android/app/build.gradle`
and the Avalonia csproj.

## What is here

| Folder | Store | Images | Rig | Read in |
|---|---|---|---|---|
| `microsoft-screenshots/<lang>/` | Microsoft Store | 8 × 5 = 40 | `tools/screenshots-windows/Shoot-Set.ps1` on the packaged build | `microsoft-READING.md` |
| `macos-screenshots/<lang>/` | Mac App Store, 1440x900 | 7 × 5 = 35, with a `RUN.txt` per language | `tools/macos-store-captures.sh` | `apple-READING.md` |
| `ios-screenshots/<lang>/listing/` | App Store, iPhone and iPad | 9 × 2 × 5 = 90, with logs beside each PNG | `tools/ios-screenshots.sh` | `apple-READING.md` |
| `play/phone/<lang>/` | Google Play phone, 1080x2400 | 8 × 5 = 40 | the hosted **Android Screenshots** workflow (run 40038655357) | `play-linux-READING.md` |
| `linux/<lang>/` | Linux listing, 1280x800 | 7 × 5 = 35, with a `RUN.txt` per language | `tools/linux/store-captures.sh` | `play-linux-READING.md` |

Folder names: `fr-FR` is `fr` for the Mac and iOS rigs (App Store Connect's name), and Spain is `es` on
the Mac and iOS rigs and `es-ES` elsewhere. 240 images in total. Every image was displayed and read by
the agent that shot it; the per-image tables are in the three READING files. Two images were also opened
by the coordinator per store group (Spanish play, Windows and iOS/Mac), and found right.

## Gate results

`python3 tools/capture-gate/gate.py --store <store> <set> --out <dir>`, then again with
`--against docs/qa/captures-2.2/<set>` for `en`, `fr-CA` and `fr-FR`.

- **Microsoft after the #802 re-shoot:** en, fr-CA and fr-FR 0 to look at; es-419 and es-ES 2 each
  (03 and 04, the same `person` OCR-accent false positive; accents checked whole by eye in 04 and 07).
  Against 2.2 (en, fr-CA, fr-FR): 04 differs by 0.43 percent and 07 by about 0.5 percent of pixels, all
  inside the signature and the marked name, which is what changed on purpose.
- **Microsoft, Play, Linux:** every flag is the `person` check on a Spanish image, and every one was
  checked at full size. The gate's OCR has no Spanish data (`eng+fra` only) and drops accents, so
  it cannot clear `Sofía Ramírez` or `Lucía Fernández`; the accents are whole in every image.
- **iOS listing:** 0 flagged in en, fr-CA and fr-FR; 3 in es-419 and 2 in es-ES, the same OCR
  accents plus one real layout note (es-419 iPad `pages`: document text runs behind the sidebar, the
  same as the other languages).
- **Mac:** 4 images per language are flagged "2 rows" in the toolbar band (text, sign, pages, redact).
  That is the gate's measure, not a second toolbar: the 49 px tab strip is gone (native window tabs,
  #711), so the first line of the page falls inside the gate's 160 px band. The gate was left alone.
  The 2.2 sets pass with 0 flagged.
- **Against 2.2:** French and English differ only where something changed on purpose or by shorter
  staged file names (below). Mac `01-reading` and `07-home` are pixel-identical to 2.2, the other five
  slots differ by the 49 px strip only. Every iPhone image matches 2.2, so none of the 2.3 or 2.4
  phone work shows in a listing frame. All nine iPad images differ slightly (a little wider and lower
  layout, a different text-edit keyboard state); nobody attributed it to a commit.

## Defects found and fixed in the run

- Windows: the es-419 tab read "Condiciones de arrendamiento.pd" (clipped); the staged file is now
  `Condiciones generales.pdf`. The Spanish recents list printed "D: › Documents"; the staging folder
  is now `D:\Documentos\`. The English tab read "Equipment Rental Agreement.pd" (also in the 2.2 set);
  the staged file is now `Rental Agreement.pdf` and English was re-shot.
- Windows `sign` and `redact` (#802, re-shot 2026-10-24, all five languages, 04 and 07 only; the
  other six slots are untouched and a re-run of 02 and 03 came out pixel-identical to the filed
  ones). Four causes, three of them in what we ship or how we pose it, none of them DWM or Skia text
  clipping: (1) the remove chip's X had been blank in the app since 2d9960cc (#348, 2026-09-24):
  moving the code lost the private-use character from the string literal, so the chip in the
  `redact` frame was an empty white square (it was empty in the 2.2 set too); fixed in
  `DocumentView.xaml.cs` (`"\uE711"`). (2) The corner dot and the chip sat on the last letter of the
  name because a redaction mark's selection chrome was drawn exactly on the marked words' box; it now
  stands off by 4 dip the way an added text box's already did. (3) A dotted underline ran under
  "Name: Jane Whitfield": the viewer's hover cue for the text run under the pointer, left on by the
  rig parking the cursor with a warp the app never hears; `Shot-Redact.ps1` now walks the pointer off
  the text first. (4) The signature's `g` loop hung across the "Sign above the line" label: the rig
  clicks the signature's centre and 14 px higher the loop ends at the rule, so `Shoot-Set.ps1`
  places it at Y 1446 instead of 1460. Also seen while reading: in es-419 and es-ES the redaction
  mark began through the last "e" of "Nombre" (the drag starts at x 850 and the Spanish label is
  longer); the Spanish sets now start it at x 872, in the space before the name. The package these
  frames come from is the CI build of PR #805's app change (`MegaPDF.App_2.3.0.0_x64.msix`, run
  40106879293), test-signed and installed through `Install-QaTestPackage`.
- iOS: the English iPhone text-edit had no caret on the first take; re-shot six times on a fresh
  install, all six showed it.

- Android, pages view: the Spanish selection title read "Seleccionadas:" over "2" on two lines in es-419 and
  es-ES. The string is now "Selección: %1$d" (es-419 hand-written, es-ES derived; `ES-REVIEW #724`, Fable has not
  read it), and the two `play/phone/{es-419,es-ES}/android-pages.png` files were re-shot from the Android
  Screenshots workflow (run 40052390473, branch `wip/pages-selected-es`): one line in both. The other 38 Play
  images were not replaced; en, fr-CA, es-419 and es-ES came out byte-identical to the filed ones apart from
  these two, and fr-FR differed in the status-bar icon pixels (seven frames) and in the redact frame (not investigated; those files were not replaced).

## Found and not fixed (for Dave)

- **Android title bar:** the file name is ellipsised in es-419 and es-ES, and es-ES cuts into the
  extension ("Contrato de alquiler.p…"). Cosmetic.
- **Every Play frame shows the 3-button navigation bar.** It is the same in the signed-off 2.2 set;
  the runbook says "no navigation buttons".
- **iPad `pages` slot:** a faint "Hold a page to move it" hint is drawn across the page 3 and 4 tiles,
  in all five languages and in 2.2 too. A product fix, not a re-shoot. No issue filed.
- **Windows empty state (`08-home`):** Undo and Redo are drawn darker than the other disabled
  commands, in every language and in 2.2. The run's own check reports all disabled but Open.
- **Android `redact` slot, fr-CA and fr-FR:** the marked line's fill is lighter than in 2.2 (now the
  grey-blue English always had). Inside the mark only; the commit that changed it was not found.

## Spanish toolbar width (Windows)

Measured on the packaged build: es-419 and es-ES keep icon and label on every command down to a 1720 px
window (1147 effective px) and go icon-only at 1700 px (1133 effective px). The Store frame is 1667 effective px, so Spanish
clears it by about 520. **The font and size pickers were measured on 2026-10-24 (#802):** they add 200 effective
px in every language, and Spanish still clears the Store frame by more than 330.

| language | no pickers (app's own measure) | with pickers (app) | packaged, pickers shown: icons-only at / labelled at |
|---|---|---|---|
| en | 1060 | 1260 | 1914 px (1276 eff) / 1922 px (1281 eff) |
| fr-CA, fr-FR | 1187 | 1387 | 2104 px (1403) / 2116 px (1411), fr-CA swept |
| es-419 | 1130 | 1330 | 2014 px (1343) / 2020 px (1347) |
| es-ES | 1122 | 1322 | 2004 px (1336) / 2008 px (1339) |

Spanish is narrower than French in both states (by 57 to 65 effective px), and French was signed off, so
**no Spanish label needs shortening** and none was changed. Below the breakpoint every label drops together
(icons only, nothing clipped). The numbers that stood in the docs for English and French (1430, 1475, 1610) were
stale from before the one-row toolbar and are corrected in `tools/screenshots-windows/README.md`,
`docs/localisation.md` and `docs/microsoft-store-listing.md`. Recorded there too.

## Not verified

- No native speaker has read the French or Spanish in these frames. Checks were mechanical: demo
  names, accents, no English UI word, the right regional page (*alquiler* and *recogida* in Spain,
  *arrendamiento* and *recolección* in Latin America).
- No Retina Mac image, no real iPhone (neither is a gate), no phone of Dave's for Play.
- The Windows set was shot in two sessions about 20 minutes apart (fr-CA and fr-FR first), so it is
  not strictly back to back; the ten `04-sign` and `07-redact` files were then re-shot on 2026-10-24
  (#802) in one run per language, from the package named above rather than the 2.3.0.0 CI package
  of 2026-10-23. Not exercised by hand: clicking the remove chip (the `redact` and `sign` Windows
  self-test states pass locally and drive that route in code, 19 and 12 checks). The five iPad sets were: 19:20 to 19:56 EDT, all showing the same date.
- `website/megapdf/screenshots/linux/` is not updated.
