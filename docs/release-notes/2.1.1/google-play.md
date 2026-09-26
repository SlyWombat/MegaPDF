# Google Play — release notes, 2.1.1 (versionCode 12)

Play Console → the release → **Release notes**, per language. Limit **500
characters**, the tightest of the four, and the field where the French has to be
cut rather than translated long. The public Android version is 2.1.0
(versionCode 11), so these notes are the delta from one point release to the
next and can spend every character on what changed.

`tools/play_listing.py` pushes the text it finds in `android/RELEASING.md`'s
copy-by-language section, which `tools/gen_listing_copy.py` fills from this file
(the [README](README.md) says how).

*The French is new for 2.1.1 and was reviewed on 2026-09-26 (#343; see the
[README](README.md#french-review-2026-09-26)); it follows
`docs/localisation-glossary.md`. The France block is derived from the Canadian
one by `check_copy.py` in this folder — edit the Canadian block, then run it.*

## English (Canada) — `en-CA`

**Release notes** [500] (479)

```
Open with: MegaPDF is now offered when you open a PDF from Files, Drive, Gmail or any app that hands one over.

Share, in the More menu, sends the document through the share sheet. If it has unsaved changes it offers Save, Share without saving, or Cancel.

Export as Markdown, in the More menu, writes the document's text, headings, lists and filled-in values as a file you can paste anywhere. An export, not a save — the PDF is untouched, and a scanned page says it has no text.
```

## Français (Canada) — `fr-CA`

**Notes de version** [500] (479)

```
Ouvrir avec : MegaPDF est proposé quand vous ouvrez un PDF depuis Fichiers, Drive, Gmail ou une autre application.

Partager, dans le menu Plus, envoie le document par la feuille de partage. Avec des modifications non enregistrées : Enregistrer, Partager sans enregistrer ou Annuler.

Exporter en Markdown, dans le menu Plus, écrit le texte du document : titres, listes et valeurs remplies, à coller n'importe où. Une exportation, pas un enregistrement : le PDF n'est pas touché.
```

## Français (France) — `fr-FR`

**Notes de version** [500] (479)

```
Ouvrir avec : MegaPDF est proposé quand vous ouvrez un PDF depuis Fichiers, Drive, Gmail ou une autre application.

Partager, dans le menu Plus, envoie le document par la feuille de partage. Avec des modifications non enregistrées : Enregistrer, Partager sans enregistrer ou Annuler.

Exporter en Markdown, dans le menu Plus, écrit le texte du document : titres, listes et valeurs remplies, à coller n'importe où. Une exportation, pas un enregistrement : le PDF n'est pas touché.
```

> The France block is derived from the Canadian one: nothing in this copy is one
> of the words the derivation table changes, and the only punctuation France
> spaces differently from Quebec here is the colon, which both space the same
> way. The French spends 479 of the 500 characters — it drops the English's
> *and a scanned page says it has no text* and shortens the list of what a
> Markdown file holds — so anything added to it has to come out of it first.
> The third paragraph names the row that ships, *Export as Markdown* /
> *Exporter en Markdown* (#409): Save a copy is PDF-only.
