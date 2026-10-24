# Microsoft Store listing screenshots, 2.4: the reading

Shot 2026-10-23 on GPD-DAVE, packaged build `ElectricRV.MegaPDF` 2.3.0.0 (CI `MegaPDF-store-packages` from main bbe771d3, the Windows app is unchanged in d065597), test-signed copy installed through `Install-QaTestPackage`, display at 150 percent, 2500x1550 frame (2482x1541 captured), `Shoot-Set.ps1` for each language. Every one of the 40 images was opened and read by eye (the six images in the table below that were re-shot were read in their re-shot form; the first shots were read too, and what was wrong with them is the defects list).

## Verdict

All 40 images are OK. Nothing was uploaded anywhere.

## Defects found and fixed in this run

1. **es-419 05-pages and 06-search: clipped tab.** The terms file was staged as `Condiciones de arrendamiento.pdf`, 32 characters, and the tab drew `Condiciones de arrendamiento.pd` against the close button. Staged as `Condiciones generales.pdf` and both slots re-shot (`Shoot-Set.ps1`).
2. **es-419 and es-ES 08-home: an English word.** The recents list prints the folder path and the staging folder was `D:\Documents\...`, so a Spanish capture said `Documents`. Staging folders are now `D:\Documentos\Arrendamientos` and `D:\Documentos\Alquileres`; both sets re-shot whole (`Shoot-Set.ps1`). French keeps `Documents`, which is the French word too.
3. **en 02/03/04/07/08: clipped tab (inherited).** The 2.2 English set's dirty tab read `Equipment Rental Agreement.pd`. The agreement is now staged as `Rental Agreement.pdf` and the English set re-shot whole. This is why the English images differ from 2.2 (below).

