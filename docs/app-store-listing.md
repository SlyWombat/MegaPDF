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

**Description** [4000] (2757)
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
Draw your signature with a finger, or photograph the one on paper — the white background disappears automatically. Your signatures stay in a private library on your device; drop one onto any document, move and resize it until it sits right on the line.

Save without fear
Save writes back to the original file — safely. MegaPDF verifies every document before it touches your original, so a failed save can never corrupt the file someone sent you. Or keep the original and save a copy. Export as Markdown writes the document's text — headings, lists, the values you filled in — as a Markdown file, and leaves the PDF as it was.

Find any word
Search the whole document as you type. Every match lights up and the counter tells you how many there are, so the one clause you need in a forty-page lease is a few taps away.

Private by design
MegaPDF requests zero permissions and makes zero network connections. Your documents and your signature never leave your device — there is no server for them to go to. The app is open source, so anyone can verify that.

Works with everything
Open a PDF from Mail, Files, iCloud Drive or any app that shares one — MegaPDF is among the apps they offer to open it in — and send it back with Share. Documents you fill and sign here are standard PDFs: they open perfectly in any other PDF app.

MegaPDF is deliberately simple. It doesn't rearrange pages, run OCR, or bury you in toolbars. It opens, it fixes, it saves. Done.
```

**Keywords** [100] (89)
```
pdf,sign,signature,fill,form,checkbox,esign,editor,search,document,annotate,fill and sign
```

**What's new** [4000] (1043) — 2.1.1, from `docs/release-notes/2.1.1/app-store.md`
```
Open with MegaPDF

MegaPDF is now a PDF viewer as far as your iPhone and iPad are concerned. A PDF in Files, an attachment in Mail, or a document in any app's share sheet can be opened in MegaPDF from right there, instead of only through Open PDF on MegaPDF's own first screen. If a document with unsaved changes is already open, it asks before switching.

Share

Share, in the More menu, hands the document to the share sheet: Mail, Messages, AirDrop, Save to Files, whatever you have. If the document has unsaved changes it says so first and offers Save, Share without saving — the last saved copy goes out and your edits stay open — or Cancel.

Export as Markdown

Export as Markdown, beside Save a copy in the More menu, writes the document's text — headings, paragraphs, lists and the values you filled in — as a Markdown file, saved wherever you choose in Files. It is an export, not a save: the PDF is untouched, and it still asks to be saved if you had changed it. A scanned page has no text to give, and the file says so in its place.
```

**Screenshot captions** (optional overlay text), iPhone 6.9" and iPad 13" in this order
```
viewer: Checked and signed in under a minute
text-edit: Fix a typo in the document itself
redact: Redact removes it. It does not just cover it.
text: Type on the blank line — your size, your font
search: Find any word, on every page
sign: Your signatures, saved on your device
draw: Draw it once, use it everywhere
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

**Description** [4000] (3438)
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

Signez pour de vrai
Dessinez votre signature du doigt, ou photographiez celle sur papier : le fond blanc disparaît automatiquement. Vos signatures restent dans une bibliothèque privée sur votre appareil; déposez-en une sur n'importe quel document, déplacez-la et redimensionnez-la jusqu'à ce qu'elle soit bien sur la ligne.

Enregistrez sans crainte
Enregistrer écrit dans le fichier original, en toute sécurité. MegaPDF vérifie chaque document avant de toucher à votre original : un enregistrement raté ne peut jamais corrompre le fichier qu'on vous a envoyé. Ou gardez l'original et enregistrez une copie. Exporter en Markdown écrit le texte du document (titres, listes, les valeurs que vous avez remplies) dans un fichier Markdown, et laisse le PDF tel quel.

Trouvez n'importe quel mot
Cherchez dans tout le document à mesure que vous tapez. Chaque résultat s'allume et le compteur vous dit combien il y en a, pour que la seule clause dont vous avez besoin dans un bail de quarante pages soit à quelques touches.

