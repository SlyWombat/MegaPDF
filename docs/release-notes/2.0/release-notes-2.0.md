# MegaPDF 2.0 — release notes

The long form: everything 2.0 changes, per platform, for the GitHub release body
and the website. The store fields are the short versions of this, in the other
files in this folder.

Written from `git log ios-v1.7.0..ee2751d` (188 commits) and, for Android,
`git log android-v1.2.0..ee2751d` — Android's public version is 1.2.0, so it
gains 1.3 through 1.7 in the same release.

*The French is signed off: a francophone reviewed it (Dave, 2026-09-18), and the
Fable review's recommendations are applied, the redaction section included
(#242) — see [README](README.md).*

---

## English

### Fix the document's own text, far more often

MegaPDF has always refused a text change it could not make without disturbing
the rest of the page — it would rather say no than silently move something you
did not touch. 2.0 makes it right far more often instead of refusing: over a
battery of 22,267 real edits across the test corpus, all 112 edits that were
refused in 1.7 now go through, with no regression anywhere else and no page
disturbed. That took eleven fixes to the PDF writer underneath (patches 0015 to
0025 of MegaPDF's PDFium branch): form resources, stroked text under a scaled
transform, patterns, glyph clips, grey colours, JPEG scaling, and more.

When a change genuinely cannot be made, MegaPDF now says which part of the page
is in the way rather than giving one general refusal.

A line the document draws twice — the old trick for faking bold, outlines and
shadows — now edits and deletes cleanly instead of leaving a ghost behind, even
when the copies are far apart in the content stream. And an edit keeps the
document's own font only if that font can draw every character you typed;
otherwise MegaPDF substitutes a similar standard font and tells you it did.

**New on iPhone, iPad and Android:** correcting the document's own text at all.
Tap a line, retype it, and Undo puts the original back exactly.

### Redact, and it really is gone

Whiteout covers, and has only ever covered: what sits under it stays in the
file, where another app can select it, copy it, search for it. That is the
well-documented way a redaction fails, and it is why 2.0 has a second tool.

Mark what has to come out — a name, an address, a picture — and MegaPDF takes it
out of the file rather than covering it over. What was underneath is gone: no
other app can copy it or search for it.

Drag a box, or drag across text to mark the words. A mark is translucent with an
outline, so you can still read what you are about to remove, and until you save
it is only a mark: move it, select it, undo it. **Nothing is removed until you
save.** Save then asks — *Redaction permanently removes the marked content. This
can't be undone after saving* — and offers **Save as a copy**, named
*…-redacted*, as the default. Afterwards a short summary says what went:
*1 area redacted: 13 characters*.

What comes out is the content, not the picture of it. Letters are removed from
the page's own text, whole letters at a time, and a line only partly covered is
rewritten so the words beside it do not move. Copies of the same text drawn
underneath go too. Inside a picture it is the pixels themselves that are
overwritten before the picture is re-encoded, a scan included. Vector drawing,
annotations, form fields and links reaching into the area are removed, and so
are the places text can hide off the page: the document's information,
bookmarks, the text a screen reader would read, page labels. The undo history
and the crash-recovery journal keep nothing of it either. What is left is a
plain black box with no annotation behind it.

None of that is taken on trust. The tests save the file and then go looking for
what was removed — in the text the engine extracts, in every decompressed
stream, in the raw bytes as ASCII, as UTF-16 and as PDF hex digits, in the
document information, the bookmarks, every annotation and every link address, and
in the pixels inside the area — over the redaction fixtures and across a whole
corpus, where the requirement is 0 leaks, 0 crashes, 0 hangs and no change to
the page outside the areas.

**It refuses rather than half-finishes.** Some documents draw part of a page from
a shared block MegaPDF cannot take apart safely. Marking inside one gets
*Nothing was removed* and the reason: your marks stay where they are and your
file is untouched. A file that looks redacted and is not would be worse than no
feature at all. A document whose owner does not allow changes cannot be redacted
either, and the tool says so instead of failing quietly.

**Whiteout stays, and now says what it is.** On Windows, the Mac and Linux it is
still the quick way to cover something on a form — *Whiteout* on Windows,
*Cover* on the Mac and Linux — and its tooltip and its first-use hint now say
that it covers without removing, and send you to Redact for the rest. iPhone,
iPad and Android have Redact and no whiteout, so there is nothing there to
confuse it with.

### Nothing gets lost

- Closing, quitting or opening another file with unsaved changes always asks
  first — on every platform, including the Mac, which used to discard them.
- A save that fails can no longer leave the original empty. The file you were
  sent stays exactly as it was until a complete, verified copy is ready to take
  its place.
- Edits made while a save is running are no longer counted as saved.
- Editing and Close are locked during a save or a password change, rather than
  racing it.
- Crash recovery keeps each running instance's journal separate, and never
  deletes one that another instance is using.

### It tells you what it is doing

Opening, saving, checking the saved file, searching, checking a page, applying a
change, making a smaller copy, preparing to print, restoring your edits: each
announces itself while it runs — a strip under the toolbar for work on the whole
document, a small spinner on the page for work on one line.

Nothing quicker than half a second shows an indicator at all, and an indicator
that does appear stays at least a third of a second, so nothing ever blinks. The
labels are polite live regions, so a screen reader reads them without stealing
focus. On Windows, printing has moved off the UI thread; on the Mac, so has all
engine work — the window stays responsive throughout.

### Protected PDFs

- Open a PDF that asks for a password, on every platform and every standard
  security handler (RC4-40, RC4-128, AES-128, AES-256), including non-ASCII
  passwords.
- If the owner restricted what may be changed, MegaPDF honours it: the tools the
  owner disallowed are disabled, a notice explains why, and an attempted change
  says so rather than failing silently.
- The owner password unlocks a restricted document for the rest of the session.
- Set a password on a document, change it, or remove it.
- Nothing typed is kept: what you type goes straight to the engine and is gone
  with the dialog. Crash recovery keeps nothing from a protected document in
  the clear.

### One row of tools

Every platform's toolbar is now a single row. The size and font pickers appear
beside the tool they belong to and go away with it; zoom is one control instead
of four; and everything else lives under **More**. On Windows the row overflows
into the CommandBar's own menu as the window narrows. On the Mac the menu bar
carries the same commands with their shortcuts, and Cmd+S does nothing when
nothing has changed. On the phones the everyday tools moved to a bottom bar
where a thumb reaches them, and the file commands moved to More at the top.

### Very large PDFs

MegaPDF used to read a whole file into memory to open it. It now reads on demand
through the file itself:

| | 1.7 | 2.0 |
|---|---|---|
| 2.5 GB, 1,000 pages: open | 62 s, 5.2 GB | 4.1 ms, 6 MB |
| 1 GB file, peak memory | 6,171 MB | 1,058 MB |
| a page with one 20,000 × 15,000 image | re-decoded on every render and edit | decoded once, kept at the size the render needs |
| 20,000 text objects on one page | 21.8 s to read | linear in the number of objects |
| CMYK / ICC colour-managed pages | — | about 6× faster |

Files of 2 GB and over open at all now, and a file too big for the device says
so plainly instead of failing.

`/UserUnit` is honoured throughout — page size, zoom, placement, print, shrink
and the layout guard's tolerances — so a 10-metre banner measures, prints and
edits at its real size rather than at a 72-dpi fiction.

### Warned before a page changes under you

Some changes make the PDF writer regenerate a whole page, and on a page it
cannot rewrite faithfully that can alter parts you never touched. Before the
first such change on a page, MegaPDF checks the page and, if it would change,
asks: **Change this page?** Continue or Cancel. It asks once per page, the check
starts early so the answer is usually ready, and if the check runs past a 1.5
second budget the change is applied without a warning rather than making you
wait.

### Mac

- A PDF opened from the Finder saves back to that file, instead of asking where
  to put it.
- The first view fits the whole page.
- The page is drawn at the display's full resolution.
- The **More** and zoom menus show their items (they opened empty in a real
  window).
- VoiceOver names the toolbar, the scroll bar's buttons and track, the font and
  size pickers and a page's checkboxes; the pickers' empty wrapper is gone from
  the tree.
- Tools ▸ Text font and Text size are greyed out outside Add text rather than
  offering an empty submenu.
- Zoom in on Cmd+Shift+= as well as Cmd+=.
- An inline editor's starting text is no longer an undo step of its own.
- The print panel says why it wants to look for devices on the local network.

### Windows

- Tab moves through a page's fields, checkboxes and editable lines; Enter or
  Space activates the one you are on.
- The unsaved-changes prompt also appears before opening another file.

### Android

Android was at 1.2.0, so 2.0 also brings everything the other platforms got in
1.3 through 1.7:

- **French**, following the device language, or chosen per app under
  Settings ▸ Apps ▸ MegaPDF ▸ Language on Android 13 and later.
- **Correct the document's own text.**
- **Type a name to make a signature**, a third way alongside drawing and
  photographing one.
- **The signature library is a sheet**, showing each signature's real ink on its
  own card, with rename and delete on the card.
- Documents open from their descriptor and are read on demand.

### iPhone and iPad

- Correct the document's own text.
- Redaction.
- Protected PDFs.
- The bottom toolbar and the More menu.
- Busy feedback and the page warning.
- Large files: a 2.5 GB document opens in a fifth of a second at about 110 MB.

---

## Français (Canada)

### Corriger le texte du document, bien plus souvent

MegaPDF a toujours refusé une modification de texte qu'il ne pouvait pas faire
sans perturber le reste de la page : il préfère dire non plutôt que de déplacer
en silence quelque chose que vous n'avez pas touché. La version 2.0 la réussit
bien plus souvent au lieu de refuser : sur une série de 22 267 modifications
réelles dans le corpus de test, les 112 modifications refusées par la 1.7
passent toutes, sans aucune régression ailleurs et sans page dérangée. Il a
fallu onze correctifs au moteur d'écriture PDF (les correctifs 0015 à 0025 de la
branche PDFium de MegaPDF) : ressources de formulaire, texte tracé en contour sous une
transformation mise à l'échelle, motifs, glyphes servant de découpe, couleurs
grises, mise à l'échelle JPEG, et d'autres.

Quand une modification est vraiment impossible, MegaPDF indique maintenant
quelle partie de la page fait obstacle, au lieu d'un seul refus général.

Une ligne que le document dessine deux fois — la vieille astuce pour imiter le
gras, les contours et les ombres — se modifie et se supprime maintenant
proprement, sans laisser de fantôme, même quand les copies sont éloignées l'une
de l'autre dans le contenu. Et une modification garde la police du document
seulement si cette police peut dessiner tous les caractères tapés; sinon,
MegaPDF utilise une police standard semblable et vous le dit.

**Nouveau sur iPhone, iPad et Android :** pouvoir corriger le texte du document,
tout court. Touchez une ligne, retapez-la, et Annuler remet l'original
exactement.

### Caviardez, et c'est parti pour de bon

Le correcteur masque, et n'a jamais fait que masquer : ce qui se trouve dessous
reste dans le fichier, où une autre application peut le sélectionner, le copier,
le retrouver par une recherche. C'est la façon bien connue dont un caviardage rate, et c'est
pourquoi la 2.0 ajoute un second outil.

Marquez ce qui doit disparaître — un nom, une adresse, une image — et MegaPDF le
retire du fichier au lieu de le recouvrir. Ce qui était dessous n'y est plus :
aucune autre application ne peut le copier ni le retrouver par une recherche.

Glissez pour tracer une zone, ou glissez sur du texte pour en marquer les mots.
Une marque est translucide, avec un contour : vous lisez encore ce que vous allez
retirer, et tant que vous n'enregistrez pas, ce n'est qu'une marque —
déplacez-la, sélectionnez-la, annulez-la. **Rien n'est retiré tant que vous
n'enregistrez pas.** L'enregistrement pose alors la question — *Le caviardage
retire définitivement le contenu marqué. Impossible d'annuler après
l'enregistrement.* — et propose **Enregistrer une copie**, nommée
*…-caviardé*, comme choix par défaut. Ensuite, un court résumé dit ce qui est
parti : *1 zone caviardée : 13 caractères*.

Ce qui sort, c'est le contenu, pas son image. Les lettres sont retirées du texte
de la page, des lettres entières à la fois, et une ligne qui n'est couverte
qu'en partie est réécrite pour que les mots d'à côté ne bougent pas. Les copies
du même texte dessinées dessous partent aussi. Dans une image, ce sont les
pixels eux-mêmes qui sont réécrits avant que l'image soit ré-encodée, une
numérisation comprise. Les tracés vectoriels, les annotations, les champs de
formulaire et les liens qui touchent la zone sont retirés, et les endroits où le
texte peut se cacher hors de la page partent avec : les informations du document,
les signets, le texte que lirait un lecteur d'écran, les étiquettes de page.
L'historique d'annulation et le journal de récupération n'en gardent rien non
plus. Il reste un rectangle noir uni, sans annotation derrière.

Rien de cela n'est tenu pour acquis. Les tests enregistrent le fichier, puis
vont chercher ce qui a été retiré — dans le texte que le moteur extrait, dans
chaque flux décompressé, dans les octets bruts en ASCII, en UTF-16 et en chiffres
hexadécimaux PDF, dans les informations du document, les signets, chaque annotation
et chaque adresse de lien, et dans les pixels à l'intérieur de la zone — sur les
fichiers d'essai du caviardage et sur tout un corpus, où l'exigence est de 0
fuite, 0 plantage, 0 blocage et aucun changement de la page hors des zones.

**Il refuse plutôt que de faire les choses à moitié.** Certains documents
dessinent une partie de la page à partir d'un bloc partagé que MegaPDF ne peut
pas défaire sans risque. Marquer à l'intérieur donne *Rien n'a été retiré* et la
raison : vos marques restent où elles sont et votre fichier n'est pas touché. Un
fichier qui a l'air caviardé sans l'être serait pire que pas de fonction du
tout. Un document dont le propriétaire n'autorise pas les modifications ne peut
pas être caviardé non plus, et l'outil le dit au lieu d'échouer en silence.

**Le correcteur reste, et dit maintenant ce qu'il est.** Sous Windows, sur le
Mac et sous Linux, il demeure la façon rapide de masquer quelque chose sur un
formulaire — *Correcteur* sous Windows, *Masquer* sur le Mac et sous Linux — et
son infobulle et son conseil de première utilisation disent maintenant qu'il
masque sans rien retirer, et renvoient à Caviarder pour le reste. L'iPhone,
l'iPad et Android ont Caviarder et pas de correcteur : il n'y a rien là-bas avec
quoi le confondre.

### Rien ne se perd

- Fermer, quitter ou ouvrir un autre fichier alors que des modifications ne sont
  pas enregistrées pose toujours la question d'abord — sur toutes les
  plateformes, y compris le Mac, qui les abandonnait.
- Un enregistrement raté ne peut plus vider l'original. Le fichier qu'on vous a
  envoyé reste exactement tel quel tant qu'une copie complète et vérifiée n'est
  pas prête à le remplacer.
- Les modifications faites pendant un enregistrement ne sont plus comptées comme
  enregistrées.
- Modifier et Fermer sont bloqués pendant un enregistrement ou un changement de
  mot de passe, au lieu d'entrer en conflit avec lui.
- La récupération après une fermeture inattendue garde le journal de chaque
  instance séparé, et n'en supprime jamais un qu'une autre instance utilise.

### Il vous dit ce qu'il fait

Ouverture, enregistrement, vérification du fichier enregistré, recherche,
vérification d'une page, application de la modification, création d'une copie
réduite, préparation de l'impression, restauration de vos modifications : chaque
tâche s'annonce pendant qu'elle travaille — une bande sous la barre d'outils
pour le travail sur tout le document, un petit indicateur sur la page pour le
travail sur une ligne.

Rien de plus rapide qu'une demi-seconde n'affiche d'indicateur, et un indicateur
qui apparaît reste au moins un tiers de seconde, alors rien ne clignote jamais.
Les libellés sont des zones dynamiques « polies » au sens des lecteurs d'écran :
un lecteur d'écran les lit sans voler le focus. Sous Windows, l'impression est
passée hors du fil d'exécution de l'interface;
sur le Mac, tout le travail du moteur aussi — la fenêtre reste réactive du début
à la fin.

### PDF protégés

- Ouvrez un PDF qui demande un mot de passe, sur toutes les plateformes et avec
  tous les gestionnaires de sécurité standards (RC4-40, RC4-128, AES-128,
  AES-256), y compris les mots de passe non ASCII.
- Si le propriétaire a restreint les modifications, MegaPDF le respecte : les
  outils qu'il n'autorise pas sont désactivés, un avis explique pourquoi, et une
  modification tentée le dit au lieu d'échouer en silence.
- Le mot de passe du propriétaire déverrouille un document restreint pour le
  reste de la session.
- Définissez un mot de passe sur un document, changez-le ou retirez-le.
- Rien de ce que vous tapez n'est conservé : ce que vous tapez va directement au
  moteur et disparaît avec la fenêtre. La récupération après une fermeture
  inattendue ne garde rien en clair d'un document protégé.

### Une seule rangée d'outils

La barre d'outils de chaque plateforme tient maintenant sur une seule rangée.
Les sélecteurs de taille et de police apparaissent à côté de l'outil auquel ils
appartiennent et disparaissent avec lui; le zoom est un seul contrôle au lieu de
quatre; et tout le reste se trouve sous **Plus**. Sous Windows, la rangée
déborde dans le menu de la CommandBar à mesure que la fenêtre rétrécit. Sur le
Mac, la barre de menus porte les mêmes commandes avec leurs raccourcis, et Cmd+S
ne fait rien quand rien n'a changé. Sur les téléphones, les outils de tous les
jours sont passés dans une barre du bas, là où le pouce les atteint, et les
commandes de fichier sont passées sous Plus, en haut.

### PDF très volumineux

MegaPDF lisait tout un fichier en mémoire pour l'ouvrir. Il le lit maintenant à
la demande, dans le fichier lui-même :

| | 1.7 | 2.0 |
|---|---|---|
| 2,5 Go, 1 000 pages : ouverture | 62 s, 5,2 Go | 4,1 ms, 6 Mo |
| fichier de 1 Go, mémoire maximale | 6 171 Mo | 1 058 Mo |
| une page avec une image de 20 000 × 15 000 | redécodée à chaque affichage et modification | décodée une fois, gardée à la taille nécessaire |
| 20 000 objets texte sur une page | 21,8 s à lire | linéaire selon le nombre d'objets |
| pages en couleurs gérées CMJN / ICC | — | environ 6× plus rapide |

Les fichiers de 2 Go et plus s'ouvrent maintenant, et un fichier trop volumineux
pour l'appareil le dit clairement au lieu d'échouer.

`/UserUnit` est honoré partout — taille de page, zoom, placement, impression,
réduction et tolérances du garde-fou de mise en page — de sorte qu'une bannière
de 10 mètres est mesurée, imprimée et modifiée à sa taille réelle plutôt qu'à
une fiction de 72 points par pouce.

### Prévenu avant qu'une page change sous vos yeux

Certaines modifications obligent le moteur d'écriture à régénérer toute une
page, et sur une page qu'il ne peut pas réécrire fidèlement, cela peut changer
des parties que vous n'avez jamais touchées. Avant la première modification de
ce genre sur une page, MegaPDF vérifie la page et, si elle devait changer,
demande : **Modifier cette page?** Continuer ou Annuler. Il le demande une seule
fois par page, la vérification commence tôt pour que la réponse soit
habituellement prête, et si elle dépasse un budget de 1,5 seconde, la
modification est appliquée sans avertissement plutôt que de vous faire attendre.

### Mac

- Un PDF ouvert à partir du Finder s'enregistre dans ce fichier, au lieu de
  demander où le mettre.
- La première vue montre la page entière.
- La page est dessinée à la pleine résolution de l'écran.
- Les menus **Plus** et de zoom montrent leurs éléments (ils s'ouvraient vides
  dans une vraie fenêtre).
- VoiceOver nomme la barre d'outils, les boutons et la piste de la barre de
  défilement, les sélecteurs de police et de taille et les cases à cocher d'une
  page; l'enveloppe vide des sélecteurs a disparu de l'arborescence.
- Outils ▸ Police du texte et Taille du texte sont grisés hors d'Ajouter du
  texte, au lieu d'offrir un sous-menu vide.
- Zoom avant avec Cmd+Maj+= autant qu'avec Cmd+=.
- Le texte de départ de l'éditeur sur la page n'est plus une étape
  d'annulation à lui seul.
- La fenêtre d'impression explique pourquoi elle veut chercher des appareils sur
  le réseau local.

### Windows

- Tab parcourt les champs, les cases à cocher et les lignes modifiables d'une
  page; Entrée ou Espace active celle où vous êtes.
- La question sur les modifications non enregistrées apparaît aussi avant
  l'ouverture d'un autre fichier.

### Android

Android en était à 1.2.0, alors la 2.0 apporte aussi tout ce que les autres
plateformes ont reçu de la 1.3 à la 1.7 :

- **Le français**, selon la langue de l'appareil, ou choisi par application dans
  Paramètres ▸ Applications ▸ MegaPDF ▸ Langue sur Android 13 et plus.
- **Corriger le texte du document.**
- **Taper un nom pour créer une signature**, une troisième façon, en plus de la
  dessiner et de la photographier.
- **La bibliothèque de signatures est une feuille** qui montre l'encre réelle de
  chaque signature sur sa propre carte, avec renommer et supprimer sur la carte.
- Les documents s'ouvrent à partir de leur descripteur et sont lus à la demande.

### iPhone et iPad

- Corriger le texte du document.
- Le caviardage.
- Les PDF protégés.
- La barre d'outils du bas et le menu Plus.
- Les indicateurs d'activité et l'avertissement de page.
- Les fichiers volumineux : un document de 2,5 Go s'ouvre en un cinquième de
  seconde, à environ 110 Mo.

---

## Français (France)

Identique au texte canadien ci-dessus, aux règles de dérivation près
(`tools/gen_strings.py fr-fr`) : *courriel* → *e-mail*, *infonuagique* →
*cloud*, *crochet* → *coche*, *fin de semaine* → *week-end*, et une espace
insécable avant `?`, `!` et `;` comme avant `:`. Aucun de ces mots n'apparaît
dans ce texte; seules les ponctuations changent.

Le texte prêt à publier est produit par la même dérivation que les catalogues de
chaînes; les blocs des magasins dans ce dossier portent déjà les deux variantes.
