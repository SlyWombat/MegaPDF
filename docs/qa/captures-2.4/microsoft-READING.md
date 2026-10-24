# Microsoft Store listing screenshots, 2.4: the reading

Shot 2026-10-23 on GPD-DAVE, packaged build `ElectricRV.MegaPDF` 2.3.0.0 (CI `MegaPDF-store-packages` from main bbe771d3, the Windows app is unchanged in d065597), test-signed copy installed through `Install-QaTestPackage`, display at 150 percent, 2500x1550 frame (2482x1541 captured), `Shoot-Set.ps1` for each language. Every one of the 40 images was opened and read by eye (the six images in the table below that were re-shot were read in their re-shot form; the first shots were read too, and what was wrong with them is the defects list).

## Verdict

All 40 images are OK. Nothing was uploaded anywhere.

## Defects found and fixed in this run

1. **es-419 05-pages and 06-search: clipped tab.** The terms file was staged as `Condiciones de arrendamiento.pdf`, 32 characters, and the tab drew `Condiciones de arrendamiento.pd` against the close button. Staged as `Condiciones generales.pdf` and both slots re-shot (`Shoot-Set.ps1`).
2. **es-419 and es-ES 08-home: an English word.** The recents list prints the folder path and the staging folder was `D:\Documents\...`, so a Spanish capture said `Documents`. Staging folders are now `D:\Documentos\Arrendamientos` and `D:\Documentos\Alquileres`; both sets re-shot whole (`Shoot-Set.ps1`). French keeps `Documents`, which is the French word too.
3. **en 02/03/04/07/08: clipped tab (inherited).** The 2.2 English set's dirty tab read `Equipment Rental Agreement.pd`. The agreement is now staged as `Rental Agreement.pdf` and the English set re-shot whole. This is why the English images differ from 2.2 (below).

## Observations not changed

- **Undo/Redo on the empty state (08-home, every language) are drawn darker than the other disabled commands.** The run's own UIA check reports all 30 commands disabled but Open (#617), and the 2.2 set looks the same, so it is a rendering detail of the disabled style on that button pair, not a state change. Left; worth a look by whoever owns the toolbar.
- **Signature descender touches the footer label** on 04 and 07 in every language, and **the redaction selection's handle covers the last letter of the name** on 07. Both are the pose (2.2 identical).
- **Gate `person` flags on es-419 and es-ES 03/04 are false positives.** The OCR here has `eng` and `fra` data only, so it drops Spanish accents; at 2x zoom the names read `Sofía Ramírez` and `Lucía Fernández` with every accent. Nothing else was flagged.

## Capture gate

`python3 tools/capture-gate/gate.py --store microsoft docs/qa/captures-2.4/microsoft-screenshots`: en 8 images, 0 to look at; fr-CA 0; fr-FR 0; es-419 2 (03, 04: the OCR false positive above); es-ES 2 (03, 04: same). 'Not run' checks are the OCR ones that need data the machine lacks plus cross-language siblings that need a pair.
With `--against docs/qa/captures-2.2/microsoft-screenshots` (en, fr-CA, fr-FR): **fr-CA and fr-FR: 0 images to look at, every check passes against the signed-off 2.2 set**, so the French listing is unchanged by 2.3 and 2.4 in these eight poses. **en: 02, 03, 04, 07 differ by 0.11 percent of pixels and 08 by 0.32 percent, all from the shorter file name** (title bar, tab and recents row); 01, 05 and 06 match. Nothing changed because of the 2.3/2.4 UI: the toolbar, tabs and panes are identical.

## Spanish toolbar width

Measured on the packaged build with a ticked box (Save with its dot): es-419 and es-ES keep icon and label on every command, nothing in More, from the 2500 px Store frame (1667 effective px) down to a 1720 px window (1147 effective); at 1700 px (1133 effective) they are icons only. The Store frame clears the Spanish breakpoint by about 520 effective px. See `tools/screenshots-windows/README.md`.

## The images

