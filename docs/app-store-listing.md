# App Store listing — paste-ready copy (MegaPDF for iOS)

Everything App Store Connect asks for at submission, in order. Fields with
character limits show the count in brackets.

**Most of this pushes itself.** `tools/asc_publish.py` reads this file (the
copy blocks under "Copy by language"), `docs/app-review-notes.md` (the Notes
field) and a capture folder (screenshots and previews), and writes them to the
editable version through the App Store Connect API — `version`, `copy`,
`screenshots`, `previews`, `review`, `build`, `submit`, in that order. What is
left by hand: creating the app record (once), the App Privacy answers, and
pricing. Done that way for 1.7.0 on 2026-09-11.

---

## App Information

| Field | Value |
|---|---|
| **Name** [30] | `MegaPDF` |
| **Subtitle** [30] | `Fill, check & sign PDFs` |
| **Primary category** | Productivity |
| **Secondary category** | Business |
| **Content rights** | Does not contain, show, or access third-party content |
| **Age rating** | Answer **No** to every question → **4+** |
| **Copyright** | `2026 Electric RV` — matches the privacy policy's data-controller framing. **Where:** this field is *not* under App Information; it's on the **version page** — App Store tab → your version (e.g. "1.0 Prepare for Submission") → scroll below the description/keywords block → **Copyright**. |

## URLs

