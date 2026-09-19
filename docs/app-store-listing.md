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

**Description** [4000] (2304)
```
Open. Fix. Save. Done.

MegaPDF does the one job most people actually have with a PDF: someone sent you a form, and you need to send it back filled in, checked off, and signed. No account. No subscription. No cloud. Everything happens on your device.

Check any box
Tap a checkbox and it's checked — real interactive form fields and plain printed squares alike. MegaPDF recognizes drawn checkboxes that other apps treat as decoration.

Type on any line
Tap where the answer goes and type it. Choose the size and the face — sans, serif or monospace — so what you add matches the form you are filling in. Drag it into place, or tap it again to fix a typo. Everything you add is real, searchable text, not a sticker on top of the page.

Fix the document's own text
Wrong date? Misspelled name? Tap the line and retype it. MegaPDF keeps the document's own font where it can and tells you when it had to use a similar one. If a change would disturb the rest of the page, it says so instead of quietly moving things. Undo puts the original back exactly.

Sign like you mean it
Draw your signature with a finger, or photograph the one on paper — the white background disappears automatically. Your signatures stay in a private library on your device; drop one onto any document, move and resize it until it sits right on the line.

Save without fear
Save writes back to the original file — safely. MegaPDF verifies every document before it touches your original, so a failed save can never corrupt the file someone sent you. Or keep the original and save a copy.

Find any word
Search the whole document as you type. Every match lights up and the counter tells you how many there are, so the one clause you need in a forty-page lease is a few taps away.

Private by design
MegaPDF requests zero permissions and makes zero network connections. Your documents and your signature never leave your device — there is no server for them to go to. The app is open source, so anyone can verify that.

Works with everything
Open PDFs from Mail, Files, iCloud Drive, or any app that shares files. Documents you fill and sign here are standard PDFs: they open perfectly in any other PDF app.

MegaPDF is deliberately simple. It doesn't rearrange pages, run OCR, or bury you in toolbars. It opens, it fixes, it saves. Done.
```

**Keywords** [100] (89)
```
pdf,sign,signature,fill,form,checkbox,esign,editor,search,document,annotate,fill and sign
```

**What's new** [4000] (1760) — 2.0, from `docs/release-notes/2.0/app-store.md`
```
Correct the document's own text

Tap a line in the document and retype it. Wrong date, misspelled name, the wrong amount — you no longer have to cover it up and write beside it. MegaPDF keeps the line's own font where it can and tells you when it had to use a similar one, and Undo puts the original back exactly. If a change would disturb the rest of the page, it says so instead of quietly moving things.

Redact, and it really is gone

Mark what has to come out — a name, an address, a picture — and MegaPDF takes it out of the file rather than covering it over. What was underneath is gone: no other app can copy it or search for it.

Protected PDFs

Open a PDF that asks for a password. If its owner restricted what may be changed, MegaPDF respects that and tells you, and the owner password unlocks it. You can also set a password on a document, change it, or remove it.

One row of tools

The tools now sit in a single row along the bottom, where a thumb reaches them, and everything done to the file as a whole is under More at the top.

It tells you what it is doing

Opening, saving, checking the saved file, searching, checking a page, applying a change: each says so while it works, and repeat taps are ignored rather than queued. Anything quicker than half a second still says nothing at all.

Very large PDFs

A 2.5 GB, thousand-page file used to be out of reach. It now opens straight away and stays well inside what a phone can hold. Banner-size pages measure at their real size, and pages full of pictures draw several times faster.

Smaller things

Lines a document draws twice — the old trick for fake bold — now edit cleanly instead of leaving a ghost. Search reaches the match itself when the page is zoomed in, not just the page it is on.
```

**Screenshot captions** (optional overlay text), iPhone 6.9" and iPad 13" in this order
```
viewer: Checked and signed in under a minute
text: Type on the blank line — your size, your font
text-edit: Fix a typo in the document itself
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

**Description** [4000] (2914)
```
Ouvrir. Corriger. Enregistrer. Terminé.

MegaPDF fait la seule chose que la plupart des gens ont vraiment à faire avec un PDF : quelqu'un vous a envoyé un formulaire, et vous devez le renvoyer rempli, coché et signé. Pas de compte. Pas d'abonnement. Pas d'infonuagique. Tout se passe sur votre appareil.