Confidentiel par conception
MegaPDF ne demande aucune permission et n'établit aucune connexion réseau. Vos documents et votre signature ne quittent jamais votre appareil : il n'y a aucun serveur où ils pourraient aller. L'application est un logiciel libre; n'importe qui peut le vérifier.

Compatible avec tout
Ouvrez un PDF depuis Mail, Fichiers, iCloud Drive ou toute application qui en partage un (MegaPDF fait partie des applications qu'elles proposent pour l'ouvrir) et renvoyez-le avec Partager. Les documents remplis et signés ici sont des PDF standard : ils s'ouvrent parfaitement dans toute autre application PDF.

MegaPDF est volontairement simple. Il ne réorganise pas les pages, ne fait pas de reconnaissance de caractères et ne vous noie pas sous les barres d'outils. Il ouvre, il corrige, il enregistre. Terminé.
```

**Keywords** [100] (86)
```
pdf,signer,signature,remplir,formulaire,case,cocher,éditeur,recherche,document,annoter
```

**What's new** [4000] (1289) — 2.1.1, from `docs/release-notes/2.1.1/app-store.md`
```
Ouvrir avec MegaPDF

Pour votre iPhone et votre iPad, MegaPDF est maintenant un lecteur de PDF. Un PDF dans Fichiers, une pièce jointe dans Mail ou un document dans la feuille de partage de n'importe quelle application peut s'ouvrir dans MegaPDF directement à partir de là, et non plus seulement par Ouvrir un PDF à l'accueil. Si un document avec des modifications non enregistrées est déjà ouvert, il demande avant de changer de document.

Partager

Partager, dans le menu Plus, remet le document à la feuille de partage : Mail, Messages, AirDrop, Enregistrer dans Fichiers, tout ce que vous avez. Si le document a des modifications non enregistrées, il le dit d'abord et propose Enregistrer, Partager sans enregistrer (la dernière copie enregistrée est envoyée, et vos modifications restent ouvertes) ou Annuler.

Exporter en Markdown

Exporter en Markdown, à côté d'Enregistrer une copie dans le menu Plus, écrit le texte du document (titres, paragraphes, listes et les valeurs que vous avez remplies) dans un fichier Markdown, enregistré où vous voulez dans Fichiers. C'est une exportation, pas un enregistrement : le PDF n'est pas touché, et il demande toujours à être enregistré si vous l'aviez modifié. Une page numérisée n'a aucun texte à extraire, et le fichier le dit à sa place.
```

**Screenshot captions** (optional overlay text), iPhone 6.9" and iPad 13" in this order
```
viewer: Coché et signé en moins d'une minute
text-edit: Corrigez une coquille dans le document même
redact: Caviardez : c'est retiré, pas seulement couvert.
text: Écrivez sur la ligne vide : votre taille, votre police
search: Trouvez n'importe quel mot, sur chaque page
sign: Vos signatures, enregistrées sur votre appareil
draw: Dessinez-la une fois, utilisez-la partout
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

**Description** [4000] (3436)
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

Signez pour de vrai
Dessinez votre signature du doigt, ou photographiez celle sur papier : le fond blanc disparaît automatiquement. Vos signatures restent dans une bibliothèque privée sur votre appareil ; déposez-en une sur n'importe quel document, déplacez-la et redimensionnez-la jusqu'à ce qu'elle soit bien sur la ligne.

Enregistrez sans crainte
Enregistrer écrit dans le fichier original, en toute sécurité. MegaPDF vérifie chaque document avant de toucher à votre original : un enregistrement raté ne peut jamais corrompre le fichier qu'on vous a envoyé. Ou gardez l'original et enregistrez une copie. Exporter en Markdown écrit le texte du document (titres, listes, les valeurs que vous avez remplies) dans un fichier Markdown, et laisse le PDF tel quel.

