# MegaPDF 2.1.1 — release notes

The long form: everything 2.1.1 changes, per platform, for the GitHub release
bodies and the website. The store fields are the short versions of this, in the
other files in this folder.

Written from `git log linux-v2.1.0..029c849` (110 commits) and the issues behind
it: #348 (tabs), #142 phases 1–3 — #353, #354, #355, #356, #357 — and the
accuracy work on the same engine (#360, #363, #375, #384), #386 (the Markdown
export in every app), #376 and #377 (the phones as PDF viewers), #378 (Share),
#2 (Windows keyboard), #145 (a Mac quit leak) and #352 (the snap build). 2.1.0
went out on every store on 2026-09-21, so these notes cover the same span for
all four.

*The French awaits its francophone read (#343); the [README](README.md) lists
every new phrase, here and in the app's own new strings.*

---

## English

### Tabs

Until now a second document was a second copy of MegaPDF. On Windows and Linux
a double-click in the file manager started another process with its own window;
on the Mac it replaced the document you had open, with a Save / Don't Save /
Cancel question in the way, and if you opened several files at once only the
first arrived.

Open a second PDF now and it opens in a tab beside the first, in the same
window. Open picks several files at once, a file dropped on the window opens
too (new on the Mac and Linux, which had no drag-and-drop before), and a
document opened from outside — File Explorer, the Finder, a file manager, or
any app that hands MegaPDF a PDF — lands in the window you already have: as a
new tab, or, if that file is already open, on its tab. A file is never opened
twice.

Each tab is its own document: its own undo history, find, zoom, armed tool,
scroll position, recovery journal and unsaved-changes dot, which now sits on
the tab rather than in the title bar. The toolbar acts on the tab you are
looking at. A tab in the background gives its rendered pages back to memory
and draws them again when you return.

Closing the window, or quitting, asks about each document that needs saving,
one at a time, and a Cancel on the second leaves the first exactly as it was —
including its recovery journal, which is only deleted once you have answered
for that document. After a crash, the offer to restore is made once per launch,
for every document that was open, and each comes back in its own tab.

Close tab and New window are on the toolbar's More menu on Windows, and Close
Tab, Close Window and New Window in the File menu on the Mac and Linux, with
each platform's own keys: Ctrl+W closes a tab (⌘W on the Mac; with one tab,
that closes the window), Ctrl+Shift+N opens a new window (⌘N), and on the Mac
⇧⌘W closes the window and ⌃Tab / ⌃⇧Tab move between tabs, as the Window menu
says; on Linux Ctrl+Page Up / Ctrl+Page Down do the same. A screen reader hears each tab as its file
name, with *unsaved changes* when it has them.

**What is not here.** No tab tears out into its own window yet, and no tab
moves between windows; the Mac uses MegaPDF's own tab strip rather than the
system's window tabs. Nothing changes on the phones.

### Save As Markdown

Save As on Windows, the Mac and Linux, Save a copy on Android and Export as
Markdown on iPhone and iPad now write the document's text as a Markdown file:
headings at their levels, paragraphs unwrapped with bold, italic and monospace
kept, lists with their markers, and form fields as a task list — a box that is
ticked or not — or as `**name:** value`. A page with no text layer, a scan, is
written as a one-line note in its place, because MegaPDF does no OCR and does
not pretend to.

It is an export, not a save. The file cannot hold your edits the way a PDF
does, so nothing about the document changes: the PDF is untouched, the
unsaved-changes dot stays if it was there, and the app still asks to save the
PDF when you close it. The wording says so throughout — *Exporting…*,
*Exported*, *Couldn't export* — never *Saved*.

Where it is: on Windows and the Mac and Linux, a second file type, **Markdown
document**, in the Save As panel beside PDF document; on Android, a second type
in Save a copy's own file dialog; on iPhone and iPad, **Export as Markdown**
beside Save a copy in the More menu, which writes the file wherever you choose
in Files.

### megapdf-cli

The same text comes out of a terminal: `megapdf-cli extract file.pdf` writes
the document's text to standard output or `--out`, and `--format md` writes the
Markdown above. `--pages` takes a range, a password comes from a file or from
standard input (never from the command line, where every other process could
read it), and the exit codes mean what they say: 0 text written, 2 the file
could not be opened, 3 a password is needed or wrong, 5 no requested page had a
text layer, 130 interrupted. It is a small native binary with no runtime to
install.