Cochez n'importe quelle case
Touchez une case et elle est cochée : les vrais champs de formulaire interactifs comme les simples carrés imprimés. MegaPDF reconnaît les cases dessinées que d'autres applications prennent pour de la décoration.

Écrivez sur n'importe quelle ligne
Touchez l'endroit où va la réponse et tapez-la. Choisissez la taille et la police (sans empattement, avec empattement ou à chasse fixe) pour que votre ajout s'accorde au formulaire. Glissez-le en place, ou touchez-le de nouveau pour corriger une coquille. Tout ce que vous ajoutez est du vrai texte, dans lequel on peut chercher, pas un autocollant posé sur la page.

Corrigez le texte du document
Mauvaise date? Nom mal orthographié? Touchez la ligne et retapez-la. MegaPDF garde la police du document quand il le peut et vous prévient quand il a dû en utiliser une semblable. Si une modification devait perturber le reste de la page, il vous le dit plutôt que de déplacer les choses en silence. Annuler remet l'original exactement.

Signez pour de vrai
Dessinez votre signature du doigt, ou photographiez celle sur papier : le fond blanc disparaît automatiquement. Vos signatures restent dans une bibliothèque privée sur votre appareil; déposez-en une sur n'importe quel document, déplacez-la et redimensionnez-la jusqu'à ce qu'elle soit bien sur la ligne.

Enregistrez sans crainte
Enregistrer écrit dans le fichier original, en toute sécurité. MegaPDF vérifie chaque document avant de toucher à votre original : un enregistrement raté ne peut jamais corrompre le fichier qu'on vous a envoyé. Ou gardez l'original et enregistrez une copie.

Trouvez n'importe quel mot
Cherchez dans tout le document à mesure que vous tapez. Chaque résultat s'allume et le compteur vous dit combien il y en a, pour que la seule clause dont vous avez besoin dans un bail de quarante pages soit à quelques touches.

Confidentiel par conception
MegaPDF ne demande aucune permission et n'établit aucune connexion réseau. Vos documents et votre signature ne quittent jamais votre appareil : il n'y a aucun serveur où ils pourraient aller. L'application est un logiciel libre; n'importe qui peut le vérifier.

Compatible avec tout
Ouvrez des PDF depuis Mail, Fichiers, iCloud Drive ou toute application qui partage des fichiers. Les documents remplis et signés ici sont des PDF standard : ils s'ouvrent parfaitement dans toute autre application PDF.

MegaPDF est volontairement simple. Il ne réorganise pas les pages, ne fait pas de reconnaissance de caractères et ne vous noie pas sous les barres d'outils. Il ouvre, il corrige, il enregistre. Terminé.
```

**Keywords** [100] (86)
```
pdf,signer,signature,remplir,formulaire,case,cocher,éditeur,recherche,document,annoter
```

**What's new** [4000] (2130) — 2.0, from `docs/release-notes/2.0/app-store.md`
```
Corrigez le texte du document

Touchez une ligne du document et retapez-la. Mauvaise date, nom mal orthographié, mauvais montant : vous n'avez plus à le masquer et à écrire à côté. MegaPDF garde la police de la ligne quand il le peut et vous prévient quand il a dû en utiliser une semblable, et Annuler remet l'original exactement. Si une modification devait perturber le reste de la page, il vous le dit plutôt que de déplacer les choses en silence.

Caviardez, et c'est parti pour de bon

Marquez ce qui doit disparaître — un nom, une adresse, une image — et MegaPDF le retire du fichier au lieu de le recouvrir. Ce qui était dessous n'y est plus : aucune autre application ne peut le copier ni le retrouver par une recherche.

PDF protégés

Ouvrez un PDF qui demande un mot de passe. Si son propriétaire a restreint les modifications, MegaPDF le respecte et vous le dit, et le mot de passe du propriétaire le déverrouille. Vous pouvez aussi définir un mot de passe sur un document, le changer ou le retirer.

Une seule rangée d'outils

Les outils tiennent maintenant sur une seule rangée en bas, là où le pouce les atteint, et tout ce qui touche au fichier dans son ensemble se trouve sous Plus, en haut.

Il vous dit ce qu'il fait