Trouvez n'importe quel mot
Cherchez dans tout le document à mesure que vous tapez. Chaque résultat s'allume et le compteur vous dit combien il y en a, pour que la seule clause dont vous avez besoin dans un bail de quarante pages soit à quelques touches.

Confidentiel par conception
MegaPDF ne demande aucune permission et n'établit aucune connexion réseau. Vos documents et votre signature ne quittent jamais votre appareil : il n'y a aucun serveur où ils pourraient aller. L'application est un logiciel libre ; n'importe qui peut le vérifier.

Compatible avec tout
Ouvrez un PDF depuis Mail, Fichiers, iCloud Drive ou toute application qui en partage un (MegaPDF fait partie des applications qu'elles proposent pour l'ouvrir) et renvoyez-le avec Partager. Les documents remplis et signés ici sont des PDF standard : ils s'ouvrent parfaitement dans toute autre application PDF.

MegaPDF est volontairement simple. Il ne réorganise pas les pages, ne fait pas de reconnaissance de caractères et ne vous noie pas sous les barres d'outils. Il ouvre, il corrige, il enregistre. Terminé.
```

**Keywords** [100] (86)
```
pdf,signer,signature,remplir,formulaire,case,cocher,éditeur,recherche,document,annoter
```

**What's new** [4000] (1289) — 2.1.1, from `docs/release-notes/2.1.1/app-store.md`
```
Ouvrir avec MegaPDF

Pour votre iPhone et votre iPad, MegaPDF est maintenant un lecteur de PDF. Un PDF dans Fichiers, une pièce jointe dans Mail ou un document dans la feuille de partage de n'importe quelle application peut s'ouvrir dans MegaPDF directement à partir de là, et non plus seulement par Ouvrir un PDF à l'accueil. Si un document avec des modifications non enregistrées est déjà ouvert, il demande avant de changer de document.

Partager

Partager, dans le menu Plus, remet le document à la feuille de partage : Mail, Messages, AirDrop, Enregistrer dans Fichiers, tout ce que vous avez. Si le document a des modifications non enregistrées, il le dit d'abord et propose Enregistrer, Partager sans enregistrer (la dernière copie enregistrée est envoyée, et vos modifications restent ouvertes) ou Annuler.

Exporter en Markdown

Exporter en Markdown, à côté d'Enregistrer une copie dans le menu Plus, écrit le texte du document (titres, paragraphes, listes et les valeurs que vous avez remplies) dans un fichier Markdown, enregistré où vous voulez dans Fichiers. C'est une exportation, pas un enregistrement : le PDF n'est pas touché, et il demande toujours à être enregistré si vous l'aviez modifié. Une page numérisée n'a aucun texte à extraire, et le fichier le dit à sa place.
```

**Screenshot captions** (optional overlay text), iPhone 6.9" and iPad 13" in this order
```
viewer: Coché et signé en moins d'une minute
text-edit: Corrigez une coquille dans le document même
redact: Caviardez : c'est retiré, pas seulement couvert.
text: Écrivez sur la ligne vide : votre taille, votre police
search: Trouvez n'importe quel mot, sur chaque page
sign: Vos signatures, enregistrées sur votre appareil
draw: Dessinez-la une fois, utilisez-la partout
home: Pas de compte. Pas de cloud. Pas de suivi.
```

### Mac App Store — English (Canada) — `mac-en-CA`

*The Mac version's description and promotional text. Name, subtitle and keywords are the shared ones above.*

**Promotional text** [170] (144)
```
Someone emailed you a PDF to sign? Open it, click the boxes, drop in your signature, save. Done in under a minute — no account, no subscription.
```

**Description** [4000] (3223)
```
Open. Fix. Save. Done.

MegaPDF does the one job most people actually have with a PDF: someone sent you a form, and you need to send it back filled in, checked off, and signed. No account. No subscription. No cloud. Everything happens on your Mac.