Linux has it in every package: `megapdf-cli` on `PATH` from the APT
repository, the `.deb` and the tarball's `install.sh`; `flatpak run
--command=megapdf-cli ca.electricrv.MegaPDF file.pdf` in the Flatpak;
`snap run megapdf.cli extract file.pdf` in the snap. Windows (x64 and arm64)
and macOS (universal) get it as a zip on the GitHub releases page —
`megapdf-cli-windows-<arch>-2.1.1.zip`, unsigned, because MegaPDF holds no
code-signing certificate for Windows; `megapdf-cli-macos-universal-2.1.1.zip`,
signed with the app's Developer ID and notarized. **It is not in any store
package**: a Store app cannot put a binary on your `PATH`, on either platform.

### On the phones: Open with, and Share

**MegaPDF is a PDF viewer now, as far as the phone is concerned.** On Android,
opening a PDF from Files, Drive, Gmail or any app that hands one over offers
MegaPDF in the Open with list. On iPhone and iPad, a PDF in Files, an attachment
in Mail or a document in any app's share sheet opens in MegaPDF from right
there. Until now the only way in was Open PDF on MegaPDF's own first screen,
through the picker. If a
document with unsaved changes is already open, the phone asks — Save, Discard
or Cancel — before switching, because the alternative was to lose those edits
silently.

**Share**, in the More menu on both phones, hands the document to the system's
share sheet: Mail, Messages, AirDrop, Save to Files, Drive, whatever you have.
If the document has unsaved changes it says so first — *This document has
unsaved changes. They won't be in the shared copy unless you save first* — and
offers **Save**, **Share without saving** or **Cancel**. The middle button was
going to say Discard, and does not: pressing it discards nothing, the last saved
file goes out and your edits stay open, and a button that says Discard but
destroys nothing would teach people that Discard is safe — which it is not, in
the Close dialog beside it.

### Under the hood: the text the Markdown is made of

The Markdown export and `megapdf-cli` read the same structure the engine infers
from a page — which text is a heading, what is a paragraph, where the columns
are, what is furniture — and that inference was measured against the whole test
corpus (4,158 real documents, 22,180 pages) and corrected four times in this
release. None of it is a feature you can see; all of it is why the Markdown
reads like the document.

- **Words were being split.** The gap the engine used to decide that two
  characters belong to different words was calibrated for a tight box PDFium
  does not actually report, so real words came out in fragments. Recalibrated
  from the corpus, with superscripts and rotated text handled on their own
  terms (#363). Token fidelity against the raw text layer went from 0.93 to
  0.998 across the corpus, and the export is gated on that number from now on.
- **A hyphen at a line's end joins the word**, the way the engine already did
  it, only when PDFium says it is a line-wrap hyphen — never for *well-known*
  in the middle of a line (#360).
- **A table is not a list of headings.** Bold text at body size is a heading
  when it stands alone, as a real heading does. A run of them back to back — a
  table's column labels, a form's field names, an email's header block — is
  not, and neither is a bare figure like a dollar amount. Those are paragraphs
  again (#375): 62 % fewer false headings across the corpus, no true heading
  lost on any fixture.
- **A page whose reading order jumps about** — a title page, a cover, a page
  the layout genuinely does not order — now carries a lower confidence score,
  so the writer can say so rather than present the jumble as prose (#384).

### Mac

- Tabs, as above, in the Mac's own idiom: ⌘W, ⇧⌘W, ⌘N, ⌃Tab and ⌃⇧Tab, the
  File and Window menus, files dropped on the window, and a Finder or Dock open
  — one file or several — landing in the running window as tabs. Verified on
  the Mac mini through `open -a`, the path the Finder uses, with four documents
  from three separate opens ending up as four tabs in one process.
- The Save As panel offers Markdown document.
- Quitting with several windows open asks each in turn, and a Cancel in the
  second no longer leaves the first believing it had already been answered —
  which would have let it close without asking, later, for the life of the
  window (#145).
- `megapdf-cli` is the separate zip above, not part of the Store app.

### Windows

- Tabs, as above: Ctrl+W and Ctrl+Shift+N, Close tab and New window in the
  More menu, multi-select Open, drag-and-drop, and a double-click
  in File Explorer landing in the running window — several at once, each its
  own tab, none dropped. Verified on GPD-DAVE, where the single-instance code
  was found to be silently opening nothing (a WinRT cast that compiles and
  never matches) and fixed before it shipped.
- Save As offers Markdown document.
- Keyboard (#2): ticking a drawn box announces *Box, ticked* rather than *Box
  to tick* again; the resize handle and the ✕ of a selected signature sit in a
  40 × 40 hit target, unchanged to look at; an arrow-key nudge is kept when a
  click follows within the moment it used to lose it.
- `megapdf-cli` is the separate zip above, not part of the Store package.

### Linux

- Tabs, as above: Ctrl+W, Ctrl+Shift+N, Ctrl+Page Up / Ctrl+Page Down, drag-and-drop,
  and a second `megapdf file.pdf` — from a file manager's Open With or a
  terminal — handed to the running instance over a socket in the runtime
  directory and opened as a tab, inside the Flatpak and the snap as well. The
  desktop entry now takes several files (`%F`).
- Save As offers Markdown document.
- `megapdf-cli` is in every package, on `PATH` or one command away (above).
- The snap builds again (#352).

### Android

- Open with: MegaPDF is offered when a PDF is opened from Files, Drive, Gmail
  or any app that hands one over. If a document with unsaved changes is open,
  the same Save / Discard / Cancel question as Close comes first.
- Share, in the More menu, through the system's share sheet, with Save / Share
  without saving / Cancel when there are unsaved changes.
- Save a copy offers Markdown as a second type in its file dialog; a `.md` name
  is the export, a `.pdf` name is the copy it always was.

### iPhone and iPad

- Open with: a PDF in Files, an attachment in Mail, or a document in any app's
  share sheet opens in MegaPDF, which now declares itself a PDF viewer. With a
  document with unsaved changes open, it asks first.
- Share, in the More menu, through the share sheet — with a popover anchored
  to the More button on iPad, where an unanchored one would have crashed.
- Export as Markdown, beside Save a copy in the More menu, through the Files
  picker.
- The iPad still shows the iPhone layout; its own layout is later work (#172).

---

## Français (Canada)

### Des onglets

Jusqu'ici, un deuxième document était une deuxième copie de MegaPDF. Sur Windows
et Linux, un double-clic dans le gestionnaire de fichiers lançait un autre
processus avec sa propre fenêtre; sur le Mac, il remplaçait le document ouvert,
avec une question Enregistrer / Ne pas enregistrer / Annuler en travers du
chemin, et si vous ouvriez plusieurs fichiers d'un coup, seul le premier
arrivait.

Ouvrez un deuxième PDF : il s'ouvre maintenant dans un onglet à côté du premier,
dans la même fenêtre. Ouvrir choisit plusieurs fichiers à la fois, un fichier
déposé sur la fenêtre s'ouvre aussi (une nouveauté sur le Mac et Linux, qui
n'avaient pas de glisser-déposer), et un document ouvert de l'extérieur, depuis
l'Explorateur de fichiers, le Finder, un gestionnaire de fichiers ou toute
application qui remet un PDF à MegaPDF, arrive dans la fenêtre que vous avez
déjà : dans un nouvel onglet ou, si ce fichier est déjà ouvert, sur son onglet.
Un fichier n'est jamais ouvert deux fois.

Chaque onglet est son propre document : son historique d'annulation, sa
recherche, son zoom, son outil armé, sa position de défilement, son journal de
récupération et son point de modification non enregistrée, qui est maintenant
sur l'onglet plutôt que dans la barre de titre. La barre d'outils agit sur
l'onglet que vous regardez. Un onglet en arrière-plan rend à la mémoire ses
pages dessinées et les redessine quand vous y revenez.

Fermer la fenêtre, ou quitter, demande quoi faire pour chaque document à
enregistrer, un à la fois, et un Annuler sur le deuxième laisse le premier
exactement tel quel, y compris son journal de récupération, qui n'est supprimé
qu'une fois que vous avez répondu pour ce document. Après une fermeture
inattendue, la proposition de restaurer est faite une fois par lancement, pour
chaque document qui était ouvert, et chacun revient dans son propre onglet.

Fermer l'onglet et Nouvelle fenêtre sont dans le menu Plus d'options de la
barre d'outils sur Windows, et Fermer l'onglet, Fermer la fenêtre et Nouvelle
fenêtre dans le menu Fichier sur le Mac et Linux, avec les touches propres à chaque
plateforme : Ctrl+W ferme un onglet (⌘W sur le Mac; avec un seul onglet, cela
ferme la fenêtre), Ctrl+Maj+N ouvre une nouvelle fenêtre (⌘N), et sur le Mac,
⇧⌘W ferme la fenêtre et ⌃Tab / ⌃⇧Tab passent d'un onglet à l'autre, comme le
dit le menu Fenêtre; sur Linux, Ctrl+Page précédente / Ctrl+Page suivante font
de même. Un lecteur d'écran entend chaque onglet comme son nom
de fichier, avec *modifications non enregistrées* quand il en a.

**Ce qui n'y est pas.** Aucun onglet ne se détache encore dans sa propre
fenêtre, et aucun ne passe d'une fenêtre à l'autre; le Mac utilise la barre
d'onglets de MegaPDF plutôt que les onglets de fenêtre du système. Rien ne
change sur les téléphones.

### Enregistrer sous, en Markdown

Enregistrer sous sur Windows, le Mac et Linux, Enregistrer une copie sur Android
et Exporter en Markdown sur iPhone et iPad écrivent maintenant le texte du
document dans un fichier Markdown : les titres à leur niveau, les paragraphes
remis sur une ligne avec le gras, l'italique et la chasse fixe conservés, les
listes avec leurs puces, et les champs de formulaire sous forme de liste de
tâches (une case cochée ou non) ou de `**nom :** valeur`. Une page sans couche
de texte, une numérisation, est écrite comme une note d'une ligne à sa place,
parce que MegaPDF ne fait pas de reconnaissance de caractères et ne prétend pas
en faire.

C'est une exportation, pas un enregistrement. Le fichier ne peut pas contenir
vos modifications comme un PDF le fait, alors rien ne change au document : le
PDF n'est pas touché, le point de modification non enregistrée reste s'il était
là, et l'application demande toujours d'enregistrer le PDF quand vous le
fermez. Les mots le disent partout — *Exportation…*, *Exporté*, *Impossible
d'exporter* — jamais *Enregistré*.

Où le trouver : sur Windows, le Mac et Linux, un deuxième type de fichier,
**Document Markdown**, dans la zone de dialogue Enregistrer sous, à côté de
Document PDF; sur Android, un deuxième type dans la zone de dialogue
d'Enregistrer une copie; sur iPhone et iPad, **Exporter en Markdown**, à côté
d'Enregistrer une copie dans le menu Plus, qui écrit le fichier où vous voulez
dans Fichiers.

### megapdf-cli

Le même texte sort d'un terminal : `megapdf-cli extract fichier.pdf` écrit le
texte du document sur la sortie standard ou dans `--out`, et `--format md`
écrit le Markdown ci-dessus. `--pages` prend une plage, un mot de passe vient
d'un fichier ou de l'entrée standard (jamais de la ligne de commande, où tout
autre processus pourrait le lire), et les codes de sortie veulent dire ce
qu'ils disent : 0 texte écrit, 2 fichier impossible à ouvrir, 3 mot de passe
requis ou incorrect, 5 aucune page demandée n'a de couche de texte, 130
interrompu. C'est un petit binaire natif, sans environnement d'exécution à
installer.

Linux l'a dans chaque paquet : `megapdf-cli` sur le `PATH` depuis le dépôt APT,
le `.deb` et l'`install.sh` de l'archive; `flatpak run --command=megapdf-cli
ca.electricrv.MegaPDF fichier.pdf` dans le Flatpak; `snap run megapdf.cli
extract fichier.pdf` dans le snap. Windows (x64 et arm64) et macOS (universel)
l'obtiennent en archive zip sur la page des versions GitHub :
`megapdf-cli-windows-<arch>-2.1.1.zip`, non signée, parce que MegaPDF n'a pas
de certificat de signature de code pour Windows;
`megapdf-cli-macos-universal-2.1.1.zip`, signée avec le Developer ID de
l'application et notarisée. **Il n'est dans aucun paquet de boutique** : une
application de boutique ne peut pas mettre un binaire sur votre `PATH`, sur
aucune des deux plateformes.

### Sur les téléphones : Ouvrir avec, et Partager

**MegaPDF est maintenant un lecteur de PDF, pour le téléphone.** Sur Android,
ouvrir un PDF depuis Fichiers, Drive, Gmail ou toute application qui en remet
un propose MegaPDF dans la liste Ouvrir avec. Sur iPhone et iPad, un PDF dans
Fichiers, une pièce jointe dans Mail ou un document dans la feuille de partage
de n'importe quelle application s'ouvre dans MegaPDF directement de là.
Jusqu'ici, la seule entrée était Ouvrir un PDF à l'accueil, par le sélecteur.
Si un document avec des modifications non enregistrées est déjà ouvert, le
téléphone demande (Enregistrer, Abandonner ou Annuler) avant de changer de
document, parce que l'autre option était de perdre ces modifications en silence.

**Partager**, dans le menu Plus des deux téléphones, remet le document à la
feuille de partage du système : Mail, Messages, AirDrop, Enregistrer dans
Fichiers, Drive, ce que vous avez. Si le document a des modifications non
enregistrées, il le dit d'abord — *Ce document contient des modifications non
enregistrées. Elles ne feront pas partie de la copie partagée si vous ne
l'enregistrez pas d'abord* — et propose **Enregistrer**, **Partager sans
enregistrer** ou **Annuler**. Le bouton du milieu allait dire Abandonner, et ne
le dit pas : appuyer dessus n'abandonne rien, le dernier fichier enregistré est envoyé et
vos modifications restent ouvertes, et un bouton qui dit Abandonner sans rien
détruire apprendrait aux gens qu'Abandonner est sans danger, ce qui n'est pas
le cas dans la zone de dialogue Fermer à côté.

### Sous le capot : le texte dont le Markdown est fait

L'exportation Markdown et `megapdf-cli` lisent la même structure que le moteur
déduit d'une page (quel texte est un titre, ce qui est un paragraphe, où sont
les colonnes, ce qui n'est qu'en-tête ou pied de page), et cette déduction a été mesurée
contre tout le corpus de test (4 158 vrais documents, 22 180 pages) et corrigée
quatre fois dans cette version. Rien de tout cela n'est une fonction visible;
tout cela est ce qui fait que le Markdown se lit comme le document.

- **Des mots étaient coupés.** L'écart que le moteur utilisait pour décider que
  deux caractères appartiennent à des mots différents était calibré pour une
  boîte serrée que PDFium ne rapporte pas vraiment, alors de vrais mots
  sortaient en morceaux. Recalibré d'après le corpus, avec les exposants et le
  texte pivoté traités pour ce qu'ils sont (#363). La fidélité des mots par
  rapport à la couche de texte brute est passée de 0,93 à 0,998 sur tout le
  corpus, et l'exportation est désormais conditionnée à ce chiffre.
- **Un trait d'union en fin de ligne recolle le mot**, comme le moteur le
  faisait déjà, seulement quand PDFium dit que c'est une césure, jamais pour
  *bien-aimé* au milieu d'une ligne (#360).
- **Un tableau n'est pas une liste de titres.** Un texte en gras à la taille du
  texte courant est un titre quand il est seul, comme un vrai titre. Plusieurs
  à la suite (les étiquettes de colonnes d'un tableau, les noms de champs d'un
  formulaire, l'en-tête d'un courriel) n'en sont pas, pas plus qu'un chiffre nu
  comme un montant en dollars. Ce sont de nouveau des paragraphes
  (#375) : 62 % de faux titres en moins sur le corpus, aucun vrai titre perdu
  sur aucun fichier de test.
- **Une page dont l'ordre de lecture saute** (une page titre, une couverture,
  une page que la mise en page n'ordonne vraiment pas) porte maintenant un
  indice de confiance plus bas, pour que l'exportation puisse le dire plutôt que
  de présenter le désordre comme de la prose (#384).

### Mac

- Les onglets, comme ci-dessus, dans l'idiome du Mac : ⌘W, ⇧⌘W, ⌘N, ⌃Tab et
  ⌃⇧Tab, les menus Fichier et Fenêtre, les fichiers déposés sur la fenêtre, et
  une ouverture depuis le Finder ou le Dock, d'un fichier ou de plusieurs, qui
  arrive dans la fenêtre en cours sous forme d'onglets. Vérifié sur le Mac mini
  avec `open -a`, le chemin que le Finder emprunte, avec quatre documents venus
  de trois ouvertures distinctes qui finissent en quatre onglets dans un seul
  processus.
- La zone de dialogue Enregistrer sous propose Document Markdown.
- Quitter avec plusieurs fenêtres ouvertes interroge chacune à son tour, et un
  Annuler dans la deuxième ne laisse plus la première croire qu'on lui avait
  déjà répondu, ce qui lui aurait permis de se fermer sans demander, plus tard,
  pour toute la vie de la fenêtre (#145).
- `megapdf-cli` est l'archive zip séparée ci-dessus, pas une partie de
  l'application de la boutique.

### Windows

- Les onglets, comme ci-dessus : Ctrl+W et Ctrl+Maj+N, Fermer l'onglet et
  Nouvelle fenêtre dans le menu Plus d'options, Ouvrir à sélection multiple,
  le glisser-déposer, et un double-clic dans l'Explorateur de fichiers qui
  arrive dans la fenêtre en cours, plusieurs à la fois, chacun dans son onglet,
  aucun perdu. Vérifié sur GPD-DAVE, où le code d'instance unique s'est révélé
  n'ouvrir rien du tout en silence (une conversion WinRT qui compile et ne
  correspond jamais) et a été corrigé avant la sortie.
- Enregistrer sous propose Document Markdown.
- Clavier (#2) : cocher une case dessinée annonce *Case cochée* plutôt que de
  nouveau *Case à cocher*; la poignée de redimensionnement et le ✕ d'une
  signature sélectionnée occupent une zone de 40 × 40, sans changement
  visible; un déplacement aux flèches est conservé quand un clic suit dans
  l'instant où il le perdait auparavant.
- `megapdf-cli` est l'archive zip séparée ci-dessus, pas une partie du paquet
  de la boutique.

### Linux

- Les onglets, comme ci-dessus : Ctrl+W, Ctrl+Maj+N, Ctrl+Page précédente /
  Ctrl+Page suivante, le glisser-déposer, et un deuxième `megapdf fichier.pdf`, depuis l'Ouvrir
  avec d'un gestionnaire de fichiers ou un terminal, remis à l'instance en
  cours par un socket dans le répertoire d'exécution et ouvert dans un onglet,
  dans le Flatpak et le snap aussi. L'entrée de bureau accepte maintenant
  plusieurs fichiers (`%F`).
- Enregistrer sous propose Document Markdown.
- `megapdf-cli` est dans chaque paquet, sur le `PATH` ou accessible en une commande
  (ci-dessus).
- Le snap se construit de nouveau (#352).

### Android

- Ouvrir avec : MegaPDF est proposé quand un PDF est ouvert depuis Fichiers,
  Drive, Gmail ou toute application qui en remet un. Si un document avec des
  modifications non enregistrées est ouvert, la même question Enregistrer /
  Abandonner / Annuler que Fermer vient d'abord.
- Partager, dans le menu Plus, par la feuille de partage du système, avec
  Enregistrer / Partager sans enregistrer / Annuler quand il y a des
  modifications non enregistrées.
- Enregistrer une copie propose Markdown comme deuxième type dans sa zone de
  dialogue; un nom en `.md` est l'exportation, un nom en `.pdf` est la copie
  qu'il a toujours été.

### iPhone et iPad

- Ouvrir avec : un PDF dans Fichiers, une pièce jointe dans Mail ou un document
  dans la feuille de partage de n'importe quelle application s'ouvre dans
  MegaPDF, qui se déclare maintenant lecteur de PDF. Si un document avec des
  modifications non enregistrées est ouvert, il demande d'abord.
- Partager, dans le menu Plus, par la feuille de partage, avec une fenêtre
  contextuelle ancrée au bouton Plus sur iPad, là où une fenêtre sans ancrage
  aurait planté.
- Exporter en Markdown, à côté d'Enregistrer une copie dans le menu Plus, par le
  sélecteur de Fichiers.
- L'iPad affiche encore la disposition de l'iPhone; sa propre disposition
  viendra plus tard (#172).

---

## Français (France)

> Derived from the Canadian French above by `check_copy.py` in this folder,
> with the same rules `tools/gen_strings.py fr-fr` applies to the app's
> catalogues: *courriel* → *e-mail* (the one word in this text the derivation
> table changes), and a non-breaking space before `?`, `!` and `;` as well as
> `:`. Do not edit below this note; edit the Canadian section and run the script.

### Des onglets

Jusqu'ici, un deuxième document était une deuxième copie de MegaPDF. Sur Windows
et Linux, un double-clic dans le gestionnaire de fichiers lançait un autre
processus avec sa propre fenêtre ; sur le Mac, il remplaçait le document ouvert,
avec une question Enregistrer / Ne pas enregistrer / Annuler en travers du
chemin, et si vous ouvriez plusieurs fichiers d'un coup, seul le premier
arrivait.

Ouvrez un deuxième PDF : il s'ouvre maintenant dans un onglet à côté du premier,
dans la même fenêtre. Ouvrir choisit plusieurs fichiers à la fois, un fichier
déposé sur la fenêtre s'ouvre aussi (une nouveauté sur le Mac et Linux, qui
n'avaient pas de glisser-déposer), et un document ouvert de l'extérieur, depuis
l'Explorateur de fichiers, le Finder, un gestionnaire de fichiers ou toute
application qui remet un PDF à MegaPDF, arrive dans la fenêtre que vous avez
déjà : dans un nouvel onglet ou, si ce fichier est déjà ouvert, sur son onglet.
Un fichier n'est jamais ouvert deux fois.

Chaque onglet est son propre document : son historique d'annulation, sa
recherche, son zoom, son outil armé, sa position de défilement, son journal de
récupération et son point de modification non enregistrée, qui est maintenant
sur l'onglet plutôt que dans la barre de titre. La barre d'outils agit sur
l'onglet que vous regardez. Un onglet en arrière-plan rend à la mémoire ses
pages dessinées et les redessine quand vous y revenez.

Fermer la fenêtre, ou quitter, demande quoi faire pour chaque document à
enregistrer, un à la fois, et un Annuler sur le deuxième laisse le premier
exactement tel quel, y compris son journal de récupération, qui n'est supprimé
qu'une fois que vous avez répondu pour ce document. Après une fermeture
inattendue, la proposition de restaurer est faite une fois par lancement, pour
chaque document qui était ouvert, et chacun revient dans son propre onglet.

Fermer l'onglet et Nouvelle fenêtre sont dans le menu Plus d'options de la
barre d'outils sur Windows, et Fermer l'onglet, Fermer la fenêtre et Nouvelle
fenêtre dans le menu Fichier sur le Mac et Linux, avec les touches propres à chaque
plateforme : Ctrl+W ferme un onglet (⌘W sur le Mac ; avec un seul onglet, cela
ferme la fenêtre), Ctrl+Maj+N ouvre une nouvelle fenêtre (⌘N), et sur le Mac,
⇧⌘W ferme la fenêtre et ⌃Tab / ⌃⇧Tab passent d'un onglet à l'autre, comme le
dit le menu Fenêtre ; sur Linux, Ctrl+Page précédente / Ctrl+Page suivante font
de même. Un lecteur d'écran entend chaque onglet comme son nom
de fichier, avec *modifications non enregistrées* quand il en a.

**Ce qui n'y est pas.** Aucun onglet ne se détache encore dans sa propre
fenêtre, et aucun ne passe d'une fenêtre à l'autre ; le Mac utilise la barre
d'onglets de MegaPDF plutôt que les onglets de fenêtre du système. Rien ne
change sur les téléphones.

### Enregistrer sous, en Markdown

Enregistrer sous sur Windows, le Mac et Linux, Enregistrer une copie sur Android
et Exporter en Markdown sur iPhone et iPad écrivent maintenant le texte du
document dans un fichier Markdown : les titres à leur niveau, les paragraphes
remis sur une ligne avec le gras, l'italique et la chasse fixe conservés, les
listes avec leurs puces, et les champs de formulaire sous forme de liste de
tâches (une case cochée ou non) ou de `**nom :** valeur`. Une page sans couche
de texte, une numérisation, est écrite comme une note d'une ligne à sa place,
parce que MegaPDF ne fait pas de reconnaissance de caractères et ne prétend pas
en faire.

C'est une exportation, pas un enregistrement. Le fichier ne peut pas contenir
vos modifications comme un PDF le fait, alors rien ne change au document : le
PDF n'est pas touché, le point de modification non enregistrée reste s'il était
là, et l'application demande toujours d'enregistrer le PDF quand vous le
fermez. Les mots le disent partout — *Exportation…*, *Exporté*, *Impossible
d'exporter* — jamais *Enregistré*.

Où le trouver : sur Windows, le Mac et Linux, un deuxième type de fichier,
**Document Markdown**, dans la zone de dialogue Enregistrer sous, à côté de
Document PDF ; sur Android, un deuxième type dans la zone de dialogue
d'Enregistrer une copie ; sur iPhone et iPad, **Exporter en Markdown**, à côté
d'Enregistrer une copie dans le menu Plus, qui écrit le fichier où vous voulez
dans Fichiers.

### megapdf-cli

Le même texte sort d'un terminal : `megapdf-cli extract fichier.pdf` écrit le
texte du document sur la sortie standard ou dans `--out`, et `--format md`
écrit le Markdown ci-dessus. `--pages` prend une plage, un mot de passe vient
d'un fichier ou de l'entrée standard (jamais de la ligne de commande, où tout
autre processus pourrait le lire), et les codes de sortie veulent dire ce
qu'ils disent : 0 texte écrit, 2 fichier impossible à ouvrir, 3 mot de passe
requis ou incorrect, 5 aucune page demandée n'a de couche de texte, 130
interrompu. C'est un petit binaire natif, sans environnement d'exécution à
installer.

Linux l'a dans chaque paquet : `megapdf-cli` sur le `PATH` depuis le dépôt APT,
le `.deb` et l'`install.sh` de l'archive ; `flatpak run --command=megapdf-cli
ca.electricrv.MegaPDF fichier.pdf` dans le Flatpak ; `snap run megapdf.cli
extract fichier.pdf` dans le snap. Windows (x64 et arm64) et macOS (universel)
l'obtiennent en archive zip sur la page des versions GitHub :
`megapdf-cli-windows-<arch>-2.1.1.zip`, non signée, parce que MegaPDF n'a pas
de certificat de signature de code pour Windows ;
`megapdf-cli-macos-universal-2.1.1.zip`, signée avec le Developer ID de
l'application et notarisée. **Il n'est dans aucun paquet de boutique** : une
application de boutique ne peut pas mettre un binaire sur votre `PATH`, sur
aucune des deux plateformes.

### Sur les téléphones : Ouvrir avec, et Partager

**MegaPDF est maintenant un lecteur de PDF, pour le téléphone.** Sur Android,
ouvrir un PDF depuis Fichiers, Drive, Gmail ou toute application qui en remet
un propose MegaPDF dans la liste Ouvrir avec. Sur iPhone et iPad, un PDF dans
Fichiers, une pièce jointe dans Mail ou un document dans la feuille de partage
de n'importe quelle application s'ouvre dans MegaPDF directement de là.
Jusqu'ici, la seule entrée était Ouvrir un PDF à l'accueil, par le sélecteur.
Si un document avec des modifications non enregistrées est déjà ouvert, le
téléphone demande (Enregistrer, Abandonner ou Annuler) avant de changer de
document, parce que l'autre option était de perdre ces modifications en silence.

**Partager**, dans le menu Plus des deux téléphones, remet le document à la
feuille de partage du système : Mail, Messages, AirDrop, Enregistrer dans
Fichiers, Drive, ce que vous avez. Si le document a des modifications non
enregistrées, il le dit d'abord — *Ce document contient des modifications non
enregistrées. Elles ne feront pas partie de la copie partagée si vous ne
l'enregistrez pas d'abord* — et propose **Enregistrer**, **Partager sans
enregistrer** ou **Annuler**. Le bouton du milieu allait dire Abandonner, et ne
le dit pas : appuyer dessus n'abandonne rien, le dernier fichier enregistré est envoyé et
vos modifications restent ouvertes, et un bouton qui dit Abandonner sans rien
détruire apprendrait aux gens qu'Abandonner est sans danger, ce qui n'est pas
le cas dans la zone de dialogue Fermer à côté.

### Sous le capot : le texte dont le Markdown est fait

L'exportation Markdown et `megapdf-cli` lisent la même structure que le moteur
déduit d'une page (quel texte est un titre, ce qui est un paragraphe, où sont
les colonnes, ce qui n'est qu'en-tête ou pied de page), et cette déduction a été mesurée
contre tout le corpus de test (4 158 vrais documents, 22 180 pages) et corrigée
quatre fois dans cette version. Rien de tout cela n'est une fonction visible ;
tout cela est ce qui fait que le Markdown se lit comme le document.

- **Des mots étaient coupés.** L'écart que le moteur utilisait pour décider que
  deux caractères appartiennent à des mots différents était calibré pour une
  boîte serrée que PDFium ne rapporte pas vraiment, alors de vrais mots
  sortaient en morceaux. Recalibré d'après le corpus, avec les exposants et le
  texte pivoté traités pour ce qu'ils sont (#363). La fidélité des mots par
  rapport à la couche de texte brute est passée de 0,93 à 0,998 sur tout le
  corpus, et l'exportation est désormais conditionnée à ce chiffre.
- **Un trait d'union en fin de ligne recolle le mot**, comme le moteur le
  faisait déjà, seulement quand PDFium dit que c'est une césure, jamais pour
  *bien-aimé* au milieu d'une ligne (#360).
- **Un tableau n'est pas une liste de titres.** Un texte en gras à la taille du
  texte courant est un titre quand il est seul, comme un vrai titre. Plusieurs
  à la suite (les étiquettes de colonnes d'un tableau, les noms de champs d'un
  formulaire, l'en-tête d'un e-mail) n'en sont pas, pas plus qu'un chiffre nu
  comme un montant en dollars. Ce sont de nouveau des paragraphes
  (#375) : 62 % de faux titres en moins sur le corpus, aucun vrai titre perdu
  sur aucun fichier de test.
- **Une page dont l'ordre de lecture saute** (une page titre, une couverture,
  une page que la mise en page n'ordonne vraiment pas) porte maintenant un
  indice de confiance plus bas, pour que l'exportation puisse le dire plutôt que
  de présenter le désordre comme de la prose (#384).

### Mac

- Les onglets, comme ci-dessus, dans l'idiome du Mac : ⌘W, ⇧⌘W, ⌘N, ⌃Tab et
  ⌃⇧Tab, les menus Fichier et Fenêtre, les fichiers déposés sur la fenêtre, et
  une ouverture depuis le Finder ou le Dock, d'un fichier ou de plusieurs, qui
  arrive dans la fenêtre en cours sous forme d'onglets. Vérifié sur le Mac mini
  avec `open -a`, le chemin que le Finder emprunte, avec quatre documents venus
  de trois ouvertures distinctes qui finissent en quatre onglets dans un seul
  processus.
- La zone de dialogue Enregistrer sous propose Document Markdown.
- Quitter avec plusieurs fenêtres ouvertes interroge chacune à son tour, et un
  Annuler dans la deuxième ne laisse plus la première croire qu'on lui avait
  déjà répondu, ce qui lui aurait permis de se fermer sans demander, plus tard,
  pour toute la vie de la fenêtre (#145).
- `megapdf-cli` est l'archive zip séparée ci-dessus, pas une partie de
  l'application de la boutique.

### Windows

- Les onglets, comme ci-dessus : Ctrl+W et Ctrl+Maj+N, Fermer l'onglet et
  Nouvelle fenêtre dans le menu Plus d'options, Ouvrir à sélection multiple,
  le glisser-déposer, et un double-clic dans l'Explorateur de fichiers qui
  arrive dans la fenêtre en cours, plusieurs à la fois, chacun dans son onglet,
  aucun perdu. Vérifié sur GPD-DAVE, où le code d'instance unique s'est révélé
  n'ouvrir rien du tout en silence (une conversion WinRT qui compile et ne
  correspond jamais) et a été corrigé avant la sortie.
- Enregistrer sous propose Document Markdown.
- Clavier (#2) : cocher une case dessinée annonce *Case cochée* plutôt que de
  nouveau *Case à cocher* ; la poignée de redimensionnement et le ✕ d'une
  signature sélectionnée occupent une zone de 40 × 40, sans changement
  visible ; un déplacement aux flèches est conservé quand un clic suit dans
  l'instant où il le perdait auparavant.
- `megapdf-cli` est l'archive zip séparée ci-dessus, pas une partie du paquet
  de la boutique.

### Linux

- Les onglets, comme ci-dessus : Ctrl+W, Ctrl+Maj+N, Ctrl+Page précédente /
  Ctrl+Page suivante, le glisser-déposer, et un deuxième `megapdf fichier.pdf`, depuis l'Ouvrir
  avec d'un gestionnaire de fichiers ou un terminal, remis à l'instance en
  cours par un socket dans le répertoire d'exécution et ouvert dans un onglet,
  dans le Flatpak et le snap aussi. L'entrée de bureau accepte maintenant
  plusieurs fichiers (`%F`).
- Enregistrer sous propose Document Markdown.
- `megapdf-cli` est dans chaque paquet, sur le `PATH` ou accessible en une commande
  (ci-dessus).
- Le snap se construit de nouveau (#352).

### Android

- Ouvrir avec : MegaPDF est proposé quand un PDF est ouvert depuis Fichiers,
  Drive, Gmail ou toute application qui en remet un. Si un document avec des
  modifications non enregistrées est ouvert, la même question Enregistrer /
  Abandonner / Annuler que Fermer vient d'abord.
- Partager, dans le menu Plus, par la feuille de partage du système, avec
  Enregistrer / Partager sans enregistrer / Annuler quand il y a des
  modifications non enregistrées.
- Enregistrer une copie propose Markdown comme deuxième type dans sa zone de
  dialogue ; un nom en `.md` est l'exportation, un nom en `.pdf` est la copie
  qu'il a toujours été.

### iPhone et iPad

- Ouvrir avec : un PDF dans Fichiers, une pièce jointe dans Mail ou un document
  dans la feuille de partage de n'importe quelle application s'ouvre dans
  MegaPDF, qui se déclare maintenant lecteur de PDF. Si un document avec des
  modifications non enregistrées est ouvert, il demande d'abord.
- Partager, dans le menu Plus, par la feuille de partage, avec une fenêtre
  contextuelle ancrée au bouton Plus sur iPad, là où une fenêtre sans ancrage
  aurait planté.
- Exporter en Markdown, à côté d'Enregistrer une copie dans le menu Plus, par le
  sélecteur de Fichiers.
- L'iPad affiche encore la disposition de l'iPhone ; sa propre disposition
  viendra plus tard (#172).

---