| Field | Value |
|---|---|
| **Support URL** | `https://github.com/SlyWombat/MegaPDF` |
| **Marketing URL** (optional) | `https://electricrv.ca/megapdf/` *(landing page — issue #25; use the GitHub URL until it's live)* |
| **Privacy Policy URL** | `https://electricrv.ca/megapdf/privacy/` *(hosted on electricrv.ca alongside SlyLED's — created by issue #25; the page must be live before submission. Same for Google Play's listing.)* |

<!-- copy-by-language -->
## Copy by language

App Store Connect → the version page → **Localizations**: **English (Canada)** is the primary language; add **French (Canada)** and **French**. Every field for every language is below as a block that pastes as-is. Character limits are in brackets with the actual count beside them.

*French copy was translated by the build assistant against `docs/localisation-glossary.md` and signed off by a francophone reviewer for 2.0 (2026-09-18, #242); new copy gets the same read before it goes live. The France variant is derived from the Canadian one by `tools/gen_listing_copy.py`.*

### English (Canada) — `en-CA`

**Name** [30] (7)
```
MegaPDF
```

**Subtitle** [30] (23)
```
Fill, check & sign PDFs
```

**Promotional text** [170] (142)
```
Someone emailed you a PDF to sign? Open it, tap the boxes, drop in your signature, save. Done in under a minute — no account, no subscription.
```

**Description** [4000] (3214)
```
Open. Fix. Save. Done.

MegaPDF does the one job most people actually have with a PDF: someone sent you a form, and you need to send it back filled in, checked off, and signed. No account. No subscription. No cloud. Everything happens on your device.

Check any box
Tap a checkbox and it's checked — real interactive form fields and plain printed squares alike. MegaPDF recognizes drawn checkboxes that other apps treat as decoration.

Type on any line
Tap where the answer goes and type it. Choose the size and the face — sans, serif or monospace — so what you add matches the form you are filling in. Drag it into place, or tap it again to fix a typo. Everything you add is real, searchable text, not a sticker on top of the page.

Fix the document's own text
Wrong date? Misspelled name? Tap the line and retype it. MegaPDF keeps the document's own font where it can and tells you when it had to use a similar one. If a change would disturb the rest of the page, it says so instead of quietly moving things. Undo puts the original back exactly.

Redact, and it really is gone
Mark what has to come out — a name, an address, a picture — and MegaPDF takes it out of the file rather than covering it over. What was underneath is gone: no other app can copy it or search for it.

Sign like you mean it
Draw your signature with a finger, or photograph the one on paper — the white background disappears automatically. Your signatures stay in a private library on your device; drop one on a document and move and resize it until it sits right on the line.

Save without fear
Save writes back to the original file — safely. MegaPDF verifies every document before it touches your original, so a failed save can never corrupt the file someone sent you. Or keep the original and save a copy. Export as Markdown writes the document's text — headings, lists, the values you filled in — as a Markdown file, and leaves the PDF as it was.

Find any word
Search the whole document as you type: every match lights up and the counter says how many, so the one clause you need in a forty-page lease is a few taps away.

Read it, not the app
Reading mode takes the toolbars off the screen and leaves the page. Page colours come with it: Sepia for a long read, Night for a dark room — and Night inverts pictures too, deliberately. MegaPDF can open every document that way.

Put the pages in order
Rotate a page that came in sideways, delete one, drag pages into the right order, insert a blank page, add the pages of another PDF, or save a few pages out as their own file. Each is a single step, and one Undo puts it back.

Private by design
MegaPDF requests zero permissions and makes zero network connections. Your documents and your signature never leave your device — there is no server for them to go to. The app is open source, so anyone can verify that.

Works with everything
Open a PDF from Mail, Files, iCloud Drive or any app that shares one — MegaPDF is among the apps they offer to open it in — and send it back with Share. Documents you fill and sign here are standard PDFs: they open perfectly in any other PDF app.

MegaPDF is deliberately simple: it doesn't bury you in toolbars. It opens, it fixes, it saves. Done.
```

**Keywords** [100] (96)
```
pdf,sign,signature,fill,form,checkbox,esign,editor,search,document,annotate,fill and sign,reader
```

**What's new** [4000] (1975) — 2.2, from `docs/release-notes/2.2/app-store.md`
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

**Screenshot captions** (optional overlay text), iPhone 6.9" and iPad 13" in this order
```
reading: Reading mode leaves nothing but the page — the one you checked and signed
text-edit: Fix a typo in the document itself
sign: Your signatures, saved on your device
draw: Draw it once, use it everywhere
pages: Rotate, reorder, delete — one Undo puts it all back
text: Type on the blank line — your size, your font
search: Find any word, on every page
redact: Redact removes it. It does not just cover it.
home: No account. No cloud. No tracking.
```

### Français (Canada) — `fr-CA`

**Name** [30] (7)
```
MegaPDF
```

**Subtitle** [30] (23)
```
Remplir, cocher, signer
```

**Promotional text** [170] (155)
```
On vous a envoyé un PDF à signer? Ouvrez-le, cochez les cases, apposez votre signature, enregistrez. Fait en moins d'une minute, sans compte ni abonnement.
```

**Description** [4000] (3946)
```
Ouvrir. Corriger. Enregistrer. Terminé.

MegaPDF fait la seule chose que la plupart des gens ont vraiment à faire avec un PDF : quelqu'un vous a envoyé un formulaire, et vous devez le renvoyer rempli, coché et signé. Pas de compte. Pas d'abonnement. Pas d'infonuagique. Tout se passe sur votre appareil.

Cochez n'importe quelle case
Touchez une case et elle est cochée : les vrais champs de formulaire interactifs comme les simples carrés imprimés. MegaPDF reconnaît les cases dessinées que d'autres applications prennent pour de la décoration.

Écrivez sur n'importe quelle ligne
Touchez l'endroit où va la réponse et tapez-la. Choisissez la taille et la police (sans empattement, avec empattement ou à chasse fixe) pour que votre ajout s'accorde au formulaire. Glissez-le en place, ou touchez-le de nouveau pour corriger une coquille. Tout ce que vous ajoutez est du vrai texte, dans lequel on peut chercher, pas un autocollant posé sur la page.

Corrigez le texte du document
Mauvaise date? Nom mal orthographié? Touchez la ligne et retapez-la. MegaPDF garde la police du document quand il le peut et vous prévient quand il a dû en utiliser une semblable. Si une modification devait perturber le reste de la page, il vous le dit plutôt que de déplacer les choses en silence. Annuler remet l'original exactement.

Caviardez, et c'est parti pour de bon
Marquez ce qui doit disparaître — un nom, une adresse, une image — et MegaPDF le retire du fichier au lieu de le recouvrir. Ce qui était dessous n'y est plus : aucune autre application ne peut le copier ni le retrouver par une recherche.

Signez comme sur papier
Dessinez votre signature du doigt, ou photographiez celle sur papier : le fond blanc disparaît automatiquement. Vos signatures restent dans une bibliothèque privée sur votre appareil; déposez-en une sur un document, puis déplacez-la et redimensionnez-la jusqu'à ce qu'elle soit bien sur la ligne.

Enregistrez sans crainte
Enregistrer écrit dans le fichier original, en toute sécurité. MegaPDF vérifie chaque document avant de toucher à votre original : un enregistrement raté ne peut jamais corrompre le fichier qu'on vous a envoyé. Ou gardez l'original et enregistrez une copie. Exporter en Markdown écrit le texte du document (titres, listes, les valeurs que vous avez remplies) dans un fichier Markdown, et laisse le PDF tel quel.

Trouvez n'importe quel mot
Cherchez dans tout le document à mesure que vous tapez : chaque résultat s'allume et le compteur dit combien il y en a, pour que la seule clause utile dans un bail de quarante pages soit à quelques touches.

Lisez le document, pas l'application
Le mode lecture retire les barres d'outils de l'écran et laisse la page. Les couleurs de la page viennent avec : Sépia pour une longue lecture, Nuit pour une pièce sombre — et le mode nuit inverse aussi les images, à dessein. MegaPDF peut ouvrir chaque document ainsi.

Mettez les pages en ordre
Faites pivoter une page arrivée de travers, supprimez-en une, glissez les pages dans le bon ordre, insérez une page vierge, ajoutez les pages d'un autre PDF, ou extrayez quelques pages dans un fichier à part. Chaque geste est une étape, et une seule annulation le défait.

Confidentiel par conception
MegaPDF ne demande aucune permission et n'établit aucune connexion réseau. Vos documents et votre signature ne quittent jamais votre appareil : il n'y a aucun serveur où ils pourraient aller. L'application est un logiciel libre; n'importe qui peut le vérifier.

Compatible avec tout
Ouvrez un PDF depuis Mail, Fichiers, iCloud Drive ou toute application qui en partage un (MegaPDF fait partie des applications qu'elles proposent pour l'ouvrir) et renvoyez-le avec Partager. Les documents remplis et signés ici sont des PDF standard : ils s'ouvrent parfaitement dans toute autre application PDF.

MegaPDF est volontairement simple : il ne vous noie pas sous les barres d'outils. Il ouvre, il corrige, il enregistre. Terminé.
```

**Keywords** [100] (94)
```
pdf,signer,signature,remplir,formulaire,case,cocher,éditeur,recherche,document,annoter,lecture
```

**What's new** [4000] (2218) — 2.2, from `docs/release-notes/2.2/app-store.md`
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

**Screenshot captions** (optional overlay text), iPhone 6.9" and iPad 13" in this order
```
reading: Le mode lecture ne laisse que la page : celle que vous avez cochée et signée
text-edit: Corrigez une coquille dans le document même
sign: Vos signatures, enregistrées sur votre appareil
draw: Dessinez-la une fois, utilisez-la partout
pages: Faites pivoter, réordonnez, supprimez : une seule annulation remet tout en place
text: Écrivez sur la ligne vide : votre taille, votre police
search: Trouvez n'importe quel mot, sur chaque page
redact: Caviardez : c'est retiré, pas seulement couvert.
home: Pas de compte. Pas d'infonuagique. Pas de suivi.
```

### Français — `fr`

**Name** [30] (7)
```
MegaPDF
```

**Subtitle** [30] (23)
```
Remplir, cocher, signer
```

**Promotional text** [170] (156)
```
On vous a envoyé un PDF à signer ? Ouvrez-le, cochez les cases, apposez votre signature, enregistrez. Fait en moins d'une minute, sans compte ni abonnement.
```

**Description** [4000] (3944)
```
Ouvrir. Corriger. Enregistrer. Terminé.

MegaPDF fait la seule chose que la plupart des gens ont vraiment à faire avec un PDF : quelqu'un vous a envoyé un formulaire, et vous devez le renvoyer rempli, coché et signé. Pas de compte. Pas d'abonnement. Pas de cloud. Tout se passe sur votre appareil.

Cochez n'importe quelle case
Touchez une case et elle est cochée : les vrais champs de formulaire interactifs comme les simples carrés imprimés. MegaPDF reconnaît les cases dessinées que d'autres applications prennent pour de la décoration.

Écrivez sur n'importe quelle ligne
Touchez l'endroit où va la réponse et tapez-la. Choisissez la taille et la police (sans empattement, avec empattement ou à chasse fixe) pour que votre ajout s'accorde au formulaire. Glissez-le en place, ou touchez-le de nouveau pour corriger une coquille. Tout ce que vous ajoutez est du vrai texte, dans lequel on peut chercher, pas un autocollant posé sur la page.

Corrigez le texte du document
Mauvaise date ? Nom mal orthographié ? Touchez la ligne et retapez-la. MegaPDF garde la police du document quand il le peut et vous prévient quand il a dû en utiliser une semblable. Si une modification devait perturber le reste de la page, il vous le dit plutôt que de déplacer les choses en silence. Annuler remet l'original exactement.

Caviardez, et c'est parti pour de bon
Marquez ce qui doit disparaître — un nom, une adresse, une image — et MegaPDF le retire du fichier au lieu de le recouvrir. Ce qui était dessous n'y est plus : aucune autre application ne peut le copier ni le retrouver par une recherche.

Signez comme sur papier
Dessinez votre signature du doigt, ou photographiez celle sur papier : le fond blanc disparaît automatiquement. Vos signatures restent dans une bibliothèque privée sur votre appareil ; déposez-en une sur un document, puis déplacez-la et redimensionnez-la jusqu'à ce qu'elle soit bien sur la ligne.

Enregistrez sans crainte
Enregistrer écrit dans le fichier original, en toute sécurité. MegaPDF vérifie chaque document avant de toucher à votre original : un enregistrement raté ne peut jamais corrompre le fichier qu'on vous a envoyé. Ou gardez l'original et enregistrez une copie. Exporter en Markdown écrit le texte du document (titres, listes, les valeurs que vous avez remplies) dans un fichier Markdown, et laisse le PDF tel quel.

Trouvez n'importe quel mot
Cherchez dans tout le document à mesure que vous tapez : chaque résultat s'allume et le compteur dit combien il y en a, pour que la seule clause utile dans un bail de quarante pages soit à quelques touches.

Lisez le document, pas l'application
Le mode lecture retire les barres d'outils de l'écran et laisse la page. Les couleurs de la page viennent avec : Sépia pour une longue lecture, Nuit pour une pièce sombre — et le mode nuit inverse aussi les images, à dessein. MegaPDF peut ouvrir chaque document ainsi.

Mettez les pages en ordre
Faites pivoter une page arrivée de travers, supprimez-en une, glissez les pages dans le bon ordre, insérez une page vierge, ajoutez les pages d'un autre PDF, ou extrayez quelques pages dans un fichier à part. Chaque geste est une étape, et une seule annulation le défait.

Confidentiel par conception
MegaPDF ne demande aucune permission et n'établit aucune connexion réseau. Vos documents et votre signature ne quittent jamais votre appareil : il n'y a aucun serveur où ils pourraient aller. L'application est un logiciel libre ; n'importe qui peut le vérifier.

Compatible avec tout
Ouvrez un PDF depuis Mail, Fichiers, iCloud Drive ou toute application qui en partage un (MegaPDF fait partie des applications qu'elles proposent pour l'ouvrir) et renvoyez-le avec Partager. Les documents remplis et signés ici sont des PDF standard : ils s'ouvrent parfaitement dans toute autre application PDF.

MegaPDF est volontairement simple : il ne vous noie pas sous les barres d'outils. Il ouvre, il corrige, il enregistre. Terminé.
```

**Keywords** [100] (94)
```
pdf,signer,signature,remplir,formulaire,case,cocher,éditeur,recherche,document,annoter,lecture
```

**What's new** [4000] (2220) — 2.2, from `docs/release-notes/2.2/app-store.md`
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

**Screenshot captions** (optional overlay text), iPhone 6.9" and iPad 13" in this order
```
reading: Le mode lecture ne laisse que la page : celle que vous avez cochée et signée
text-edit: Corrigez une coquille dans le document même
sign: Vos signatures, enregistrées sur votre appareil
draw: Dessinez-la une fois, utilisez-la partout
pages: Faites pivoter, réordonnez, supprimez : une seule annulation remet tout en place
text: Écrivez sur la ligne vide : votre taille, votre police
search: Trouvez n'importe quel mot, sur chaque page
redact: Caviardez : c'est retiré, pas seulement couvert.
home: Pas de compte. Pas de cloud. Pas de suivi.
```

### Mac App Store — English (Canada) — `mac-en-CA`

*The Mac version's description and promotional text. Name, subtitle and keywords are the shared ones above.*

**Promotional text** [170] (144)
```
Someone emailed you a PDF to sign? Open it, click the boxes, drop in your signature, save. Done in under a minute — no account, no subscription.
```

**Description** [4000] (3279)
```
Open. Fix. Save. Done.

MegaPDF does the one job most people actually have with a PDF: someone sent you a form, and you need to send it back filled in, checked off and signed, on your own Mac.

Check any box
Click a checkbox and it's checked — real form fields and plain printed squares alike. MegaPDF recognizes drawn checkboxes that other apps treat as decoration.

Type on any line
Click where the answer goes and type it, in the size and face — sans, serif or monospace — that matches the form. Drag it into place, or double-click it to fix a typo. Everything you add is real, searchable text, not a sticker on top of the page.

Fix the document's own text
Wrong date? Misspelled name? Click the line and retype it. MegaPDF keeps the document's own font where it can, and says so when it had to use a similar one. If a change would disturb the page it says so instead of quietly moving things, and Undo puts the original back exactly.

Redact, and it really is gone
Mark what has to come out — a name, an address, a picture — and MegaPDF takes it out of the file rather than covering it over. What was underneath is gone: no other app can copy it or search for it. Cover is there too, and says it only covers.

Sign like you mean it
Draw your signature with the trackpad or mouse, type it, or photograph the one on paper — the white background disappears automatically. Your signatures stay in a private library on your Mac; drop one on a document and move and resize it until it sits right on the line.

Save without fear
Save writes back to the file you opened — safely. MegaPDF verifies every document first, so a failed save can never corrupt the file someone sent you. Or save a copy instead, or a Markdown file of the document's text, with the PDF left as it was. Closing or quitting with unsaved changes always asks first.

Find any word
Search the whole document as you type: every match lights up and the counter says how many, so the one clause you need in a forty-page lease is a keystroke away.

Read it, not the app
Reading mode (Shift-Command-R) takes the toolbar and the sidebar away and leaves the page; Escape brings the window back. Page colours sit beside it in Options: Sepia for a long read, Night for a dark room — and Night inverts pictures too, deliberately.

Put the pages in order
The Thumbnails sidebar is somewhere to work now: rotate a page that came in sideways, delete one, drag pages into the right order, insert a blank page, insert the pages of another PDF, or save a selection as its own file. Each is one step, and one Undo puts it back.

Private by design
MegaPDF makes zero network connections — its sandbox does not even allow them — and opens only the files you choose. Your documents and your signature never leave your Mac. The app is open source, so anyone can verify that.

At home on the Mac
Every command is in the menu bar with the shortcut you expect, documents open as tabs in one window, and a PDF opened from the Finder joins the one you already have. VoiceOver reads the toolbar and the page, the app follows your light or dark appearance, and what you sign here is a standard PDF that opens in Preview and anywhere else.

MegaPDF is deliberately simple: it doesn't bury you in toolbars. It opens, it fixes, it saves. Done.
```

### Mac App Store — Français (Canada) — `mac-fr-CA`

*The Mac version's description and promotional text. Name, subtitle and keywords are the shared ones above.*

**Promotional text** [170] (160)
```
On vous a envoyé un PDF à signer? Ouvrez-le, cliquez sur les cases, apposez votre signature, enregistrez. Fait en moins d'une minute, sans compte ni abonnement.
```

**Description** [4000] (3984)
```
Ouvrir. Corriger. Enregistrer. Terminé.

MegaPDF fait la seule chose que la plupart des gens ont vraiment à faire avec un PDF : quelqu'un vous a envoyé un formulaire, et vous devez le renvoyer rempli, coché et signé, sur votre propre Mac.

Cochez n'importe quelle case
Cliquez sur une case et elle est cochée : les vrais champs de formulaire comme les simples carrés imprimés. MegaPDF reconnaît les cases dessinées que d'autres applications prennent pour de la décoration.

Écrivez sur n'importe quelle ligne
Cliquez à l'endroit où va la réponse et tapez-la, dans la taille et la police (sans empattement, avec empattement ou à chasse fixe) qui s'accordent au formulaire. Glissez-le en place, ou double-cliquez dessus pour corriger une coquille. Tout ce que vous ajoutez est du vrai texte, où l'on peut chercher, pas un autocollant posé sur la page.

Corrigez le texte du document
Mauvaise date? Nom mal orthographié? Cliquez sur la ligne et retapez-la. MegaPDF garde la police du document quand il le peut et le dit quand il a dû en utiliser une semblable. Si une modification devait perturber la page, il le dit plutôt que de déplacer en silence; Annuler remet l'original exactement.

Caviardez, et c'est parti pour de bon
Marquez ce qui doit disparaître — un nom, une adresse, une image — et MegaPDF le retire du fichier au lieu de le recouvrir. Ce qui était dessous n'y est plus : aucune autre application ne peut le copier ni le retrouver par une recherche. Masquer est là aussi, et précise qu'il ne fait que recouvrir.

Signez comme sur papier
Dessinez votre signature au pavé tactile ou à la souris, tapez-la, ou photographiez celle sur papier : le fond blanc disparaît automatiquement. Vos signatures restent dans une bibliothèque privée sur votre Mac; déposez-en une sur un document, puis déplacez-la et redimensionnez-la jusqu'à ce qu'elle soit bien sur la ligne.

Enregistrez sans crainte
Enregistrer écrit dans le fichier ouvert, en toute sécurité. MegaPDF vérifie d'abord chaque document : un enregistrement raté ne peut jamais corrompre le fichier qu'on vous a envoyé. Ou enregistrez plutôt une copie, ou un fichier Markdown de son texte, le PDF restant tel quel. Fermer ou quitter avec des modifications non enregistrées demande toujours d'abord.

Trouvez n'importe quel mot
Cherchez dans tout le document à mesure que vous tapez : chaque résultat s'allume et le compteur dit combien il y en a, pour que la seule clause utile dans un bail de quarante pages soit à une touche près.

Lisez le document, pas l'application
Le mode lecture (Maj-Commande-R) retire la barre d'outils et le panneau latéral, et laisse la page; Échap ramène la fenêtre. Les couleurs de la page sont à côté, dans les Options : Sépia pour une longue lecture, Nuit pour une pièce sombre — et le mode nuit inverse aussi les images, à dessein.

Mettez les pages en ordre
Le panneau Vignettes est maintenant un endroit où travailler : faites pivoter une page arrivée de travers, supprimez-en une, glissez les pages dans le bon ordre, insérez une page vierge, insérez celles d'un autre PDF, ou extrayez une sélection. Chaque geste est une étape, et une seule annulation le défait.

Confidentiel par conception
MegaPDF n'établit aucune connexion réseau (son bac à sable ne le lui permet même pas) et n'ouvre que les fichiers que vous choisissez. Vos documents et votre signature ne quittent jamais votre Mac. L'application est un logiciel libre; n'importe qui peut le vérifier.

Chez lui sur le Mac
Chaque commande est dans la barre des menus avec le raccourci attendu, les documents s'ouvrent en onglets dans une même fenêtre, et un PDF ouvert depuis le Finder rejoint celle que vous avez déjà. VoiceOver lit la barre d'outils et la page, l'application suit l'apparence claire ou sombre, et ce que vous signez ici est un PDF standard qui s'ouvre dans Aperçu comme ailleurs.

MegaPDF est volontairement simple : il ne vous noie pas sous les barres d'outils. Il ouvre, il corrige, il enregistre. Terminé.
```

### Mac App Store — Français — `mac-fr`

*The Mac version's description and promotional text. Name, subtitle and keywords are the shared ones above.*

**Promotional text** [170] (161)
```
On vous a envoyé un PDF à signer ? Ouvrez-le, cliquez sur les cases, apposez votre signature, enregistrez. Fait en moins d'une minute, sans compte ni abonnement.
```

**Description** [4000] (3990)
```
Ouvrir. Corriger. Enregistrer. Terminé.

MegaPDF fait la seule chose que la plupart des gens ont vraiment à faire avec un PDF : quelqu'un vous a envoyé un formulaire, et vous devez le renvoyer rempli, coché et signé, sur votre propre Mac.

Cochez n'importe quelle case
Cliquez sur une case et elle est cochée : les vrais champs de formulaire comme les simples carrés imprimés. MegaPDF reconnaît les cases dessinées que d'autres applications prennent pour de la décoration.

Écrivez sur n'importe quelle ligne
Cliquez à l'endroit où va la réponse et tapez-la, dans la taille et la police (sans empattement, avec empattement ou à chasse fixe) qui s'accordent au formulaire. Glissez-le en place, ou double-cliquez dessus pour corriger une coquille. Tout ce que vous ajoutez est du vrai texte, où l'on peut chercher, pas un autocollant posé sur la page.

Corrigez le texte du document
Mauvaise date ? Nom mal orthographié ? Cliquez sur la ligne et retapez-la. MegaPDF garde la police du document quand il le peut et le dit quand il a dû en utiliser une semblable. Si une modification devait perturber la page, il le dit plutôt que de déplacer en silence ; Annuler remet l'original exactement.

Caviardez, et c'est parti pour de bon
Marquez ce qui doit disparaître — un nom, une adresse, une image — et MegaPDF le retire du fichier au lieu de le recouvrir. Ce qui était dessous n'y est plus : aucune autre application ne peut le copier ni le retrouver par une recherche. Masquer est là aussi, et précise qu'il ne fait que recouvrir.

Signez comme sur papier
Dessinez votre signature au pavé tactile ou à la souris, tapez-la, ou photographiez celle sur papier : le fond blanc disparaît automatiquement. Vos signatures restent dans une bibliothèque privée sur votre Mac ; déposez-en une sur un document, puis déplacez-la et redimensionnez-la jusqu'à ce qu'elle soit bien sur la ligne.

Enregistrez sans crainte
Enregistrer écrit dans le fichier ouvert, en toute sécurité. MegaPDF vérifie d'abord chaque document : un enregistrement raté ne peut jamais corrompre le fichier qu'on vous a envoyé. Ou enregistrez plutôt une copie, ou un fichier Markdown de son texte, le PDF restant tel quel. Fermer ou quitter avec des modifications non enregistrées demande toujours d'abord.

Trouvez n'importe quel mot
Cherchez dans tout le document à mesure que vous tapez : chaque résultat s'allume et le compteur dit combien il y en a, pour que la seule clause utile dans un bail de quarante pages soit à une touche près.

Lisez le document, pas l'application
Le mode lecture (Maj-Commande-R) retire la barre d'outils et le panneau latéral, et laisse la page ; Échap ramène la fenêtre. Les couleurs de la page sont à côté, dans les Options : Sépia pour une longue lecture, Nuit pour une pièce sombre — et le mode nuit inverse aussi les images, à dessein.

Mettez les pages en ordre
Le panneau Vignettes est maintenant un endroit où travailler : faites pivoter une page arrivée de travers, supprimez-en une, glissez les pages dans le bon ordre, insérez une page vierge, insérez celles d'un autre PDF, ou extrayez une sélection. Chaque geste est une étape, et une seule annulation le défait.

Confidentiel par conception
MegaPDF n'établit aucune connexion réseau (son bac à sable ne le lui permet même pas) et n'ouvre que les fichiers que vous choisissez. Vos documents et votre signature ne quittent jamais votre Mac. L'application est un logiciel libre ; n'importe qui peut le vérifier.

Chez lui sur le Mac
Chaque commande est dans la barre des menus avec le raccourci attendu, les documents s'ouvrent en onglets dans une même fenêtre, et un PDF ouvert depuis le Finder rejoint celle que vous avez déjà. VoiceOver lit la barre d'outils et la page, l'application suit l'apparence claire ou sombre, et ce que vous signez ici est un PDF standard qui s'ouvre dans Aperçu comme ailleurs.

MegaPDF est volontairement simple : il ne vous noie pas sous les barres d'outils. Il ouvre, il corrige, il enregistre. Terminé.
```

<!-- /copy-by-language -->

## App Privacy (Data Collection)

Select **"Data is not collected"** for every category. Justification if asked:
the app makes no network connections at all (it requests no network
entitlements), has no analytics SDKs, no accounts, and processes documents
entirely on-device.

## Screenshots

Run the **iOS Screenshots** workflow (Actions → iOS Screenshots → Run
workflow). It captures once per listing language — artifacts
`appstore-screenshots-en`, `-fr-CA` and `-fr` (#91): the French runs launch the
app with `-AppleLanguages`, so the chrome is French and the document is the
French agreement (`demo-fr.pdf` for fr-CA, `demo-fr-FR.pdf` for fr — France's
own text since #310 — both searched for "location"). Upload each set under
its own localisation, same slots:

| File | Slot | Suggested caption (optional overlay text) |
|---|---|---|
| `iphone-6_9-reading.png` | iPhone 6.9" #1 | *Reading mode leaves nothing but the page — the one you checked and signed* |
| `iphone-6_9-text-edit.png` | iPhone 6.9" #2 | *Fix a typo in the document itself* |
| `iphone-6_9-sign.png` | iPhone 6.9" #3 | *Your signatures, saved on your device* |
| `iphone-6_9-draw.png` | iPhone 6.9" #4 | *Draw it once, use it everywhere* |
| `iphone-6_9-pages.png` | iPhone 6.9" #5 | *Rotate, reorder, delete — one Undo puts it all back* |
| `iphone-6_9-text.png` | iPhone 6.9" #6 | *Type on the blank line — your size, your font* |
| `iphone-6_9-search.png` | iPhone 6.9" #7 | *Find any word, on every page* |
| `iphone-6_9-redact.png` | iPhone 6.9" #8 | *Redact removes it. It does not just cover it.* |
| `iphone-6_9-home.png` | iPhone 6.9" #9 | *No account. No cloud. No tracking.* |
| `ipad-13-*.png` | iPad 13" #1–9 | same order |

**Re-cut for 2.2 (#613, Dave 2026-10-01):** *"Redaction and whiteout are minor
features that move to the back, signing, editing and reading are common
features."* Reading and the page tools lead, the way Mac, Linux, Windows and
Android all now open; redaction moves back behind search. `viewer` — 2.0
through 2.1.1's lead slot, a filled and signed agreement with no feature of its
own — is dropped from the listing; it is still shot, into `review/`, because the
QA inventory is worth keeping even where the listing is not. Both `text-edit`
(correcting the document's own text, #113) and `text` (adding a new line, #43)
keep their own slot — different stories, and the App Store's ten-image limit
never forces the choice Play's eight did on the same pose set. `draw` keeps its
slot too, next to `sign`, both being signature-adjacent — **pending Dave's
sign-off on keeping it at all; see PR #651, which introduced this order.** The
iPad's nine slots are shot in the same order as the iPhone's: its page tools
are a sidebar rather than a sheet and its own toolbar is new in 2.2 (#172), but
the same nine stories are told either way, and
`tools/capture-gate/stores.py`'s single `order` list for this store assumes
one sequence shared by both devices.

Reading mode (#506) and Pages (#174, #613) both open the bundled six-page
agreement (`demo-pages.pdf` / `demo-fr-pages.pdf` / `demo-fr-FR-pages.pdf`,
`tools/gen_test_fixtures.py`) rather than the one-page demo every other slot
opens: a single-page document makes the reading bar's counter read "1 of 1" and
the Pages strip a single tile, both a picture of nothing. Page 1 of that document
is byte-identical to the one-page demo's, so the set still reads as one
document. The reading slot pins the floating bar up (`ViewerModel
.screenshotPinsReadingBar`, set only by `-screenshot reading`) rather than
leaving it to its ordinary two-second idle fade, and leaves the page colour at
Normal — Sepia and Night are reading mode's too and are better shown as a
*choice*, which is what the description already does. `-screenshot reading` and
`-screenshot pages` each print `screenshot <state>: …` on success and
`::error::…` on their own stderr if the mode did not actually turn on or the
document did not have enough pages; `tools/ios-screenshots.sh` reads that log
back for every state and fails the run rather than uploading an ordinary viewer
shot wearing the reading slot's caption.

Each shot only exists once its screenshot state ships. `search` is not in the
1.0 builds, and `text` is newer still (#43) — upload each with the update that
actually carries the feature, or the listing promises something the binary does
not do.

The same set comes off the in-house Mac without a runner:
`tools/ios-screenshots.sh <lang>` (see `tools/mac-mini.md`). Same files, same
slots.

**The nine above land in `listing/`; everything else the run takes lands in
`review/`** — `viewer`, and the dark-mode `search`, `sign` and `redact`. They are
for looking at, not for uploading. A folder of more images than the table has
slots is how a review shot ends up on a store listing.

The iPad simulator is put in **Full Screen Apps** first (Settings → Multitasking
& Gestures, driven by `CaptureSimulatorSetupUITests`): in Windowed Apps iPadOS 26
draws a resize grabber in the corner of every image, and the first 2.0 set
carried it in all of them — and the 2.1.1 CI set too, because the workflow did
not run that step until #406. Every simulator also gets
`com.apple.keyboard.preferences DidShowContinuousPathIntroduction` set before
the app is installed: the first keyboard on a fresh simulator comes up under
iOS's QuickPath tip ("Speed up your typing by sliding your finger…", in the
system language), which is what `text-edit` shot on the runner in all three
languages. The in-house simulators had shown it once and never again.

## Screenshots — Mac App Store

The Mac is the same App Store record's other platform, so it has its own set, its
own slots and its own captions. **Seven** images per listing language at
**1440×900** since #613, from `tools/macos-store-captures.sh <lang>` on the
in-house Mac (there is no workflow: the Mac app needs a real display and a runner
has none).

| File | Slot | Suggested caption (optional overlay text) |
|---|---|---|
| `light-01-reading.png` | Mac #1 | *Reading mode leaves nothing but the page — the one you ticked and signed* |
| `light-02-text.png` | Mac #2 | *Type on the blank line — your size, your font* |
| `light-03-sign.png` | Mac #3 | *Your signatures, saved on your Mac* |
| `light-04-pages.png` | Mac #4 | *Rotate, reorder, delete — and one Undo puts the document back* |
| `light-05-search.png` | Mac #5 | *Find any word, on every page* |
| `light-06-redact.png` | Mac #6 | *Redact removes it. It does not just cover it.* |
| `light-07-home.png` | Mac #7 | *No account. No cloud. No tracking.* |

**The order is Dave's, 2026-10-01 (#613):** *"Redaction and whiteout are minor
features that move to the back, signing, editing and reading are common features."*
So what people come to the app for leads, and redaction moves behind search.
**Reading replaces the old viewer slot** at the front rather than joining it: both
are a picture of a page, and the reading one says something as well.

Four notes on the captions, which were re-read as a sequence rather than
transplanted:

1. **Slot 1 had to keep its old job while taking a new one.** The caption it
   replaces — *Checked and signed in under a minute* — was the only one in the set
   that said the app fills forms, and the picture still shows that page. The new one
   names reading mode in the app's own words (*"takes the toolbar and the sidebar off
   the screen and leaves the page"*, from the 2.2 copy) and ends on what is on the
   page: *the one you ticked and signed*. The speed claim goes, and nothing is left
   promising a minute.
2. **Slot 4 does not reuse the 2.2 copy's own line for it** (*"somewhere to work
   now, not only somewhere to look"*), though it was the obvious one. Slot 6 already
   argues in that shape — *removes it, does not just cover it* — and the strong
   instance should be the only one. The caption names the three commands the picture
   shows and ends on Undo, which is the reassurance this feature needs: a page tool
   changes the document, and the one thing a reader wants to know is that it is one
   step back.
3. **Search and redact now sit next to each other**, which is a better pair than
   either had before — find the clause, then take it out — and they read as that
   without a connective. (The Linux set adds a *"Then"* to its redact caption for the
   same adjacency; that set's captions are narrative sentences and this one's are
   fragments, so the same word would not sit in this voice.)
4. **The sequence still ends where it did.** Read it, write on it, sign it,
   rearrange it, search it, take something out of it — and none of it left your Mac.

Not 2880×1800. `--scale 2` draws every page overlay at twice its offset, so the
capture refuses rather than write a wrong image; 1440×900 is an accepted size and
the set is composed for it. `docs/qa/mac-store-captures.md` is the runbook and
the gate for both Apple platforms.

## App preview videos

`tools/ios-demo-video.sh <lang> [device] [label]` records the real app filling
in the agreement — two boxes ticked, the library signature placed, a printed
name typed under the line, every "rental" found — on a simulator, driven by
`ios/MegaPDFUITests/DemoFlowUITests.swift` (scheme `MegaPDFDemo`, launch mode
`-screenshot story`, which opens the *unfilled* agreement `demo-blank.pdf` /
`demo-fr-blank.pdf` / `demo-fr-FR-blank.pdf`). It writes, per language and device:

| File | Use |
|---|---|
| `<label>-preview.mp4` | **Upload this one.** Time-compressed to under 30 s, the App Store Connect limit (15–30 s), 30 fps, H.264, no audio. |
| `<label>-demo.mp4` | Real pace (~50 s) for the website or a README. |
| `<label>-raw.mp4` | As recorded, springboard lead-in and all. |

Sizes match the screenshot slots: `iphone-6_9` (iPhone 17 Pro Max, 1320×2868)
and `ipad-13` (iPad Pro 13", 2064×2752). App Store Connect takes one preview
per device size per localisation, ahead of the screenshots; the French runs
use the French agreement and search for "location".

## App Review Information

> **A brief Notes field got 1.0 rejected** under Guideline 2.1 on 2026-08-14 — the
> 357 characters there covered what the app is and how to test it, but Apple wants
> seven specific things including a screen recording and the devices tested. Full
> answers in `docs/app-review-notes.md`; paste them into Notes for every submission.


| Field | Value |
|---|---|
| Sign-in required | **No** — there are no accounts |
| Contact | David Seaman, info@electricrv.ca, +1 888 555-1212 (company contact, as everywhere public) |
| Notes | see below |

> MegaPDF is a local-only PDF form filler: no account, no server, no network
> access. To test: open any PDF via the Files picker (any PDF with checkboxes
> works — e.g. an IRS form), tap a checkbox to check it, tap Sign → Draw to
> create a signature, tap the page to place it, then Save. The app writes
> back to the original file via the system file coordinator.

## TestFlight

**Beta App Description:**
> MegaPDF fills, checks, and signs PDF forms entirely on your device — no
> account, no cloud. This beta covers the full loop: open, check boxes, place
> a drawn or photographed signature, and save back to the original file.

**What to Test:**
> 1. Open a PDF from Mail or Files (real forms with checkboxes are best).
> 2. Tap printed checkbox squares — do they get an ✗? Tap again to remove.
> 3. Sign → Draw a signature, place it, drag/resize it, save.
> 4. Reopen the saved file in another app (Files preview, Acrobat) — is
>    everything where you put it?
> 5. Tap the magnifier and search a long document — do the highlights and the
>    "3 of 17" count match what you see, and does the return key walk through
>    the matches and wrap around at the end?
> 6. Tap Text, tap a blank line, and type — try a different size and font
>    before adding it. Then tap the text you placed: drag it, use the pencil to
>    fix a typo or change its size, and undo the lot.
> 7. If you use MegaPDF on more than one device: sign on one, open on the
>    other — the signature should be movable on both, and text added on one
>    should keep its size and face on the other.

---

*Android/Play counterpart: `android/RELEASING.md`. Windows/Microsoft Store
copy: the Store runbook. Keep the three tellings of the story consistent.*
