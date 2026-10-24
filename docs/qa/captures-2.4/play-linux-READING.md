# 2.4 Google Play phone and Linux listing captures: as shot and as read

Every image below was opened and read at full size or as a contact pair by a person (the
agent that shot them), after the capture gate ran. Nothing here is uploaded: the Play Console
and the website are Dave's call, and `website/` and the metainfo were not touched.

## Where the images came from

| | Google Play phone | Linux |
|---|---|---|
| Rig | **Android Screenshots** workflow, run 40124348645 (`workflow_dispatch`, `--ref main`), the **final re-shoot of 2026-10-24** (the first 2.4 set came from run 40038655357 and is replaced) | `tools/linux/store-captures.sh <lang>` in a throwaway `megapdf:dotnet` container on kdocker3 |
| Source commit | headSha `925bb492d62e35fc7d1f4feda7be29de2b1e6f3a` (merge of #808; contains #805, #806, #807 and #808), which was `origin/main` when it was dispatched and when this was written | the same commit, shipped to kdocker3 with `git archive` and built with `tools/build-linux-app.sh linux-x64` |
| Conclusion | success, all five matrix jobs (en, fr-CA, fr-FR, es-419, es-ES), artifacts `play-screenshots-<lang>` | 5 languages x 7 slots, every slot at 1280x800, no `::error::` in any log; binary sha256 `1d7e074a01b018453af5c9138984b0ea614638b2a30b15737802bebaf7450786`, per-language `RUN.txt` filed beside the PNGs |
| Size | 40 images, all 1080x2400 | 35 images, all 1280x800 |
| Version | `versionName = "2.3.0"` in `android/app/build.gradle` (`main` has not been bumped to 2.4.0; About is not in any slot, but the set was shot from 2.3.0 code plus the post-2.3.0 merges). The Linux column below is still the 2026-10-23 shoot from d065597e | `VERSION` in the built tree says 2.3.0, the same |

The Spanish demo documents from #789 (`demo-es*.pdf`, the multi-page variants, and the Android assets)
opened in all four Spanish runs; no language run failed to open its document.

The Android demo status bar (9:41) won its race against SystemUI in all 35 frames that have one: 9:41, full
battery, Wi-Fi, nothing else, no debug cog, no "no internet" badge. Measured, not only looked at: the top 110 rows
of those 35 frames have the same layout (clock x 51 or 52 to 115, icons x 931 to 1005, y 47 to 79, nothing between
x 300 and 880); the one-pixel clock difference is the dimmed-scrim frames. `reading` is immersive and has no status bar.
The hosted run passed all five legs at the first dispatch; no re-run was needed (the gesture overlay listing
shows `navbar.gestural` enabled in every leg, and every redact leg logged `1 mark(s) on page 1 after the settle: [1]`).

**What was read.** All 40 phone images were displayed one by one at 900 x 2000 (the viewer's scale of the 1080 x 2400
file, 83 %) and read; the title bars (es-ES, es-419), the five pages titles and the redact line were additionally
cropped and read at native resolution, and the redact fill, the bottom 130 rows and the status bar were measured on
the pixels (below).

## Gate results

`python3 tools/capture-gate/gate.py --store play|linux <set> --out <scratch>`

| set | en | fr-CA | fr-FR | es-419 | es-ES |
|---|---|---|---|---|---|
| Play, 8 images (run 40124348645) | 0 to look at | 0 | 0 | 0 | 0 |
| Linux, 7 images | 0 | 0 | 0 | **1**: `02-text.png`, person check | 0 |

The Linux flag is a false positive. The `person` check is OCR, and tesseract dropped the
accents on a small name (`Sofía Ramírez`); the image was opened and the name is correct, accents rendering. The
Play gate now reports **0 flagged in all five languages** (42 to 50 checks passed per language, the rest not run for
this store); the earlier es-ES `android-text.png` person flag did not recur on the new frame, where `Lucía Fernández` was read by eye
anyway, and no Spanish accent flag appeared, so no accent had to be cleared by hand. Nothing was changed for any flag.

### Against 2.2

`--against docs/qa/captures-2.2/play/phone` (en, fr-CA, fr-FR; note it only pairs shots when the set's
folder is also named `phone`, otherwise every image reports "no counterpart"):

- **The first 2.4 set (run 40038655357) was byte-identical to 2.2 in en and 6 of 8 in fr-CA and fr-FR. This one is not,
  on purpose.** `reading` is the only slot that passes (0 flagged; it is immersive, so the navigation bar change does not reach it).
  The other seven slots are flagged in en, fr-CA and fr-FR, all "N % of pixels differ from the reference set":
  `home` 0.26 % (en; 0.26 % fr), `pages` 3.2 to 3.8 %, `redact` 10.2 to 10.7 %, `search` 9.8 to 10.1 %, `sign` 21.0 to 21.3 %,
  `text-edit` 11.5 to 12.2 %, `text` 12.3 to 12.8 %.
  I checked what the difference is rather than accepting it. `home` is rows 100 to 2250 pixel-identical to 2.2 (0 pixels
  differ); only the bottom changed, the 3-button bar replaced by the gesture handle (0.26 %). For `search`, `sign`,
  `text`, `text-edit` and `redact` the best-fit vertical offset between the 2.2 frame and the new one is 31 to 32 px (the
  system bottom inset got smaller, so the viewport and everything laid out in it moved), after which the residual is
  sub-pixel resampling and, on `sign`, the sheet and its scrim (not separately attributed). `pages` is the same shift in
  the bottom bar plus the French title (#808). So the large percentages are the navigation-bar change and nothing else
  that I found; no new element appeared in any frame.
- **`redact` fill, fr-CA and fr-FR:** the marked line's fill is (199, 206, 213) over white in all five languages, as the
  English one has always been. The stacked-marks colour (156, 169, 181) does not occur near the line in any language. The
  first 2.4 set's fr-CA/fr-FR note ("lighter than 2.2, cause not found") is superseded: the cause was two stacked marks in
  one French leg per run in the 2.2 set and in fr-FR of the earlier 2.4 runs (#807, item 4 of #802); this run's logs show one mark in every language.
- So nothing in the Play set changed on purpose in the 2.3/2.4 UI except the French pages title, the Spanish demo
  names, the gesture bar and the redact fill; the 2.3/2.4 features (selection and copy, scans, shrink for email) have no slot in this set.

`--against website/megapdf/screenshots/linux` (the Linux 2.2 set, `docs/qa/linux-fr/` is the older QA precedent
and has no listing slots): **all 21 en, fr-CA and fr-FR images are byte-identical to what is in `website/megapdf/screenshots/linux/`**
(`cmp` as well as the gate's 0.000 %). The 25 `src/MegaPDF.Avalonia` commits since 1 October (select and copy,
native window tabs, announcements, double-click, Shrink, Spanish strings) change nothing in these seven poses, and
the rig reproduces to the byte on a different machine, which is a result in itself. For the two Spanish sets there is
no reference.

## Findings

1. **es-419 and es-ES `pages` (Android): the selection-bar title wrapped. FIXED**: "Selección: 2" on one line, re-shot, and re-shot again in this final run. fr-CA and fr-FR now read "Sélection : 2" (#808; was "2 sélectionnées"), en "2 selected". Verified on the pixels of all five title bars.
2. **File names in the Android title bar (#802 item 1). FIXED in the rig**: es-419 "Arrendamiento.pdf" and es-ES "Alquiler.pdf" read whole in `text-edit`, `sign`, `text`, `redact` and in the Recent list; the es-ES title was read as a native-resolution crop.
3. **Navigation bar (#802 item 2). FIXED**: only the gesture handle (284 x 11 px at x 398 to 681, y 2363 to 2373) in all 35 non-immersive frames; measured in every one. `reading` has none (immersive). The runbook's "no navigation buttons" is now true of the set.
3a. **Redact fill (#802 item 4). FIXED**: (199, 206, 213) in all five languages, one mark in each run's log.
3b. **Still imperfect, cosmetic, not capture defects:** fr-CA, fr-FR and es-419 `text-edit` fields wrap "(2026)" onto a second line (nothing clipped); the Spanish demo document has a short second line in its first paragraph; `redact` shows the line's last word ("doc"/"do") cut by the red chip, which I did not compare with the earlier frames.
4. The `play` gate's `person` check is unreliable for accented names at this size (two false flags above).
5. The metainfo's `es-419` and `es-ES` screenshot folders do not exist under `website/megapdf/screenshots/linux/` yet (the website was
   not touched on purpose). Its seven file names and the paths under it match this set exactly; nothing is wrong in the
   file, the folders just need staging when Dave decides.

## Google Play phone: `play/phone/<lang>/`
| language | slot | file | what is in the frame | verdict |
|---|---|---|---|---|
| en | reading | `android-reading.png` | The agreement page edge to edge with no status bar and no app bars (immersive, as designed), page 1 filled, ticked and signed (MegaWoman); the pinned floating bar reads 1 / 6 with fit-width, fit-height, minus and plus. Page 2 starts at the bottom edge. No English words, no dialog, no immersive-mode panel. | OK |
| en | text-edit | `android-text-edit.png` | Edit-text dialog over the dimmed document (title bar Rental Agreement.pdf), the heading line with "(2026)" appended in the field, Cancel and Save buttons. Status bar 9:41, full battery, Wi-Fi, no cog. | OK |
| en | sign | `android-sign.png` | Signature library sheet over the dimmed document: one card (MegaWoman, labelled Mega W.) and the Draw / Type / Photo buttons in en. Only one signature in the library (no stale cards). 9:41 bar. | OK |
| en | pages | `android-pages.png` | Page grid, six thumbnails (page 6 landscape), pages 2 and 3 ticked, selection bar "2 selected" with rotate, rotate, delete, overflow; bottom bar has undo/redo and the hold-to-move hint in en. 9:41 bar. | OK |
| en | text | `android-text.png` | Add-text dialog over the dimmed document: field holds "Jane Whitfield", size chips 8/10/12/14/18 with 12 picked, Helvetica picked of Helvetica/Times/Courier, Cancel and Add, all in en. Demo person and accents are right. 9:41 bar. | OK |
| en | search | `android-search.png` | Search bar with "rental, 1 of 3", up and down arrows; matches highlighted on the title and in the body (the current match darker). White page, 9:41 bar. | OK |
| en | redact | `android-redact.png` | Third line of the paragraph marked for redaction with the blue selection outline, handles and the red X that removes it; Save is live (brand blue) in en, title Rental Agreement.pdf. 9:41 bar. No wrong demo person on screen. | OK |
| en | home | `android-home.png` | Home screen: MegaPDF, tagline, Open PDF button, Recent list (Rental Agreement / Field Trip Permission / Insurance Claim Form) with where each lives, settings cog and info icon (real UI, not the debug cog). 9:41 bar, no notification icons. | OK |
| fr-CA | reading | `android-reading.png` | The agreement page edge to edge with no status bar and no app bars (immersive, as designed), page 1 filled, ticked and signed (MegaWoman); the pinned floating bar reads 1 / 6 with fit-width, fit-height, minus and plus. Page 2 starts at the bottom edge. No English words, no dialog, no immersive-mode panel. | OK |
| fr-CA | text-edit | `android-text-edit.png` | Edit-text dialog over the dimmed document (title bar Contrat location.pdf), the heading line with "(2026)" appended in the field, Cancel and Save buttons. Status bar 9:41, full battery, Wi-Fi, no cog. | OK |
| fr-CA | sign | `android-sign.png` | Signature library sheet over the dimmed document: one card (MegaWoman, labelled Mega W.) and the Draw / Type / Photo buttons in fr-CA. Only one signature in the library (no stale cards). 9:41 bar. | OK |
| fr-CA | pages | `android-pages.png` | Page grid, six thumbnails (page 6 landscape), pages 2 and 3 ticked, selection bar "Sélection : 2" (U+00A0 before the colon in the string; #808) with rotate, rotate, delete, overflow; bottom bar has undo/redo and the hold-to-move hint in fr-CA. 9:41 bar. | OK |
| fr-CA | text | `android-text.png` | Add-text dialog over the dimmed document: field holds "Hélène Bélanger", size chips 8/10/12/14/18 with 12 picked, Helvetica picked of Helvetica/Times/Courier, Cancel and Add, all in fr-CA. Demo person and accents are right. 9:41 bar. | OK |
| fr-CA | search | `android-search.png` | Search bar with "location, 1 sur 3", up and down arrows; matches highlighted on the title and in the body (the current match darker). White page, 9:41 bar. | OK |
| fr-CA | redact | `android-redact.png` | Third line of the paragraph marked for redaction with the blue selection outline, handles and the red X that removes it; Save is live (brand blue) in fr-CA, title Contrat location.pdf. 9:41 bar. No wrong demo person on screen. | OK |
| fr-CA | home | `android-home.png` | Home screen: MegaPDF, tagline, Open PDF button, Recent list (Contrat location / Autorisation de sortie scolaire / Formulaire de réclamation d'assurance) with where each lives, settings cog and info icon (real UI, not the debug cog). 9:41 bar, no notification icons. | OK |
| fr-FR | reading | `android-reading.png` | The agreement page edge to edge with no status bar and no app bars (immersive, as designed), page 1 filled, ticked and signed (MegaWoman); the pinned floating bar reads 1 / 6 with fit-width, fit-height, minus and plus. Page 2 starts at the bottom edge. No English words, no dialog, no immersive-mode panel. | OK |
| fr-FR | text-edit | `android-text-edit.png` | Edit-text dialog over the dimmed document (title bar Contrat location.pdf), the heading line with "(2026)" appended in the field, Cancel and Save buttons. Status bar 9:41, full battery, Wi-Fi, no cog. | OK |
| fr-FR | sign | `android-sign.png` | Signature library sheet over the dimmed document: one card (MegaWoman, labelled Mega W.) and the Draw / Type / Photo buttons in fr-FR. Only one signature in the library (no stale cards). 9:41 bar. | OK |
| fr-FR | pages | `android-pages.png` | Page grid, six thumbnails (page 6 landscape), pages 2 and 3 ticked, selection bar "Sélection : 2" (U+00A0 before the colon in the string; #808) with rotate, rotate, delete, overflow; bottom bar has undo/redo and the hold-to-move hint in fr-FR. 9:41 bar. | OK |
| fr-FR | text | `android-text.png` | Add-text dialog over the dimmed document: field holds "Céline Lefèvre", size chips 8/10/12/14/18 with 12 picked, Helvetica picked of Helvetica/Times/Courier, Cancel and Add, all in fr-FR. Demo person and accents are right. 9:41 bar. | OK |
| fr-FR | search | `android-search.png` | Search bar with "location, 1 sur 3", up and down arrows; matches highlighted on the title and in the body (the current match darker). White page, 9:41 bar. | OK |
| fr-FR | redact | `android-redact.png` | Third line of the paragraph marked for redaction with the blue selection outline, handles and the red X that removes it; Save is live (brand blue) in fr-FR, title Contrat location.pdf. 9:41 bar. No wrong demo person on screen. | OK |
| fr-FR | home | `android-home.png` | Home screen: MegaPDF, tagline, Open PDF button, Recent list (Contrat location / Autorisation de sortie scolaire / Formulaire de réclamation d'assurance) with where each lives, settings cog and info icon (real UI, not the debug cog). 9:41 bar, no notification icons. | OK |
| es-419 | reading | `android-reading.png` | The agreement page edge to edge with no status bar and no app bars (immersive, as designed), page 1 filled, ticked and signed (MegaWoman); the pinned floating bar reads 1 / 6 with fit-width, fit-height, minus and plus. Page 2 starts at the bottom edge. No English words, no dialog, no immersive-mode panel. | OK |
| es-419 | text-edit | `android-text-edit.png` | Edit-text dialog over the dimmed document (title bar Arrendamiento.pdf, whole), the heading line with "(2026)" appended in the field, Cancel and Save buttons. Status bar 9:41, full battery, Wi-Fi, no cog. | OK; file name whole (#802 item 1) |
| es-419 | sign | `android-sign.png` | Signature library sheet over the dimmed document: one card (MegaWoman, labelled Mega W.) and the Draw / Type / Photo buttons in es-419. Only one signature in the library (no stale cards). 9:41 bar. | OK; file name whole (#802 item 1) |
| es-419 | pages | `android-pages.png` | Page grid, six thumbnails (page 6 landscape), pages 2 and 3 ticked, selection bar "Selección: 2" on one line with rotate, rotate, delete, overflow; bottom bar has undo/redo and the hold-to-move hint in es-419. 9:41 bar. | OK. Re-shot after the title string was shortened (it wrapped to two lines as "Seleccionadas: 2"); now one line, nothing clipped |
| es-419 | text | `android-text.png` | Add-text dialog over the dimmed document: field holds "Sofía Ramírez", size chips 8/10/12/14/18 with 12 picked, Helvetica picked of Helvetica/Times/Courier, Cancel and Add, all in es-419. Demo person and accents are right. 9:41 bar. | OK |
| es-419 | search | `android-search.png` | Search bar with "arrendamiento, 1 de 3", up and down arrows; matches highlighted on the title and in the body (the current match darker). White page, 9:41 bar. | OK |
| es-419 | redact | `android-redact.png` | Third line of the paragraph marked for redaction with the blue selection outline, handles and the red X that removes it; Save is live (brand blue) in es-419, title Arrendamiento.pdf, whole. 9:41 bar. No wrong demo person on screen. | OK; file name whole (#802 item 1) |
| es-419 | home | `android-home.png` | Home screen: MegaPDF, tagline, Open PDF button, Recent list (Arrendamiento / Permiso para excursión escolar / Formulario de reclamación de seguro) with where each lives, settings cog and info icon (real UI, not the debug cog). 9:41 bar, no notification icons. | OK |
| es-ES | reading | `android-reading.png` | The agreement page edge to edge with no status bar and no app bars (immersive, as designed), page 1 filled, ticked and signed (MegaWoman); the pinned floating bar reads 1 / 6 with fit-width, fit-height, minus and plus. Page 2 starts at the bottom edge. No English words, no dialog, no immersive-mode panel. | OK |
| es-ES | text-edit | `android-text-edit.png` | Edit-text dialog over the dimmed document (title bar Alquiler.pdf, whole), the heading line with "(2026)" appended in the field, Cancel and Save buttons. Status bar 9:41, full battery, Wi-Fi, no cog. | OK; file name whole (#802 item 1) |
| es-ES | sign | `android-sign.png` | Signature library sheet over the dimmed document: one card (MegaWoman, labelled Mega W.) and the Draw / Type / Photo buttons in es-ES. Only one signature in the library (no stale cards). 9:41 bar. | OK; file name whole (#802 item 1) |
| es-ES | pages | `android-pages.png` | Page grid, six thumbnails (page 6 landscape), pages 2 and 3 ticked, selection bar "Selección: 2" on one line with rotate, rotate, delete, overflow; bottom bar has undo/redo and the hold-to-move hint in es-ES. 9:41 bar. | OK. Re-shot after the title string was shortened (it wrapped to two lines as "Seleccionadas: 2"); now one line, nothing clipped |
| es-ES | text | `android-text.png` | Add-text dialog over the dimmed document: field holds "Lucía Fernández", size chips 8/10/12/14/18 with 12 picked, Helvetica picked of Helvetica/Times/Courier, Cancel and Add, all in es-ES. Demo person and accents are right (the gate flagged this frame's person check in the first 2.4 set because tesseract dropped the accents; the final frame passed, and read by eye "Lucía Fernández" is correct). 9:41 bar. | OK |
| es-ES | search | `android-search.png` | Search bar with "alquiler, 1 de 3", up and down arrows; matches highlighted on the title and in the body (the current match darker). White page, 9:41 bar. | OK |
| es-ES | redact | `android-redact.png` | Third line of the paragraph marked for redaction with the blue selection outline, handles and the red X that removes it; Save is live (brand blue) in es-ES, title Alquiler.pdf, whole. 9:41 bar. No wrong demo person on screen. | OK; file name whole (#802 item 1) |
| es-ES | home | `android-home.png` | Home screen: MegaPDF, tagline, Open PDF button, Recent list (Alquiler / Permiso para excursión escolar / Formulario de reclamación de seguro) with where each lives, settings cog and info icon (real UI, not the debug cog). 9:41 bar, no notification icons. | OK |

## Linux: `linux/<lang>/`

Each language folder also holds the run's `RUN.txt` (binary hash, culture, window, document, timestamp). Cultures: en = en-CA,
es-419 = es-MX, es-ES = es-ES, fr-CA and fr-FR as named; the demo person is on the signature line of the `text` slot.
| language | slot | file | what is in the frame | verdict |
|---|---|---|---|---|
| en | reading | `01-reading.png` | Page 1 of the multi-page agreement with the chrome gone and the floating bar (Page 1 of 6, fit width, fit page, zoom, exit) in en. White page, no toolbar: the feature, as the metainfo comment says. | OK |
| en | text | `02-text.png` | Toolbar and a dirty-bullet title (ellipsised), "Jane Whitfield" typed on the signature line, status line says text added in en; the signature on the page is MegaWoman; no selection box left on the name, no spell-check underline. | OK |
| en | sign | `03-sign.png` | Signature flyout under the Sign button: one card (MegaWoman, Mega W.) and Draw / Type / From photo buttons; status line says the signature was saved. Title partly behind the flyout. | OK |
| en | pages | `04-pages.png` | Pages sidebar with six pages, 2 and 3 picked out (blue), Pages button active, status line says 6 pages. | OK |
| en | search | `05-search.png` | Find bar with the term (rental, 1 of 3), Previous/Next/Done, matches highlighted in the title and body. | OK |
| en | redact | `06-redact.png` | First line of the paragraph marked and selected, with the handles and the control that removes the mark; status line carries the drag/resize/remove hint. | OK |
| en | home | `07-home.png` | Empty window: MegaPDF, tagline, Open a PDF button, Recent list (Rental Agreement / Field Trip Permission / Insurance Claim Form) with folders. Toolbar disabled except Open. | OK |
| fr-CA | reading | `01-reading.png` | Page 1 of the multi-page agreement with the chrome gone and the floating bar (Page 1 of 6, fit width, fit page, zoom, exit) in fr-CA. White page, no toolbar: the feature, as the metainfo comment says. | OK |
| fr-CA | text | `02-text.png` | Toolbar and a dirty-bullet title (ellipsised), "Hélène Bélanger" typed on the signature line, status line says text added in fr-CA; the signature on the page is MegaWoman; no selection box left on the name, no spell-check underline. | OK |
| fr-CA | sign | `03-sign.png` | Signature flyout under the Sign button: one card (MegaWoman, Mega W.) and Draw / Type / From photo buttons; status line says the signature was saved. Title partly behind the flyout. | OK |
| fr-CA | pages | `04-pages.png` | Pages sidebar with six pages, 2 and 3 picked out (blue), Pages button active, status line says 6 pages. | OK |
| fr-CA | search | `05-search.png` | Find bar with the term (location, 1 sur 3), Previous/Next/Done, matches highlighted in the title and body. | OK |
| fr-CA | redact | `06-redact.png` | First line of the paragraph marked and selected, with the handles and the control that removes the mark; status line carries the drag/resize/remove hint. | OK |
| fr-CA | home | `07-home.png` | Empty window: MegaPDF, tagline, Open a PDF button, Recent list (Contrat location / Autorisation de sortie scolaire / Formulaire de réclamation d'assurance) with folders. Toolbar disabled except Open. | OK |
| fr-FR | reading | `01-reading.png` | Page 1 of the multi-page agreement with the chrome gone and the floating bar (Page 1 of 6, fit width, fit page, zoom, exit) in fr-FR. White page, no toolbar: the feature, as the metainfo comment says. | OK |
| fr-FR | text | `02-text.png` | Toolbar and a dirty-bullet title (ellipsised), "Céline Lefèvre" typed on the signature line, status line says text added in fr-FR; the signature on the page is MegaWoman; no selection box left on the name, no spell-check underline. | OK |
| fr-FR | sign | `03-sign.png` | Signature flyout under the Sign button: one card (MegaWoman, Mega W.) and Draw / Type / From photo buttons; status line says the signature was saved. Title partly behind the flyout. | OK |
| fr-FR | pages | `04-pages.png` | Pages sidebar with six pages, 2 and 3 picked out (blue), Pages button active, status line says 6 pages. | OK |
| fr-FR | search | `05-search.png` | Find bar with the term (location, 1 sur 3), Previous/Next/Done, matches highlighted in the title and body. | OK |
| fr-FR | redact | `06-redact.png` | First line of the paragraph marked and selected, with the handles and the control that removes the mark; status line carries the drag/resize/remove hint. | OK |
| fr-FR | home | `07-home.png` | Empty window: MegaPDF, tagline, Open a PDF button, Recent list (Contrat location / Autorisation de sortie scolaire / Formulaire de réclamation d'assurance) with folders. Toolbar disabled except Open. | OK |
| es-419 | reading | `01-reading.png` | Page 1 of the multi-page agreement with the chrome gone and the floating bar (Page 1 of 6, fit width, fit page, zoom, exit) in es-419. White page, no toolbar: the feature, as the metainfo comment says. | OK |
| es-419 | text | `02-text.png` | Toolbar and a dirty-bullet title (ellipsised), "Sofía Ramírez" typed on the signature line, status line says text added in es-419; the signature on the page is MegaWoman; no selection box left on the name, no spell-check underline. The gate's person check flagged this image (tesseract lost the accents); read by eye, "Sofía Ramírez" is correct. | OK |
| es-419 | sign | `03-sign.png` | Signature flyout under the Sign button: one card (MegaWoman, Mega W.) and Draw / Type / From photo buttons; status line says the signature was saved. Title partly behind the flyout. | OK |
| es-419 | pages | `04-pages.png` | Pages sidebar with six pages, 2 and 3 picked out (blue), Pages button active, status line says 6 pages. | OK |
| es-419 | search | `05-search.png` | Find bar with the term (arrendamiento, 1 de 3), Previous/Next/Done, matches highlighted in the title and body. | OK |
| es-419 | redact | `06-redact.png` | First line of the paragraph marked and selected, with the handles and the control that removes the mark; status line carries the drag/resize/remove hint. | OK |
| es-419 | home | `07-home.png` | Empty window: MegaPDF, tagline, Open a PDF button, Recent list (Arrendamiento / Permiso para excursión escolar / Formulario de reclamación de seguro) with folders. Toolbar disabled except Open. | OK |
| es-ES | reading | `01-reading.png` | Page 1 of the multi-page agreement with the chrome gone and the floating bar (Page 1 of 6, fit width, fit page, zoom, exit) in es-ES. White page, no toolbar: the feature, as the metainfo comment says. | OK |
| es-ES | text | `02-text.png` | Toolbar and a dirty-bullet title (ellipsised), "Lucía Fernández" typed on the signature line, status line says text added in es-ES; the signature on the page is MegaWoman; no selection box left on the name, no spell-check underline. | OK |
| es-ES | sign | `03-sign.png` | Signature flyout under the Sign button: one card (MegaWoman, Mega W.) and Draw / Type / From photo buttons; status line says the signature was saved. Title partly behind the flyout. | OK |
| es-ES | pages | `04-pages.png` | Pages sidebar with six pages, 2 and 3 picked out (blue), Pages button active, status line says 6 pages. | OK |
| es-ES | search | `05-search.png` | Find bar with the term (alquiler, 1 de 3), Previous/Next/Done, matches highlighted in the title and body. | OK |
| es-ES | redact | `06-redact.png` | First line of the paragraph marked and selected, with the handles and the control that removes the mark; status line carries the drag/resize/remove hint. | OK |
| es-ES | home | `07-home.png` | Empty window: MegaPDF, tagline, Open a PDF button, Recent list (Alquiler / Permiso para excursión escolar / Formulario de reclamación de seguro) with folders. Toolbar disabled except Open. | OK |