Check any box
Click a checkbox and it's checked — real interactive form fields and plain printed squares alike. MegaPDF recognizes drawn checkboxes that other apps treat as decoration.

Type on any line
Click where the answer goes and type it. Choose the size and the face — sans, serif or monospace — so what you add matches the form you are filling in. Drag it into place, or double-click it to fix a typo. Everything you add is real, searchable text, not a sticker on top of the page.

Fix the document's own text
Wrong date? Misspelled name? Click the line and retype it. MegaPDF keeps the document's own font where it can and tells you when it had to use a similar one. If a change would disturb the rest of the page, it says so instead of quietly moving things. Undo puts the original back exactly.

Redact, and it really is gone
Mark what has to come out — a name, an address, a picture — and MegaPDF takes it out of the file rather than covering it over. What was underneath is gone: no other app can copy it or search for it. Cover is there too, for when hiding it on the page is enough, and it says that it only covers.

Sign like you mean it
Draw your signature with the trackpad or mouse, type it, or use a photo of the one on paper — the white background disappears automatically. Your signatures stay in a private library on your Mac; drop one onto any document, move and resize it until it sits right on the line.

Save without fear
Double-click a PDF in the Finder and Save writes back to that file — safely. MegaPDF verifies every document before it touches your original, so a failed save can never corrupt the file someone sent you. Or keep the original and save a copy — or, from the same Save As panel, a Markdown file of the document's text, with the PDF left as it was. Closing or quitting with unsaved changes always asks first.

Find any word
Search the whole document as you type. Every match lights up and the counter tells you how many there are, so the one clause you need in a forty-page lease is a keystroke away.

Protect, shrink, print
Set, change or remove a document's password. Save a smaller copy to send by email. Print through the standard macOS print panel.

Private by design
MegaPDF makes zero network connections — its sandbox does not even allow them — and opens only the files you choose. Your documents and your signature never leave your Mac. The app is open source, so anyone can verify that.

At home on the Mac
Every command is in the menu bar with the keyboard shortcut you expect. Documents open as tabs in one window, and a PDF opened from the Finder joins the window you already have. VoiceOver reads the toolbar and the page, and the app follows your Mac's light or dark appearance. Documents you fill and sign here are standard PDFs: they open perfectly in Preview and any other PDF app.

MegaPDF is deliberately simple. It doesn't rearrange pages, run OCR, or bury you in toolbars. It opens, it fixes, it saves. Done.
```

### Mac App Store — Français (Canada) — `mac-fr-CA`

*The Mac version's description and promotional text. Name, subtitle and keywords are the shared ones above.*

**Promotional text** [170] (160)
```
On vous a envoyé un PDF à signer? Ouvrez-le, cliquez sur les cases, apposez votre signature, enregistrez. Fait en moins d'une minute, sans compte ni abonnement.
```

**Description** [4000] (3978)
```
Ouvrir. Corriger. Enregistrer. Terminé.

MegaPDF fait la seule chose que la plupart des gens ont vraiment à faire avec un PDF : quelqu'un vous a envoyé un formulaire, et vous devez le renvoyer rempli, coché et signé. Pas de compte. Pas d'abonnement. Pas d'infonuagique. Tout se passe sur votre Mac.

Cochez n'importe quelle case
Cliquez sur une case et elle est cochée : les vrais champs de formulaire interactifs comme les simples carrés imprimés. MegaPDF reconnaît les cases dessinées que d'autres applications prennent pour de la décoration.

Écrivez sur n'importe quelle ligne
Cliquez à l'endroit où va la réponse et tapez-la. Choisissez la taille et la police (sans empattement, avec empattement ou à chasse fixe) pour que votre ajout s'accorde au formulaire. Glissez-le en place, ou double-cliquez dessus pour corriger une coquille. Tout ce que vous ajoutez est du vrai texte, dans lequel on peut chercher, pas un autocollant posé sur la page.

