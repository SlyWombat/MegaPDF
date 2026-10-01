# MegaPDF 2.2 — release notes

The long form: everything 2.2 changes, per platform, for the GitHub release body and
the website. The store fields in this folder's other files are the short versions of
this; `check_copy.py` derives this file's France section from the Canadian one, the
same way it derives the four store files'.

Written from the pull requests merged against milestone 2.2 (opened 2026-09-27,
91 issues): reading mode (#505, #511, behind #541 Windows / #533 Mac and Linux / #539
Android / #540 iPhone and iPad); the page tools (#174, behind #560 / #556 / #554 /
#570, over the core contract #430); zoom anchored on the point you're looking at
(#528, #527, #530, behind #546 / #534 / #529 / #553); progress and the ability to
Stop a long job, now on every platform (#145, behind #568 / #563 / #580 / #591);
whiteout's move/resize chrome and text that takes more than one line, also now on
every platform (#3, #4, behind #564 / #535 / #565 / #591); a withheld page
permission that explains itself instead of only refusing (#558, #581); a warning
before saving destroys a document's own digital signature, with an option to strip
one a save is about to break, so far on the Mac and Linux (#476, #481, #576, #610,
behind #486 / #491 / #492 / #497); the iPad's own toolbar (#172, #435); the
vocabulary fix that keeps the two meanings of "signature" apart (#602, #606); and a
fix to the Windows empty state, which had offered document commands with no
document open (#617, #629).

**Tabs, Save As Markdown and `megapdf-cli` are not in this release.** They shipped in
2.1.1 (2026-09-21) and are unchanged here; see
[`docs/release-notes/2.1.1/release-notes-2.1.1.md`](../2.1.1/release-notes-2.1.1.md).

*The French below is new and has **not** been reviewed. 2.1.1's French was reviewed
on 2026-09-26 (#343); every 2.2 string in the app still carries its own `FR-REVIEW`
comment. The France section is derived from the Canadian one by `check_copy.py` in
this folder — edit the Canadian section, then run it.*

---

## English

### Reading mode

MegaPDF can get out of the way. Reading mode takes the toolbars — and, on the
desktops, the side pane — off the screen and leaves the page. A small bar carries
the page number and the way back: on the phones a tap brings it and a tap takes it
away again; on the Mac it floats and Escape returns the window to normal, with full
screen offered once you're in it; on Windows and Linux it opens from the View
command or Ctrl+H. The page number opens a box to jump anywhere in the document.

Page colours sit beside reading mode, in Settings on Windows and the phones, Options
on the Mac: Normal, Sepia for a long read, and Night for a dark room. Night inverts
the page, pictures included — that is a decision, not a defect, and the setting says
so, because a photograph read at night is a photograph in negative. If reading is
mostly what you do with a PDF, **Open documents in reading mode** starts every one
of them that way.

Reading mode is on every platform (#541, #533, #539, #540).

### Page tools

Tap or click Pages and the document's pages are in front of you, somewhere to work
rather than only to look: the Pages pane on Windows, the Thumbnails sidebar on the
Mac (⌥⌘2), a sheet on the iPhone and a sidebar beside the page on the iPad, a pane
on Android and Linux. Rotate a page that was scanned sideways, delete one, drag
pages into a different order, insert a blank page, insert the pages of another PDF,
or save the pages you picked as a file of their own. Each of those is one step in
the undo history, so one Undo puts the document back the way it was.

Page tools are on every platform (#560, #556, #554, #570), built on the same core
contract (#430) so the same operation behaves the same way everywhere.

### A toolbar made for the iPad

The iPad had no toolbar of its own before 2.2 — the titles its code asked for never
drew, so it quietly ran the iPhone's layout stretched wide. Now the everyday tools
sit in a wide row under the navigation bar, each with its title beside its icon
instead of an icon you have to guess at, and each tool's picker opens as a popover
next to the tool it belongs to. The keyboard commands are there too (#172, #435).

### Zoom where you're looking

Zoom now holds the point you're pointing at instead of the corner of the page, on
every platform and every way of asking for it — the menu, the keyboard, a pinch, or
the wheel.

- **Mac:** a trackpad pinch zooms the page, which it never did before, on top of
  the anchor fix.
- **Linux:** Control and the wheel now zoom at all, which they didn't before, and
  hold the point you're pointing at. There is no pinch gesture on Linux — Avalonia
  bridges a trackpad magnify gesture on macOS only.
- **Windows:** Control and the wheel already zoomed; only the anchor was wrong, and
  is now fixed.
- **Android and iPhone/iPad:** the pinch you already had now anchors on the point
  between your fingers — the gesture's centroid on Android, the midpoint of the
  touches on iOS — instead of the corner of the page.

(#528, #527, #530, behind #546, #534, #529, #553.)

### Long work says what it's doing, and some of it you can stop

Opening a large document, searching it, saving it, making a smaller copy, and
saving pages out each now say what they're working on and how far they've got,
instead of looking like a hang, on every platform. Searching, shrinking for email
and saving pages out can be stopped partway on the desktops; search and saving
pages out can be stopped on Android and on iPhone and iPad too, which have no
shrink feature to stop. Stopping never leaves a half-written file behind, and
saving itself is never stoppable anywhere, on purpose — there's no safe partial
save to leave.

(#145, behind #568 Windows, #563 Mac and Linux, #580 Android, #591 iPhone and
iPad.)

### Whiteout you can move, and text on more than one line

Draw a whiteout and it stays selected: drag it to move it, take a corner to resize
it, Delete to remove it. That's new chrome on a tool Windows, the Mac and Linux
already had. **On Android and on iPhone and iPad, whiteout itself is new** —
neither had a cover-the-page tool before 2.2, only Redact, which removes rather
than covers; both now have whiteout too, with the same move/resize chrome as the
desktops. Added text can now take more than one line everywhere: Return starts
the next one, and the box grows to fit what you type — on the phones, a longer
note becomes that many separately-selectable text boxes, stacked under one undo
step, since the file format has no single multi-line text object.

(#3, #4, behind #564 Windows, #535 Mac and Linux, #565 Android, #591 iPhone and
iPad.)

### A withheld page permission explains itself

Some documents arrive with an owner password and a permission set that forbids
reordering, deleting or inserting pages — a form meant to be filled in but not
restructured, for instance. Android is the one platform whose page tools already
consult that bit (found while building #554), so it's the one platform where this
now says so rather than only refusing: *the author of this document asked that its
pages not be reordered, removed or added to*, and leaves the choice to you, the same
pattern MegaPDF already uses for a document whose owner restricted editing. The
desktops and iOS don't consult this permission bit for page operations yet
(#558, landed on Android alone as #581).

### Before a document's own signature is lost

Some documents arrive carrying a **digital signature** — the cryptographic kind,
backed by a certificate, which stops verifying the moment the file's bytes change
at all. Saving over one of those now says so first and offers to save a copy
instead, so the signed original stays intact. A document certified closed to all
changes (a `/DocMDP` certification) gets its own, stronger wording, because that
kind of document doesn't merely stop verifying — its author said it should not be
changed at all. Either way, the warning appears once, at the point of Save, on
every platform (#476, #481, behind #486 core / #491 desktop / #492 iOS / #497
Android).

**On the Mac and Linux, that warning now also offers to remove the signature
it's about to break** — a checkbox, unchecked by default, so whether an
already-broken signature still travels with the saved file is the person's
choice, not the app's. The three buttons the warning already had are untouched;
overwriting and keeping the original signature bytes exactly as they are is
still one click. Windows, Android and iOS still only warn, as before — this is
the Mac and Linux half of a change that isn't finished (#576, #610).

**This is not a new way to sign.** The signature you draw, type or photograph and
place on a page — what the app has always called *Signatures*, *sign*, *your
signature* — is unchanged: placing one still does not make a document verifiable,
tamper-evident, certified or legally binding, and nothing in 2.2 implies otherwise.
The two meanings share the English and French word "signature" and nothing else;
2.2 fixed a string that had called the cryptographic kind a "Digital Encrypted
Signature," which named neither thing correctly and is withdrawn (#602, #606). On
Windows, the signature library's thumbnails are now drawn from the signature's own
bytes rather than a saved path, and a library reload says which images are missing
instead of leaving a blank slot (#402, #428).

### Fixed along the way

- **Windows: a freshly launched window, with no document open, no longer offers
  to Save, Print, Redact, set a password, Shrink or use the Pages menu.** With
  no tab, those commands' bindings fell back to their properties' own
  defaults — `Visibility` defaults to visible, `IsEnabled` to true — so 28
  commands painted as available with nothing to act on (#617, #629).
- **A form built for Adobe Reader explains itself instead of looking blank.**
  Some PDF forms (dynamic XFA) cannot be filled in by any app but Adobe Reader's;
  MegaPDF used to leave their fields looking like nothing was there. It now says
  so, names what still works — viewing, printing, saving, sharing — and links to
  Adobe Reader, on the desktops, Android and iOS (#456, #457).
- **A redaction or whiteout mark keeps its identity when an undo brings it back**,
  on the desktops, Android and iOS. Undoing a removed mark used to risk losing
  track of which mark it was (#429, #441).
- **iPhone and iPad: Save a copy lets you keep working with the file it just
  wrote**, instead of leaving the share sheet pointed at the old one (#572, #589,
  #597).
- **Mac: a file opened from the Finder, the Dock or a second launch lands as its
  own tab however it arrives**, and the window's title follows whichever tab you're
  looking at — two gaps left in 2.1.1's tabs (#398, #399).
- **Windows: a tab opened from outside the app** — File Explorer, a second launch —
  **reliably gets its toolbar wired up**, closing an intermittent miss from 2.1.1
  (#427).

### What isn't here

- **The informed page-permission choice is Android only.** Windows, macOS,
  Linux and iOS still refuse outright when a document's owner restricted page
  assembly.
- **Stripping a signature a save is about to break is Mac and Linux only**
  (above). Windows, Android and iOS still warn without that option.
- **Reflow — a document laid out to fit the screen — is not in 2.2**, on any
  platform.

### Windows

- Reading mode: Ctrl+H, or the View menu; tiers 1 and 2 (#541, #504, #510).
- Page tools in the Pages pane: rotate, delete, reorder, insert a blank page,
  insert another PDF's pages, extract a selection. One undo step each (#560).
- Zoom anchored on the point you're pointing at, including Control+wheel, which
  already zoomed and now anchors correctly (#546, #528).
- Long work shows progress; searching, shrinking for email and saving pages out
  can be stopped (#568).
- Whiteout stays selected to move, resize or delete; added text takes more than
  one line (#564).
- The signature library's thumbnails come from the signature's own bytes, and a
  reload says which images are missing (#402, #428).
- Fix: a tab opened from File Explorer or a second launch now reliably gets its
  toolbar wired up (#427).
- Fix: a freshly launched window with no document open no longer offers Save,
  Print, Redact, Password, Shrink or the Pages menu (#617, #629).
- No change: a withheld page permission still refuses outright, and
  `megapdf-cli` is not part of the Store package.

### Mac

- Reading mode: View > Reading mode, or ⇧⌘R; Escape to leave, full screen (⌃⌘F)
  offered once you're in it; tiers 1 and 2 (#533, #505, #511).
- Page tools in the Thumbnails sidebar (⌥⌘2): rotate, delete, reorder, insert a
  blank page, insert another PDF's pages, extract a selection. One undo step each
  (#556).
- Zoom anchored on the point you're pointing at; a trackpad pinch zooms the page,
  which it never did before (#534, #528).
- Long work shows progress; searching, shrinking for email and saving pages out
  can be stopped (#563).
- Whiteout stays selected to move, resize or delete; added text takes more than
  one line (#535).
- The save warning before overwriting a signed document now offers a checkbox to
  also strip the signature it's about to break (#576, #610).
- Fix: a file opened from the Finder, the Dock or a second launch lands as its own
  tab however it arrives, and the window's title follows the active tab (#398,
  #399).
- No change: a withheld page permission still refuses outright, and `megapdf-cli`
  is the separate zip from 2.1.1, not part of the Store app.

### Linux

- Reading mode, tiers 1 and 2 (#533, #505, #511).
- Page tools: rotate, delete, reorder, insert a blank page, insert another PDF's
  pages, extract a selection. One undo step each (#556).
- Zoom anchored on the point you're pointing at; Control and the wheel zoom at
  all now, which they didn't before — there is no trackpad pinch on Linux (#534,
  #528).
- Long work shows progress; searching, shrinking for email and saving pages out
  can be stopped (#563).
- Whiteout stays selected to move, resize or delete; added text takes more than
  one line (#535).
- The save warning before overwriting a signed document now offers a checkbox to
  also strip the signature it's about to break (#576, #610).
- No change: a withheld page permission still refuses outright, and `megapdf-cli`
  is unchanged, in every package.

### Android

- Reading mode, tiers 1 and 2 (#539, #507, #513).
- Page tools: rotate, delete, reorder, combine with another PDF, extract. One
  undo step each (#554).
- Pinch-to-zoom now anchors on the gesture's centroid instead of the corner of
  the page (#529, #527).
- Long work shows progress; searching and saving pages out can be stopped (#580).
- **Whiteout is new** — Android had only Redact before. It has the same
  move/resize chrome as the desktops, and added text now takes more than one
  line (#565).
- **The one platform where a withheld page permission explains itself and leaves
  the choice to you**, instead of only refusing (#558, #581).
- Fix: a redaction mark keeps its identity when an undo brings it back (#429).

### iPhone and iPad

- Reading mode, tiers 1 and 2 (#540, #506, #512).
- Page tools: rotate, delete, reorder, insert a blank page, insert another PDF's
  pages, extract a selection. One undo step each (#570).
- **iPad gets its own toolbar**: the everyday tools in a wide, titled row under
  the navigation bar, with popovers and keyboard commands, replacing the stretched
  iPhone layout it silently ran before (#172, #435).
- Pinch-to-zoom anchors on the point between your fingers (#553, #530).
- **Whiteout, new**: drag to cover an area, move it, resize it or remove it, the
  same as the other platforms (#591).
- Added text takes more than one line: a longer note becomes several
  separately-selectable text boxes under one undo step (#591).
- Long work shows progress; searching and extracting pages can be stopped.
  There is no shrink feature on iOS to stop (#591).
- Fix: Save a copy lets you keep working with the file it just wrote (#572, #589,
  #597).
- Fix: a redaction mark keeps its identity when an undo brings it back (#441).

---

## Français (Canada)

### Mode lecture

MegaPDF sait s'effacer. Le mode lecture retire les barres d'outils — et, sur les
ordinateurs de bureau, le panneau latéral — de l'écran et laisse la page. Une
petite barre porte le numéro de page et le chemin du retour : sur les téléphones,
une touche l'amène et une autre la fait repartir; sur le Mac, elle flotte et Échap
ramène la fenêtre à la normale, le plein écran étant offert une fois que vous y
êtes; sur Windows et Linux, elle s'ouvre depuis le menu Présentation ou Ctrl+H. Le
numéro de page ouvre une case pour aller n'importe où dans le document.

Les couleurs de la page sont juste à côté du mode lecture, dans les Paramètres sur
Windows et les téléphones, dans les Options sur le Mac : Normales, Sépia pour une
longue lecture et Nuit pour une pièce sombre. Le mode nuit inverse la page, images
comprises — c'est une décision, pas un défaut, et le réglage le dit, parce qu'une
photo lue la nuit est une photo en négatif. Si vous lisez un PDF plus souvent que
vous ne le remplissez, **Ouvrir les documents en mode lecture** les ouvre tous
ainsi.

Le mode lecture est sur toutes les plateformes (#541, #533, #539, #540).

### Outils de page

Touchez ou cliquez sur Pages et les pages du document sont devant vous, un endroit
où travailler plutôt que seulement regarder : le volet Pages sur Windows, le
panneau Vignettes sur le Mac (⌥⌘2), une feuille sur l'iPhone et un panneau à côté
de la page sur l'iPad, un panneau sur Android et Linux. Faites pivoter une page
numérisée de travers, supprimez-en une, glissez les pages dans un autre ordre,
insérez une page vierge, insérez les pages d'un autre PDF, ou enregistrez les
pages choisies dans un fichier à part. Chacun de ces gestes est une seule étape de
l'historique, et une seule annulation remet le document comme il était.

Les outils de page sont sur toutes les plateformes (#560, #556, #554, #570),
bâtis sur le même contrat de base (#430), pour que la même opération se comporte
de la même façon partout.

### Une barre d'outils faite pour l'iPad

L'iPad n'avait pas sa propre barre d'outils avant 2.2 — les titres que son code
demandait ne s'affichaient jamais, et il faisait tourner en silence la disposition
de l'iPhone étirée. Les outils de tous les jours occupent maintenant une rangée
large sous la barre de navigation, chacun avec son titre à côté de son icône
plutôt qu'une icône à deviner, et le sélecteur de chaque outil s'ouvre juste à
côté de l'outil auquel il appartient. Les commandes au clavier y sont aussi
(#172, #435).

### Le zoom là où vous regardez

Le zoom garde maintenant le point que vous visez au lieu du coin de la page, sur
toutes les plateformes et par tous les moyens de le demander : le menu, le
clavier, un pincement ou la molette.

- **Mac :** un pincement sur le pavé tactile zoome la page, ce qu'il ne faisait
  jamais avant, en plus de la correction de l'ancrage.
- **Linux :** Contrôle et la molette zooment maintenant, ce qu'ils ne faisaient
  pas avant, et gardent le point que vous visez. Il n'y a pas de geste de
  pincement sur Linux — Avalonia ne relie un geste de pincement du pavé tactile
  que sur macOS.
- **Windows :** Contrôle et la molette zoomaient déjà; seul l'ancrage était faux,
  et il est maintenant corrigé.
- **Android et iPhone/iPad :** le pincement que vous aviez déjà s'ancre
  maintenant sur le point entre vos doigts — le centre du geste sur Android, le
  milieu des points de contact sur iOS — plutôt que sur le coin de la page.

(#528, #527, #530, derrière #546, #534, #529, #553.)

### Les longues opérations disent ce qu'elles font, et certaines peuvent être arrêtées

Ouvrir un gros document, y chercher, l'enregistrer, en faire une copie réduite et
en extraire des pages disent maintenant chacun sur quoi ils travaillent et où ils
en sont, au lieu d'avoir l'air figés, sur toutes les plateformes. La recherche, la
copie réduite pour courriel et l'extraction de pages peuvent être arrêtées en
cours de route sur les ordinateurs de bureau; la recherche et l'extraction de
pages peuvent l'être sur Android et sur l'iPhone et l'iPad aussi, qui n'ont pas de
fonction de copie réduite à arrêter. Un arrêt ne laisse jamais de fichier à moitié
écrit, et l'enregistrement lui-même ne peut jamais être arrêté nulle part, de
façon voulue — il n'y a pas d'enregistrement partiel sûr à laisser derrière.

(#145, derrière #568 Windows, #563 Mac et Linux, #580 Android, #591 iPhone et
iPad.)

### Un correcteur qui se déplace, et du texte sur plus d'une ligne

Tracez du correcteur et il reste sélectionné : glissez-le pour le déplacer,
prenez un coin pour le redimensionner, Supprimer pour l'enlever. C'est une
nouvelle façon d'utiliser un outil que Windows, le Mac et Linux avaient déjà.
**Sur Android et sur l'iPhone et l'iPad, le correcteur lui-même est nouveau** —
aucun des deux n'avait d'outil pour masquer la page avant 2.2, seulement
Caviarder, qui retire plutôt que masquer; les deux ont maintenant aussi le
correcteur, avec le même déplacement et redimensionnement que les ordinateurs de
bureau. Le texte ajouté peut maintenant tenir sur plus d'une ligne partout :
Retour commence la suivante, et la zone grandit pour contenir ce que vous tapez —
sur les téléphones, une note plus longue devient autant de zones de texte
sélectionnables séparément, empilées sous une seule annulation, puisque le format
de fichier n'a pas d'objet de texte à lignes multiples.

(#3, #4, derrière #564 Windows, #535 Mac et Linux, #565 Android, #591 iPhone et
iPad.)

### Une permission de page refusée s'explique

Certains documents arrivent avec un mot de passe du propriétaire et un ensemble
de permissions qui interdit de réordonner, supprimer ou insérer des pages — un
formulaire pensé pour être rempli mais non restructuré, par exemple. Android est
la seule plateforme dont les outils de page consultaient déjà ce bit (découvert
en bâtissant #554), c'est donc la seule plateforme où cela le dit maintenant
plutôt que de seulement refuser : *l'auteur de ce document a demandé que ses
pages ne soient pas réordonnées, retirées ni complétées*, et vous laisse
choisir, le même principe que MegaPDF applique déjà à un document dont le
propriétaire a restreint les modifications. Les ordinateurs de bureau et iOS ne
consultent pas encore cette permission pour les opérations de page (#558, arrivé
sur Android seul sous #581).

### Avant que la signature du document ne soit perdue

Certains documents arrivent avec une **signature numérique** — la signature
cryptographique, appuyée sur un certificat, qui cesse d'être vérifiable dès que
les octets du fichier changent. Enregistrer par-dessus un tel document le dit
maintenant d'abord et propose d'enregistrer une copie, pour que l'original signé
reste intact. Un document certifié fermé à toute modification (une certification
`/DocMDP`) a son propre message, plus ferme, parce que ce genre de document ne
fait pas que cesser d'être vérifiable — son auteur a déclaré qu'il ne devait
subir aucune modification. Dans les deux cas, l'avertissement apparaît une fois,
au moment d'enregistrer, sur toutes les plateformes (#476, #481, derrière #486
pour le moteur, #491 ordinateurs de bureau, #492 iOS, #497 Android).

**Sur le Mac et Linux, cet avertissement propose maintenant aussi de retirer la
signature qu'il s'apprête à briser** — une case à cocher, décochée par défaut,
pour que ce soit la personne, et non l'application, qui décide si une signature
déjà brisée continue de voyager avec le fichier enregistré. Les trois boutons que
l'avertissement avait déjà ne changent pas; écraser l'original en conservant les
octets de la signature tels quels reste possible en un clic. Windows, Android et
iOS ne font encore qu'avertir, comme avant — c'est la moitié Mac et Linux d'un
changement qui n'est pas terminé (#576, #610).

**Ce n'est pas une nouvelle façon de signer.** La signature que vous dessinez,
tapez ou photographiez pour la poser sur une page — ce que l'application a
toujours appelé *Signatures*, *signer*, *vos signatures* — ne change pas : la
poser ne rend toujours pas un document vérifiable, inviolable, certifié ou
juridiquement contraignant, et rien dans 2.2 ne le laisse croire. Les deux sens
partagent le mot « signature » en français comme en anglais, et rien d'autre;
2.2 a corrigé une phrase qui appelait la signature cryptographique une
« signature chiffrée numérique », qui ne nommait correctement ni l'une ni
l'autre et qui est retirée (#602, #606). Sur Windows, les vignettes de la
bibliothèque de signatures viennent maintenant des octets de la signature
elle-même plutôt que d'un chemin enregistré, et un rechargement dit quelles
images manquent plutôt que de laisser un espace vide (#402, #428).

### Corrigé en chemin

- **Windows : une fenêtre tout juste lancée, sans document ouvert, n'offre
  plus Enregistrer, Imprimer, Caviarder, définir un mot de passe, Réduire ni le
  menu Pages.** Sans onglet, les liaisons de ces commandes retombaient sur les
  valeurs par défaut de leurs propriétés — `Visibility` vaut visible par défaut,
  `IsEnabled` vaut vrai par défaut — si bien que 28 commandes apparaissaient
  offertes sans rien sur quoi agir (#617, #629).
- **Un formulaire conçu pour Adobe Reader s'explique au lieu d'avoir l'air
  vide.** Certains formulaires PDF (XFA dynamique) ne peuvent être remplis par
  aucune application sauf Adobe Reader; MegaPDF laissait leurs champs avoir l'air
  de ne rien contenir. L'application le dit maintenant, nomme ce qui fonctionne
  encore — afficher, imprimer, enregistrer, partager — et propose un lien vers
  Adobe Reader, sur les ordinateurs de bureau, Android et iOS (#456, #457).
- **Une marque de caviardage ou de correcteur garde son identité quand une
  annulation la ramène**, sur les ordinateurs de bureau, Android et iOS. Annuler
  une marque retirée risquait auparavant de perdre la trace de laquelle il
  s'agissait (#429, #441).
- **iPhone et iPad : Enregistrer une copie vous laisse continuer à travailler
  avec le fichier qu'elle vient d'écrire**, au lieu de laisser la feuille de
  partage pointer vers l'ancien (#572, #589, #597).
- **Mac : un fichier ouvert depuis le Finder, le Dock ou un second lancement
  arrive dans son propre onglet quel que soit son chemin**, et le titre de la
  fenêtre suit l'onglet que vous regardez — deux manques laissés par les onglets
  de 2.1.1 (#398, #399).
- **Windows : un onglet ouvert de l'extérieur de l'application** — l'Explorateur
  de fichiers, un second lancement — **obtient maintenant sa barre d'outils de
  façon fiable**, corrigeant un manque intermittent de 2.1.1 (#427).

### Ce qui n'y est pas

- **Le choix éclairé pour une permission de page est réservé à Android.**
  Windows, macOS, Linux et iOS refusent toujours carrément quand le propriétaire
  d'un document a restreint l'assemblage des pages.
- **Retirer une signature qu'un enregistrement s'apprête à briser est réservé au
  Mac et à Linux** (voir plus haut). Windows, Android et iOS ne font encore
  qu'avertir, sans cette option.
- **La reformulation — un document mis en page pour s'ajuster à l'écran — n'est
  pas dans 2.2**, sur aucune plateforme.

### Windows

- Mode lecture : Ctrl+H, ou le menu Présentation; paliers 1 et 2 (#541, #504,
  #510).
- Outils de page dans le volet Pages : pivoter, supprimer, réordonner, insérer
  une page vierge, insérer les pages d'un autre PDF, extraire une sélection. Une
  seule étape d'annulation par geste (#560).
- Zoom ancré sur le point que vous visez, y compris Contrôle+molette, qui
  zoomait déjà et s'ancre maintenant correctement (#546, #528).
- Les longues opérations montrent leur progression; la recherche, la copie
  réduite pour courriel et l'extraction de pages peuvent être arrêtées (#568).
- Le correcteur reste sélectionné pour le déplacer, le redimensionner ou le
  supprimer; le texte ajouté tient sur plus d'une ligne (#564).
- Les vignettes de la bibliothèque de signatures viennent des octets de la
  signature elle-même, et un rechargement dit quelles images manquent (#402,
  #428).
- Correction : un onglet ouvert depuis l'Explorateur de fichiers ou un second
  lancement obtient maintenant sa barre d'outils de façon fiable (#427).
- Correction : une fenêtre tout juste lancée sans document ouvert n'offre plus
  Enregistrer, Imprimer, Caviarder, Mot de passe, Réduire ni le menu Pages
  (#617, #629).
- Sans changement : une permission de page refusée refuse toujours carrément, et
  `megapdf-cli` ne fait pas partie du paquet du Store.

### Mac

- Mode lecture : Présentation > Mode lecture, ou ⇧⌘R; Échap pour sortir, le
  plein écran (⌃⌘F) offert une fois que vous y êtes; paliers 1 et 2 (#533, #505,
  #511).
- Outils de page dans le panneau Vignettes (⌥⌘2) : pivoter, supprimer,
  réordonner, insérer une page vierge, insérer les pages d'un autre PDF,
  extraire une sélection. Une seule étape d'annulation par geste (#556).
- Zoom ancré sur le point que vous visez; un pincement sur le pavé tactile
  zoome la page, ce qu'il ne faisait jamais avant (#534, #528).
- Les longues opérations montrent leur progression; la recherche, la copie
  réduite pour courriel et l'extraction de pages peuvent être arrêtées (#563).
- Le correcteur reste sélectionné pour le déplacer, le redimensionner ou le
  supprimer; le texte ajouté tient sur plus d'une ligne (#535).
- L'avertissement avant d'écraser un document signé propose maintenant une case
  à cocher pour retirer aussi la signature qu'il s'apprête à briser (#576, #610).
- Correction : un fichier ouvert depuis le Finder, le Dock ou un second
  lancement arrive dans son propre onglet quel que soit son chemin, et le titre
  de la fenêtre suit l'onglet actif (#398, #399).
- Sans changement : une permission de page refusée refuse toujours carrément, et
  `megapdf-cli` reste l'archive zip séparée de 2.1.1, pas une partie de
  l'application du Store.

### Linux

- Mode lecture, paliers 1 et 2 (#533, #505, #511).
- Outils de page : pivoter, supprimer, réordonner, insérer une page vierge,
  insérer les pages d'un autre PDF, extraire une sélection. Une seule étape
  d'annulation par geste (#556).
- Zoom ancré sur le point que vous visez; Contrôle et la molette zooment
  maintenant, ce qu'ils ne faisaient pas avant — il n'y a pas de pincement sur
  le pavé tactile sur Linux (#534, #528).
- Les longues opérations montrent leur progression; la recherche, la copie
  réduite pour courriel et l'extraction de pages peuvent être arrêtées (#563).
- Le correcteur reste sélectionné pour le déplacer, le redimensionner ou le
  supprimer; le texte ajouté tient sur plus d'une ligne (#535).
- L'avertissement avant d'écraser un document signé propose maintenant une case
  à cocher pour retirer aussi la signature qu'il s'apprête à briser (#576, #610).
- Sans changement : une permission de page refusée refuse toujours carrément, et
  `megapdf-cli` est inchangé, dans chaque paquet.

### Android

- Mode lecture, paliers 1 et 2 (#539, #507, #513).
- Outils de page : pivoter, supprimer, réordonner, combiner avec un autre PDF,
  extraire. Une seule étape d'annulation par geste (#554).
- Le pincement pour zoomer s'ancre maintenant sur le centre du geste plutôt que
  sur le coin de la page (#529, #527).
- Les longues opérations montrent leur progression; la recherche et
  l'extraction de pages peuvent être arrêtées (#580).
- **Le correcteur est nouveau** — Android n'avait que Caviarder avant. Il a le
  même déplacement et redimensionnement que les ordinateurs de bureau, et le
  texte ajouté tient maintenant sur plus d'une ligne (#565).
- **La seule plateforme où une permission de page refusée s'explique et vous
  laisse choisir**, au lieu de seulement refuser (#558, #581).
- Correction : une marque de caviardage garde son identité quand une annulation
  la ramène (#429).

### iPhone et iPad

- Mode lecture, paliers 1 et 2 (#540, #506, #512).
- Outils de page : pivoter, supprimer, réordonner, insérer une page vierge,
  insérer les pages d'un autre PDF, extraire une sélection. Une seule étape
  d'annulation par geste (#570).
- **L'iPad a maintenant sa propre barre d'outils** : les outils de tous les
  jours dans une rangée large et titrée sous la barre de navigation, avec des
  fenêtres contextuelles et des commandes au clavier, remplaçant la disposition
  étirée de l'iPhone qu'il faisait tourner en silence avant (#172, #435).
- Le pincement pour zoomer s'ancre sur le point entre vos doigts (#553, #530).
- **Correcteur, nouveau** : glissez pour couvrir une zone, déplacez-la,
  redimensionnez-la ou supprimez-la, comme sur les autres plateformes (#591).
- Le texte ajouté tient sur plus d'une ligne : une note plus longue devient
  plusieurs zones de texte sélectionnables séparément, sous une seule
  annulation (#591).
- Les longues opérations montrent leur progression; la recherche et
  l'extraction de pages peuvent être arrêtées. Il n'y a pas de fonction de
  copie réduite sur iOS à arrêter (#591).
- Correction : Enregistrer une copie vous laisse continuer à travailler avec le
  fichier qu'elle vient d'écrire (#572, #589, #597).
- Correction : une marque de caviardage garde son identité quand une annulation
  la ramène (#441).

---

## Français (France)

> Dérivé du Français (Canada) ci-dessus par `check_copy.py` dans ce dossier, selon
> les mêmes règles que `tools/gen_strings.py fr-fr` applique aux catalogues de
> l'application : *courriel* → *e-mail* (le seul mot de ce texte que la table de
> dérivation change), et une espace insécable avant `?`, `!` et `;` en plus de
> `:`. Ne modifiez pas ce qui suit cette note; modifiez la section canadienne et
> lancez le script.

### Mode lecture

MegaPDF sait s'effacer. Le mode lecture retire les barres d'outils — et, sur les
ordinateurs de bureau, le panneau latéral — de l'écran et laisse la page. Une
petite barre porte le numéro de page et le chemin du retour : sur les téléphones,
une touche l'amène et une autre la fait repartir ; sur le Mac, elle flotte et Échap
ramène la fenêtre à la normale, le plein écran étant offert une fois que vous y
êtes ; sur Windows et Linux, elle s'ouvre depuis le menu Présentation ou Ctrl+H. Le
numéro de page ouvre une case pour aller n'importe où dans le document.

Les couleurs de la page sont juste à côté du mode lecture, dans les Paramètres sur
Windows et les téléphones, dans les Options sur le Mac : Normales, Sépia pour une
longue lecture et Nuit pour une pièce sombre. Le mode nuit inverse la page, images
comprises — c'est une décision, pas un défaut, et le réglage le dit, parce qu'une
photo lue la nuit est une photo en négatif. Si vous lisez un PDF plus souvent que
vous ne le remplissez, **Ouvrir les documents en mode lecture** les ouvre tous
ainsi.

Le mode lecture est sur toutes les plateformes (#541, #533, #539, #540).

### Outils de page

Touchez ou cliquez sur Pages et les pages du document sont devant vous, un endroit
où travailler plutôt que seulement regarder : le volet Pages sur Windows, le
panneau Vignettes sur le Mac (⌥⌘2), une feuille sur l'iPhone et un panneau à côté
de la page sur l'iPad, un panneau sur Android et Linux. Faites pivoter une page
numérisée de travers, supprimez-en une, glissez les pages dans un autre ordre,
insérez une page vierge, insérez les pages d'un autre PDF, ou enregistrez les
pages choisies dans un fichier à part. Chacun de ces gestes est une seule étape de
l'historique, et une seule annulation remet le document comme il était.

Les outils de page sont sur toutes les plateformes (#560, #556, #554, #570),
bâtis sur le même contrat de base (#430), pour que la même opération se comporte
de la même façon partout.

### Une barre d'outils faite pour l'iPad

L'iPad n'avait pas sa propre barre d'outils avant 2.2 — les titres que son code
demandait ne s'affichaient jamais, et il faisait tourner en silence la disposition
de l'iPhone étirée. Les outils de tous les jours occupent maintenant une rangée
large sous la barre de navigation, chacun avec son titre à côté de son icône
plutôt qu'une icône à deviner, et le sélecteur de chaque outil s'ouvre juste à
côté de l'outil auquel il appartient. Les commandes au clavier y sont aussi
(#172, #435).

### Le zoom là où vous regardez

Le zoom garde maintenant le point que vous visez au lieu du coin de la page, sur
toutes les plateformes et par tous les moyens de le demander : le menu, le
clavier, un pincement ou la molette.

- **Mac :** un pincement sur le pavé tactile zoome la page, ce qu'il ne faisait
  jamais avant, en plus de la correction de l'ancrage.
- **Linux :** Contrôle et la molette zooment maintenant, ce qu'ils ne faisaient
  pas avant, et gardent le point que vous visez. Il n'y a pas de geste de
  pincement sur Linux — Avalonia ne relie un geste de pincement du pavé tactile
  que sur macOS.
- **Windows :** Contrôle et la molette zoomaient déjà ; seul l'ancrage était faux,
  et il est maintenant corrigé.
- **Android et iPhone/iPad :** le pincement que vous aviez déjà s'ancre
  maintenant sur le point entre vos doigts — le centre du geste sur Android, le
  milieu des points de contact sur iOS — plutôt que sur le coin de la page.

(#528, #527, #530, derrière #546, #534, #529, #553.)

### Les longues opérations disent ce qu'elles font, et certaines peuvent être arrêtées

Ouvrir un gros document, y chercher, l'enregistrer, en faire une copie réduite et
en extraire des pages disent maintenant chacun sur quoi ils travaillent et où ils
en sont, au lieu d'avoir l'air figés, sur toutes les plateformes. La recherche, la
copie réduite pour l'e-mail et l'extraction de pages peuvent être arrêtées en
cours de route sur les ordinateurs de bureau ; la recherche et l'extraction de
pages peuvent l'être sur Android et sur l'iPhone et l'iPad aussi, qui n'ont pas de
fonction de copie réduite à arrêter. Un arrêt ne laisse jamais de fichier à moitié
écrit, et l'enregistrement lui-même ne peut jamais être arrêté nulle part, de
façon voulue — il n'y a pas d'enregistrement partiel sûr à laisser derrière.

(#145, derrière #568 Windows, #563 Mac et Linux, #580 Android, #591 iPhone et
iPad.)

### Un correcteur qui se déplace, et du texte sur plus d'une ligne

Tracez du correcteur et il reste sélectionné : glissez-le pour le déplacer,
prenez un coin pour le redimensionner, Supprimer pour l'enlever. C'est une
nouvelle façon d'utiliser un outil que Windows, le Mac et Linux avaient déjà.
**Sur Android et sur l'iPhone et l'iPad, le correcteur lui-même est nouveau** —
aucun des deux n'avait d'outil pour masquer la page avant 2.2, seulement
Caviarder, qui retire plutôt que masquer ; les deux ont maintenant aussi le
correcteur, avec le même déplacement et redimensionnement que les ordinateurs de
bureau. Le texte ajouté peut maintenant tenir sur plus d'une ligne partout :
Retour commence la suivante, et la zone grandit pour contenir ce que vous tapez —
sur les téléphones, une note plus longue devient autant de zones de texte
sélectionnables séparément, empilées sous une seule annulation, puisque le format
de fichier n'a pas d'objet de texte à lignes multiples.

(#3, #4, derrière #564 Windows, #535 Mac et Linux, #565 Android, #591 iPhone et
iPad.)

### Une permission de page refusée s'explique

Certains documents arrivent avec un mot de passe du propriétaire et un ensemble
de permissions qui interdit de réordonner, supprimer ou insérer des pages — un
formulaire pensé pour être rempli mais non restructuré, par exemple. Android est
la seule plateforme dont les outils de page consultaient déjà ce bit (découvert
en bâtissant #554), c'est donc la seule plateforme où cela le dit maintenant
plutôt que de seulement refuser : *l'auteur de ce document a demandé que ses
pages ne soient pas réordonnées, retirées ni complétées*, et vous laisse
choisir, le même principe que MegaPDF applique déjà à un document dont le
propriétaire a restreint les modifications. Les ordinateurs de bureau et iOS ne
consultent pas encore cette permission pour les opérations de page (#558, arrivé
sur Android seul sous #581).

### Avant que la signature du document ne soit perdue

Certains documents arrivent avec une **signature numérique** — la signature
cryptographique, appuyée sur un certificat, qui cesse d'être vérifiable dès que
les octets du fichier changent. Enregistrer par-dessus un tel document le dit
maintenant d'abord et propose d'enregistrer une copie, pour que l'original signé
reste intact. Un document certifié fermé à toute modification (une certification
`/DocMDP`) a son propre message, plus ferme, parce que ce genre de document ne
fait pas que cesser d'être vérifiable — son auteur a déclaré qu'il ne devait
subir aucune modification. Dans les deux cas, l'avertissement apparaît une fois,
au moment d'enregistrer, sur toutes les plateformes (#476, #481, derrière #486
pour le moteur, #491 ordinateurs de bureau, #492 iOS, #497 Android).

**Sur le Mac et Linux, cet avertissement propose maintenant aussi de retirer la
signature qu'il s'apprête à briser** — une case à cocher, décochée par défaut,
pour que ce soit la personne, et non l'application, qui décide si une signature
déjà brisée continue de voyager avec le fichier enregistré. Les trois boutons que
l'avertissement avait déjà ne changent pas ; écraser l'original en conservant les
octets de la signature tels quels reste possible en un clic. Windows, Android et
iOS ne font encore qu'avertir, comme avant — c'est la moitié Mac et Linux d'un
changement qui n'est pas terminé (#576, #610).

**Ce n'est pas une nouvelle façon de signer.** La signature que vous dessinez,
tapez ou photographiez pour la poser sur une page — ce que l'application a
toujours appelé *Signatures*, *signer*, *vos signatures* — ne change pas : la
poser ne rend toujours pas un document vérifiable, inviolable, certifié ou
juridiquement contraignant, et rien dans 2.2 ne le laisse croire. Les deux sens
partagent le mot « signature » en français comme en anglais, et rien d'autre ;
2.2 a corrigé une phrase qui appelait la signature cryptographique une
« signature chiffrée numérique », qui ne nommait correctement ni l'une ni
l'autre et qui est retirée (#602, #606). Sur Windows, les vignettes de la
bibliothèque de signatures viennent maintenant des octets de la signature
elle-même plutôt que d'un chemin enregistré, et un rechargement dit quelles
images manquent plutôt que de laisser un espace vide (#402, #428).

### Corrigé en chemin

- **Windows : une fenêtre tout juste lancée, sans document ouvert, n'offre
  plus Enregistrer, Imprimer, Caviarder, définir un mot de passe, Réduire ni le
  menu Pages.** Sans onglet, les liaisons de ces commandes retombaient sur les
  valeurs par défaut de leurs propriétés — `Visibility` vaut visible par défaut,
  `IsEnabled` vaut vrai par défaut — si bien que 28 commandes apparaissaient
  offertes sans rien sur quoi agir (#617, #629).
- **Un formulaire conçu pour Adobe Reader s'explique au lieu d'avoir l'air
  vide.** Certains formulaires PDF (XFA dynamique) ne peuvent être remplis par
  aucune application sauf Adobe Reader ; MegaPDF laissait leurs champs avoir l'air
  de ne rien contenir. L'application le dit maintenant, nomme ce qui fonctionne
  encore — afficher, imprimer, enregistrer, partager — et propose un lien vers
  Adobe Reader, sur les ordinateurs de bureau, Android et iOS (#456, #457).
- **Une marque de caviardage ou de correcteur garde son identité quand une
  annulation la ramène**, sur les ordinateurs de bureau, Android et iOS. Annuler
  une marque retirée risquait auparavant de perdre la trace de laquelle il
  s'agissait (#429, #441).
- **iPhone et iPad : Enregistrer une copie vous laisse continuer à travailler
  avec le fichier qu'elle vient d'écrire**, au lieu de laisser la feuille de
  partage pointer vers l'ancien (#572, #589, #597).
- **Mac : un fichier ouvert depuis le Finder, le Dock ou un second lancement
  arrive dans son propre onglet quel que soit son chemin**, et le titre de la
  fenêtre suit l'onglet que vous regardez — deux manques laissés par les onglets
  de 2.1.1 (#398, #399).
- **Windows : un onglet ouvert de l'extérieur de l'application** — l'Explorateur
  de fichiers, un second lancement — **obtient maintenant sa barre d'outils de
  façon fiable**, corrigeant un manque intermittent de 2.1.1 (#427).

### Ce qui n'y est pas

- **Le choix éclairé pour une permission de page est réservé à Android.**
  Windows, macOS, Linux et iOS refusent toujours carrément quand le propriétaire
  d'un document a restreint l'assemblage des pages.
- **Retirer une signature qu'un enregistrement s'apprête à briser est réservé au
  Mac et à Linux** (voir plus haut). Windows, Android et iOS ne font encore
  qu'avertir, sans cette option.
- **La reformulation — un document mis en page pour s'ajuster à l'écran — n'est
  pas dans 2.2**, sur aucune plateforme.

### Windows

- Mode lecture : Ctrl+H, ou le menu Présentation ; paliers 1 et 2 (#541, #504,
  #510).
- Outils de page dans le volet Pages : pivoter, supprimer, réordonner, insérer
  une page vierge, insérer les pages d'un autre PDF, extraire une sélection. Une
  seule étape d'annulation par geste (#560).
- Zoom ancré sur le point que vous visez, y compris Contrôle+molette, qui
  zoomait déjà et s'ancre maintenant correctement (#546, #528).
- Les longues opérations montrent leur progression ; la recherche, la copie
  réduite pour l'e-mail et l'extraction de pages peuvent être arrêtées (#568).
- Le correcteur reste sélectionné pour le déplacer, le redimensionner ou le
  supprimer ; le texte ajouté tient sur plus d'une ligne (#564).
- Les vignettes de la bibliothèque de signatures viennent des octets de la
  signature elle-même, et un rechargement dit quelles images manquent (#402,
  #428).
- Correction : un onglet ouvert depuis l'Explorateur de fichiers ou un second
  lancement obtient maintenant sa barre d'outils de façon fiable (#427).
- Correction : une fenêtre tout juste lancée sans document ouvert n'offre plus
  Enregistrer, Imprimer, Caviarder, Mot de passe, Réduire ni le menu Pages
  (#617, #629).
- Sans changement : une permission de page refusée refuse toujours carrément, et
  `megapdf-cli` ne fait pas partie du paquet du Store.

### Mac

- Mode lecture : Présentation > Mode lecture, ou ⇧⌘R ; Échap pour sortir, le
  plein écran (⌃⌘F) offert une fois que vous y êtes ; paliers 1 et 2 (#533, #505,
  #511).
- Outils de page dans le panneau Vignettes (⌥⌘2) : pivoter, supprimer,
  réordonner, insérer une page vierge, insérer les pages d'un autre PDF,
  extraire une sélection. Une seule étape d'annulation par geste (#556).
- Zoom ancré sur le point que vous visez ; un pincement sur le pavé tactile
  zoome la page, ce qu'il ne faisait jamais avant (#534, #528).
- Les longues opérations montrent leur progression ; la recherche, la copie
  réduite pour l'e-mail et l'extraction de pages peuvent être arrêtées (#563).
- Le correcteur reste sélectionné pour le déplacer, le redimensionner ou le
  supprimer ; le texte ajouté tient sur plus d'une ligne (#535).
- L'avertissement avant d'écraser un document signé propose maintenant une case
  à cocher pour retirer aussi la signature qu'il s'apprête à briser (#576, #610).
- Correction : un fichier ouvert depuis le Finder, le Dock ou un second
  lancement arrive dans son propre onglet quel que soit son chemin, et le titre
  de la fenêtre suit l'onglet actif (#398, #399).
- Sans changement : une permission de page refusée refuse toujours carrément, et
  `megapdf-cli` reste l'archive zip séparée de 2.1.1, pas une partie de
  l'application du Store.

### Linux

- Mode lecture, paliers 1 et 2 (#533, #505, #511).
- Outils de page : pivoter, supprimer, réordonner, insérer une page vierge,
  insérer les pages d'un autre PDF, extraire une sélection. Une seule étape
  d'annulation par geste (#556).
- Zoom ancré sur le point que vous visez ; Contrôle et la molette zooment
  maintenant, ce qu'ils ne faisaient pas avant — il n'y a pas de pincement sur
  le pavé tactile sur Linux (#534, #528).
- Les longues opérations montrent leur progression ; la recherche, la copie
  réduite pour l'e-mail et l'extraction de pages peuvent être arrêtées (#563).
- Le correcteur reste sélectionné pour le déplacer, le redimensionner ou le
  supprimer ; le texte ajouté tient sur plus d'une ligne (#535).
- L'avertissement avant d'écraser un document signé propose maintenant une case
  à cocher pour retirer aussi la signature qu'il s'apprête à briser (#576, #610).
- Sans changement : une permission de page refusée refuse toujours carrément, et
  `megapdf-cli` est inchangé, dans chaque paquet.

### Android

- Mode lecture, paliers 1 et 2 (#539, #507, #513).
- Outils de page : pivoter, supprimer, réordonner, combiner avec un autre PDF,
  extraire. Une seule étape d'annulation par geste (#554).
- Le pincement pour zoomer s'ancre maintenant sur le centre du geste plutôt que
  sur le coin de la page (#529, #527).
- Les longues opérations montrent leur progression ; la recherche et
  l'extraction de pages peuvent être arrêtées (#580).
- **Le correcteur est nouveau** — Android n'avait que Caviarder avant. Il a le
  même déplacement et redimensionnement que les ordinateurs de bureau, et le
  texte ajouté tient maintenant sur plus d'une ligne (#565).
- **La seule plateforme où une permission de page refusée s'explique et vous
  laisse choisir**, au lieu de seulement refuser (#558, #581).
- Correction : une marque de caviardage garde son identité quand une annulation
  la ramène (#429).

### iPhone et iPad

- Mode lecture, paliers 1 et 2 (#540, #506, #512).
- Outils de page : pivoter, supprimer, réordonner, insérer une page vierge,
  insérer les pages d'un autre PDF, extraire une sélection. Une seule étape
  d'annulation par geste (#570).
- **L'iPad a maintenant sa propre barre d'outils** : les outils de tous les
  jours dans une rangée large et titrée sous la barre de navigation, avec des
  fenêtres contextuelles et des commandes au clavier, remplaçant la disposition
  étirée de l'iPhone qu'il faisait tourner en silence avant (#172, #435).
- Le pincement pour zoomer s'ancre sur le point entre vos doigts (#553, #530).
- **Correcteur, nouveau** : glissez pour couvrir une zone, déplacez-la,
  redimensionnez-la ou supprimez-la, comme sur les autres plateformes (#591).
- Le texte ajouté tient sur plus d'une ligne : une note plus longue devient
  plusieurs zones de texte sélectionnables séparément, sous une seule
  annulation (#591).
- Les longues opérations montrent leur progression ; la recherche et
  l'extraction de pages peuvent être arrêtées. Il n'y a pas de fonction de
  copie réduite sur iOS à arrêter (#591).
- Correction : Enregistrer une copie vous laisse continuer à travailler avec le
  fichier qu'elle vient d'écrire (#572, #589, #597).
- Correction : une marque de caviardage garde son identité quand une annulation
  la ramène (#441).

---
