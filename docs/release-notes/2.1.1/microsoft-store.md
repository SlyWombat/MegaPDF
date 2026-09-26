# Microsoft Store — "What's new in this version", 2.1.1.0

Partner Center → the submission → **Store listings** → *What's new in this
version*, per language. Limit 1500 characters; the count is beside each block.
Paste the English into both `en-US` and `en-CA`. `tools/msstore_submit.py` reads
this file itself, by the locale codes in the headings.

The Windows app on the Store is the MSIX, and `megapdf-cli` is not in it — a
Store app cannot put a binary on `PATH` — so nothing here names the command
line. Tabs and the Markdown export are the two things this build changes on
Windows; the keyboard items under *Smaller things* are #2's.

*The French is new for 2.1.1 and awaits its francophone read (#343); it follows
`docs/localisation-glossary.md`. The France block is derived from the Canadian
one by `check_copy.py` in this folder — edit the Canadian block, then run it.*

## English — `en-US` and `en-CA`

**What's new** [1500] (1054)

```
Tabs

Open a second PDF and it opens in a tab beside the first, in the same window. Open picks several files at once, a file dropped on the window opens too, and a double-click in File Explorer lands in the window you already have — as a new tab, or on the tab that already shows that file. Each tab keeps its own undo history, find, zoom and unsaved-changes dot; Ctrl+W closes one, and closing the window asks about each document that needs saving. After a crash, every document that was open comes back in its own tab.

Save As Markdown

Save As now offers Markdown document beside PDF document. It writes the document's text — headings, paragraphs, lists and the values you filled in — as a Markdown file you can paste anywhere. It is an export, not a save: the PDF is untouched, and it still asks to be saved if you had changed it.

Smaller things

Ticking a drawn box now tells a screen reader that it is ticked. The resize handle and the ✕ on a selected signature are easier to hit. An arrow-key nudge is kept even if you click straight afterwards.
```

## Français (Canada) — `fr-CA`

**Quoi de neuf** [1500] (1341)

```
Des onglets

Ouvrez un deuxième PDF : il s'ouvre dans un onglet à côté du premier, dans la même fenêtre. Ouvrir choisit plusieurs fichiers à la fois, un fichier déposé sur la fenêtre s'ouvre aussi, et un PDF double-cliqué dans l'Explorateur de fichiers arrive dans la fenêtre que vous avez déjà, dans un nouvel onglet ou sur l'onglet qui le montre déjà. Chaque onglet garde son propre historique d'annulation, sa recherche, son zoom et son point de modification non enregistrée; Ctrl+W en ferme un, et fermer la fenêtre demande quoi faire pour chaque document à enregistrer. Après une fermeture inattendue, chaque document qui était ouvert revient dans son propre onglet.

Enregistrer sous, en Markdown

Enregistrer sous propose maintenant Document Markdown à côté de Document PDF. Il écrit le texte du document (titres, paragraphes, listes et les valeurs que vous avez remplies) dans un fichier Markdown à coller n'importe où. C'est une exportation, pas un enregistrement : le PDF n'est pas touché, et il demande toujours à être enregistré si vous l'aviez modifié.

Et aussi

Cocher une case dessinée indique maintenant au lecteur d'écran qu'elle est cochée. La poignée de redimensionnement et le ✕ d'une signature sélectionnée sont plus faciles à atteindre. Un déplacement aux flèches est conservé même si vous cliquez tout de suite après.
```

## Français (France) — `fr-FR`

**Quoi de neuf** [1500] (1342)

```
Des onglets

Ouvrez un deuxième PDF : il s'ouvre dans un onglet à côté du premier, dans la même fenêtre. Ouvrir choisit plusieurs fichiers à la fois, un fichier déposé sur la fenêtre s'ouvre aussi, et un PDF double-cliqué dans l'Explorateur de fichiers arrive dans la fenêtre que vous avez déjà, dans un nouvel onglet ou sur l'onglet qui le montre déjà. Chaque onglet garde son propre historique d'annulation, sa recherche, son zoom et son point de modification non enregistrée ; Ctrl+W en ferme un, et fermer la fenêtre demande quoi faire pour chaque document à enregistrer. Après une fermeture inattendue, chaque document qui était ouvert revient dans son propre onglet.

Enregistrer sous, en Markdown

Enregistrer sous propose maintenant Document Markdown à côté de Document PDF. Il écrit le texte du document (titres, paragraphes, listes et les valeurs que vous avez remplies) dans un fichier Markdown à coller n'importe où. C'est une exportation, pas un enregistrement : le PDF n'est pas touché, et il demande toujours à être enregistré si vous l'aviez modifié.

Et aussi

Cocher une case dessinée indique maintenant au lecteur d'écran qu'elle est cochée. La poignée de redimensionnement et le ✕ d'une signature sélectionnée sont plus faciles à atteindre. Un déplacement aux flèches est conservé même si vous cliquez tout de suite après.
```

> The France block is derived from the Canadian one: nothing in this copy is one
> of the words the derivation table changes (*courriel*, *infonuagique*,
> *crochet*), so the two differ only by the non-breaking space France puts before
> `;` (« non enregistrée ; Ctrl+W »).