4. **04 sign and 07 redact, all five languages (#802), re-shot 2026-10-24.** Four things were wrong, each found by cropping the frame at 3x and then by looking in the code and the rig: the signature's `g` loop crossed the "Sign above the line" label (the rig placed the signature 14 px too low; `Shoot-Set.ps1` now clicks at Y 1446); the remove chip beside the marked name was an empty white square (the app's own bug since 2d9960cc, 2026-09-24: a lost U+E711 in `DocumentView.xaml.cs`, fixed; the same blank chip is in the 2.2 set); the corner dot and the chip covered the last letter of the name (a redaction mark's selection chrome now stands off its words by 4 dip, as an added text box's does); and a dotted underline ran under the name line (the viewer's hover cue left on because the rig parked the pointer with a warp; `Shot-Redact.ps1` now walks it off the text with real moves and waits for the scroll indicator that wakes to hide). In es-419 and es-ES the mark also started through the last "e" of "Nombre"; the Spanish sets now start the drag at x 872. 02 and 03 of the same runs were pixel-identical to the filed ones, so the rig reproduces. The five 04 and 07 pairs were each read at full size after the re-shoot.

## Observations not changed

- **Undo/Redo on the empty state (08-home, every language) are drawn darker than the other disabled commands.** The run's own UIA check reports all 30 commands disabled but Open (#617), and the 2.2 set looks the same, so it is a rendering detail of the disabled style on that button pair, not a state change. Left; worth a look by whoever owns the toolbar.
- **Gate `person` flags on es-419 and es-ES 03/04 are false positives.** The OCR here has `eng` and `fra` data only, so it drops Spanish accents; at 2x zoom the names read `Sofía Ramírez` and `Lucía Fernández` with every accent. Nothing else was flagged.

## Capture gate

After the 2026-10-24 re-shoot of 04 and 07: en, fr-CA and fr-FR 0 to look at; es-419 and es-ES 2 each (03 and 04, the OCR accent false positive; both names read with every accent at full size). Against 2.2: fr-CA and fr-FR 04 and 07 differ by 0.4 to 0.5 percent of pixels, inside the signature and the marked name only; en 04 and 07 likewise. The paragraph below is the 2026-10-23 run before that.

`python3 tools/capture-gate/gate.py --store microsoft docs/qa/captures-2.4/microsoft-screenshots`: en 8 images, 0 to look at; fr-CA 0; fr-FR 0; es-419 2 (03, 04: the OCR false positive above); es-ES 2 (03, 04: same). 'Not run' checks are the OCR ones that need data the machine lacks plus cross-language siblings that need a pair.
With `--against docs/qa/captures-2.2/microsoft-screenshots` (en, fr-CA, fr-FR): **fr-CA and fr-FR: 0 images to look at, every check passes against the signed-off 2.2 set**, so the French listing is unchanged by 2.3 and 2.4 in these eight poses. **en: 02, 03, 04, 07 differ by 0.11 percent of pixels and 08 by 0.32 percent, all from the shorter file name** (title bar, tab and recents row); 01, 05 and 06 match. Nothing changed because of the 2.3/2.4 UI: the toolbar, tabs and panes are identical.

## Spanish toolbar width

With the font and size pickers (#802, 2026-10-24): they add 200 effective px in every language. Spanish keeps its labels down to about 1340 effective px (es-419) and 1336 (es-ES) with the pickers showing, French down to about 1410, English 1280; the table is in `README.md` here and `tools/screenshots-windows/README.md`. No Spanish label needs shortening. Without the pickers, measured 2026-10-23 on the packaged build with a ticked box (Save with its dot): es-419 and es-ES keep icon and label on every command, nothing in More, from the 2500 px Store frame (1667 effective px) down to a 1720 px window (1147 effective); at 1700 px (1133 effective) they are icons only. The Store frame clears the Spanish breakpoint by about 520 effective px. See `tools/screenshots-windows/README.md`.

## The images

| language | slot | file | what is in the frame | verdict |
|---|---|---|---|---|
| en | 01 reading | 01-reading.png | Reading mode, windowed (title bar kept), page 4 of 12 of Rental Terms and Conditions.pdf; floating bar shows 'Page 4 of 12' and 'Exit'; no toolbar, no tooltip, no focus box. | OK |
| en | 02 text | 02-text.png | Agreement open, inline editor on the name line reading 'Name: Jane Whitfield' (typo fixed on camera); toolbar labelled, Undo/Redo greyed, tab 'Rental Agreement.pdf'; no squiggle. | OK after re-shoot. Re-shot with the new file name in the title bar and tab. |
| en | 03 checkbox | 03-checkbox.png | Name corrected, two of three boxes ticked, Save shows its dot, Undo live; tab '● Rental Agreement.pdf' now fits. | OK after re-shoot. Re-shot: first shot's tab read '● Equipment Rental Agreement.pd' (clipped; inherited from the 2.2 set); agreement staged as 'Rental Agreement.pdf'. |
| en | 04 sign | 04-sign.png | MegaWoman signature on the line, boxes ticked; signature not selected (no handles). The `g` loop now ends at the rule and clears the "Sign above the line" label. | OK after re-shoot (#802). Re-shot for the descender; the signature sits 14 px higher. |
| en | 05 pages | 05-pages.png | Pages pane open with 12 tiles, tiles 3 and 4 selected, '2 pages selected'; page 1 in the main pane. | OK |
| en | 06 search | 06-search.png | Find bar: 'equipment', '1 of 29', first hit selected, others highlighted; no pane open. | OK |
| en | 07 redact | 07-redact.png | Name text marked for redaction: selection box standing off 'Jane Whitfield' with one blue corner dot below the last letter and the remove chip, with its X, beside it; signature and date 'March 18, 2026' in place; no underline, no scroll indicator. | OK after re-shoot (#802). Re-shot: the last letter is no longer covered, the chip shows its X, the hover underline is gone. |
| en | 08 home | 08-home.png | Empty state, only Open enabled; recents list Rental Terms and Conditions / Rental Agreement / Scanned Agreement under 'D: > Documents > Rentals'; no real paths. Undo/Redo glyphs are drawn darker than the other disabled commands (UIA reports them disabled: 30 commands checked, 0 enabled; same in 2.2). | OK after re-shoot. Re-shot: recents now list 'Rental Agreement.pdf'. |
| fr-CA | 01 reading | 01-reading.png | Reading mode, page 4 sur 12 of Conditions de location.pdf; 'Sortir' on the bar; all French. | OK |
| fr-CA | 02 text | 02-text.png | Editor open on 'Nom : Hélène Bélanger' (accents on the corrected name); toolbar labelled in French; tab 'Contrat de location.pdf'; Quebec text ($, pi, lb). | OK |
| fr-CA | 03 checkbox | 03-checkbox.png | Nom : Hélène Bélanger corrected, two boxes ticked, 'Enregistrer ●', Annuler live. | OK |
| fr-CA | 04 sign | 04-sign.png | MegaWoman on the line, 'Signez au-dessus de la ligne' footer, clear of the loop. | OK after re-shoot (#802). |
| fr-CA | 05 pages | 05-pages.png | Pane with 12 tiles, 3 and 4 selected, '2 pages sélectionnées', French page text. | OK |
| fr-CA | 06 search | 06-search.png | Find bar 'location', '1 sur 52', first hit selected (heading 'Conditions de location'). | OK |
| fr-CA | 07 redact | 07-redact.png | Name 'Hélène Bélanger' marked (box stands off, dot below the final r, chip with its X), signature, date '18 mars 2026'. | OK after re-shoot (#802). |
| fr-CA | 08 home | 08-home.png | Empty state; recents 'Conditions de location / Contrat de location / Contrat numérisé' under 'D: > Documents > Locations' ('Documents' is also French). Undo/Rétablir drawn dark as in English. | OK |
| fr-FR | 01 reading | 01-reading.png | Reading mode, page 4 sur 12 of Conditions de location.pdf; 'Sortir'. | OK |
| fr-FR | 02 text | 02-text.png | Editor open on 'Nom : Céline Lefèvre' (accents correct); France text (week-end, enlèvement, 3 000 kg, 2 m, €); toolbar French. | OK |
| fr-FR | 03 checkbox | 03-checkbox.png | Nom : Céline Lefèvre corrected, two boxes ticked, Save dot, Annuler live. | OK |
| fr-FR | 04 sign | 04-sign.png | MegaWoman on the line, footer clear of the loop. | OK after re-shoot (#802). |
| fr-FR | 05 pages | 05-pages.png | Pane with 12 tiles, 3 and 4 selected, '2 pages sélectionnées'. | OK |
| fr-FR | 06 search | 06-search.png | Find bar 'location', '1 sur 52', first hit selected. | OK |
| fr-FR | 07 redact | 07-redact.png | 'Céline Lefèvre' marked (box stands off, last letter whole, chip with its X), signature, date '18 mars 2026'. | OK after re-shoot (#802). |
| fr-FR | 08 home | 08-home.png | Empty state; recents French names under 'D: > Documents > Locations'. Undo/Rétablir drawn dark as elsewhere. | OK |
| es-419 | 01 reading | 01-reading.png | Reading mode, 'Página 4 de 12' of Condiciones generales.pdf; 'Salir'; all Spanish, Latin American wording (arrendamiento). | OK |
| es-419 | 02 text | 02-text.png | Editor open on 'Nombre: Sofía Ramírez' (accents correct); toolbar Abrir/Guardar/Firmas/Agregar texto/Corrector/Censurar/Deshacer/Rehacer, all labelled, nothing in More; $ and 2,700 kg. | OK |
| es-419 | 03 checkbox | 03-checkbox.png | Name corrected, two boxes ticked, 'Guardar ●', Deshacer live; tab '● Contrato de arrendamiento.pdf' fits. (Gate person check flags missing accents: false positive, the machine has no Spanish OCR data; accents verified at 2x zoom.) | OK |
| es-419 | 04 sign | 04-sign.png | MegaWoman on the line, 'Firma sobre la línea' footer, clear of the loop. (Same gate person false positive.) | OK after re-shoot (#802). |
| es-419 | 05 pages | 05-pages.png | Pane with 12 tiles, 3 and 4 selected, '2 páginas seleccionadas'; tab 'Condiciones generales.pdf' now complete. | OK after re-shoot. Re-shot: first shot's tab read 'Condiciones de arrendamiento.pd' (clipped by the close button); terms now staged as 'Condiciones generales.pdf'. |
| es-419 | 06 search | 06-search.png | Find bar 'equipo', '1 de 29', first hit selected. | OK after re-shoot. Re-shot for the same clipped tab; now complete. |
| es-419 | 07 redact | 07-redact.png | 'Sofía Ramírez' marked (the box now starts at the name, not through the "e" of "Nombre"; dot clear of the final z, chip with its X), signature, date '18 de marzo de 2026'. | OK after re-shoot (#802). |
| es-419 | 08 home | 08-home.png | Empty state; recents 'Condiciones generales / Contrato de arrendamiento / Contrato escaneado' under 'D: > Documentos > Arrendamientos'. Undo/Rehacer drawn dark as elsewhere. | OK after re-shoot. Re-shot: first shot printed 'D: > Documents > Arrendamientos' (English word on a Spanish capture); folder is now D:\Documentos\Arrendamientos. |
| es-ES | 01 reading | 01-reading.png | Reading mode, 'Página 4 de 12' of Condiciones de alquiler.pdf; 'Salir'; Spain wording (alquiler). | OK |
| es-ES | 02 text | 02-text.png | Editor open on 'Nombre: Lucía Fernández' (accents correct); toolbar with 'Añadir texto' (es-ES), all labelled; 2.700 kg, 1,8 m, €. | OK |
| es-ES | 03 checkbox | 03-checkbox.png | Name corrected, two boxes ticked, 'Guardar ●', Deshacer live. (Gate person flag is the same OCR false positive; accents verified at 2x zoom.) | OK |
| es-ES | 04 sign | 04-sign.png | MegaWoman on the line, 'Firme sobre la línea' (es-ES imperative), clear of the loop. (Same gate false positive.) | OK after re-shoot (#802). |
| es-ES | 05 pages | 05-pages.png | Pane with 12 tiles, 3 and 4 selected, '2 páginas seleccionadas'; tab complete. | OK |
| es-ES | 06 search | 06-search.png | Find bar 'equipo', '1 de 29', first hit selected. | OK |
| es-ES | 07 redact | 07-redact.png | 'Lucía Fernández' marked (box starts at the name, dot clear of the final z, chip with its X), signature, date '18 de marzo de 2026'. | OK after re-shoot (#802). |
| es-ES | 08 home | 08-home.png | Empty state; recents Condiciones de alquiler / Contrato de alquiler / Contrato escaneado under 'D: > Documentos > Alquileres'. Undo/Rehacer drawn dark as elsewhere. | OK after re-shoot. Re-shot: first shot printed 'D: > Documents > Alquileres'; folder is now D:\Documentos\Alquileres. |
