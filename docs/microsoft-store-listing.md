# Mega PDF — Store listing copy (paste into Partner Center)

**Everything meant for pasting is in a fenced code block.** Copy from inside the
block only — no `>`, `-` or `1.` prefixes, since Partner Center takes those
literally. The Description below is plain prose and pastes as-is.

**Three listings, two sets of copy.** The packages declare `en-us`, `en-ca` and
`fr-ca` (#91), so Partner Center asks for an **English (United States)**, an
**English (Canada)** and a **French (Canada)** listing. The English below is
used verbatim for both English listings — it carries no US-vs-CA spelling.
The French listing takes the copy under [Français (Canada)](#français-canada)
at the end of this file: a translation, not a paste.

## Short description / first line
(The first sentence shows in search results — it carries the whole pitch.)

```
The lightweight PDF editor for Windows. Open. Fix. Save. Done.
```

## Description
(Partner Center → Store listing → Description. Plain text; blank lines between
paragraphs survive, markdown does not.)

Mega PDF is a free, lightweight PDF editor built for people who find the big
PDF suites too bloated and complex. It does five things exceptionally well —
and deliberately nothing else.

EDIT TEXT — Click any text in the document and type, like editing a Word file.
Fix a typo, change a date, update a number. Done.

ADD TEXT — Click a blank line and type on it. Pick the size and the face —
sans, serif or monospace — so what you add matches the rest of the form. Drag
it into place, or double-click it later to change it.

CHECK BOXES — Click an empty square and it becomes a checked box. Forms that
were never meant to be filled digitally, filled digitally.

APPLY SIGNATURES — Keep a small personal library of signature images. Pick one
and click where it goes. Move it, nudge it, resize it until it sits exactly
right.

SAVE — Save overwrites, Save As creates a copy. No export wizards, no
"flatten" dialogs, no surprises.

Also included, because real documents need them: find any word with Ctrl+F
(every match highlighted, Enter to step through them), print your PDF, and
shrink oversized scans for email with one click (image downsampling and JPEG
recompression).

PRIVATE BY DESIGN — No account. No cloud. No subscription. No telemetry. Every
document is processed entirely on your device and never uploaded anywhere.
Ideal for contracts, medical forms, and anything else you'd rather not hand to
someone else's server.

Mega PDF is open source (Apache-2.0): github.com/SlyWombat/MegaPDF

## Feature bullets
(Store listing → "App features", up to 20 × 200 chars. Use all nine.)

One feature per box in Partner Center. Paste them one line at a time — the
lines below carry no bullet character:

```
Edit text in any PDF by clicking and typing — like a Word document
Add text on any blank line, in the size and face that matches the form
Click empty squares to check boxes on any form
Place a signature from your personal library with a click, then nudge and resize it
Find any word in the document with Ctrl+F — every match highlighted, Enter steps through them
Save overwrites, Save As copies — no export wizards or flatten dialogs
Shrink oversized scans for email with one click
Print directly from the app
100% local processing: no account, no cloud, no subscription, no telemetry
```

## Search terms
(Max 7; no term over 30 chars.)

```
PDF editor
edit PDF
sign PDF
fill PDF form
PDF signature
compress PDF
lightweight PDF
```

## Other fields
Category: **Productivity**. Pricing: **Free**. The rest are paste values:

```
https://electricrv.ca/megapdf/privacy/
```
```
https://electricrv.ca/megapdf/
```
```
dave@drscapital.com
```
```
© 2026 Electric RV. Licensed under Apache-2.0.
```
(privacy policy URL, website, support contact, copyright — in that order)

## Store logo

**Optional for MSIX apps — the package already carries its own logos.** Both
`.msix` files ship the full tile set (`StoreLogo.png` 50×50, `Square44x44Logo`
with every targetsize variant, `Square71x71`, `Square150x150`, `Square310x310`,
`Wide310x150`), declared in `Package.appxmanifest`, and the Store derives listing
and tile imagery from those. Leaving the listing's Store logo field empty is
valid and the listing will still show the app icon.

If you want to supply one explicitly — it gives you control over the listing
thumbnail rather than letting the Store pick — use:

    dist/store-assets/store-logo-300.png    (300×300)

Generated 2026-09-09 by Lanczos-downscaling the canonical 512×512 brand icon
(`website/megapdf/icon.png`, byte-identical to `dist/play-assets/icon-512.png`,
so Windows, Play and the website all show the same mark). Regenerate with:

    python3 -c "from PIL import Image; Image.open('website/megapdf/icon.png').convert('RGBA').resize((300,300), Image.LANCZOS).save('dist/store-assets/store-logo-300.png')"

Vector source is `assets/branding/icon.svg` / `logo.svg` if a different size is
ever needed. Partner Center lists the sizes it accepts on the page itself —
check there before assuming 300×300 is the only one.

## Screenshots
`artifacts/store/screenshots/` (2482×1541, well over the 1366×768 minimum; at
least one required, up to nine allowed). Upload all five in this order, one
caption each:

| File | Caption (≤ 200 chars) |
|---|---|
| `01-edit-text.png` | Click any text and type — editing a PDF like a Word file. |
| `02-checkbox.png` | Click an empty square to check it. Drawn checkboxes too, not just real form fields. |
| `03-signature.png` | Drop a saved signature on the line, then nudge it into place. |
| `04-shrink.png` | Shrink oversized scans to email-friendly sizes in one click. |
| `05-add-text.png` | Type on a blank line, in the size and face that matches the form. |

All five shots were re-taken 2026-09-09 from the packaged **1.7.0** build on
GPD-DAVE, so they match the binary being submitted and show the size and font
pickers (#43). `05-add-text.png` now exists.

They are 2482×1541 rather than the old 3038×1989: that machine's display tops out
at 2560×1600, and windows are hard-clamped to the display. Still roughly 1.8× the
Store's minimum on both axes, and all five share one frame.

**Display scale matters as much as resolution.** `ApplyToolbarLayout` switches on
*effective* pixels, so at 200% scale a 2500 px window is only 1250 effective —
below the toolbar's Full breakpoint, and the toolbar loses its labels. The
breakpoint is measured from the labels themselves since #91 (about 1430
effective px in English, 1475 in French). The capture machine was set to **150%**
so 2500 px reads as 1667 effective and the toolbar keeps icon + label in either
language, which is what these captions and the description assume. Check the
toolbar in the shots before uploading; if the labels are gone, the scale is
wrong, not the resolution.

Re-shooting needs a Windows desktop with the build installed; the harness is
`tools/screenshots-windows/` (start with its README). It cannot be done from CI —
unlike iOS and Android, whose screenshots come from the Actions workflows.

The signature in shot 3 is **MegaWoman** (`tools/assets/megawoman-sig.jpg`), seeded
with `Add-SignatureToLibrary.ps1`. If the library on the capture machine has other
entries, place the MegaWoman one — it is the brand signature for the listing.

Shot 3 is click-to-place, not drag: picking a signature from the library arms
placement and the next click on the page drops it there, selected for nudging.
Don't write a caption that promises a drag.

## Français (Canada)

The **French (Canada)** listing (#91). Same rules as the English: paste from
inside the code blocks only. Translated by the build assistant against
`docs/localisation-glossary.md`; have a francophone read it before it goes live.
The screenshots are the English ones until a French set is shot — see
`tools/screenshots-windows/README.md` § French screenshots.

### Description courte / première ligne

```
L'éditeur PDF léger pour Windows. Ouvrir. Corriger. Enregistrer. Terminé.
```

### Description

Mega PDF est un éditeur PDF gratuit et léger, conçu pour ceux qui trouvent les
grandes suites PDF trop lourdes et trop compliquées. Il fait cinq choses
exceptionnellement bien, et volontairement rien d'autre.

MODIFIER LE TEXTE — Cliquez sur n'importe quel texte du document et tapez,
comme dans un document Word. Corrigez une coquille, changez une date, mettez un
montant à jour. Terminé.

AJOUTER DU TEXTE — Cliquez sur une ligne vide et écrivez dessus. Choisissez la
taille et la police (sans empattement, avec empattement ou à chasse fixe) pour
que votre ajout s'accorde au reste du formulaire. Glissez-le en place, ou
double-cliquez dessus plus tard pour le modifier.

COCHER DES CASES — Cliquez sur un carré vide et il devient une case cochée. Des
formulaires jamais conçus pour être remplis à l'écran, remplis à l'écran.

APPOSER DES SIGNATURES — Gardez une petite bibliothèque personnelle d'images de
signature. Choisissez-en une et cliquez à l'endroit voulu. Déplacez-la,
ajustez-la, redimensionnez-la jusqu'à ce qu'elle soit exactement à sa place.

ENREGISTRER — Enregistrer remplace le fichier, Enregistrer sous en crée une
copie. Pas d'assistant d'exportation, pas de boîte de dialogue
d'« aplatissement », pas de surprise.

Aussi inclus, parce que les vrais documents en ont besoin : rechercher un mot
avec Ctrl+F (chaque résultat surligné, Entrée pour passer au suivant), imprimer
le PDF, et réduire les numérisations trop lourdes pour le courriel en un clic
(sous-échantillonnage des images et recompression JPEG).

CONFIDENTIEL PAR CONCEPTION — Pas de compte. Pas d'infonuagique. Pas
d'abonnement. Pas de télémétrie. Chaque document est traité entièrement sur
votre appareil et n'est jamais téléversé nulle part. Idéal pour les contrats,
les formulaires médicaux et tout ce que vous préférez ne pas confier au serveur
de quelqu'un d'autre.

Mega PDF est un logiciel libre (Apache-2.0) : github.com/SlyWombat/MegaPDF

### Fonctionnalités
(« App features », jusqu'à 20 × 200 caractères. Une par case, sans puce.)

```
Modifiez le texte de n'importe quel PDF en cliquant et en tapant, comme dans un document Word
Ajoutez du texte sur une ligne vide, dans la taille et la police du formulaire
Cliquez sur les carrés vides pour cocher les cases de n'importe quel formulaire
Apposez une signature de votre bibliothèque personnelle d'un clic, puis ajustez-la et redimensionnez-la
Trouvez n'importe quel mot avec Ctrl+F : chaque résultat surligné, Entrée pour passer au suivant
Enregistrer remplace, Enregistrer sous copie : pas d'assistant d'exportation ni de dialogue d'aplatissement
Réduisez les numérisations trop lourdes pour le courriel en un clic
Imprimez directement depuis l'application
Traitement 100 % local : pas de compte, pas d'infonuagique, pas d'abonnement, pas de télémétrie
```

### Termes de recherche
(7 au maximum; 30 caractères au plus chacun.)

```
éditeur PDF
modifier PDF
signer PDF
remplir formulaire PDF
signature PDF
compresser PDF
PDF léger
```

### Autres champs

The URLs and the support contact are the same as the English listing. Copyright:

```
© 2026 Electric RV. Sous licence Apache-2.0.
```

### Captures d'écran
Same five files, same order, one caption each (≤ 200 caractères):

| Fichier | Légende |
|---|---|
| `01-edit-text.png` | Cliquez sur n'importe quel texte et tapez : modifier un PDF comme un document Word. |
| `02-checkbox.png` | Cliquez sur un carré vide pour le cocher. Les cases dessinées aussi, pas seulement les vrais champs de formulaire. |
| `03-signature.png` | Déposez une signature enregistrée sur la ligne, puis ajustez-la en place. |
| `04-shrink.png` | Réduisez les numérisations trop lourdes à une taille adaptée au courriel en un clic. |
| `05-add-text.png` | Écrivez sur une ligne vide, dans la taille et la police qui correspondent au formulaire. |
