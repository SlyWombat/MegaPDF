# App Store (iPhone and iPad) — "What's New in This Version", 2.2

App Store Connect → the 2.2 version page → **Localizations** → *What's New in
This Version*, per language. Limit 4000 characters. `tools/asc_publish.py` reads
this folder for the version 2.2 (see the [README](README.md)), by the locale code
at the end of each heading.

No other platform is named anywhere in this copy (App Review 2.3.10) — iPhone and
iPad are the same platform and may both be named. App Store Connect refuses the ✕
character (#340), and this copy contains none.

**What is deliberately not here, because it is not on iPhone or iPad.** Whiteout,
multi-line added text and the progress-with-Stop strip are PR
[#591](https://github.com/SlyWombat/MegaPDF/pull/591), which is **open**;
[#592](https://github.com/SlyWombat/MegaPDF/pull/592) was merged into
`wip/3-4-ios`, not `main`, so `gh pr list --state merged` shows it as merged and
it is not in the build. The informed-permission choice (#558) is Android only.
See the [README](README.md#what-each-channel-may-claim).

*The French below is new and has **not** been reviewed. 2.1.1's was reviewed on
2026-09-26 (#343); every 2.2 string in the app still carries an `FR-REVIEW`
comment of its own. The France block is derived from the Canadian one by
`check_copy.py` in this folder — edit the Canadian block, then run it.*

## English (Canada) — `en-CA`

**What's New** [4000] (1975)

```
Reading mode

MegaPDF can get out of the way. Reading mode takes the toolbars off the screen and leaves the page. A tap brings a small bar back, and the page number on it opens a box to go anywhere in the document; a tap takes the bar away again. Page colours are in Settings, under Reading: Normal, Sepia for a long read, and Night for a dark room. Night inverts the page, pictures included — that is deliberate, and the setting says so, because a photograph read at night is a photograph in negative. If reading is mostly what you do with a PDF, Open documents in reading mode starts every one of them that way.

Page tools

Tap Pages and the document's pages are in front of you: a sheet on an iPhone, a sidebar beside the page on an iPad. Rotate a page that was scanned sideways, delete one, drag pages into a different order, insert a blank page, add the pages of another PDF, or save the pages you picked as a file of their own. Each of those is one step in the undo history, so one Undo puts the document back the way it was.

A toolbar made for the iPad

On an iPad the everyday tools now sit in a wide row of their own under the navigation bar, each with its title beside its icon instead of an icon you have to guess at, and each tool's picker opens as a popover next to the tool it belongs to. The keyboard commands are there too.

Zoom where you are looking

A pinch now zooms on the point between your fingers rather than the corner of the page, so the line you were reading stays under them.

Before a document's own signature is lost

Some documents arrive carrying a digital signature — the cryptographic kind, which stops verifying the moment the file changes at all. Saving over one of those now says so first and offers to save a copy instead, so the signed original stays intact. That is about a signature already in the document; the signature you draw, type or photograph and place on the page is a picture of your name, and placing one has not changed.
```

## Français (Canada) — `fr-CA`

**Quoi de neuf** [4000] (2218)

```
Mode lecture

MegaPDF sait s'effacer. Le mode lecture retire les barres d'outils de l'écran et laisse la page. Une touche ramène une petite barre, et le numéro de page qu'elle porte ouvre un champ où taper la page voulue; une autre touche la fait repartir. Les couleurs de la page sont dans les Réglages, sous Lecture : Normales, Sépia pour une longue lecture et Nuit pour une pièce sombre. Le mode nuit inverse la page, images comprises : c'est voulu, et le réglage le dit, parce qu'une photo lue la nuit est une photo en négatif. Si vous lisez un PDF plus souvent que vous ne le remplissez, Ouvrir les documents en mode lecture les ouvre tous ainsi.

Outils de page

Touchez Pages et les pages du document sont devant vous : une feuille sur l'iPhone, un panneau à côté de la page sur l'iPad. Faites pivoter une page numérisée de travers, supprimez-en une, glissez les pages dans un autre ordre, insérez une page vierge, ajoutez les pages d'un autre PDF, ou enregistrez les pages choisies dans un fichier à part. Chacun de ces gestes est une seule étape de l'historique, et une seule annulation remet le document comme il était.

Une barre d'outils faite pour l'iPad

Sur l'iPad, les outils de tous les jours occupent maintenant une large rangée bien à eux, sous la barre de navigation, chacun avec son titre à côté de son icône plutôt qu'une icône à deviner, et le sélecteur de chaque outil s'ouvre juste à côté de l'outil auquel il appartient. Les raccourcis clavier y sont aussi.

Le zoom là où vous regardez

Un pincement zoome maintenant sur le point entre vos doigts plutôt que sur le coin de la page : la ligne que vous lisiez reste sous vos doigts.

Avant que la signature du document ne soit perdue

Certains documents arrivent avec une signature numérique — la signature cryptographique, adossée à un certificat — qui cesse d'être vérifiable dès que le fichier change. Enregistrer par-dessus un tel document le dit maintenant d'abord et propose d'enregistrer une copie, pour que l'original signé reste intact. Il s'agit d'une signature déjà présente dans le document; la signature que vous dessinez, tapez ou photographiez pour la poser sur la page est une image de votre nom, et rien n'a changé de ce côté.
```

## Français (France) — `fr`

**Quoi de neuf** [4000] (2220)

```
Mode lecture

MegaPDF sait s'effacer. Le mode lecture retire les barres d'outils de l'écran et laisse la page. Une touche ramène une petite barre, et le numéro de page qu'elle porte ouvre un champ où taper la page voulue ; une autre touche la fait repartir. Les couleurs de la page sont dans les Réglages, sous Lecture : Normales, Sépia pour une longue lecture et Nuit pour une pièce sombre. Le mode nuit inverse la page, images comprises : c'est voulu, et le réglage le dit, parce qu'une photo lue la nuit est une photo en négatif. Si vous lisez un PDF plus souvent que vous ne le remplissez, Ouvrir les documents en mode lecture les ouvre tous ainsi.

Outils de page

Touchez Pages et les pages du document sont devant vous : une feuille sur l'iPhone, un panneau à côté de la page sur l'iPad. Faites pivoter une page numérisée de travers, supprimez-en une, glissez les pages dans un autre ordre, insérez une page vierge, ajoutez les pages d'un autre PDF, ou enregistrez les pages choisies dans un fichier à part. Chacun de ces gestes est une seule étape de l'historique, et une seule annulation remet le document comme il était.

Une barre d'outils faite pour l'iPad

Sur l'iPad, les outils de tous les jours occupent maintenant une large rangée bien à eux, sous la barre de navigation, chacun avec son titre à côté de son icône plutôt qu'une icône à deviner, et le sélecteur de chaque outil s'ouvre juste à côté de l'outil auquel il appartient. Les raccourcis clavier y sont aussi.

Le zoom là où vous regardez

Un pincement zoome maintenant sur le point entre vos doigts plutôt que sur le coin de la page : la ligne que vous lisiez reste sous vos doigts.

Avant que la signature du document ne soit perdue

Certains documents arrivent avec une signature numérique — la signature cryptographique, adossée à un certificat — qui cesse d'être vérifiable dès que le fichier change. Enregistrer par-dessus un tel document le dit maintenant d'abord et propose d'enregistrer une copie, pour que l'original signé reste intact. Il s'agit d'une signature déjà présente dans le document ; la signature que vous dessinez, tapez ou photographiez pour la poser sur la page est une image de votre nom, et rien n'a changé de ce côté.
```

> Apple's French locale for France is `fr`, not `fr-FR`. The France block is
> derived from the Canadian one by `check_copy.py`.
>
> **The two senses of "signature" (#602).** The vocabulary is Adobe's and the
> PDF specification's: an **electronic signature** is the broad kind, which
> includes the drawn, typed or photographed mark this app places; a **digital
> signature** is the specific kind backed by a certificate (whose certificate
> and key are a **digital ID**). In the interface the placed one is simply
> *signature* / *sign* / *your signature*, and this copy keeps that. The last
> item is the one place the copy has to hold both apart: the document's own
> **digital signature** and the **signature you place**. Nothing anywhere in
> this file says that placing a signature makes a document verifiable,
> tamper-evident, certified, legally binding or secure; *protected* and *secure*
> are left to the password feature, which 2.2 does not change.
>
> The English has two different noun phrases for them. The French has one noun
> for both, `signature`, and distinguishes them only by `numérique` — and
> `signature numérique` in everyday French also reads as *a signature made on a
> screen*, which is exactly the thing it is here contrasted with. The copy
> used to lean on `celle du chiffrement` to break the tie, but *chiffrement*
> means encryption, not the cryptographic-signature sense intended, and Fable's
> 2026-10-01 review caught it (#620 review, WRONG item W1): a reader would infer
> the document itself is encrypted. It now reads `la signature cryptographique,
> adossée à un certificat`, and still names the placed one `une image de votre
> nom`. `signature numérique` itself is unchanged and confirmed correct — OQLF
> and Adobe's own French interface both use it for the certificate-backed kind
> — this was a wording fix around it, not a rename.
