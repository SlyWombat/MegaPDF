# 2.4 Google Play phone and Linux listing captures: as shot and as read

Every image below was opened and read at full size or as a contact pair by a person (the
agent that shot them), after the capture gate ran. Nothing here is uploaded: the Play Console
and the website are Dave's call, and `website/` and the metainfo were not touched.

## Where the images came from

| | Google Play phone | Linux |
|---|---|---|
| Rig | **Android Screenshots** workflow, run 40038655357 (`workflow_dispatch`, `--ref main`) | `tools/linux/store-captures.sh <lang>` in a throwaway `megapdf:dotnet` container on kdocker3 |
| Source commit | headSha `d065597e5097940d29b4e90868667c1fa83d0e9f`, which was `origin/main` when it was dispatched and when this was written | the same commit, shipped to kdocker3 with `git archive` and built with `tools/build-linux-app.sh linux-x64` |
| Conclusion | success, all five matrix jobs (en, fr-CA, fr-FR, es-419, es-ES), artifacts `play-screenshots-<lang>` | 5 languages x 7 slots, every slot at 1280x800, no `::error::` in any log; binary sha256 `1d7e074a01b018453af5c9138984b0ea614638b2a30b15737802bebaf7450786`, per-language `RUN.txt` filed beside the PNGs |
| Size | 40 images, all 1080x2400 | 35 images, all 1280x800 |
| Version | `versionName = "2.3.0"` in `android/app/build.gradle` (`main` has not been bumped to 2.4.0; About is not in any slot, but the set was shot from 2.3.0 code plus the post-2.3.0 merges) | `VERSION` in the built tree says 2.3.0, the same |

The Spanish demo documents from #789 (`demo-es*.pdf`, the multi-page variants, and the Android assets)
opened in all four Spanish runs; no language run failed to open its document.

The Android demo status bar (9:41) won its race against SystemUI in all 40 shots: 9:41, full
battery, Wi-Fi, nothing else, no debug cog, no "no internet" badge. No re-run was needed.

## Gate results

`python3 tools/capture-gate/gate.py --store play|linux <set> --out <scratch>`

| set | en | fr-CA | fr-FR | es-419 | es-ES |
|---|---|---|---|---|---|
| Play, 8 images | 0 to look at | 0 | 0 | 0 | **1**: `android-text.png`, person check |
| Linux, 7 images | 0 | 0 | 0 | **1**: `02-text.png`, person check | 0 |

Both flags are the same false positive. The `person` check is OCR, and tesseract dropped the
accents on a small name (`Lucía Fernández`, `Sofía Ramírez`). Both images were opened and the name
is correct, accents rendering, in each. Nothing was changed for them.

### Against 2.2

`--against docs/qa/captures-2.2/play/phone` (en, fr-CA, fr-FR; note it only pairs shots when the set's
folder is also named `phone`, otherwise every image reports "no counterpart"):

- **en: all 8 byte-identical to 2.2.**
- **fr-CA and fr-FR: 6 of 8 byte-identical.** `android-reading.png` differs by 2 pixels and
  `android-sign.png` by 17 pixels out of 2.6 million (anti-aliasing noise; the gate rounds both to 0.000 %
  and passes them). **`android-redact.png` is the one real change, 0.26 % of pixels (6,611) and the gate flags it.**
  In 2.2 the marked line's fill under the text in French was a darker slate (`#9CA9B5`); in this run it is the light grey-blue
  (`#C7CED5`) that the English redact image has always had, and the English image is unchanged. So the
  French mark now agrees with the English one. I could not tie this to a specific commit: the Android UI
  changes since the 2.2 set (selection handles and copy #710, Shrink for email #593, form-field editor #701,
  Spanish) are not features this pose shows, and I did not bisect. It is a fill-tint change inside the mark only; the
  mark position, the handles, the red X and everything else in the frame are the same. Read by eye, it is not a defect.
- So nothing in the Play set changed on purpose in the 2.3/2.4 UI: reading mode, the page grid, the signature
  sheet and the rest are posed exactly as in 2.2, and the 2.3/2.4 features (selection and copy, scans,
  shrink for email) have no slot in this set.

