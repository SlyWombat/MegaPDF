# Apple store captures 2.4: as read

Mac App Store (7 slots x 5 languages = 35) and App Store iPhone 6.9" + iPad 13" (9 slots x 2 devices x 5 languages = 90). 125 images. Shot on the in-house Mac mini on 2026-10-23 from origin/main `d065597e` (Merge of #789, the Spanish rigs), and filed here as shot, **except the eight `pages` frames re-shot on 2026-10-24 (see "Re-shoot of the pages frames" below)**. Nothing was uploaded; putting images on a listing is Dave's call.

**Every one of the 125 images was opened and looked at with the Read tool at full size**, not read from the gate sheet. (My first pass over each language's set returned no pixels for a number of images; I noticed, kept a ledger of which images I had actually seen, and re-read every one that was missing until the ledger was complete. The table below is from that complete read.) The per-image sentence says what was in the frame; the verdict is mine. Where the gate disagreed with my eye, it is said.

## How they were shot

| | |
|---|---|
| Mac app | `tools/build-macos-app.sh osx-arm64` from the clone of `d065597e`; sha256 of the binary is in each `RUN.txt` (`eff4ca4c...`). Run with `tools/macos-store-captures.sh <lang>` for en, fr-CA, fr, es-419, es in one pass, 19:17-19:20 EDT. The Mac renders its own window, so no screen-recording grant was involved (the previous Mac sets were made the same way; the TCC grant only matters for `macos-record-demo.sh`). |
| iOS app | `xcodebuild build-for-testing`, scheme MegaPDFDemo, derived data in the run's own folder. `tools/ios-screenshots.sh <lang>` for en, fr-CA, fr, es-419, es back to back, 19:20-19:56 EDT. Both devices of a language are shot in the same invocation, and the five invocations ran one after another with nothing else started in between. |
| Simulators | Two of my own, `megapdf-agent-724-iphone` (iPhone 17 Pro Max) and `megapdf-agent-724-ipad` (iPad Pro 13-inch M5), created for the run and deleted after. The script picks the first stock simulator that matches a regex, which on this machine can be someone else's, so on the mini's clone only I changed the two `capture '<regex>' ...` lines and the `iPad*` case to my device names (clone-only edit, not committed). The rig itself is unchanged. |
| Timing | The run started at 19:15 and finished at 19:56 by the mini's clock, so no midnight was near. |

## The iPad same-day check

The iPad date is drawn by the simulator and cannot be posed. All 45 iPad images read **"Fri Oct 23"** (in the four re-shot `pages` frames the date cell is spliced; see "Re-shoot of the pages frames") (checked by eye on every one; the gate's status-bar sibling check agrees: no `chrome`/`siblings` flag on any iPad image in any language). File times on the mini: first iPad image 19:26:14 (en pages), last 19:55:23 (es home).

## Re-shoot of the pages frames (2026-10-24, #800, #801)

PR #808 changed the iOS phone selection title (`"%lld selected"`, shown by `PagesView.swift` as the Pages
sheet's footer on iPhone and iPad): fr and fr-CA `Sélection : N` with U+00A0 before the colon (checked by
code point in `Localizable.xcstrings`), es and es-419 `Selección: N`; English stays "2 selected". The eight
filed `pages` frames in those four languages still read "2 sélectionnées" / "2 seleccionadas" (read at full
size before re-shooting) and are replaced. Nothing else in the 90 iOS images shows the string: it is used in
one place only, OCR (eng+fra) of all 90 filed images finds "selected"/"sélectionnées"/"seleccionadas" only in
the 10 pages frames, and the other strings #808 changed (owner-password and "the author asked that..." notices
in `DocumentSecuritySheet`, `PdfEngine`, `ViewerModel`) appear in no pose. I did not re-read the 80 other
images by eye in this pass; they are untouched files and were read in the original run.

| | |
|---|---|
| Shot from | `main` 925bb492 (contains #808), cloned on the Mac mini from a git bundle. The mini's CI runner had no job (`pgrep -x Runner.Worker` empty) when the build and the shots started; I waited about 15 minutes for one to finish first. |
| Rig | `tools/ios-screenshots.sh` unchanged, run from a copy on the mini that I changed only to (a) shoot the `pages` state alone, (b) use my own simulators, `megapdf-agent-800-iphone` (iPhone 17 Pro Max, iOS 26.5) and `megapdf-agent-800-ipad` (iPad Pro 13-inch M5, iOS 26.5), the same models and runtime as the filed set, (c) skip the viewer and dark shots. Same clock (`9:41`), battery, bars, light appearance, fresh install per language, same fixtures. Languages fr-CA, fr, es-419, es and en (en as a control), one after another, 15:32-15:44 EDT. Both simulators were deleted afterwards, with the clone, the build and the bundle. |
| Frames replaced | `ios-screenshots/{fr-CA,fr,es-419,es}/listing/{iphone-6_9,ipad-13}-pages.png` (8). Not replaced: the English pair (control), and the other 82. |
| App's own log | every shot printed `screenshot pages: 6 pages, 2 selected` on stdout; those logs were not filed (the filed pages frames have none beside them either). |

**What changed, by image diff** (new against filed, every pixel; regions found by dilating the differing
pixels):

- iPhone, fr-CA / fr / es-419 / es: only the footer title (a box about 280 x 53 px at x 60-344, y 2659-2712).
  Everything else is identical, apart from 1,321 pixels of at most 5 (sum over three channels, scale 765)
  along the sheet edge in fr-CA, the same pixels as in the English control: rendering noise, not content.
- iPad, same four: only the footer title (about 200 x 44 px at x 21-225, y 2638-2682) and the date cell
  (below). fr also has 32 pixels of at most 4 in the toolbar buttons' edges; the English control has 64.
- English control, iPhone and iPad: 1,323 and 64 pixels of at most 5 and 4, plus the iPad date. The English
  frames are therefore not changed by #808 and were not replaced.

**Status bar.** iPhone: "9:41", signal, wifi and battery are pixel-identical to the filed frames of other slots
with the same background (sign: 0 differing pixels in the clock and icon boxes) in all four languages. iPad:
"9:41 AM" and the icons are identical to the filed search, redact and reading frames (0 differing pixels in
the boxes), and the date is spliced, as follows.

**The iPad date, and the splice.** The date on the iPad's status bar is drawn by the simulator from the
host's clock. Today is Sat Oct 24, so the new raw iPad frames read **"Sat Oct 24"** against "Fri Oct 23"
on the other 41 filed iPad frames. `simctl status_bar override --time` accepts an ISO date (it needs
fractional seconds and an offset, `2026-10-23T09:41:00.000-04:00`) and changes the clock text, but I measured
that it does not move the date (tried 2026-10-23 and 2007-01-09; the date still read Sat Oct 24), and the
host's clock and timezone cannot be moved on a machine that is also CI's. So that the iPad set keeps one
date, **in each of the four new iPad frames I copied the date cell, a 172 x 52 px rectangle at x 160, y 6,
from the filed frame of the same slot and language over the new frame** (PIL paste; the background in the
rectangle is the same flat toolbar colour in both). I checked that before the paste the two frames differ in
that rectangle only in the date glyphs (1,798 pixels, one cluster), and after it the only differences
left are the footer title and the noise above. These four iPad files are therefore not pure captures. If Dave
would rather file the unspliced frames with "Sat Oct 24" (or re-shoot the whole iPad set on one day), say so;
the raw frames are not kept in the repo.

**Each new frame, read at full size** (Read tool, all eight; the two English control frames were compared by
pixels and not displayed):

| Language | Device | Footer now reads | Other checks |
|---|---|---|---|
| fr-CA | iPhone | `Sélection : 2` | no clipping; 2 tiles ticked; space before the colon is the narrow gap of a no-break space; tab title "Contrat de loc..." as before |
| fr-CA | iPad | `Sélection : 2` | date "Fri Oct 23" (spliced); hint over tiles 3/4 as before (#794) |
| fr | iPhone | `Sélection : 2` | same; body text is the France set ("enlèvement", "week-end") |
| fr | iPad | `Sélection : 2` | date as above; hint |
| es-419 | iPhone | `Selección: 2` | no space before the colon, as Spanish wants; no clipping |
| es-419 | iPad | `Selección: 2` | date as above; hint "Mantén presionada una página para moverla" runs past the sidebar's left edge (the old frame's too) |
| es | iPhone | `Selección: 2` | no clipping; "Contrato de alquil..." as before |
| es | iPad | `Selección: 2` | date as above; hint "Mantén pulsada una página para moverla" |

The iPad hint "Hold a page to move it" (#794, not fixed) is still the only defect in the iPad frames, as
before. Nobody who reads French or Spanish natively has looked at the new titles; the colon spacing was
checked by code point in the catalogue and by eye in the image.

**Gate** (`tools/capture-gate/gate.py --store ios --only /listing`). Without `--against`: en 0 flagged,
fr-CA 0, fr-FR 0, es-419 3 (iPad pages "clipping: right edge", the document text behind the sidebar, flagged
before too; iPad and iPhone `text`, "Sofía Ramírez": the OCR cannot read Spanish accents, whole by eye), es-ES 2
(`text`, "Lucía Fernández", the same OCR caveat). That is identical to the earlier run. Against the filed 2.4
set: en 0 flagged and no difference; the iPhone `pages` frames of fr-CA, fr-FR, es-419 and es-ES read
"certified" at 0.10-0.11 % of pixels differing (the title, which is what was meant); the iPad `pages` frames did
not trip the threshold; no `chrome` or `siblings` flag on any image.

## Gate results

`python3 tools/capture-gate/gate.py --store mac docs/qa/captures-2.4/macos-screenshots` (and `--store ios ... --only /listing`), then again with `--against docs/qa/captures-2.2/...` for en, fr-CA and fr(FR). The gate's HTML sheets are not kept; the results are here.

| Store | Language | Images | Passed | Not run | Images flagged |
|---|---|---|---|---|---|
| Mac | en | 7 | 44 | 22 | 4 |
| Mac | fr-CA | 7 | 58 | 15 | 4 |
| Mac | fr-FR | 7 | 58 | 15 | 4 |
| Mac | es-419 | 7 | 57 | 15 | 4 |
| Mac | es-ES | 7 | 57 | 15 | 4 |
| iOS listing | en | 18 | 92 | 88 | 0 |
| iOS listing | fr-CA | 18 | 146 | 70 | 0 |
| iOS listing | fr-FR | 18 | 146 | 70 | 0 |
| iOS listing | es-419 | 18 | 143 | 70 | 3 |
| iOS listing | es-ES | 18 | 144 | 70 | 2 |

For reference the **2.2 sets** (`docs/qa/captures-2.2/macos-screenshots`) pass the same gate with 0 flagged in all three languages, so the Mac flags below are new and I explain each.

**Mac, the 4 flagged per language (02-text, 03-sign, 04-pages, 06-redact): all "toolbar".** The gate says "2 rows of controls in a 160 px band" (03-sign: also; 04-pages: "band is 160 px"). That is a gate measurement artefact, not a second toolbar. In 2.2 a 49 px tab strip sat under the toolbar, so the 160 px search window ended in the strip; with native window tabs on the Mac (#711) the strip is gone, the page reaches the toolbar sooner, and the first line of the page now falls inside the 160 px and is counted as a second row of ink. I looked at all four poses in all five languages: one row of nine controls plus the zoom group and the "..." button, as before. The profile's `tab_underline` rows (93-95) and the 160 px depth in `tools/capture-gate/stores.py` assume the 2.2 layout; I did not change the gate (out of scope), and it needs updating before the next Mac set is gated. The gate also flagged `person` on the es-419 and es 02-text: tesseract dropped the accents of "Sofía Ramírez" / "Lucía Fernández"; I read both at full size, accents whole (the README's own caveat about the reader and Spanish).

**Mac, `--against 2.2`**: en, fr-CA, fr add "certified" on 02, 03, 04, 05, 06 (8-14 % of pixels differ); 01-reading and 07-home are **pixel-identical to 2.2 in all three languages**. The differences are one thing: the 49 px tab strip that 2.2 shows under the toolbar is gone in 2.4, so everything under it moves up 49 px. I proved it by shifting the 2.2 image 49 px and comparing: 02-text 1,104 px of 1.0 M differ, 04-pages 268, 05-search 1,024, 06-redact 1,104 (02-text and 05-search: the leftover is a 16 px column at the scrollbar; I did not look into the rest). 03-sign differs more (66-67 k px) because the flyout hangs from the toolbar and did not move, so my 49 px shift misaligns it; under it 2.2 shows the tab title ("Rental Agreeme...") and 2.4 shows page content. All of that is the intended #711 change (native macOS window tabs). Nothing from #710 (select and copy), #774/#775 (double click), reading mode or Vision scan reading (#676) appears in these poses: none of them selects text or shows a scanned page.

**iOS (no `--against`)**: en 0 flagged. **es-419 ipad-13-pages** "clipping: right edge": the document text runs off the right edge behind the Pages sidebar; the same layout is in the en and fr shots (the Spanish line is longer, so it trips the heuristic). **es-419 and es `text` on both devices**: the same accent-drop OCR caveat as above, read by eye, accents whole.

**iOS `--against 2.2` (en, fr-CA, fr)**: all 9 iPad images in each language read "certified" (en: ipad reading 0.2 %, draw 1.4 %, sign 2.3 %, home 2.3 %, redact 3.4 %, text 3.5 %, search 4.3 %, pages 4.8 %, text-edit 48.9 %). Every **iPhone image is identical to 2.2** (original run; the fr-CA, fr, es-419 and es `pages` frames now differ from 2.2 by the selection title) (en: reading, text-edit, pages, text, search, redact, home 0 px; sign 27 px; draw 477 px; nothing else). See Findings 2 and 4 for the iPad differences.

## Findings

1. **Defect found and fixed: English iPhone `text-edit`.** The first take had no caret in the field (blink phase). The gate flagged it ("0 accent px against the pose's median of 330"). The other 14 phone/iPad text-edit images all show the caret. I re-shot that one image six times on a fresh install of the same build on my own simulator (same flags, same status bar override); all six showed the caret and were identical to each other, and I filed the first. `iphone-6_9-text-edit.png`'s old log files were empty, as the new ones are. After this the gate has 0 flagged on en.
2. **What changed vs 2.2 on purpose, and what did not.** *Mac:* the tab strip is gone (native window tabs, #711). *iPhone:* nothing visible; every pose is pixel-identical to 2.2, which also means none of 2.3/2.4's phone work (shrink for email #593, select and copy #710, "Saved" notices #631) is in a listing frame. *iPad:* the words and order are identical, but the toolbar and the page under it are laid out slightly wider and lower than 2.2 (toolbar labels at x=80/212/381 against 78/205/369 in the 1500 px display, page title 2 px lower). I cannot attribute that to a commit: none of the iOS commit subjects since 2.2 names the iPad toolbar. It may be the new simulator rather than the app; I did not measure that and do not claim it. It does not change what the images say.
3. **iPad `pages` (all five languages): a faint hint across the tiles.** "Hold a page to move it" (`ios/MegaPDF/PagesView.swift`, `grid`, localised: "Maintenez une page pour la déplacer" etc.) is placed after the grid inside the scroll view; in the iPad sidebar shot it is drawn over the page 3 / page 4 tiles as very faint light text. It is **in the 2.2 image too** (compared side by side, en and fr-CA, identical position), so this is not new, but it is a visible defect in what is the iPad's first listing slot. A re-shoot cannot remove it; it is a product fix (the hint belongs under the grid, not over page 3/4's tile). I did not file an issue (not asked); the coordinator should.
4. **iPad `text-edit` has a docked keyboard now.** 2.2's iPad text-edit showed the sheet low with the minimised floating keyboard pill and the date "Fri Oct 2"; 2.4's shows the sheet higher with the full docked keyboard. I did not determine why (a different keyboard state on the new simulator is my guess, not a measurement). It is the cause of the 49 % "certified" difference. I think the 2.4 frame reads better, but it is a change a reader should know about.
5. **Mac `light-03-sign` in 2.2 had the tab title peeking at the left under the flyout; 2.4 does not** (tab strip gone).
6. **French and Spanish text shown has not been read by a native speaker** (the standing gate; Fable's review is separate). What I checked is mechanical: names, accents, "no English UI word", right regional page (Québec vs France; Latin-American vs Spain), "Page 1 sur 6"/"Página 1 de 6", and I saw no "encrypted" / "chiffré" / "cifrado" about signatures. I did not check the French non-breaking spaces by code point on these images (it cannot be done from an image).
7. **What I could not do:** no real iPhone (not a gate, per the standing note), no Retina Mac image (the rig's 1440x900 is the Mac slot).

## Mac App Store, 1440x900 (35)

| Language | Slot | File | In the frame | Verdict |
|---|---|---|---|---|
| en | 01 | `macos-screenshots/en/light-01-reading.png` | Chrome-less page 1 of 6 on the canvas, scrollbar at the right, floating bar pinned at the bottom ("Page 1 of 6" … "Exit"); no toolbar, tab strip or status line. English UI throughout. | OK |
| en | 02 | `macos-screenshots/en/light-02-text.png` | One-page agreement, one-row toolbar, zoom 100 %, "Jane Whitfield" typed under the signature line, nothing selected, status "Text added" in the set's language. Accents whole (read by eye at full size). | OK |
| en | 03 | `macos-screenshots/en/light-03-sign.png` | Signature flyout with one card, "Mega W.", and the Draw / Type / From photo buttons in the set's language; document behind it, status line says the signature was saved. | OK |
| en | 04 | `macos-screenshots/en/light-04-pages.png` | Pages sidebar with six thumbnails, rows 2 and 3 selected, Pages button toggled on, status line "Rental Agreement.pdf, 6 pages" in the set's language; document name is the same as in every other shot. | OK |
| en | 05 | `macos-screenshots/en/light-05-search.png` | Find bar with "rental", count "1 of 3", first hit selected and the other two highlighted; nothing else armed. | OK |
| en | 06 | `macos-screenshots/en/light-06-redact.png` | First line of the paragraph marked: translucent band, box, four corner handles, the remove mark at the top-right corner; status line "Drag to move…" in the set's language; Undo live. | OK |
| en | 07 | `macos-screenshots/en/light-07-home.png` | Empty window, MegaPDF + one-line tagline + open button, three recents ("Rental Agreement.pdf" first) with fixture locations only, status "Open a PDF to get started" in the set's language; no hover. | OK |
| fr-CA | 01 | `macos-screenshots/fr-CA/light-01-reading.png` | Chrome-less page 1 of 6 on the canvas, scrollbar at the right, floating bar pinned at the bottom ("Page 1 sur 6" … "Sortir"); no toolbar, tab strip or status line. Québec page ("Livraison et ramassage"). | OK |
| fr-CA | 02 | `macos-screenshots/fr-CA/light-02-text.png` | One-page agreement, one-row toolbar, zoom 100 %, "Hélène Bélanger" typed under the signature line, nothing selected, status "Text added" in the set's language. Accents whole (read by eye at full size). | OK |
| fr-CA | 03 | `macos-screenshots/fr-CA/light-03-sign.png` | Signature flyout with one card, "Mega W.", and the Draw / Type / From photo buttons in the set's language; document behind it, status line says the signature was saved. | OK |
| fr-CA | 04 | `macos-screenshots/fr-CA/light-04-pages.png` | Pages sidebar with six thumbnails, rows 2 and 3 selected, Pages button toggled on, status line "Contrat de location.pdf, 6 pages" in the set's language; document name is the same as in every other shot. | OK |
| fr-CA | 05 | `macos-screenshots/fr-CA/light-05-search.png` | Find bar with "location", count "1 sur 3", first hit selected and the other two highlighted; nothing else armed. | OK |
| fr-CA | 06 | `macos-screenshots/fr-CA/light-06-redact.png` | First line of the paragraph marked: translucent band, box, four corner handles, the remove mark at the top-right corner; status line "Drag to move…" in the set's language; Undo live. | OK |
| fr-CA | 07 | `macos-screenshots/fr-CA/light-07-home.png` | Empty window, MegaPDF + one-line tagline + open button, three recents ("Contrat de location.pdf" first) with fixture locations only, status "Open a PDF to get started" in the set's language; no hover. | OK |
| fr (France) | 01 | `macos-screenshots/fr/light-01-reading.png` | Chrome-less page 1 of 6 on the canvas, scrollbar at the right, floating bar pinned at the bottom ("Page 1 sur 6" … "Sortir"); no toolbar, tab strip or status line. France's own page ("enlèvement", "week-end", "Groupe électrogène"). | OK |
| fr (France) | 02 | `macos-screenshots/fr/light-02-text.png` | One-page agreement, one-row toolbar, zoom 100 %, "Céline Lefèvre" typed under the signature line, nothing selected, status "Text added" in the set's language. Accents whole (read by eye at full size). | OK |
| fr (France) | 03 | `macos-screenshots/fr/light-03-sign.png` | Signature flyout with one card, "Mega W.", and the Draw / Type / From photo buttons in the set's language; document behind it, status line says the signature was saved. | OK |
| fr (France) | 04 | `macos-screenshots/fr/light-04-pages.png` | Pages sidebar with six thumbnails, rows 2 and 3 selected, Pages button toggled on, status line "Contrat de location.pdf, 6 pages" in the set's language; document name is the same as in every other shot. | OK |
| fr (France) | 05 | `macos-screenshots/fr/light-05-search.png` | Find bar with "location", count "1 sur 3", first hit selected and the other two highlighted; nothing else armed. | OK |
| fr (France) | 06 | `macos-screenshots/fr/light-06-redact.png` | First line of the paragraph marked: translucent band, box, four corner handles, the remove mark at the top-right corner; status line "Drag to move…" in the set's language; Undo live. | OK |
| fr (France) | 07 | `macos-screenshots/fr/light-07-home.png` | Empty window, MegaPDF + one-line tagline + open button, three recents ("Contrat de location.pdf" first) with fixture locations only, status "Open a PDF to get started" in the set's language; no hover. | OK |
| es-419 | 01 | `macos-screenshots/es-419/light-01-reading.png` | Chrome-less page 1 of 6 on the canvas, scrollbar at the right, floating bar pinned at the bottom ("Página 1 de 6" … "Salir"); no toolbar, tab strip or status line. Latin American page ("arrendamiento"). | OK |
| es-419 | 02 | `macos-screenshots/es-419/light-02-text.png` | One-page agreement, one-row toolbar, zoom 100 %, "Sofía Ramírez" typed under the signature line, nothing selected, status "Text added" in the set's language. Accents whole (read by eye at full size). | OK |
| es-419 | 03 | `macos-screenshots/es-419/light-03-sign.png` | Signature flyout with one card, "Mega W.", and the Draw / Type / From photo buttons in the set's language; document behind it, status line says the signature was saved. | OK |
| es-419 | 04 | `macos-screenshots/es-419/light-04-pages.png` | Pages sidebar with six thumbnails, rows 2 and 3 selected, Pages button toggled on, status line "Contrato de arrendamiento.pdf, 6 pages" in the set's language; document name is the same as in every other shot. | OK |
| es-419 | 05 | `macos-screenshots/es-419/light-05-search.png` | Find bar with "arrendamiento", count "1 de 3", first hit selected and the other two highlighted; nothing else armed. | OK |
| es-419 | 06 | `macos-screenshots/es-419/light-06-redact.png` | First line of the paragraph marked: translucent band, box, four corner handles, the remove mark at the top-right corner; status line "Drag to move…" in the set's language; Undo live. | OK |
| es-419 | 07 | `macos-screenshots/es-419/light-07-home.png` | Empty window, MegaPDF + one-line tagline + open button, three recents ("Contrato de arrendamiento.pdf" first) with fixture locations only, status "Open a PDF to get started" in the set's language; no hover. | OK |
| es (Spain) | 01 | `macos-screenshots/es/light-01-reading.png` | Chrome-less page 1 of 6 on the canvas, scrollbar at the right, floating bar pinned at the bottom ("Página 1 de 6" … "Salir"); no toolbar, tab strip or status line. Spain's page ("alquiler", "Firme sobre la línea", "Añadir texto"). | OK |
| es (Spain) | 02 | `macos-screenshots/es/light-02-text.png` | One-page agreement, one-row toolbar, zoom 100 %, "Lucía Fernández" typed under the signature line, nothing selected, status "Text added" in the set's language. Accents whole (read by eye at full size). | OK |
| es (Spain) | 03 | `macos-screenshots/es/light-03-sign.png` | Signature flyout with one card, "Mega W.", and the Draw / Type / From photo buttons in the set's language; document behind it, status line says the signature was saved. | OK |
| es (Spain) | 04 | `macos-screenshots/es/light-04-pages.png` | Pages sidebar with six thumbnails, rows 2 and 3 selected, Pages button toggled on, status line "Contrato de alquiler.pdf, 6 pages" in the set's language; document name is the same as in every other shot. | OK |
| es (Spain) | 05 | `macos-screenshots/es/light-05-search.png` | Find bar with "alquiler", count "1 de 3", first hit selected and the other two highlighted; nothing else armed. | OK |
| es (Spain) | 06 | `macos-screenshots/es/light-06-redact.png` | First line of the paragraph marked: translucent band, box, four corner handles, the remove mark at the top-right corner; status line "Drag to move…" in the set's language; Undo live. | OK |
| es (Spain) | 07 | `macos-screenshots/es/light-07-home.png` | Empty window, MegaPDF + one-line tagline + open button, three recents ("Contrato de alquiler.pdf" first) with fixture locations only, status "Open a PDF to get started" in the set's language; no hover. | OK |

## App Store iPhone 6.9", 1320x2868 (45)

| Language | Slot | File | In the frame | Verdict |
|---|---|---|---|---|
| en | 1 | `ios-screenshots/en/listing/iphone-6_9-reading.png` | Reading mode: page 1 with the chrome gone, bar pinned ("1/6", fit, zoom, find, "Exit"), 9:41 status bar, no toolbar. English UI throughout. | OK |
| en | 2 | `ios-screenshots/en/listing/iphone-6_9-text-edit.png` | Edit-text sheet on the document heading with "(2026)" appended, caret in the field, keyboard up, no first-run QuickPath tip. **Re-shot** (first take had no caret, Findings 1). | OK |
| en | 3 | `ios-screenshots/en/listing/iphone-6_9-sign.png` | Signatures sheet with one card, "Mega W.", and the three add buttons in the set's language over the dimmed document. | OK |
| en | 4 | `ios-screenshots/en/listing/iphone-6_9-draw.png` | Draw-a-signature pad with the demo "MegaWoman" signature drawn, Cancel / Clear / Save in the set's language. | OK |
| en | 5 | `ios-screenshots/en/listing/iphone-6_9-pages.png` | Pages sheet, "6 pages", pages 2 and 3 ticked, "2 selected" in the set's language, page tiles in the set's language. | OK |
| en | 6 | `ios-screenshots/en/listing/iphone-6_9-text.png` | Add-text sheet with "Jane Whitfield" typed, Size 12, Font Helvetica; accents whole by eye (the gate's OCR drops them in es). | OK |
| en | 7 | `ios-screenshots/en/listing/iphone-6_9-search.png` | Find bar with "rental", "1 of 3", first hit selected, other hits highlighted. | OK |
| en | 8 | `ios-screenshots/en/listing/iphone-6_9-redact.png` | First line marked with box, handles and remove mark; Save enabled, Undo live; no selection anywhere else. | OK |
| en | 9 | `ios-screenshots/en/listing/iphone-6_9-home.png` | MegaPDF home, tagline, open button, three recents ("Rental Agreement.pdf" first; "On My iPhone" / "iCloud Drive" locations are fixtures). | OK |
| fr-CA | 1 | `ios-screenshots/fr-CA/listing/iphone-6_9-reading.png` | Reading mode: page 1 with the chrome gone, bar pinned ("1/6", fit, zoom, find, "Sortir"), 9:41 status bar, no toolbar. Québec page ("Livraison et ramassage"). | OK |
| fr-CA | 2 | `ios-screenshots/fr-CA/listing/iphone-6_9-text-edit.png` | Edit-text sheet on the document heading with "(2026)" appended, caret in the field, keyboard up, no first-run QuickPath tip. | OK |
| fr-CA | 3 | `ios-screenshots/fr-CA/listing/iphone-6_9-sign.png` | Signatures sheet with one card, "Mega W.", and the three add buttons in the set's language over the dimmed document. | OK |
| fr-CA | 4 | `ios-screenshots/fr-CA/listing/iphone-6_9-draw.png` | Draw-a-signature pad with the demo "MegaWoman" signature drawn, Cancel / Clear / Save in the set's language. | OK |
| fr-CA | 5 | `ios-screenshots/fr-CA/listing/iphone-6_9-pages.png` | Pages sheet, "6 pages", pages 2 and 3 ticked, "Sélection : 2" (re-shot 2026-10-24, #800/#801: was "2 sélectionnées"/"2 seleccionadas"), page tiles in the set's language. | OK |
| fr-CA | 6 | `ios-screenshots/fr-CA/listing/iphone-6_9-text.png` | Add-text sheet with "Hélène Bélanger" typed, Size 12, Font Helvetica; accents whole by eye (the gate's OCR drops them in es). | OK |
| fr-CA | 7 | `ios-screenshots/fr-CA/listing/iphone-6_9-search.png` | Find bar with "location", "1 sur 3", first hit selected, other hits highlighted. | OK |
| fr-CA | 8 | `ios-screenshots/fr-CA/listing/iphone-6_9-redact.png` | First line marked with box, handles and remove mark; Save enabled, Undo live; no selection anywhere else. | OK |
| fr-CA | 9 | `ios-screenshots/fr-CA/listing/iphone-6_9-home.png` | MegaPDF home, tagline, open button, three recents ("Contrat de location.pdf" first; "On My iPhone" / "iCloud Drive" locations are fixtures). | OK |
| fr (France) | 1 | `ios-screenshots/fr/listing/iphone-6_9-reading.png` | Reading mode: page 1 with the chrome gone, bar pinned ("1/6", fit, zoom, find, "Sortir"), 9:41 status bar, no toolbar. France's own page ("enlèvement", "week-end", "Groupe électrogène"). | OK |
| fr (France) | 2 | `ios-screenshots/fr/listing/iphone-6_9-text-edit.png` | Edit-text sheet on the document heading with "(2026)" appended, caret in the field, keyboard up, no first-run QuickPath tip. | OK |
| fr (France) | 3 | `ios-screenshots/fr/listing/iphone-6_9-sign.png` | Signatures sheet with one card, "Mega W.", and the three add buttons in the set's language over the dimmed document. | OK |
| fr (France) | 4 | `ios-screenshots/fr/listing/iphone-6_9-draw.png` | Draw-a-signature pad with the demo "MegaWoman" signature drawn, Cancel / Clear / Save in the set's language. | OK |
| fr (France) | 5 | `ios-screenshots/fr/listing/iphone-6_9-pages.png` | Pages sheet, "6 pages", pages 2 and 3 ticked, "Sélection : 2" (re-shot 2026-10-24, #800/#801: was "2 sélectionnées"/"2 seleccionadas"), page tiles in the set's language. | OK |
| fr (France) | 6 | `ios-screenshots/fr/listing/iphone-6_9-text.png` | Add-text sheet with "Céline Lefèvre" typed, Size 12, Font Helvetica; accents whole by eye (the gate's OCR drops them in es). | OK |
| fr (France) | 7 | `ios-screenshots/fr/listing/iphone-6_9-search.png` | Find bar with "location", "1 sur 3", first hit selected, other hits highlighted. | OK |
| fr (France) | 8 | `ios-screenshots/fr/listing/iphone-6_9-redact.png` | First line marked with box, handles and remove mark; Save enabled, Undo live; no selection anywhere else. | OK |
| fr (France) | 9 | `ios-screenshots/fr/listing/iphone-6_9-home.png` | MegaPDF home, tagline, open button, three recents ("Contrat de location.pdf" first; "On My iPhone" / "iCloud Drive" locations are fixtures). | OK |
| es-419 | 1 | `ios-screenshots/es-419/listing/iphone-6_9-reading.png` | Reading mode: page 1 with the chrome gone, bar pinned ("1/6", fit, zoom, find, "Salir"), 9:41 status bar, no toolbar. Latin American page ("arrendamiento"). | OK |
| es-419 | 2 | `ios-screenshots/es-419/listing/iphone-6_9-text-edit.png` | Edit-text sheet on the document heading with "(2026)" appended, caret in the field, keyboard up, no first-run QuickPath tip. | OK |
| es-419 | 3 | `ios-screenshots/es-419/listing/iphone-6_9-sign.png` | Signatures sheet with one card, "Mega W.", and the three add buttons in the set's language over the dimmed document. | OK |
| es-419 | 4 | `ios-screenshots/es-419/listing/iphone-6_9-draw.png` | Draw-a-signature pad with the demo "MegaWoman" signature drawn, Cancel / Clear / Save in the set's language. | OK |
| es-419 | 5 | `ios-screenshots/es-419/listing/iphone-6_9-pages.png` | Pages sheet, "6 pages", pages 2 and 3 ticked, "Selección: 2" (re-shot 2026-10-24, #800/#801: was "2 seleccionadas"), page tiles in the set's language. | OK |
| es-419 | 6 | `ios-screenshots/es-419/listing/iphone-6_9-text.png` | Add-text sheet with "Sofía Ramírez" typed, Size 12, Font Helvetica; accents whole by eye (the gate's OCR drops them in es). | OK |
| es-419 | 7 | `ios-screenshots/es-419/listing/iphone-6_9-search.png` | Find bar with "arrendamiento", "1 de 3", first hit selected, other hits highlighted. | OK |
| es-419 | 8 | `ios-screenshots/es-419/listing/iphone-6_9-redact.png` | First line marked with box, handles and remove mark; Save enabled, Undo live; no selection anywhere else. | OK |
| es-419 | 9 | `ios-screenshots/es-419/listing/iphone-6_9-home.png` | MegaPDF home, tagline, open button, three recents ("Contrato de arrendamiento.pdf" first; "On My iPhone" / "iCloud Drive" locations are fixtures). | OK |
| es (Spain) | 1 | `ios-screenshots/es/listing/iphone-6_9-reading.png` | Reading mode: page 1 with the chrome gone, bar pinned ("1/6", fit, zoom, find, "Salir"), 9:41 status bar, no toolbar. Spain's page ("alquiler", "Firme sobre la línea", "Añadir texto"). | OK |
| es (Spain) | 2 | `ios-screenshots/es/listing/iphone-6_9-text-edit.png` | Edit-text sheet on the document heading with "(2026)" appended, caret in the field, keyboard up, no first-run QuickPath tip. | OK |
| es (Spain) | 3 | `ios-screenshots/es/listing/iphone-6_9-sign.png` | Signatures sheet with one card, "Mega W.", and the three add buttons in the set's language over the dimmed document. | OK |
| es (Spain) | 4 | `ios-screenshots/es/listing/iphone-6_9-draw.png` | Draw-a-signature pad with the demo "MegaWoman" signature drawn, Cancel / Clear / Save in the set's language. | OK |
| es (Spain) | 5 | `ios-screenshots/es/listing/iphone-6_9-pages.png` | Pages sheet, "6 pages", pages 2 and 3 ticked, "Selección: 2" (re-shot 2026-10-24, #800/#801: was "2 seleccionadas"), page tiles in the set's language. | OK |
| es (Spain) | 6 | `ios-screenshots/es/listing/iphone-6_9-text.png` | Add-text sheet with "Lucía Fernández" typed, Size 12, Font Helvetica; accents whole by eye (the gate's OCR drops them in es). | OK |
| es (Spain) | 7 | `ios-screenshots/es/listing/iphone-6_9-search.png` | Find bar with "alquiler", "1 de 3", first hit selected, other hits highlighted. | OK |
| es (Spain) | 8 | `ios-screenshots/es/listing/iphone-6_9-redact.png` | First line marked with box, handles and remove mark; Save enabled, Undo live; no selection anywhere else. | OK |
| es (Spain) | 9 | `ios-screenshots/es/listing/iphone-6_9-home.png` | MegaPDF home, tagline, open button, three recents ("Contrato de alquiler.pdf" first; "On My iPhone" / "iCloud Drive" locations are fixtures). | OK |

## App Store iPad 13", 2064x2752 (45)

Slot order is the iPad's own (pages, reading, text-edit, sign, draw, text, search, redact, home). Every status bar reads 9:41 AM and "Fri Oct 23".

| Language | Slot | File | In the frame | Verdict |
|---|---|---|---|---|
| en | 1 | `ios-screenshots/en/listing/ipad-13-pages.png` | Pages sidebar on the left (6 tiles, 2 ticked), document at the right, iPad toolbar with Pages toggled on. Faint white hint "Hold a page to move it" (localised) sits across the tiles at mid-sidebar, and the document text runs off the right edge behind the sidebar; both are the same in the 2.2 shot. | OK, with a note (see Findings 3) |
| en | 2 | `ios-screenshots/en/listing/ipad-13-reading.png` | Reading mode on the iPad: page 1, bar pinned at the bottom ("1/6" … "Exit"), status bar with the date. | OK |
| en | 3 | `ios-screenshots/en/listing/ipad-13-text-edit.png` | Edit-text popover on the heading, caret in the field, docked hardware-style keyboard below; toolbar visible behind. | OK, with a note (see Findings 4) |
| en | 4 | `ios-screenshots/en/listing/ipad-13-sign.png` | Signatures popover from the toolbar's Sign button, one card "Mega W.", add buttons in the set's language. | OK |
| en | 5 | `ios-screenshots/en/listing/ipad-13-draw.png` | Draw-your-signature panel over the signatures popover, "MegaWoman" drawn. | OK |
| en | 6 | `ios-screenshots/en/listing/ipad-13-text.png` | Add-text popover with "Jane Whitfield" typed, Size 12, Font Helvetica; accents whole by eye. | OK |
| en | 7 | `ios-screenshots/en/listing/ipad-13-search.png` | Find bar under the toolbar with "rental", "1 of 3", first hit selected, Search toggled on. | OK |
| en | 8 | `ios-screenshots/en/listing/ipad-13-redact.png` | First line marked: box, handles, remove mark; Undo live, Save enabled. | OK |
| en | 9 | `ios-screenshots/en/listing/ipad-13-home.png` | Home with three recents ("Rental Agreement.pdf" first; "On My iPad" location), status bar with the date. | OK |
| fr-CA | 1 | `ios-screenshots/fr-CA/listing/ipad-13-pages.png` | Pages sidebar on the left (6 tiles, 2 ticked, footer "Sélection : 2"), document at the right, iPad toolbar with Pages toggled on. Re-shot 2026-10-24 (#800/#801); date cell spliced from the filed frame. Faint white hint "Hold a page to move it" (localised) sits across the tiles at mid-sidebar, and the document text runs off the right edge behind the sidebar; both are the same in the 2.2 shot. | OK, with a note (see Findings 3) |
| fr-CA | 2 | `ios-screenshots/fr-CA/listing/ipad-13-reading.png` | Reading mode on the iPad: page 1, bar pinned at the bottom ("1/6" … "Sortir"), status bar with the date. | OK |
| fr-CA | 3 | `ios-screenshots/fr-CA/listing/ipad-13-text-edit.png` | Edit-text popover on the heading, caret in the field, docked hardware-style keyboard below; toolbar visible behind. | OK, with a note (see Findings 4) |
| fr-CA | 4 | `ios-screenshots/fr-CA/listing/ipad-13-sign.png` | Signatures popover from the toolbar's Sign button, one card "Mega W.", add buttons in the set's language. | OK |
| fr-CA | 5 | `ios-screenshots/fr-CA/listing/ipad-13-draw.png` | Draw-your-signature panel over the signatures popover, "MegaWoman" drawn. | OK |
| fr-CA | 6 | `ios-screenshots/fr-CA/listing/ipad-13-text.png` | Add-text popover with "Hélène Bélanger" typed, Size 12, Font Helvetica; accents whole by eye. | OK |
| fr-CA | 7 | `ios-screenshots/fr-CA/listing/ipad-13-search.png` | Find bar under the toolbar with "location", "1 sur 3", first hit selected, Search toggled on. | OK |
| fr-CA | 8 | `ios-screenshots/fr-CA/listing/ipad-13-redact.png` | First line marked: box, handles, remove mark; Undo live, Save enabled. | OK |
| fr-CA | 9 | `ios-screenshots/fr-CA/listing/ipad-13-home.png` | Home with three recents ("Contrat de location.pdf" first; "On My iPad" location), status bar with the date. | OK |
| fr (France) | 1 | `ios-screenshots/fr/listing/ipad-13-pages.png` | Pages sidebar on the left (6 tiles, 2 ticked, footer "Sélection : 2"), document at the right, iPad toolbar with Pages toggled on. Re-shot 2026-10-24 (#800/#801); date cell spliced from the filed frame. Faint white hint "Hold a page to move it" (localised) sits across the tiles at mid-sidebar, and the document text runs off the right edge behind the sidebar; both are the same in the 2.2 shot. | OK, with a note (see Findings 3) |
| fr (France) | 2 | `ios-screenshots/fr/listing/ipad-13-reading.png` | Reading mode on the iPad: page 1, bar pinned at the bottom ("1/6" … "Sortir"), status bar with the date. | OK |
| fr (France) | 3 | `ios-screenshots/fr/listing/ipad-13-text-edit.png` | Edit-text popover on the heading, caret in the field, docked hardware-style keyboard below; toolbar visible behind. | OK, with a note (see Findings 4) |
| fr (France) | 4 | `ios-screenshots/fr/listing/ipad-13-sign.png` | Signatures popover from the toolbar's Sign button, one card "Mega W.", add buttons in the set's language. | OK |
| fr (France) | 5 | `ios-screenshots/fr/listing/ipad-13-draw.png` | Draw-your-signature panel over the signatures popover, "MegaWoman" drawn. | OK |
| fr (France) | 6 | `ios-screenshots/fr/listing/ipad-13-text.png` | Add-text popover with "Céline Lefèvre" typed, Size 12, Font Helvetica; accents whole by eye. | OK |
| fr (France) | 7 | `ios-screenshots/fr/listing/ipad-13-search.png` | Find bar under the toolbar with "location", "1 sur 3", first hit selected, Search toggled on. | OK |
| fr (France) | 8 | `ios-screenshots/fr/listing/ipad-13-redact.png` | First line marked: box, handles, remove mark; Undo live, Save enabled. | OK |
| fr (France) | 9 | `ios-screenshots/fr/listing/ipad-13-home.png` | Home with three recents ("Contrat de location.pdf" first; "On My iPad" location), status bar with the date. | OK |
| es-419 | 1 | `ios-screenshots/es-419/listing/ipad-13-pages.png` | Pages sidebar on the left (6 tiles, 2 ticked, footer "Selección: 2"), document at the right, iPad toolbar with Pages toggled on. Re-shot 2026-10-24 (#800/#801); date cell spliced from the filed frame. Faint white hint "Hold a page to move it" (localised) sits across the tiles at mid-sidebar, and the document text runs off the right edge behind the sidebar; both are the same in the 2.2 shot. | OK, with a note (see Findings 3) |
| es-419 | 2 | `ios-screenshots/es-419/listing/ipad-13-reading.png` | Reading mode on the iPad: page 1, bar pinned at the bottom ("1/6" … "Salir"), status bar with the date. | OK |
| es-419 | 3 | `ios-screenshots/es-419/listing/ipad-13-text-edit.png` | Edit-text popover on the heading, caret in the field, docked hardware-style keyboard below; toolbar visible behind. | OK, with a note (see Findings 4) |
| es-419 | 4 | `ios-screenshots/es-419/listing/ipad-13-sign.png` | Signatures popover from the toolbar's Sign button, one card "Mega W.", add buttons in the set's language. | OK |
| es-419 | 5 | `ios-screenshots/es-419/listing/ipad-13-draw.png` | Draw-your-signature panel over the signatures popover, "MegaWoman" drawn. | OK |
| es-419 | 6 | `ios-screenshots/es-419/listing/ipad-13-text.png` | Add-text popover with "Sofía Ramírez" typed, Size 12, Font Helvetica; accents whole by eye. | OK |
| es-419 | 7 | `ios-screenshots/es-419/listing/ipad-13-search.png` | Find bar under the toolbar with "arrendamiento", "1 de 3", first hit selected, Search toggled on. | OK |
| es-419 | 8 | `ios-screenshots/es-419/listing/ipad-13-redact.png` | First line marked: box, handles, remove mark; Undo live, Save enabled. | OK |
| es-419 | 9 | `ios-screenshots/es-419/listing/ipad-13-home.png` | Home with three recents ("Contrato de arrendamiento.pdf" first; "On My iPad" location), status bar with the date. | OK |
| es (Spain) | 1 | `ios-screenshots/es/listing/ipad-13-pages.png` | Pages sidebar on the left (6 tiles, 2 ticked, footer "Selección: 2"), document at the right, iPad toolbar with Pages toggled on. Re-shot 2026-10-24 (#800/#801); date cell spliced from the filed frame. Faint white hint "Hold a page to move it" (localised) sits across the tiles at mid-sidebar, and the document text runs off the right edge behind the sidebar; both are the same in the 2.2 shot. | OK, with a note (see Findings 3) |
| es (Spain) | 2 | `ios-screenshots/es/listing/ipad-13-reading.png` | Reading mode on the iPad: page 1, bar pinned at the bottom ("1/6" … "Salir"), status bar with the date. | OK |
| es (Spain) | 3 | `ios-screenshots/es/listing/ipad-13-text-edit.png` | Edit-text popover on the heading, caret in the field, docked hardware-style keyboard below; toolbar visible behind. | OK, with a note (see Findings 4) |
| es (Spain) | 4 | `ios-screenshots/es/listing/ipad-13-sign.png` | Signatures popover from the toolbar's Sign button, one card "Mega W.", add buttons in the set's language. | OK |
| es (Spain) | 5 | `ios-screenshots/es/listing/ipad-13-draw.png` | Draw-your-signature panel over the signatures popover, "MegaWoman" drawn. | OK |
| es (Spain) | 6 | `ios-screenshots/es/listing/ipad-13-text.png` | Add-text popover with "Lucía Fernández" typed, Size 12, Font Helvetica; accents whole by eye. | OK |
| es (Spain) | 7 | `ios-screenshots/es/listing/ipad-13-search.png` | Find bar under the toolbar with "alquiler", "1 de 3", first hit selected, Search toggled on. | OK |
| es (Spain) | 8 | `ios-screenshots/es/listing/ipad-13-redact.png` | First line marked: box, handles, remove mark; Undo live, Save enabled. | OK |
| es (Spain) | 9 | `ios-screenshots/es/listing/ipad-13-home.png` | Home with three recents ("Contrato de alquiler.pdf" first; "On My iPad" location), status bar with the date. | OK |
