# MegaPDF 2.1.1 — release notes and "What's New"

Copy for every store, in `en`, `fr-CA` and `fr-FR`, for the 2.1.1 release of the
work done since 2.1.0: the 110 commits of `git log linux-v2.1.0..029c849`, on
`main`. Nothing here has been pasted into a console; this is the text to paste,
or to let the tools push, when 2.1.1 is submitted (#395).

2.1.1 has two headlines on the desktops — documents open as **tabs** in one
window, and **Save As writes Markdown** — and three on the phones: the phone
now offers MegaPDF to **open a PDF** from other apps, **Share** sends one back,
and **Save a copy / Export as Markdown** writes the text. The command-line
extractor, `megapdf-cli`, ships in every Linux package and as Windows and macOS
zips on GitHub, and is named only in the long form: **no store package carries
it**, so no store block may.

> **The French is not yet reviewed.** It was written against
> `docs/localisation-glossary.md` — the new rows for this release are in it —
> and every new phrase is listed [below](#the-new-french-for-a-francophone-to-read)
> for the francophone read #343 still owes 2.1's copy as well. The France French
> is derived from the Canadian by [`check_copy.py`](check_copy.py) in this
> folder, with the rules `tools/gen_strings.py fr-fr` applies to the app's
> catalogues; nothing in the France blocks is hand-made.

| File | Store | Field | Limit |
|---|---|---|---|
| [`microsoft-store.md`](microsoft-store.md) | Microsoft Store | What's new in this version | 1500 |
| [`app-store.md`](app-store.md) | App Store (iPhone, iPad) | What's New in This Version | 4000 |
| [`mac-app-store.md`](mac-app-store.md) | Mac App Store | What's New in This Version | 4000 |
| [`google-play.md`](google-play.md) | Google Play | Release notes | 500 |
| [`release-notes-2.1.1.md`](release-notes-2.1.1.md) | — | the long form, for the GitHub releases and the website | — |
| [`check_copy.py`](check_copy.py) | — | derives the France blocks, recounts, checks the limits, syncs `../2.1/` | — |

Microsoft Store has four listing languages (`en-US`, `en-CA`, `fr-CA`, `fr-FR`)
and the English block goes into both English listings. The Apple and Play
listings have three (`en-CA` / `en`, `fr-CA`, `fr-FR`), except that Apple spells
the France locale `fr`.

## What each store's notes are the delta from

2.1.0 went out on every store on 2026-09-21, so all four are at the same
version and 2.1.1's notes describe the same span everywhere.

| Store | Public version now | Span these notes cover |
|---|---|---|
| Microsoft Store | 2.1.0.0 | 2.1.0 → 2.1.1 |
| App Store (iOS) | 2.1.0 | 2.1.0 → 2.1.1 |
| Mac App Store | 2.1.0 | 2.1.0 → 2.1.1 |
| Google Play | 2.1.0 (versionCode 11) | 2.1.0 → 2.1.1 (versionCode 12) |
| Linux | 2.1.0 (not a store) | 2.1.0 → 2.1.1 |

Read off #395 and the tags `linux-v2.1.0`, `ios-v2.1.0`, `android-v2.1.0` —
this machine reaches none of the four consoles. **The consoles are the
authority at submission time:** if a store turns out to be holding 2.1.0 in
review when 2.1.1 goes up, these notes stay correct as written; if one had
rejected 2.1.0, its notes would have to grow to cover 2.1.0 as well, from
[`../2.1/2.1.0/`](../2.1/2.1.0/).

## Where the tools read, and why there are two copies

`tools/msstore_submit.py` derives the release-notes folder from the first two
parts of its `VERSION` (`2.1.1.0` → `2.1`), and `tools/asc_publish.py`'s
`ASC_NOTES_VERSION` defaults to `2.1`, so both read
`docs/release-notes/2.1/<store>.md`. `tools/play_listing.py` reads
`android/RELEASING.md`'s copy-by-language block, which
`tools/gen_listing_copy.py` fills from **this** folder (`NOTES_VERSION`).

So this folder is the record, and `check_copy.py` writes the four store files
into `../2.1/` as generated copies (each carries a comment saying so). The
2.1.0 blocks that used to be there are kept in `../2.1/2.1.0/`. Edit here,
run the script, then `python3 tools/gen_listing_copy.py`; `--check` on the
script fails on any drift. Verified on 2026-09-26 by running each tool's
parsing function with no network: all three pick up 2.1.1's text at the
counts below.

## Where the content comes from

| Issue | What the user sees |
|---|---|
| #348 | documents open as tabs in one window on Windows, the Mac and Linux; Open picks several; drag-and-drop (new on the Mac and Linux); an external open — File Explorer, the Finder, a file manager, another app — lands in the running window, on a new tab or the tab that already shows the file; per-tab undo, find, zoom, dot and recovery; close and quit ask per document; crash recovery restores every document into its own tab |
| #386 | Save As (Windows, Mac, Linux) and Save a copy (Android) offer a Markdown document; iOS has Export as Markdown beside Save a copy. One-way: the PDF is untouched and the unsaved dot stays |
| #142 phases 1–3: #353 #354 #355 #356 #357 | `megapdf-cli extract`, text and `--format md`; ships in the Linux packages and as Windows/macOS zips. **Not in any store package** — long form and Linux page only |
| #360 #363 #375 #384 | the extraction's accuracy: words no longer split, line-end hyphens joined only when they are hyphenation, a table's bold labels no longer headings, a lower confidence on pages whose reading order jumps. Users see these as "the Markdown is good"; the long form has them, the store blocks do not |
| #376 #377 | Android and iOS register as PDF viewers: Open with / Files / Mail / the share sheet, with the unsaved-changes question first |
| #378 | Share in the More menu on both phones; with unsaved changes, Save / Share without saving / Cancel |
| #2 | Windows keyboard: *Box, ticked* announced; 40 × 40 hit targets on a selected signature's handle and ✕; a nudge survives a quick click. *Smaller things* in the Microsoft block |
| #145 | a multi-window quit on the Mac no longer leaves a window believing it had been asked. Long form only: the window it fixes is itself new in this release |
| #352 | the snap builds again. Long form only |

**Not every platform gets every line.** The desktops get tabs and Save As
Markdown; the phones get Open with, Share and the Markdown export. The
Microsoft block adds #2. Nothing else is in any store block.

**Left out, on purpose.**

- **Pinch to zoom (#336) shipped in 2.1.0** (`a62f4f2` is an ancestor of
  `linux-v2.1.0`, and 2.1.0's App Store block already says *A pinch zooms*), so
  2.1.1's notes do not repeat it, whatever the #395 checklist's summary says.
- **`megapdf-cli`** is not in the Microsoft Store, App Store or Mac App Store
  copy: a Store app cannot put a binary on `PATH`, and the zips are a separate
  GitHub download. It is in the long form, the Linux page and the README.
- **#172, the iPad's own layout**, is still not in the build; the long form
  says so under iPhone and iPad.
- **Windows tab-switching keys.** The Microsoft block names Ctrl+W only. The
  app binds Ctrl+W and Ctrl+Shift+N itself; whatever else the TabView control
  answers (Ctrl+Tab, Ctrl+1…) was not verified on the machine and is not
  claimed.

## The new French, for a francophone to read

**In the app.** Since 2.1.0 the four apps add these strings (all hand-written
in fr-CA, fr-FR derived by `tools/gen_strings.py fr-fr`; the file paths are
the same as in [2.1's README](../2.1/README.md#the-new-french-for-a-francophone-to-read)):

| English | French | Where | The judgement in it |
|---|---|---|---|
| Close tab / Close window / New window | Fermer l'onglet / Fermer la fenêtre / Nouvelle fenêtre | Windows, Mac and Linux | *onglet* is the word every French desktop uses for a tab; the Mac writes the English in title case (Close Tab) and the French does not change |
| Show Next Tab / Show Previous Tab | Afficher l'onglet suivant / Afficher l'onglet précédent | Mac (Window menu) | the standard AppKit items, in AppKit's own French |
| {0}, tab / {0}, unsaved changes, tab | {0}, onglet / {0}, modifications non enregistrées, onglet | Windows, Mac and Linux | what a screen reader says for a tab; the dot is spelled out, as elsewhere |
| Markdown document | Document Markdown | Windows, Mac and Linux | the file-type name beside *Document PDF*; *Markdown* is not translated |
| Exporting… / Exported {0} / Exported {0}. / Exported / Couldn't export / Could not export. / Couldn't export the text. / Couldn't prepare the export. | Exportation… / {0} exporté / {0} exporté. / Exporté / Impossible d'exporter / Impossible d'exporter. / Impossible d'exporter le texte. / Impossible de préparer l'exportation. | all four | *Exporter*, never *Enregistrer*: the export is not a save, and the French keeps that distinction everywhere the English does |
| Export as Markdown | Exporter en Markdown | iOS | the menu item beside *Enregistrer une copie* |
| Exported as Markdown / Couldn't export as Markdown. | Exporté en Markdown / Impossible d'exporter en Markdown. | Android | the same family, with the format named because Android's Save a copy is also the PDF copy |
| Share | Partager | both phones | the OS's own word for its share sheet row |
| Share without saving | Partager sans enregistrer | both phones | deliberately not *Abandonner*: nothing is discarded (#378) |
| This document has unsaved changes. They won't be in the shared copy unless you save first. | Ce document contient des modifications non enregistrées. Elles ne feront pas partie de la copie partagée si vous ne l'enregistrez pas d'abord. | both phones | says the one consequence, in the house register |
| Couldn't share. Try again. | Impossible de partager. Réessayez. | Android | the *Impossible de…* family |
| Box, ticked | Case cochée | Windows | beside 2.0's *Case à cocher* (*Box to tick*): the state after the tick |

**In this copy.** Every phrase below is new for 2.1.1; the terms follow the
glossary rows added for it (*onglet*, *Ouvrir avec*, *feuille de partage*,
*Exporter en Markdown*, *Document Markdown*, *Partager sans enregistrer*).

| English | Français (Canada) | Where | Note |
|---|---|---|---|
| Tabs | Des onglets | Microsoft, Mac | the headline, with the article as 2.1's *Des marques…* |
| Open a second PDF and it opens in a tab beside the first, in the same window. | Ouvrez un deuxième PDF : il s'ouvre dans un onglet à côté du premier, dans la même fenêtre. | Microsoft, Mac | |
| Open picks several files at once, a file dropped on the window opens too, and a double-click in File Explorer lands in the window you already have — as a new tab, or on the tab that already shows that file. | Ouvrir choisit plusieurs fichiers à la fois, un fichier déposé sur la fenêtre s'ouvre aussi, et un double-clic dans l'Explorateur de fichiers arrive dans la fenêtre que vous avez déjà, dans un nouvel onglet ou sur l'onglet qui montre déjà ce fichier. | Microsoft | *l'Explorateur de fichiers* is Windows' own French name |
| Each tab keeps its own undo history, find, zoom and unsaved-changes dot; Ctrl+W closes one, and closing the window asks about each document that needs saving. | Chaque onglet garde son propre historique d'annulation, sa recherche, son zoom et son point de modification non enregistrée; Ctrl+W en ferme un, et fermer la fenêtre demande quoi faire pour chaque document à enregistrer. | Microsoft | *point de modification non enregistrée* is 2.1's phrase for the dot |
| After a crash, every document that was open comes back in its own tab. | Après une fermeture inattendue, chaque document qui était ouvert revient dans son propre onglet. | Microsoft, Mac | *fermeture inattendue* is the glossary's phrase for a crash |
| from Open, which now picks several files at once, from a file dropped on the window, or from the Finder or any other app that hands MegaPDF a document | que ce soit avec Ouvrir, qui choisit maintenant plusieurs fichiers à la fois, en déposant un fichier sur la fenêtre, ou depuis le Finder ou toute autre application qui remet un document à MegaPDF | Mac | |
| A document opened from outside lands in the window you already have — as a new tab, or on the tab that already shows it — instead of replacing what was there. | Un document ouvert de l'extérieur arrive dans la fenêtre que vous avez déjà, dans un nouvel onglet ou sur l'onglet qui le montre déjà, au lieu de remplacer ce qui s'y trouvait. | Mac | |
| ⌘W closes a tab, ⇧⌘W the window, ⌃Tab moves between tabs and ⌘N opens another window. | ⌘W ferme un onglet, ⇧⌘W la fenêtre, ⌃Tab passe d'un onglet à l'autre et ⌘N ouvre une autre fenêtre. | Mac | the key glyphs are the Mac's own, in both languages |
| Closing or quitting asks about each document that needs saving | Fermer ou quitter demande quoi faire pour chaque document à enregistrer | Mac | |
| Save As Markdown | Enregistrer sous en Markdown | Microsoft, Mac | the headline names the command (*Enregistrer sous*) and the format |
| Save As now offers Markdown document beside PDF document. | Enregistrer sous propose maintenant Document Markdown à côté de Document PDF. | Microsoft | the two file-type names as the panel shows them |
| The Save As panel now offers Markdown document beside PDF document. | La zone de dialogue Enregistrer sous propose maintenant Document Markdown à côté de Document PDF. | Mac | *zone de dialogue* is what 2.0's Mac copy calls the panel |
| It writes the document's text — headings, paragraphs, lists and the values you filled in — as a Markdown file you can paste anywhere. | Il écrit le texte du document (titres, paragraphes, listes et les valeurs que vous avez remplies) dans un fichier Markdown à coller n'importe où. | Microsoft, Mac | parentheses where the English has dashes, as 2.0's French does |
| It is an export, not a save: the PDF is untouched, and it still asks to be saved if you had changed it. | C'est une exportation, pas un enregistrement : le PDF n'est pas touché, et il demande toujours à être enregistré si vous l'aviez modifié. | Microsoft, Mac, App Store | *exportation* (OQLF), matching the app's *Exportation…* |
| A scanned page has no text to give, and the file says so in its place. | Une page numérisée n'a pas de texte à donner, et le fichier le dit à sa place. | Mac, App Store | *numérisée*, the glossary's word for a scan |
| Smaller things | Et aussi | Microsoft | 2.1's heading, kept |
| Ticking a drawn box now tells a screen reader that it is ticked. | Cocher une case dessinée indique maintenant au lecteur d'écran qu'elle est cochée. | Microsoft | |
| The resize handle and the ✕ on a selected signature are easier to hit. | La poignée de redimensionnement et le ✕ d'une signature sélectionnée sont plus faciles à atteindre. | Microsoft | *poignée*, as 2.1's *poignées de coin* |
| An arrow-key nudge is kept even if you click straight afterwards. | Un déplacement aux flèches est conservé même si vous cliquez tout de suite après. | Microsoft | |
| Open with MegaPDF | Ouvrir avec MegaPDF | App Store | the OS's phrase; Play says *Ouvrir avec :* as a label |
| MegaPDF is now a PDF viewer as far as your iPhone and iPad are concerned. | Pour votre iPhone et votre iPad, MegaPDF est maintenant un lecteur de PDF. | App Store | |
| A PDF in Files, an attachment in Mail, or a document in any app's share sheet can be opened in MegaPDF from right there, instead of only through Open PDF at home. | Un PDF dans Fichiers, une pièce jointe dans Mail ou un document dans la feuille de partage de n'importe quelle application peut s'ouvrir dans MegaPDF directement de là, et non plus seulement par Ouvrir un PDF à l'accueil. | App Store | *Fichiers*, *Mail*, *feuille de partage*: Apple's French; *Ouvrir un PDF* is the home button's string |
| If a document with unsaved changes is already open, it asks before switching. | Si un document avec des modifications non enregistrées est déjà ouvert, il demande avant de changer. | App Store | |
| Share, in the More menu, hands the document to the share sheet: Mail, Messages, AirDrop, Save to Files, whatever you have. | Partager, dans le menu Plus, remet le document à la feuille de partage : Mail, Messages, AirDrop, Enregistrer dans Fichiers, ce que vous avez. | App Store | *Enregistrer dans Fichiers* is the share sheet's own French row |
| If the document has unsaved changes it says so first and offers Save, Share without saving — the last saved copy goes out and your edits stay open — or Cancel. | Si le document a des modifications non enregistrées, il le dit d'abord et propose Enregistrer, Partager sans enregistrer (la dernière copie enregistrée part, et vos modifications restent ouvertes) ou Annuler. | App Store | the three buttons as the app names them |
| Export as Markdown, beside Save a copy in the More menu, writes the document's text … as a Markdown file, saved wherever you choose in Files. | Exporter en Markdown, à côté d'Enregistrer une copie dans le menu Plus, écrit le texte du document … dans un fichier Markdown, enregistré où vous voulez dans Fichiers. | App Store | |
| Open with: MegaPDF is now offered when you open a PDF from Files, Drive, Gmail or any app that hands one over. | Ouvrir avec : MegaPDF est proposé quand vous ouvrez un PDF depuis Fichiers, Drive, Gmail ou une autre application. | Play | cut for the 500-character field: *maintenant* and the relative clause go |
| Share, in the More menu, sends the document through the share sheet. If it has unsaved changes it offers Save, Share without saving, or Cancel. | Partager, dans le menu Plus, envoie le document par la feuille de partage. Avec des modifications non enregistrées : Enregistrer, Partager sans enregistrer ou Annuler. | Play | the second sentence is a label and a list, for room |
| Save a copy can now write Markdown as well as PDF: the document's text, headings, lists and filled-in values, as a file you can paste anywhere. An export, not a save — the PDF is untouched, and a scanned page says it has no text. | Enregistrer une copie écrit maintenant du Markdown aussi bien qu'un PDF : texte, titres, listes et valeurs remplies, à coller n'importe où. Une exportation, pas un enregistrement : le PDF n'est pas touché. | Play | the scanned-page clause is dropped for room; 490 of 500 |

The long form's French (`release-notes-2.1.1.md`, *Français (Canada)*) is the
same vocabulary at length; its own new phrases are the section titles — *Des
onglets*, *Enregistrer sous en Markdown*, *Sur les téléphones : Ouvrir avec, et
Partager*, *Sous le capot : le texte dont le Markdown est fait* — and the
prose under them. The listing descriptions (`tools/gen_listing_copy.py`) add
one or two sentences per store in the same terms, listed in the pull request
that carried them.

## Counts

Counted the way the field counts, by `check_copy.py`, `len()` of the block:

| Block | en | fr-CA | fr-FR | Limit |
|---|---:|---:|---:|---:|
| Microsoft Store | 1054 | 1342 | 1343 | 1500 |
| App Store | 1021 | 1255 | 1255 | 4000 |
| Mac App Store | 1087 | 1370 | 1370 | 4000 |
| Google Play | 486 | 490 | 490 | 500 |

The Microsoft France block is one character longer than the Canadian: the
non-breaking space France puts before `;`. The Play French is the tight one at
490; anything added there has to come out of it first. In the listings the Mac
App Store's French description is at 3978 of 4000 after its two new sentences.

## House rules these follow

- **No other platforms named** in App Store or Mac App Store copy (Apple
  2.3.10). The Microsoft Store and Play notes stay platform-neutral too.
- **Apple's What's New contains no ✕** — App Store Connect refuses the
  character (#340). The 2.1.1 Apple blocks name no glyph at all; the Microsoft
  block keeps the ✕ of the signature chip, as 2.1's did.
- No feature named that the app on *that* store does not have, and nothing
  named that the build does not have (the CLI, #172, tab tear-out).
- Sentence case, the glossary's vocabulary, and no version numbers in the body
  — the store shows the version itself.
- Each block's character count is beside it, and `check_copy.py --check` fails
  if a count, a France block or a `../2.1/` copy has drifted.
