# MegaPDF 2.1 — release notes

The long form: everything 2.1 changes, per platform, for the GitHub release body
and the website. The store fields are the short versions of this, in the other
files in this folder.

Written from `git log 3c6f8f3..HEAD` — the merges of #332, #333 and #334, and
the branches for #337 (the phones) and #338 (the desktops). 2.0 went out on
every store on 2026-09-18 and 2026-09-19, so unlike 2.0's notes these cover the
same span for all four.

*The French was reviewed on 2026-09-20 (#343); the [README](README.md) lists
what changed, here and in the app's own new strings.*

---

## English

### A redaction mark you can take back

Redaction has been in MegaPDF since 2.0, and it takes what you mark *out of the
file* rather than covering it over. What it could not do was let you change your
mind.

A mark is a decision — this name, this address, this picture — and decisions
change. A mark can now be selected by tapping or clicking it, dragged to move
it, and reshaped: each side on its own, because the area is what you are
deciding to cover, not the shape you happened to draw. The ✕ or Delete takes it
off, Undo puts it back, Redo takes it away again, and one drag is one step in
the history however many areas it marked.

**Clear all marks**, in the phones' More menu and the desktops' More and Tools
menus, drops every mark on every page as one step, and Undo brings them all back
where they were.

**The bug behind this.** A mark used to outlive the document it was made on. The
engine forgets its marks when a document closes, and each app kept its own copy
of them to draw, with nothing making it let go — so opening a file whose
redaction had been abandoned left the marks on screen and still armed, and the
next save would have applied them. Nothing happens until you say so is the whole
promise of a redaction tool, and a mark that cannot be taken off broke it in the
worst way available: it executed your own earlier intent, later, without asking.

**A mark is still not a change.** Marking, moving and removing one now raise no
unsaved dot, write no recovery-journal entry, run no page check and spend no
re-render, because nothing in the file has changed. Save stays live and asks
before it applies what you marked.

### Redact, where you can find it

On both phones, Redact was an unlabelled icon on the bottom bar — no place for a
command that removes content for good, since nobody can guess what the icon
means and the bar has no room to say. It is a named row in the menu now, with
its own armed state: a check mark, and an on/off state a screen reader reads out
rather than leaving you to infer it from a glyph.

### A pinch zooms

Pinching the page did nothing on either phone: the magnify gesture was losing to
the scroll view's own pan. It zooms now, and while the page is zoomed it pans
horizontally rather than fighting the gesture.

### Signatures

The library holds twenty signatures and says so when it is full, instead of
failing quietly on the twenty-first. A signature whose image has gone is dropped
from the list rather than left behind as a broken thumbnail, and a deletion that
fails partway is no longer able to leave the index naming a file that is not
there.

### Under the hood

- **One page check at a time.** All four apps ask the same question before a
  change that would alter parts of a page you did not touch (#139), and each kept
  its own record of what was running. The core owns that now, so starting a check
  on one page stops an unfinished check on another by construction, the page a
  change is waiting on keeps its check, and a document closed while a check runs
  settles nothing in the document that comes after it.
- **A save to a file holds one copy of the document, not two.** Saving staged a
  verified copy beside the destination and then made a second temp file to write
  it from, so a save held two full copies on disk at once and read and wrote the
  document an extra time. The verified copy is now the one that moves into place,
  and the binary swap is refused rather than quietly turned into a copy when the
  two are not on the same file system.

### Mac

- The mark lifecycle above, on the desktop's own chrome: click to select, drag to
  move, corner grips to resize each side, ✕ or Delete to remove, Clear all marks
  in the More and Tools menus.
- A mark that is gone lets the selection go, and a mark that has moved
  re-anchors on the engine's rectangle — so the ✕ always acts on the mark on
  screen and the next drag never records a wrong starting point.
- The signature-limit, thumbnail and save-copy items are the core's and arrive
  with the rest.

### Windows

- The mark lifecycle above, with a keyboard path as well: a selected mark
  announces itself, the arrow keys move it, Delete removes it and Esc lets it go.
- Print, Shrink for email and whiteout are unchanged; the Save button stays live
  while anything is marked, because saving is where a redaction is applied.

### Android

- The mark lifecycle, Redact as a named row in the menu, pinch-to-zoom, and the
  signature library's limit and tidy-up.
- The screenshot pose for redaction now poses a mark *selected*, which is the
  affordance, rather than an armed tool that draws nothing to photograph.

### iPhone and iPad

- The mark lifecycle, Redact as a named row in the menu, pinch-to-zoom, and the
  signature library's limit and tidy-up.
- The iPad still shows the iPhone layout in 2.1; its own layout is later work.

---

## Français (Canada)

### Des marques de caviardage sur lesquelles vous pouvez revenir

Le caviardage existe dans MegaPDF depuis la 2.0, et il retire du fichier ce que
vous marquez plutôt que de le recouvrir. Ce qu'il ne permettait pas, c'était de
changer d'avis.

Une marque est une décision — ce nom, cette adresse, cette image — et les
décisions changent. Une marque peut maintenant être sélectionnée en la touchant
ou en cliquant dessus, déplacée en la glissant, et redimensionnée : chaque côté
indépendamment, parce que ce qu'on décide, c'est la zone à couvrir, et non la
forme qu'on a dessinée au départ. Le ✕ ou Supprimer la retire, Annuler la remet
en place, Rétablir la retire de nouveau, et un glissement ne compte que pour une
seule étape, peu importe le nombre de zones qu'il a marquées.

**Effacer toutes les marques**, dans le menu Plus des téléphones et dans les
menus Plus et Outils des ordinateurs, retire toutes les marques de toutes les
pages en une seule étape, et Annuler les remet toutes en place.

**Le bogue derrière tout cela.** Une marque survivait au document sur lequel
elle avait été faite. Le moteur oublie ses marques quand un document se ferme,
et chaque application gardait sa propre copie pour les dessiner, sans rien pour
l'obliger à s'en défaire : ouvrir un fichier dont le caviardage avait été
abandonné laissait les marques à l'écran et toujours armées, et l'enregistrement
suivant les aurait appliquées. Rien ne se produit tant que vous ne le demandez
pas, c'est la promesse même d'un outil de caviardage, et une marque qu'on ne
peut pas retirer la brisait de la pire façon : elle exécutait plus tard, sans
rien demander, ce que vous aviez décidé plus tôt.

**Une marque n'est toujours pas une modification.** Marquer, déplacer et retirer
n'affichent aucun point de modification non enregistrée, n'écrivent rien dans le
journal de récupération, ne déclenchent aucune vérification de page et ne
forcent aucun nouveau rendu, parce que rien dans le fichier n'a changé.
Enregistrer reste actif et demande confirmation avant d'appliquer ce qui est
marqué.

### Caviarder, là où on le trouve

Sur les deux téléphones, Caviarder était une icône sans étiquette dans la barre
du bas : ce n'est pas la place d'une commande qui retire du contenu pour de bon,
puisque personne ne peut deviner ce que l'icône veut dire et que la barre n'a
pas la place de l'expliquer. C'est maintenant une entrée nommée du menu, avec
son propre état armé : un crochet, et un état activé ou désactivé qu'un lecteur
d'écran annonce, au lieu de laisser deviner à partir d'un glyphe.

### Un pincement zoome

Pincer la page ne faisait rien sur aucun des deux téléphones : le geste
d'agrandissement cédait le pas au défilement de la vue. Il fonctionne
maintenant, et une fois la page agrandie, elle se déplace horizontalement au
lieu de résister au geste.

### Signatures

La bibliothèque garde vingt signatures et le dit quand elle est pleine, au lieu
d'échouer en silence à la vingt et unième. Une signature dont l'image a disparu
est retirée de la liste plutôt que laissée là sous forme de vignette brisée, et
une suppression qui échoue à mi-chemin ne peut plus laisser l'index nommer un
fichier qui n'existe plus.

### Et aussi

- **Une seule vérification de page à la fois.** Les quatre applications posent la
  même question avant une modification qui modifierait des parties d'une page que
  vous n'avez pas touchées (#139), et chacune gardait son propre registre de ce
  qui était en cours. C'est le cœur qui s'en charge maintenant : lancer une
  vérification sur une page arrête donc une vérification inachevée sur une autre,
  la page sur laquelle une modification attend garde sa vérification, et un
  document fermé pendant une vérification ne règle rien dans celui qui suit.
- **Un enregistrement dans un fichier ne garde qu'une copie du document, pas
  deux.** L'enregistrement déposait une copie vérifiée à côté de la destination,
  puis créait un second fichier temporaire pour écrire à partir d'elle : un
  enregistrement gardait donc deux copies complètes sur le disque et lisait et
  écrivait le document une fois de plus. C'est maintenant la copie vérifiée qui
  est mise en place, et l'échange binaire est refusé plutôt que converti en
  silence en une copie quand les deux fichiers ne sont pas sur le même système de
  fichiers.

### Mac

- Le cycle de vie des marques ci-dessus, avec l'interface propre au bureau : un
  clic pour sélectionner, un glissement pour déplacer, les poignées de coin pour
  redimensionner chaque côté, le ✕ ou Supprimer pour retirer, et Effacer toutes
  les marques dans les menus Plus et Outils.
- Une marque disparue libère la sélection, et une marque déplacée se
  raccroche au rectangle du moteur : le ✕ agit donc toujours sur la marque à
  l'écran, et le glissement suivant ne note jamais un mauvais point de départ.
- La limite de signatures, le ménage des vignettes et la copie unique à
  l'enregistrement viennent du cœur et arrivent avec le reste.

### Windows

- Le cycle de vie des marques ci-dessus, avec un chemin au clavier : une marque
  sélectionnée s'annonce, les flèches la déplacent, Suppr la retire et Échap la
  relâche.
- L'impression, la copie réduite et le correcteur sont inchangés : le bouton
  Enregistrer reste actif tant que quelque chose est marqué, parce que c'est à
  l'enregistrement que le caviardage s'applique.

### Android

- Le cycle de vie des marques, Caviarder comme ligne nommée du menu, le pincement
  qui zoome, et la limite et le ménage de la bibliothèque de signatures.
- La pose de capture pour le caviardage montre maintenant une marque
  *sélectionnée*, ce qui est l'affordance, plutôt qu'un outil armé qui ne dessine
  rien à photographier.

### iPhone et iPad

- Le cycle de vie des marques, Caviarder comme ligne nommée du menu, le pincement
  qui zoome, et la limite et le ménage de la bibliothèque de signatures.
- L'iPad affiche encore la disposition de l'iPhone en 2.1 : sa propre
  disposition viendra plus tard.

---

## Français (France)

> Derived from the Canadian French above by the same rules
> `tools/gen_strings.py fr-fr` applies. The two differ by one word: the check
> mark in the Redact section is *un crochet* in Canada and *une coche* in France
> (the glossary's derivation table). The punctuation France spaces differently
> from Quebec — `:`, `;`, `?`, `!` — is already spaced the way both do, with a
> non-breaking space.

### Des marques de caviardage sur lesquelles vous pouvez revenir

Le caviardage existe dans MegaPDF depuis la 2.0, et il retire du fichier ce que
vous marquez plutôt que de le recouvrir. Ce qu'il ne permettait pas, c'était de
changer d'avis.

Une marque est une décision — ce nom, cette adresse, cette image — et les
décisions changent. Une marque peut maintenant être sélectionnée en la touchant
ou en cliquant dessus, déplacée en la glissant, et redimensionnée : chaque côté
indépendamment, parce que ce qu'on décide, c'est la zone à couvrir, et non la
forme qu'on a dessinée au départ. Le ✕ ou Supprimer la retire, Annuler la remet
en place, Rétablir la retire de nouveau, et un glissement ne compte que pour une
seule étape, peu importe le nombre de zones qu'il a marquées.

**Effacer toutes les marques**, dans le menu Plus des téléphones et dans les
menus Plus et Outils des ordinateurs, retire toutes les marques de toutes les
pages en une seule étape, et Annuler les remet toutes en place.

**Le bogue derrière tout cela.** Une marque survivait au document sur lequel
elle avait été faite. Le moteur oublie ses marques quand un document se ferme,
et chaque application gardait sa propre copie pour les dessiner, sans rien pour
l'obliger à s'en défaire : ouvrir un fichier dont le caviardage avait été
abandonné laissait les marques à l'écran et toujours armées, et l'enregistrement
suivant les aurait appliquées. Rien ne se produit tant que vous ne le demandez
pas, c'est la promesse même d'un outil de caviardage, et une marque qu'on ne
peut pas retirer la brisait de la pire façon : elle exécutait plus tard, sans
rien demander, ce que vous aviez décidé plus tôt.

**Une marque n'est toujours pas une modification.** Marquer, déplacer et retirer
n'affichent aucun point de modification non enregistrée, n'écrivent rien dans le
journal de récupération, ne déclenchent aucune vérification de page et ne
forcent aucun nouveau rendu, parce que rien dans le fichier n'a changé.
Enregistrer reste actif et demande confirmation avant d'appliquer ce qui est
marqué.

### Caviarder, là où on le trouve

Sur les deux téléphones, Caviarder était une icône sans étiquette dans la barre
du bas : ce n'est pas la place d'une commande qui retire du contenu pour de bon,
puisque personne ne peut deviner ce que l'icône veut dire et que la barre n'a
pas la place de l'expliquer. C'est maintenant une entrée nommée du menu, avec
son propre état armé : une coche, et un état activé ou désactivé qu'un lecteur
d'écran annonce, au lieu de laisser deviner à partir d'un glyphe.

### Un pincement zoome

Pincer la page ne faisait rien sur aucun des deux téléphones : le geste
d'agrandissement cédait le pas au défilement de la vue. Il fonctionne
maintenant, et une fois la page agrandie, elle se déplace horizontalement au
lieu de résister au geste.

### Signatures

La bibliothèque garde vingt signatures et le dit quand elle est pleine, au lieu
d'échouer en silence à la vingt et unième. Une signature dont l'image a disparu
est retirée de la liste plutôt que laissée là sous forme de vignette brisée, et
une suppression qui échoue à mi-chemin ne peut plus laisser l'index nommer un
fichier qui n'existe plus.

### Et aussi

- **Une seule vérification de page à la fois.** Les quatre applications posent la
  même question avant une modification qui modifierait des parties d'une page que
  vous n'avez pas touchées (#139), et chacune gardait son propre registre de ce
  qui était en cours. C'est le cœur qui s'en charge maintenant : lancer une
  vérification sur une page arrête donc une vérification inachevée sur une autre,
  la page sur laquelle une modification attend garde sa vérification, et un
  document fermé pendant une vérification ne règle rien dans celui qui suit.
- **Un enregistrement dans un fichier ne garde qu'une copie du document, pas
  deux.** L'enregistrement déposait une copie vérifiée à côté de la destination,
  puis créait un second fichier temporaire pour écrire à partir d'elle : un
  enregistrement gardait donc deux copies complètes sur le disque et lisait et
  écrivait le document une fois de plus. C'est maintenant la copie vérifiée qui
  est mise en place, et l'échange binaire est refusé plutôt que converti en
  silence en une copie quand les deux fichiers ne sont pas sur le même système de
  fichiers.

### Mac

- Le cycle de vie des marques ci-dessus, avec l'interface propre au bureau : un
  clic pour sélectionner, un glissement pour déplacer, les poignées de coin pour
  redimensionner chaque côté, le ✕ ou Supprimer pour retirer, et Effacer toutes
  les marques dans les menus Plus et Outils.
- Une marque disparue libère la sélection, et une marque déplacée se
  raccroche au rectangle du moteur : le ✕ agit donc toujours sur la marque à
  l'écran, et le glissement suivant ne note jamais un mauvais point de départ.
- La limite de signatures, le ménage des vignettes et la copie unique à
  l'enregistrement viennent du cœur et arrivent avec le reste.

### Windows

- Le cycle de vie des marques ci-dessus, avec un chemin au clavier : une marque
  sélectionnée s'annonce, les flèches la déplacent, Suppr la retire et Échap la
  relâche.
- L'impression, la copie réduite et le correcteur sont inchangés : le bouton
  Enregistrer reste actif tant que quelque chose est marqué, parce que c'est à
  l'enregistrement que le caviardage s'applique.

### Android

- Le cycle de vie des marques, Caviarder comme ligne nommée du menu, le pincement
  qui zoome, et la limite et le ménage de la bibliothèque de signatures.
- La pose de capture pour le caviardage montre maintenant une marque
  *sélectionnée*, ce qui est l'affordance, plutôt qu'un outil armé qui ne dessine
  rien à photographier.

### iPhone et iPad

- Le cycle de vie des marques, Caviarder comme ligne nommée du menu, le pincement
  qui zoome, et la limite et le ménage de la bibliothèque de signatures.
- L'iPad affiche encore la disposition de l'iPhone en 2.1 : sa propre
  disposition viendra plus tard.