Corrigez le texte du document
Mauvaise date? Nom mal orthographié? Cliquez sur la ligne et retapez-la. MegaPDF garde la police du document quand il le peut et vous prévient quand il a dû en utiliser une semblable. Si une modification devait perturber le reste de la page, il vous le dit plutôt que de déplacer les choses en silence. Annuler remet l'original exactement.

Caviardez, et c'est parti pour de bon
Marquez ce qui doit disparaître — un nom, une adresse, une image — et MegaPDF le retire du fichier au lieu de le recouvrir. Ce qui était dessous n'y est plus : aucune autre application ne peut le copier ni le retrouver par une recherche. Masquer est là aussi, quand il suffit de cacher quelque chose sur la page, et il précise qu'il ne fait que recouvrir.

Signez pour de vrai
Dessinez votre signature au pavé tactile ou à la souris, tapez-la, ou utilisez une photo de celle sur papier : le fond blanc disparaît automatiquement. Vos signatures restent dans une bibliothèque privée sur votre Mac; déposez-en une sur n'importe quel document, déplacez-la et redimensionnez-la jusqu'à ce qu'elle soit bien sur la ligne.

Enregistrez sans crainte
Double-cliquez sur un PDF dans le Finder, et Enregistrer écrit dans ce fichier, en toute sécurité. MegaPDF vérifie chaque document avant de toucher à votre original : un enregistrement raté ne peut jamais corrompre le fichier qu'on vous a envoyé. Ou gardez l'original et enregistrez une copie, ou un fichier Markdown de son texte, le PDF restant tel quel. Fermer ou quitter avec des modifications non enregistrées demande toujours d'abord.

Trouvez n'importe quel mot
Cherchez dans tout le document à mesure que vous tapez. Chaque résultat s'allume et le compteur vous dit combien il y en a, pour que la seule clause dont vous avez besoin dans un bail de quarante pages soit à une touche près.

Protéger, réduire, imprimer
Ajoutez, changez ou retirez le mot de passe d'un document. Enregistrez une copie réduite à envoyer par courriel. Imprimez avec la zone de dialogue d'impression standard de macOS.

Confidentiel par conception
MegaPDF n'établit aucune connexion réseau (son bac à sable ne le lui permet même pas) et n'ouvre que les fichiers que vous choisissez. Vos documents et votre signature ne quittent jamais votre Mac. L'application est un logiciel libre; n'importe qui peut le vérifier.

Chez lui sur le Mac
Chaque commande est dans la barre des menus, avec le raccourci clavier attendu. Les documents s'ouvrent dans les onglets d'une même fenêtre, et un PDF ouvert depuis le Finder rejoint celle que vous avez déjà. VoiceOver lit la barre d'outils et la page, et l'application suit l'apparence claire ou sombre de votre Mac. Les documents remplis et signés ici sont des PDF standard : ils s'ouvrent parfaitement dans Aperçu et dans toute autre application PDF.

MegaPDF est volontairement simple. Il ne réorganise pas les pages, ne fait pas de reconnaissance de caractères et ne vous noie pas sous les barres d'outils. Il ouvre, il corrige, il enregistre. Terminé.
```

### Mac App Store — Français — `mac-fr`

*The Mac version's description and promotional text. Name, subtitle and keywords are the shared ones above.*

**Promotional text** [170] (161)
```
On vous a envoyé un PDF à signer ? Ouvrez-le, cliquez sur les cases, apposez votre signature, enregistrez. Fait en moins d'une minute, sans compte ni abonnement.
```

**Description** [4000] (3974)
```
Ouvrir. Corriger. Enregistrer. Terminé.

MegaPDF fait la seule chose que la plupart des gens ont vraiment à faire avec un PDF : quelqu'un vous a envoyé un formulaire, et vous devez le renvoyer rempli, coché et signé. Pas de compte. Pas d'abonnement. Pas de cloud. Tout se passe sur votre Mac.

