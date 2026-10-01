# Microsoft Store — "What's new in this version", 2.2.0.0

Partner Center → the submission → **Store listings** → *What's new in this
version*, per language. Limit 1500 characters; the count is beside each block.
Paste the English into both `en-US` and `en-CA`. `tools/msstore_submit.py` reads
this file itself, by the locale codes in the headings.

The Windows app on the Store is the MSIX, and `megapdf-cli` is not in it — a Store
app cannot put a binary on `PATH` — so nothing here names the command line.

**1500 is the tightest field after Play, and the French runs about 27 % longer
than the English** (measured on 2.1.1: 1053 → 1341). The English below is kept
near 1100 so the French fits with room to spare. Anything added has to come out
of the *Smaller things* paragraph.

**What is deliberately not here.** The informed-permission choice (#558) is
Android only; on Windows a withheld permission still refuses. See the
[README](README.md#what-each-channel-may-claim).

*The French below is new and has **not** been reviewed. 2.1.1's was reviewed on
2026-09-26 (#343); every 2.2 string in the app still carries an `FR-REVIEW`
comment of its own. The France block is derived from the Canadian one by
`check_copy.py` in this folder — edit the Canadian block, then run it.*

## English — `en-US` and `en-CA`

**What's new** [1500] (1184)

```
Reading mode

Ctrl+H takes the tools off the screen and leaves the page. A small floating bar keeps the page number; Escape brings everything back. Page colours are in Settings: Normal, Sepia for a long read, Night for a dark room. Night inverts the page, pictures included — that is deliberate, and the setting says so. Every document can open in reading mode if that is what you mostly do with one.

Page tools

The Pages pane is somewhere to work now, not only somewhere to look. Rotate a page that was scanned sideways, delete one, drag pages into another order, insert a blank page, add the pages of another PDF, or save the pages you picked as a file of their own. Each is one step, and one Undo puts the document back.

Smaller things

Zoom holds the point you are pointing at instead of the corner of the page. A whiteout stays selected once you draw it, to move, resize or remove. Added text takes more than one line. Long work says what it is doing and how far it has got, and searching, shrinking and saving pages out can be stopped. Saving over a document that carries its own digital signature now warns that the signature will stop verifying, and offers Save As instead.
```

## Français (Canada) — `fr-CA`

**Quoi de neuf** [1500] (1379)

```
Mode lecture

Ctrl+H retire les outils de l'écran et laisse la page. Une petite barre flottante garde le numéro de page; Échap ramène tout. Les couleurs de la page sont dans les Paramètres : Normales, Sépia pour une longue lecture, Nuit pour une pièce sombre. Le mode nuit inverse la page, images comprises : c'est voulu, et le paramètre le dit. Chaque document peut s'ouvrir en mode lecture si c'est surtout ce que vous en faites.

Outils de page

Le volet Pages est maintenant un endroit où travailler, et non seulement où regarder. Faites pivoter une page numérisée de travers, supprimez-en une, glissez les pages dans un autre ordre, insérez une page vierge, ajoutez les pages d'un autre PDF, ou enregistrez les pages choisies dans un fichier à part. Chaque geste est une seule étape, et une seule annulation remet le document comme il était.

Et aussi

Le zoom garde le point que vous visez au lieu du coin de la page. Le correcteur reste sélectionné une fois tracé, pour le déplacer, le redimensionner ou l'enlever. Le texte ajouté tient sur plus d'une ligne. Les longues opérations disent ce qu'elles font et où elles en sont, et la recherche, la copie réduite et l'extraction de pages peuvent être arrêtées. Enregistrer par-dessus un document qui porte une signature numérique avertit maintenant que cette signature cessera d'être vérifiable, et propose Enregistrer sous.
```

## Français (France) — `fr-FR`

**Quoi de neuf** [1500] (1380)

```
Mode lecture

Ctrl+H retire les outils de l'écran et laisse la page. Une petite barre flottante garde le numéro de page ; Échap ramène tout. Les couleurs de la page sont dans les Paramètres : Normales, Sépia pour une longue lecture, Nuit pour une pièce sombre. Le mode nuit inverse la page, images comprises : c'est voulu, et le paramètre le dit. Chaque document peut s'ouvrir en mode lecture si c'est surtout ce que vous en faites.

Outils de page

Le volet Pages est maintenant un endroit où travailler, et non seulement où regarder. Faites pivoter une page numérisée de travers, supprimez-en une, glissez les pages dans un autre ordre, insérez une page vierge, ajoutez les pages d'un autre PDF, ou enregistrez les pages choisies dans un fichier à part. Chaque geste est une seule étape, et une seule annulation remet le document comme il était.

Et aussi

Le zoom garde le point que vous visez au lieu du coin de la page. Le correcteur reste sélectionné une fois tracé, pour le déplacer, le redimensionner ou l'enlever. Le texte ajouté tient sur plus d'une ligne. Les longues opérations disent ce qu'elles font et où elles en sont, et la recherche, la copie réduite et l'extraction de pages peuvent être arrêtées. Enregistrer par-dessus un document qui porte une signature numérique avertit maintenant que cette signature cessera d'être vérifiable, et propose Enregistrer sous.
```

> The France block is derived from the Canadian one by `check_copy.py`. The words
> the derivation table changes do not appear here; the difference is France's
> non-breaking space before `;` (« numéro de page ; Échap »).
>
> **"signature numérique" (#602)** is the cryptographic kind, which is what the
> last sentence is about — the same term the app's own warning uses
> (`docs/localisation-glossary.md`, #476/#481). The signature a person *places*
> is not mentioned in this block at all, so the two senses never meet in a
> sentence here; see [`app-store.md`](app-store.md) for the place they do, and
> for the question that is worth a francophone's answer.