Ouverture, enregistrement, vérification du fichier enregistré, recherche, vérification d'une page, application de la modification : chaque tâche s'annonce pendant qu'elle travaille, et les touchers répétés sont ignorés plutôt que mis en file d'attente. Ce qui prend moins d'une demi-seconde continue de ne rien dire.

PDF très volumineux

Un fichier de 2,5 Go et mille pages était hors de portée. Il s'ouvre maintenant tout de suite et reste bien en deçà de ce qu'un téléphone peut contenir. Les pages de format bannière sont mesurées à leur taille réelle, et les pages pleines d'images s'affichent plusieurs fois plus vite.

Et aussi

Les lignes qu'un document dessine deux fois — la vieille astuce pour imiter le gras — se modifient maintenant proprement, sans laisser de fantôme. La recherche atteint le résultat lui-même quand la page est agrandie, et non seulement la page où il se trouve.
```

**Screenshot captions** (optional overlay text), iPhone 6.9" and iPad 13" in this order
```
viewer: Coché et signé en moins d'une minute
text: Écrivez sur la ligne vide : votre taille, votre police
text-edit: Corrigez une coquille dans le document même
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

**Description** [4000] (2912)
```
Ouvrir. Corriger. Enregistrer. Terminé.

MegaPDF fait la seule chose que la plupart des gens ont vraiment à faire avec un PDF : quelqu'un vous a envoyé un formulaire, et vous devez le renvoyer rempli, coché et signé. Pas de compte. Pas d'abonnement. Pas de cloud. Tout se passe sur votre appareil.

Cochez n'importe quelle case
Touchez une case et elle est cochée : les vrais champs de formulaire interactifs comme les simples carrés imprimés. MegaPDF reconnaît les cases dessinées que d'autres applications prennent pour de la décoration.

Écrivez sur n'importe quelle ligne
Touchez l'endroit où va la réponse et tapez-la. Choisissez la taille et la police (sans empattement, avec empattement ou à chasse fixe) pour que votre ajout s'accorde au formulaire. Glissez-le en place, ou touchez-le de nouveau pour corriger une coquille. Tout ce que vous ajoutez est du vrai texte, dans lequel on peut chercher, pas un autocollant posé sur la page.

Corrigez le texte du document
Mauvaise date ? Nom mal orthographié ? Touchez la ligne et retapez-la. MegaPDF garde la police du document quand il le peut et vous prévient quand il a dû en utiliser une semblable. Si une modification devait perturber le reste de la page, il vous le dit plutôt que de déplacer les choses en silence. Annuler remet l'original exactement.

Signez pour de vrai
Dessinez votre signature du doigt, ou photographiez celle sur papier : le fond blanc disparaît automatiquement. Vos signatures restent dans une bibliothèque privée sur votre appareil ; déposez-en une sur n'importe quel document, déplacez-la et redimensionnez-la jusqu'à ce qu'elle soit bien sur la ligne.

Enregistrez sans crainte
Enregistrer écrit dans le fichier original, en toute sécurité. MegaPDF vérifie chaque document avant de toucher à votre original : un enregistrement raté ne peut jamais corrompre le fichier qu'on vous a envoyé. Ou gardez l'original et enregistrez une copie.

Trouvez n'importe quel mot
Cherchez dans tout le document à mesure que vous tapez. Chaque résultat s'allume et le compteur vous dit combien il y en a, pour que la seule clause dont vous avez besoin dans un bail de quarante pages soit à quelques touches.

Confidentiel par conception
MegaPDF ne demande aucune permission et n'établit aucune connexion réseau. Vos documents et votre signature ne quittent jamais votre appareil : il n'y a aucun serveur où ils pourraient aller. L'application est un logiciel libre ; n'importe qui peut le vérifier.

Compatible avec tout
Ouvrez des PDF depuis Mail, Fichiers, iCloud Drive ou toute application qui partage des fichiers. Les documents remplis et signés ici sont des PDF standard : ils s'ouvrent parfaitement dans toute autre application PDF.

MegaPDF est volontairement simple. Il ne réorganise pas les pages, ne fait pas de reconnaissance de caractères et ne vous noie pas sous les barres d'outils. Il ouvre, il corrige, il enregistre. Terminé.
```