Cochez n'importe quelle case
Cliquez sur une case et elle est cochée : les vrais champs de formulaire interactifs comme les simples carrés imprimés. MegaPDF reconnaît les cases dessinées que d'autres applications prennent pour de la décoration.

Écrivez sur n'importe quelle ligne
Cliquez à l'endroit où va la réponse et tapez-la. Choisissez la taille et la police (sans empattement, avec empattement ou à chasse fixe) pour que votre ajout s'accorde au formulaire. Glissez-le en place, ou double-cliquez dessus pour corriger une coquille. Tout ce que vous ajoutez est du vrai texte, dans lequel on peut chercher, pas un autocollant posé sur la page.

Corrigez le texte du document
Mauvaise date ? Nom mal orthographié ? Cliquez sur la ligne et retapez-la. MegaPDF garde la police du document quand il le peut et vous prévient quand il a dû en utiliser une semblable. Si une modification devait perturber le reste de la page, il vous le dit plutôt que de déplacer les choses en silence. Annuler remet l'original exactement.

Caviardez, et c'est parti pour de bon
Marquez ce qui doit disparaître — un nom, une adresse, une image — et MegaPDF le retire du fichier au lieu de le recouvrir. Ce qui était dessous n'y est plus : aucune autre application ne peut le copier ni le retrouver par une recherche. Masquer est là aussi, quand il suffit de cacher quelque chose sur la page, et il précise qu'il ne fait que recouvrir.

Signez pour de vrai
Dessinez votre signature au pavé tactile ou à la souris, tapez-la, ou utilisez une photo de celle sur papier : le fond blanc disparaît automatiquement. Vos signatures restent dans une bibliothèque privée sur votre Mac ; déposez-en une sur n'importe quel document, déplacez-la et redimensionnez-la jusqu'à ce qu'elle soit bien sur la ligne.

Enregistrez sans crainte
Double-cliquez sur un PDF dans le Finder, et Enregistrer écrit dans ce fichier, en toute sécurité. MegaPDF vérifie chaque document avant de toucher à votre original : un enregistrement raté ne peut jamais corrompre le fichier qu'on vous a envoyé. Ou gardez l'original et enregistrez une copie, ou un fichier Markdown de son texte, le PDF restant tel quel. Fermer ou quitter avec des modifications non enregistrées demande toujours d'abord.

Trouvez n'importe quel mot
Cherchez dans tout le document à mesure que vous tapez. Chaque résultat s'allume et le compteur vous dit combien il y en a, pour que la seule clause dont vous avez besoin dans un bail de quarante pages soit à une touche près.

Protéger, réduire, imprimer
Ajoutez, changez ou retirez le mot de passe d'un document. Enregistrez une copie réduite à envoyer par e-mail. Imprimez avec la zone de dialogue d'impression standard de macOS.

Confidentiel par conception
MegaPDF n'établit aucune connexion réseau (son bac à sable ne le lui permet même pas) et n'ouvre que les fichiers que vous choisissez. Vos documents et votre signature ne quittent jamais votre Mac. L'application est un logiciel libre ; n'importe qui peut le vérifier.

Chez lui sur le Mac
Chaque commande est dans la barre des menus, avec le raccourci clavier attendu. Les documents s'ouvrent dans les onglets d'une même fenêtre, et un PDF ouvert depuis le Finder rejoint celle que vous avez déjà. VoiceOver lit la barre d'outils et la page, et l'application suit l'apparence claire ou sombre de votre Mac. Les documents remplis et signés ici sont des PDF standard : ils s'ouvrent parfaitement dans Aperçu et dans toute autre application PDF.

MegaPDF est volontairement simple. Il ne réorganise pas les pages, ne fait pas de reconnaissance de caractères et ne vous noie pas sous les barres d'outils. Il ouvre, il corrige, il enregistre. Terminé.
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
French agreement (`demo-fr.pdf`, searched for "location"). Upload each set under
its own localisation, same slots:

