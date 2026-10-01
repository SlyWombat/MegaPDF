# Mac App Store — "What's New in This Version", 2.2

App Store Connect → the 2.2 version page → **Localizations** → *What's New in
This Version*, per language. Limit 4000 characters. `tools/asc_publish.py` with
`ASC_PLATFORM=MAC_OS` reads this folder for the version 2.2 (see the
[README](README.md)), by the locale code at the end of each heading.

No other platform is named anywhere in this copy (App Review 2.3.10), and no
feature is named that the Mac app on the Store does not have — `megapdf-cli` is a
separate download, not part of the sandboxed app, so it is not here. App Store
Connect refuses the ✕ character (#340), and this copy contains none.

**What is deliberately not here.** The informed-permission choice (#558) is
Android only; on the Mac a withheld permission still refuses, and
`megapdf_security_override()` is in the core and unbound. See the
[README](README.md#what-each-channel-may-claim).

*The French below is new and has **not** been reviewed. 2.1.1's was reviewed on
2026-09-26 (#343); every 2.2 string in the app still carries an `FR-REVIEW`
comment of its own. The France block is derived from the Canadian one by
`check_copy.py` in this folder — edit the Canadian block, then run it.*

## English (Canada) — `en-CA`

**What's New** [4000] (2332)

```
Reading mode

MegaPDF can get out of the way. Reading mode — View > Reading mode, or ⇧⌘R — takes the toolbar and the sidebar off the screen and leaves the page. A small floating bar keeps the page number and the way back, Escape returns the window to normal, and full screen (⌃⌘F) is offered once you are in it. Page colours sit beside it in Options: Normal, Sepia for a long read, and Night for a dark room. Night inverts the page, pictures included — that is deliberate, and the setting says so, because a photograph read at night is a photograph in negative. Open documents in reading mode starts every document that way.

Page tools

The Thumbnails sidebar (⌥⌘2) is somewhere to work now, not only somewhere to look. Rotate a page that was scanned sideways, delete one, drag pages into a different order, insert a blank page, insert the pages of another PDF, or save the pages you selected as a file of their own. Each of those is one step in the undo history, so one Undo puts the document back.

Zoom, and a pinch that works

Zoom now holds the point you are pointing at instead of the corner of the page, whether it comes from the menu, the keyboard or Control and the wheel. And a trackpad pinch zooms the page, which it never did here before.

Whiteout you can move, text that takes a second line

Draw a whiteout and it stays selected: drag it to move it, take a corner to resize it, Delete to remove it. Added text takes more than one line — Return starts the next one, and the box grows to fit what you type.

Long work says what it is doing

Opening a large document, searching it, saving it, making a smaller copy, saving pages out: each of those says what it is working on and how far it has got, instead of looking like a hang. Searching, Shrink for email and saving pages out can be stopped partway, and stopping leaves no half-written file behind.

Before a document's own signature is lost

Some documents arrive carrying a digital signature — the cryptographic kind, which stops verifying the moment the file changes at all. Saving over one of those now says so first and offers Save a copy instead, so the signed original stays intact. That is about a signature already in the document; the signature you draw, type or photograph and place on the page is a picture of your name, and placing one has not changed.
```

## Français (Canada) — `fr-CA`

**Quoi de neuf** [4000] (2694)

```
Mode lecture

MegaPDF sait s'effacer. Le mode lecture — Présentation > Mode lecture, ou ⇧⌘R — retire la barre d'outils et le panneau latéral de l'écran et laisse la page. Une petite barre flottante garde le numéro de page et le chemin du retour, Échap ramène la fenêtre à la normale, et le plein écran (⌃⌘F) est offert une fois que vous y êtes. Les couleurs de la page sont à côté, dans les Options : Normales, Sépia pour une longue lecture et Nuit pour une pièce sombre. Le mode nuit inverse la page, images comprises : c'est voulu, et le réglage le dit, parce qu'une photo lue la nuit est une photo en négatif. Ouvrir les documents en mode lecture ouvre ainsi chaque document.

Outils de page

Le panneau Vignettes (Présentation > Vignettes, ⌥⌘2) est maintenant un endroit où travailler, et non seulement où regarder. Faites pivoter une page numérisée de travers, supprimez-en une, glissez les pages dans un autre ordre, insérez une page vierge, insérez les pages d'un autre PDF, ou enregistrez les pages sélectionnées dans un fichier à part. Chacun de ces gestes est une seule étape de l'historique, et une seule annulation remet le document comme il était.

Le zoom, et un pincement qui fonctionne

Le zoom garde maintenant le point que vous visez au lieu du coin de la page, qu'il vienne du menu, du clavier ou de Contrôle avec la molette. Et un pincement sur le pavé tactile zoome la page, ce qu'il ne faisait pas du tout ici avant.

Un correcteur qui se déplace, du texte sur une deuxième ligne

Tracez du correcteur et il reste sélectionné : glissez-le pour le déplacer, prenez un coin pour le redimensionner, Supprimer pour l'enlever. Le texte ajouté tient sur plus d'une ligne — Retour commence la suivante, et la zone grandit pour contenir ce que vous tapez.

Les longues opérations disent ce qu'elles font

Ouvrir un gros document, y chercher, l'enregistrer, en faire une copie réduite, en extraire des pages : chacune de ces opérations dit sur quoi elle travaille et où elle en est, au lieu d'avoir l'air figée. La recherche, la copie réduite et l'extraction de pages peuvent être arrêtées en cours de route, et un arrêt ne laisse aucun fichier à moitié écrit.

Avant que la signature du document ne soit perdue

Certains documents arrivent avec une signature numérique, celle du chiffrement, qui cesse d'être vérifiable dès que le fichier change. Enregistrer par-dessus un tel document le dit maintenant d'abord et propose Enregistrer une copie, pour que l'original signé reste intact. Il s'agit d'une signature déjà présente dans le document; la signature que vous dessinez, tapez ou photographiez pour la poser sur la page est une image de votre nom, et la poser n'a pas changé.
```

## Français (France) — `fr`

**Quoi de neuf** [4000] (2695)

```
Mode lecture

MegaPDF sait s'effacer. Le mode lecture — Présentation > Mode lecture, ou ⇧⌘R — retire la barre d'outils et le panneau latéral de l'écran et laisse la page. Une petite barre flottante garde le numéro de page et le chemin du retour, Échap ramène la fenêtre à la normale, et le plein écran (⌃⌘F) est offert une fois que vous y êtes. Les couleurs de la page sont à côté, dans les Options : Normales, Sépia pour une longue lecture et Nuit pour une pièce sombre. Le mode nuit inverse la page, images comprises : c'est voulu, et le réglage le dit, parce qu'une photo lue la nuit est une photo en négatif. Ouvrir les documents en mode lecture ouvre ainsi chaque document.

Outils de page

Le panneau Vignettes (Présentation > Vignettes, ⌥⌘2) est maintenant un endroit où travailler, et non seulement où regarder. Faites pivoter une page numérisée de travers, supprimez-en une, glissez les pages dans un autre ordre, insérez une page vierge, insérez les pages d'un autre PDF, ou enregistrez les pages sélectionnées dans un fichier à part. Chacun de ces gestes est une seule étape de l'historique, et une seule annulation remet le document comme il était.

Le zoom, et un pincement qui fonctionne

Le zoom garde maintenant le point que vous visez au lieu du coin de la page, qu'il vienne du menu, du clavier ou de Contrôle avec la molette. Et un pincement sur le pavé tactile zoome la page, ce qu'il ne faisait pas du tout ici avant.

Un correcteur qui se déplace, du texte sur une deuxième ligne

Tracez du correcteur et il reste sélectionné : glissez-le pour le déplacer, prenez un coin pour le redimensionner, Supprimer pour l'enlever. Le texte ajouté tient sur plus d'une ligne — Retour commence la suivante, et la zone grandit pour contenir ce que vous tapez.

Les longues opérations disent ce qu'elles font

Ouvrir un gros document, y chercher, l'enregistrer, en faire une copie réduite, en extraire des pages : chacune de ces opérations dit sur quoi elle travaille et où elle en est, au lieu d'avoir l'air figée. La recherche, la copie réduite et l'extraction de pages peuvent être arrêtées en cours de route, et un arrêt ne laisse aucun fichier à moitié écrit.

Avant que la signature du document ne soit perdue

Certains documents arrivent avec une signature numérique, celle du chiffrement, qui cesse d'être vérifiable dès que le fichier change. Enregistrer par-dessus un tel document le dit maintenant d'abord et propose Enregistrer une copie, pour que l'original signé reste intact. Il s'agit d'une signature déjà présente dans le document ; la signature que vous dessinez, tapez ou photographiez pour la poser sur la page est une image de votre nom, et la poser n'a pas changé.
```

> Apple's French locale for France is `fr`, not `fr-FR`. The France block is
> derived from the Canadian one by `check_copy.py`.
>
> The two senses of "signature" are handled as in
> [`app-store.md`](app-store.md), and the note at the end of that file applies
> here word for word — including the open question for a francophone about
> whether `signature numérique` holds the distinction on its own.