`--against website/megapdf/screenshots/linux` (the Linux 2.2 set, `docs/qa/linux-fr/` is the older QA precedent
and has no listing slots): **all 21 en, fr-CA and fr-FR images are byte-identical to what is in `website/megapdf/screenshots/linux/`**
(`cmp` as well as the gate's 0.000 %). The 25 `src/MegaPDF.Avalonia` commits since 1 October (select and copy,
native window tabs, announcements, double-click, Shrink, Spanish strings) change nothing in these seven poses, and
the rig reproduces to the byte on a different machine, which is a result in itself. For the two Spanish sets there is
no reference.

## Findings (not fixed, none is a capture defect)

1. **es-419 and es-ES `pages` (Android): the selection-bar title wrapped. FIXED** (this branch): the string is now "Selección: 2", one line, and the two `android-pages.png` files were re-shot from the Android Screenshots workflow (run 40052390473). It was the catalogue string, not the capture.
2. **es-ES file name in the Android title bar reads "Contrato de alquiler.p…"**: the ellipsis cuts into the extension.
   es-419 reads "Contrato de arrenda…". Same #181 ellipsising title, expected behaviour at this length, and French shows a
   shorter "Contrat location.pdf" in full. Not a clipped glyph. Cosmetic only.
3. **The Android frames carry the 3-button navigation bar** (back, home, recents) along the bottom. It is identical in the 2.2
   set that was signed off and is a phone's own chrome, not the tablet launcher taskbar the runbook rules out; recorded
   only because runbook item 5 says "no navigation buttons".
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
| fr-CA | pages | `android-pages.png` | Page grid, six thumbnails (page 6 landscape), pages 2 and 3 ticked, selection bar "2 sélectionnées" with rotate, rotate, delete, overflow; bottom bar has undo/redo and the hold-to-move hint in fr-CA. 9:41 bar. | OK |
| fr-CA | text | `android-text.png` | Add-text dialog over the dimmed document: field holds "Hélène Bélanger", size chips 8/10/12/14/18 with 12 picked, Helvetica picked of Helvetica/Times/Courier, Cancel and Add, all in fr-CA. Demo person and accents are right. 9:41 bar. | OK |
| fr-CA | search | `android-search.png` | Search bar with "location, 1 sur 3", up and down arrows; matches highlighted on the title and in the body (the current match darker). White page, 9:41 bar. | OK |
| fr-CA | redact | `android-redact.png` | Third line of the paragraph marked for redaction with the blue selection outline, handles and the red X that removes it; Save is live (brand blue) in fr-CA, title Contrat location.pdf. 9:41 bar. No wrong demo person on screen. | OK |
| fr-CA | home | `android-home.png` | Home screen: MegaPDF, tagline, Open PDF button, Recent list (Contrat location / Autorisation de sortie scolaire / Formulaire de réclamation d'assurance) with where each lives, settings cog and info icon (real UI, not the debug cog). 9:41 bar, no notification icons. | OK |
| fr-FR | reading | `android-reading.png` | The agreement page edge to edge with no status bar and no app bars (immersive, as designed), page 1 filled, ticked and signed (MegaWoman); the pinned floating bar reads 1 / 6 with fit-width, fit-height, minus and plus. Page 2 starts at the bottom edge. No English words, no dialog, no immersive-mode panel. | OK |
| fr-FR | text-edit | `android-text-edit.png` | Edit-text dialog over the dimmed document (title bar Contrat location.pdf), the heading line with "(2026)" appended in the field, Cancel and Save buttons. Status bar 9:41, full battery, Wi-Fi, no cog. | OK |
| fr-FR | sign | `android-sign.png` | Signature library sheet over the dimmed document: one card (MegaWoman, labelled Mega W.) and the Draw / Type / Photo buttons in fr-FR. Only one signature in the library (no stale cards). 9:41 bar. | OK |
| fr-FR | pages | `android-pages.png` | Page grid, six thumbnails (page 6 landscape), pages 2 and 3 ticked, selection bar "2 sélectionnées" with rotate, rotate, delete, overflow; bottom bar has undo/redo and the hold-to-move hint in fr-FR. 9:41 bar. | OK |
| fr-FR | text | `android-text.png` | Add-text dialog over the dimmed document: field holds "Céline Lefèvre", size chips 8/10/12/14/18 with 12 picked, Helvetica picked of Helvetica/Times/Courier, Cancel and Add, all in fr-FR. Demo person and accents are right. 9:41 bar. | OK |
| fr-FR | search | `android-search.png` | Search bar with "location, 1 sur 3", up and down arrows; matches highlighted on the title and in the body (the current match darker). White page, 9:41 bar. | OK |
| fr-FR | redact | `android-redact.png` | Third line of the paragraph marked for redaction with the blue selection outline, handles and the red X that removes it; Save is live (brand blue) in fr-FR, title Contrat location.pdf. 9:41 bar. No wrong demo person on screen. | OK |
| fr-FR | home | `android-home.png` | Home screen: MegaPDF, tagline, Open PDF button, Recent list (Contrat location / Autorisation de sortie scolaire / Formulaire de réclamation d'assurance) with where each lives, settings cog and info icon (real UI, not the debug cog). 9:41 bar, no notification icons. | OK |
| es-419 | reading | `android-reading.png` | The agreement page edge to edge with no status bar and no app bars (immersive, as designed), page 1 filled, ticked and signed (MegaWoman); the pinned floating bar reads 1 / 6 with fit-width, fit-height, minus and plus. Page 2 starts at the bottom edge. No English words, no dialog, no immersive-mode panel. | OK |
| es-419 | text-edit | `android-text-edit.png` | Edit-text dialog over the dimmed document (title bar Contrato de arrenda… (ellipsised)), the heading line with "(2026)" appended in the field, Cancel and Save buttons. Status bar 9:41, full battery, Wi-Fi, no cog. | OK; the file name in the title bar is ellipsised (the #181 ellipsising title, expected at this length) |
| es-419 | sign | `android-sign.png` | Signature library sheet over the dimmed document: one card (MegaWoman, labelled Mega W.) and the Draw / Type / Photo buttons in es-419. Only one signature in the library (no stale cards). 9:41 bar. | OK; the file name in the title bar is ellipsised (the #181 ellipsising title, expected at this length) |
| es-419 | pages | `android-pages.png` | Page grid, six thumbnails (page 6 landscape), pages 2 and 3 ticked, selection bar "Selección: 2" on one line with rotate, rotate, delete, overflow; bottom bar has undo/redo and the hold-to-move hint in es-419. 9:41 bar. | OK. Re-shot after the title string was shortened (it wrapped to two lines as "Seleccionadas: 2"); now one line, nothing clipped |
| es-419 | text | `android-text.png` | Add-text dialog over the dimmed document: field holds "Sofía Ramírez", size chips 8/10/12/14/18 with 12 picked, Helvetica picked of Helvetica/Times/Courier, Cancel and Add, all in es-419. Demo person and accents are right. 9:41 bar. | OK |
| es-419 | search | `android-search.png` | Search bar with "arrendamiento, 1 de 3", up and down arrows; matches highlighted on the title and in the body (the current match darker). White page, 9:41 bar. | OK |
| es-419 | redact | `android-redact.png` | Third line of the paragraph marked for redaction with the blue selection outline, handles and the red X that removes it; Save is live (brand blue) in es-419, title Contrato de arrenda… (ellipsised). 9:41 bar. No wrong demo person on screen. | OK; the file name in the title bar is ellipsised (the #181 ellipsising title, expected at this length) |
| es-419 | home | `android-home.png` | Home screen: MegaPDF, tagline, Open PDF button, Recent list (Contrato de arrendamiento / Permiso para excursión escolar / Formulario de reclamación de seguro) with where each lives, settings cog and info icon (real UI, not the debug cog). 9:41 bar, no notification icons. | OK |
| es-ES | reading | `android-reading.png` | The agreement page edge to edge with no status bar and no app bars (immersive, as designed), page 1 filled, ticked and signed (MegaWoman); the pinned floating bar reads 1 / 6 with fit-width, fit-height, minus and plus. Page 2 starts at the bottom edge. No English words, no dialog, no immersive-mode panel. | OK |
| es-ES | text-edit | `android-text-edit.png` | Edit-text dialog over the dimmed document (title bar Contrato de alquiler.p… (ellipsised)), the heading line with "(2026)" appended in the field, Cancel and Save buttons. Status bar 9:41, full battery, Wi-Fi, no cog. | OK; the file name in the title bar is ellipsised (the #181 ellipsising title, expected at this length) |
| es-ES | sign | `android-sign.png` | Signature library sheet over the dimmed document: one card (MegaWoman, labelled Mega W.) and the Draw / Type / Photo buttons in es-ES. Only one signature in the library (no stale cards). 9:41 bar. | OK; the file name in the title bar is ellipsised (the #181 ellipsising title, expected at this length) |
| es-ES | pages | `android-pages.png` | Page grid, six thumbnails (page 6 landscape), pages 2 and 3 ticked, selection bar "Selección: 2" on one line with rotate, rotate, delete, overflow; bottom bar has undo/redo and the hold-to-move hint in es-ES. 9:41 bar. | OK. Re-shot after the title string was shortened (it wrapped to two lines as "Seleccionadas: 2"); now one line, nothing clipped |
| es-ES | text | `android-text.png` | Add-text dialog over the dimmed document: field holds "Lucía Fernández", size chips 8/10/12/14/18 with 12 picked, Helvetica picked of Helvetica/Times/Courier, Cancel and Add, all in es-ES. Demo person and accents are right (the gate's person check flagged this image because tesseract dropped the accents; read by eye at full size, "Lucía Fernández" is correct). 9:41 bar. | OK |
| es-ES | search | `android-search.png` | Search bar with "alquiler, 1 de 3", up and down arrows; matches highlighted on the title and in the body (the current match darker). White page, 9:41 bar. | OK |
| es-ES | redact | `android-redact.png` | Third line of the paragraph marked for redaction with the blue selection outline, handles and the red X that removes it; Save is live (brand blue) in es-ES, title Contrato de alquiler.p… (ellipsised). 9:41 bar. No wrong demo person on screen. | OK; the file name in the title bar is ellipsised (the #181 ellipsising title, expected at this length) |
| es-ES | home | `android-home.png` | Home screen: MegaPDF, tagline, Open PDF button, Recent list (Contrato de alquiler / Permiso para excursión escolar / Formulario de reclamación de seguro) with where each lives, settings cog and info icon (real UI, not the debug cog). 9:41 bar, no notification icons. | OK |

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
| es-419 | home | `07-home.png` | Empty window: MegaPDF, tagline, Open a PDF button, Recent list (Contrato de arrendamiento / Permiso para excursión escolar / Formulario de reclamación de seguro) with folders. Toolbar disabled except Open. | OK |
| es-ES | reading | `01-reading.png` | Page 1 of the multi-page agreement with the chrome gone and the floating bar (Page 1 of 6, fit width, fit page, zoom, exit) in es-ES. White page, no toolbar: the feature, as the metainfo comment says. | OK |
| es-ES | text | `02-text.png` | Toolbar and a dirty-bullet title (ellipsised), "Lucía Fernández" typed on the signature line, status line says text added in es-ES; the signature on the page is MegaWoman; no selection box left on the name, no spell-check underline. | OK |
| es-ES | sign | `03-sign.png` | Signature flyout under the Sign button: one card (MegaWoman, Mega W.) and Draw / Type / From photo buttons; status line says the signature was saved. Title partly behind the flyout. | OK |
| es-ES | pages | `04-pages.png` | Pages sidebar with six pages, 2 and 3 picked out (blue), Pages button active, status line says 6 pages. | OK |
| es-ES | search | `05-search.png` | Find bar with the term (alquiler, 1 de 3), Previous/Next/Done, matches highlighted in the title and body. | OK |
| es-ES | redact | `06-redact.png` | First line of the paragraph marked and selected, with the handles and the control that removes the mark; status line carries the drag/resize/remove hint. | OK |
| es-ES | home | `07-home.png` | Empty window: MegaPDF, tagline, Open a PDF button, Recent list (Contrato de alquiler / Permiso para excursión escolar / Formulario de reclamación de seguro) with folders. Toolbar disabled except Open. | OK |