| File | Slot | Suggested caption (optional overlay text) |
|---|---|---|
| `iphone-6_9-viewer.png` | iPhone 6.9" #1 | *Checked and signed in under a minute* |
| `iphone-6_9-text-edit.png` | iPhone 6.9" #2 | *Fix a typo in the document itself* |
| `iphone-6_9-redact.png` | iPhone 6.9" #3 | *Redact removes it. It does not just cover it.* |
| `iphone-6_9-text.png` | iPhone 6.9" #4 | *Type on the blank line — your size, your font* |
| `iphone-6_9-search.png` | iPhone 6.9" #5 | *Find any word, on every page* |
| `iphone-6_9-sign.png` | iPhone 6.9" #6 | *Your signatures, saved on your device* |
| `iphone-6_9-draw.png` | iPhone 6.9" #7 | *Draw it once, use it everywhere* |
| `iphone-6_9-home.png` | iPhone 6.9" #8 | *No account. No cloud. No tracking.* |
| `ipad-13-*.png` | iPad 13" #1–8 | same order |

Order matters: the viewer shot (a filled, signed agreement) leads, and the two
things the 2.0 copy leads with — correcting the document's own text, and
redaction — come straight after it (#146 §3, 2026-09-19). The App Store takes up
to ten per device.

**The set is re-shot for 2.1, and `redact` is the image that changes.** 2.0's
pose armed the tool and marked a line; since #328 the tool is a row in the ⋮
menu, so an armed tool draws nothing on the page to photograph. 2.1's pose shows
the mark **selected** instead — its chrome, and the ✕ that takes it off (#329) —
which is what the 2.1 notes say Redact now does. The other seven poses are
unchanged by 2.1, and the run still shoots all eight so that the set is one
build, one language and one status bar rather than eight images from two dates.

Each shot only exists once its screenshot state ships. `search` is not in the
1.0 builds, and `text` is newer still (#43) — upload each with the update that
actually carries the feature, or the listing promises something the binary does
not do.

The same set comes off the in-house Mac without a runner:
`tools/ios-screenshots.sh <lang>` (see `tools/mac-mini.md`). Same files, same
slots.

**The eight above land in `listing/`; everything else the run takes lands in
`review/`** — the dark-mode `search`, `sign` and `redact`. They are for looking
at, not for uploading. A folder of more images than the table has slots is how a
review shot ends up on a store listing.

The iPad simulator is put in **Full Screen Apps** first (Settings → Multitasking
& Gestures, driven by `CaptureSimulatorSetupUITests`): in Windowed Apps iPadOS 26
draws a resize grabber in the corner of every image, and the first 2.0 set
carried it in all of them.

## Screenshots — Mac App Store

The Mac is the same App Store record's other platform, so it has its own set, its
own slots and its own captions. Six images per listing language at **1440×900**,
from `tools/macos-store-captures.sh <lang>` on the in-house Mac (there is no
workflow: the Mac app needs a real display and a runner has none).

| File | Slot | Suggested caption (optional overlay text) |
|---|---|---|
| `light-01-viewer.png` | Mac #1 | *Checked and signed in under a minute* |
| `light-02-text.png` | Mac #2 | *Type on the blank line — your size, your font* |
| `light-03-search.png` | Mac #3 | *Find any word, on every page* |
| `light-04-sign.png` | Mac #4 | *Your signatures, saved on your Mac* |
| `light-05-redact.png` | Mac #5 | *Redact removes it. It does not just cover it.* |
| `light-06-home.png` | Mac #6 | *No account. No cloud. No tracking.* |

Order matters here too: the filled, signed agreement leads, and Redact is in the
set because it is what 2.0 is for.

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
`demo-fr-blank.pdf`). It writes, per language and device:

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
