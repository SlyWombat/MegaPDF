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
- iOS: the English iPhone text-edit had no caret on the first take; re-shot six times on a fresh
  install, all six showed it.

## Found and not fixed (for Dave)

- **Pages view, Spanish selection bar, Android:** the title reads "Seleccionadas:" over "2" on two
  lines in es-419 and es-ES. It does not clip, but it is visible in the Play listing image. Fixing it
  is a string change and another Fable review.
- **Android title bar:** the file name is ellipsised in es-419 and es-ES, and es-ES cuts into the
  extension ("Contrato de alquiler.p…"). Cosmetic.
- **Every Play frame shows the 3-button navigation bar.** It is the same in the signed-off 2.2 set;
  the runbook says "no navigation buttons".
- **iPad `pages` slot:** a faint "Hold a page to move it" hint is drawn across the page 3 and 4 tiles,
  in all five languages and in 2.2 too. A product fix, not a re-shoot. No issue filed.
- **Windows empty state (`08-home`):** Undo and Redo are drawn darker than the other disabled
  commands, in every language and in 2.2. The run's own check reports all disabled but Open.
- **Windows `sign` and `redact`:** the signature descender touches the footer label and the selection
  handle covers the last letter of the name. Both are the pose and match 2.2.
- **Android `redact` slot, fr-CA and fr-FR:** the marked line's fill is lighter than in 2.2 (now the
  grey-blue English always had). Inside the mark only; the commit that changed it was not found.

## Spanish toolbar width (Windows)

Measured on the packaged build: es-419 and es-ES keep icon and label on every command down to a 1720 px
window (1147 effective px) and go icon-only at 1700 px (1133 effective px). The Store frame is 1667 effective px, so Spanish
clears it by about 520. The font and size pickers shown during text editing were not measured. Recorded
in `tools/screenshots-windows/README.md` and `docs/localisation.md`.

## Not verified

- No native speaker has read the French or Spanish in these frames. Checks were mechanical: demo
  names, accents, no English UI word, the right regional page (*alquiler* and *recogida* in Spain,
  *arrendamiento* and *recolección* in Latin America).
- No Retina Mac image, no real iPhone (neither is a gate), no phone of Dave's for Play.
- The Windows set was shot in two sessions about 20 minutes apart (fr-CA and fr-FR first), so it is
  not strictly back to back. The five iPad sets were: 19:20 to 19:56 EDT, all showing the same date.
- `website/megapdf/screenshots/linux/` is not updated.