| language | slot | file | what is in the frame | verdict |
|---|---|---|---|---|
| en | 01 reading | 01-reading.png | Reading mode, windowed (title bar kept), page 4 of 12 of Rental Terms and Conditions.pdf; floating bar shows 'Page 4 of 12' and 'Exit'; no toolbar, no tooltip, no focus box. | OK |
| en | 02 text | 02-text.png | Agreement open, inline editor on the name line reading 'Name: Jane Whitfield' (typo fixed on camera); toolbar labelled, Undo/Redo greyed, tab 'Rental Agreement.pdf'; no squiggle. | OK after re-shoot. Re-shot with the new file name in the title bar and tab. |
| en | 03 checkbox | 03-checkbox.png | Name corrected, two of three boxes ticked, Save shows its dot, Undo live; tab '● Rental Agreement.pdf' now fits. | OK after re-shoot. Re-shot: first shot's tab read '● Equipment Rental Agreement.pd' (clipped; inherited from the 2.2 set); agreement staged as 'Rental Agreement.pdf'. |
| en | 04 sign | 04-sign.png | MegaWoman signature on the line, boxes ticked; signature not selected (no handles). Signature's descender touches the footer label (as in the 2.2 set). | OK after re-shoot. Re-shot for the same clipped tab. |
| en | 05 pages | 05-pages.png | Pages pane open with 12 tiles, tiles 3 and 4 selected, '2 pages selected'; page 1 in the main pane. | OK |
| en | 06 search | 06-search.png | Find bar: 'equipment', '1 of 29', first hit selected, others highlighted; no pane open. | OK |
| en | 07 redact | 07-redact.png | Name text marked for redaction (selection with one blue handle), signature and date 'March 18, 2026' in place; selection handle overlaps the last letter of 'Whitfield' (the marked target, as in 2.2). | OK after re-shoot. Re-shot for the same clipped tab. |
| en | 08 home | 08-home.png | Empty state, only Open enabled; recents list Rental Terms and Conditions / Rental Agreement / Scanned Agreement under 'D: > Documents > Rentals'; no real paths. Undo/Redo glyphs are drawn darker than the other disabled commands (UIA reports them disabled: 30 commands checked, 0 enabled; same in 2.2). | OK after re-shoot. Re-shot: recents now list 'Rental Agreement.pdf'. |
| fr-CA | 01 reading | 01-reading.png | Reading mode, page 4 sur 12 of Conditions de location.pdf; 'Sortir' on the bar; all French. | OK |
| fr-CA | 02 text | 02-text.png | Editor open on 'Nom : Hélène Bélanger' (accents on the corrected name); toolbar labelled in French; tab 'Contrat de location.pdf'; Quebec text ($, pi, lb). | OK |
| fr-CA | 03 checkbox | 03-checkbox.png | Nom : Hélène Bélanger corrected, two boxes ticked, 'Enregistrer ●', Annuler live. | OK |
| fr-CA | 04 sign | 04-sign.png | MegaWoman on the line, 'Signez au-dessus de la ligne' footer. Signature's descender touches the footer label (as in the 2.2 set). | OK |
| fr-CA | 05 pages | 05-pages.png | Pane with 12 tiles, 3 and 4 selected, '2 pages sélectionnées', French page text. | OK |
| fr-CA | 06 search | 06-search.png | Find bar 'location', '1 sur 52', first hit selected (heading 'Conditions de location'). | OK |
| fr-CA | 07 redact | 07-redact.png | Name 'Hélène Bélanger' marked (handle overlaps the final r), signature, date '18 mars 2026'. | OK |
| fr-CA | 08 home | 08-home.png | Empty state; recents 'Conditions de location / Contrat de location / Contrat numérisé' under 'D: > Documents > Locations' ('Documents' is also French). Undo/Rétablir drawn dark as in English. | OK |
| fr-FR | 01 reading | 01-reading.png | Reading mode, page 4 sur 12 of Conditions de location.pdf; 'Sortir'. | OK |
| fr-FR | 02 text | 02-text.png | Editor open on 'Nom : Céline Lefèvre' (accents correct); France text (week-end, enlèvement, 3 000 kg, 2 m, €); toolbar French. | OK |
| fr-FR | 03 checkbox | 03-checkbox.png | Nom : Céline Lefèvre corrected, two boxes ticked, Save dot, Annuler live. | OK |
| fr-FR | 04 sign | 04-sign.png | MegaWoman on the line. Signature's descender touches the footer label (as in the 2.2 set). | OK |
| fr-FR | 05 pages | 05-pages.png | Pane with 12 tiles, 3 and 4 selected, '2 pages sélectionnées'. | OK |
| fr-FR | 06 search | 06-search.png | Find bar 'location', '1 sur 52', first hit selected. | OK |
| fr-FR | 07 redact | 07-redact.png | 'Céline Lefèvre' marked (handle overlaps the final e), signature, date '18 mars 2026'. | OK |
| fr-FR | 08 home | 08-home.png | Empty state; recents French names under 'D: > Documents > Locations'. Undo/Rétablir drawn dark as elsewhere. | OK |
| es-419 | 01 reading | 01-reading.png | Reading mode, 'Página 4 de 12' of Condiciones generales.pdf; 'Salir'; all Spanish, Latin American wording (arrendamiento). | OK |
| es-419 | 02 text | 02-text.png | Editor open on 'Nombre: Sofía Ramírez' (accents correct); toolbar Abrir/Guardar/Firmas/Agregar texto/Corrector/Censurar/Deshacer/Rehacer, all labelled, nothing in More; $ and 2,700 kg. | OK |
| es-419 | 03 checkbox | 03-checkbox.png | Name corrected, two boxes ticked, 'Guardar ●', Deshacer live; tab '● Contrato de arrendamiento.pdf' fits. (Gate person check flags missing accents: false positive, the machine has no Spanish OCR data; accents verified at 2x zoom.) | OK |
| es-419 | 04 sign | 04-sign.png | MegaWoman on the line, 'Firma sobre la línea'. Signature's descender touches the footer label (as in the 2.2 set). (Same gate person false positive.) | OK |
| es-419 | 05 pages | 05-pages.png | Pane with 12 tiles, 3 and 4 selected, '2 páginas seleccionadas'; tab 'Condiciones generales.pdf' now complete. | OK after re-shoot. Re-shot: first shot's tab read 'Condiciones de arrendamiento.pd' (clipped by the close button); terms now staged as 'Condiciones generales.pdf'. |
| es-419 | 06 search | 06-search.png | Find bar 'equipo', '1 de 29', first hit selected. | OK after re-shoot. Re-shot for the same clipped tab; now complete. |
| es-419 | 07 redact | 07-redact.png | 'Sofía Ramírez' marked (handle overlaps the final z), signature, date '18 de marzo de 2026'. | OK |
| es-419 | 08 home | 08-home.png | Empty state; recents 'Condiciones generales / Contrato de arrendamiento / Contrato escaneado' under 'D: > Documentos > Arrendamientos'. Undo/Rehacer drawn dark as elsewhere. | OK after re-shoot. Re-shot: first shot printed 'D: > Documents > Arrendamientos' (English word on a Spanish capture); folder is now D:\Documentos\Arrendamientos. |
| es-ES | 01 reading | 01-reading.png | Reading mode, 'Página 4 de 12' of Condiciones de alquiler.pdf; 'Salir'; Spain wording (alquiler). | OK |
| es-ES | 02 text | 02-text.png | Editor open on 'Nombre: Lucía Fernández' (accents correct); toolbar with 'Añadir texto' (es-ES), all labelled; 2.700 kg, 1,8 m, €. | OK |
| es-ES | 03 checkbox | 03-checkbox.png | Name corrected, two boxes ticked, 'Guardar ●', Deshacer live. (Gate person flag is the same OCR false positive; accents verified at 2x zoom.) | OK |
| es-ES | 04 sign | 04-sign.png | MegaWoman on the line, 'Firme sobre la línea' (es-ES imperative). Signature's descender touches the footer label (as in the 2.2 set). (Same gate false positive.) | OK |
| es-ES | 05 pages | 05-pages.png | Pane with 12 tiles, 3 and 4 selected, '2 páginas seleccionadas'; tab complete. | OK |
| es-ES | 06 search | 06-search.png | Find bar 'equipo', '1 de 29', first hit selected. | OK |
| es-ES | 07 redact | 07-redact.png | 'Lucía Fernández' marked (handle overlaps the final z), signature, date '18 de marzo de 2026'. | OK |
| es-ES | 08 home | 08-home.png | Empty state; recents Condiciones de alquiler / Contrato de alquiler / Contrato escaneado under 'D: > Documentos > Alquileres'. Undo/Rehacer drawn dark as elsewhere. | OK after re-shoot. Re-shot: first shot printed 'D: > Documents > Alquileres'; folder is now D:\Documentos\Alquileres. |