**Keywords** [100] (86)
```
pdf,signer,signature,remplir,formulaire,case,cocher,éditeur,recherche,document,annoter
```

**What's new** [4000] (2130) — 2.0, from `docs/release-notes/2.0/app-store.md`
```
Corrigez le texte du document

Touchez une ligne du document et retapez-la. Mauvaise date, nom mal orthographié, mauvais montant : vous n'avez plus à le masquer et à écrire à côté. MegaPDF garde la police de la ligne quand il le peut et vous prévient quand il a dû en utiliser une semblable, et Annuler remet l'original exactement. Si une modification devait perturber le reste de la page, il vous le dit plutôt que de déplacer les choses en silence.

Caviardez, et c'est parti pour de bon

Marquez ce qui doit disparaître — un nom, une adresse, une image — et MegaPDF le retire du fichier au lieu de le recouvrir. Ce qui était dessous n'y est plus : aucune autre application ne peut le copier ni le retrouver par une recherche.

PDF protégés

Ouvrez un PDF qui demande un mot de passe. Si son propriétaire a restreint les modifications, MegaPDF le respecte et vous le dit, et le mot de passe du propriétaire le déverrouille. Vous pouvez aussi définir un mot de passe sur un document, le changer ou le retirer.

Une seule rangée d'outils

Les outils tiennent maintenant sur une seule rangée en bas, là où le pouce les atteint, et tout ce qui touche au fichier dans son ensemble se trouve sous Plus, en haut.

Il vous dit ce qu'il fait

Ouverture, enregistrement, vérification du fichier enregistré, recherche, vérification d'une page, application de la modification : chaque tâche s'annonce pendant qu'elle travaille, et les touchers répétés sont ignorés plutôt que mis en file d'attente. Ce qui prend moins d'une demi-seconde continue de ne rien dire.

PDF très volumineux

Un fichier de 2,5 Go et mille pages était hors de portée. Il s'ouvre maintenant tout de suite et reste bien en deçà de ce qu'un téléphone peut contenir. Les pages de format bannière sont mesurées à leur taille réelle, et les pages pleines d'images s'affichent plusieurs fois plus vite.

Et aussi

Les lignes qu'un document dessine deux fois — la vieille astuce pour imiter le gras — se modifient maintenant proprement, sans laisser de fantôme. La recherche atteint le résultat lui-même quand la page est agrandie, et non seulement la page où il se trouve.
```

**Screenshot captions** (optional overlay text), iPhone 6.9" and iPad 13" in this order
```
viewer: Coché et signé en moins d'une minute
text: Écrivez sur la ligne vide : votre taille, votre police
text-edit: Corrigez une coquille dans le document même
search: Trouvez n'importe quel mot, sur chaque page
sign: Vos signatures, enregistrées sur votre appareil
draw: Dessinez-la une fois, utilisez-la partout
home: Pas de compte. Pas de cloud. Pas de suivi.
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
| `iphone-6_9-text.png` | iPhone 6.9" #2 | *Type on the blank line — your size, your font* |
| `iphone-6_9-search.png` | iPhone 6.9" #3 | *Find any word, on every page* |
| `iphone-6_9-sign.png` | iPhone 6.9" #4 | *Your signatures, saved on your device* |
| `iphone-6_9-draw.png` | iPhone 6.9" #5 | *Draw it once, use it everywhere* |
| `iphone-6_9-home.png` | iPhone 6.9" #6 | *No account. No cloud. No tracking.* |
| `ipad-13-*.png` | iPad 13" #1–6 | same order |

Order matters: the viewer shot (a filled, signed agreement) leads.

Each shot only exists once its screenshot state ships. `search` is not in the
1.0 builds, and `text` is newer still (#43) — upload each with the update that
actually carries the feature, or the listing promises something the binary does
not do.

The same set comes off the in-house Mac without a runner:
`tools/ios-screenshots.sh <lang>` (see `tools/mac-mini.md`). Same files, same
slots.

**The six above land in `listing/`; everything else the run takes lands in
`review/`** — `text-edit`, `redact`, and the dark-mode `search`, `sign` and
`redact`. They are for looking at, not for uploading. A folder of eleven images
beside a table of six slots is how a review shot ends up on a store listing.

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
