#!/usr/bin/env python3
"""Write the per-language store copy into the three listing docs (#91).

Every field the stores ask for, in every language the apps ship, each one a
fenced block that pastes as-is. English and Canadian French are authored here;
English (Canada) is the US copy unchanged, and French (France) is derived from
Canadian French by the same rules the app catalogues use
(tools/gen_strings.py: FR_CA_TO_FR_FR + France punctuation).

    python3 tools/gen_listing_copy.py

Rewrites the "Copy by language" section of:
    docs/microsoft-store-listing.md
    docs/app-store-listing.md
    android/RELEASING.md
between the <!-- copy-by-language --> and <!-- /copy-by-language --> markers.
"""

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "tools"))
from gen_strings import to_france  # noqa: E402

# The spacing rule the 2.0 store blocks follow, applied here too: U+00A0 before ":"
# in both French variants, and before "?", "!" and ";" as well in France's
# (docs/localisation-glossary.md). The live listings carried a plain space before
# some forty colons until the French review (#242) counted them.
sys.path.insert(0, str(ROOT / "docs/release-notes/2.0"))
from fix_french_spacing import space_before  # noqa: E402

# The release whose What's New the listings carry. docs/release-notes/<version>/ is
# the record; its check_copy.py copies the four store files into the folder the
# submission tools read (2.1.1 -> docs/release-notes/2.1/, #395).
NOTES_VERSION = "2.1.1"
RELEASE_NOTES = ROOT / "docs/release-notes" / NOTES_VERSION


def release_block(name: str, language: str) -> str:
    """The first counted block in `language`'s section of the release's store-copy file, so
    the listing's What's new is that text itself rather than a second copy of it."""
    text = (RELEASE_NOTES / name).read_text(encoding="utf-8")
    section = text.split(f"\n## {language}", 1)[1]
    return re.search(r"\*\*[^*]+\*\*\s*\[\d+\]\s*\(\d+\)\s*\n\n```\n(.*?)\n```", section, re.S).group(1)

# --------------------------------------------------------------------------
# Microsoft Store
# --------------------------------------------------------------------------

MS_EN = {
    "short": "The lightweight PDF editor for Windows. Open. Fix. Save. Done.",
    "description": """MegaPDF is a free, lightweight PDF editor built for people who find the big PDF suites too bloated and complex. It does six things exceptionally well — and deliberately nothing else.

EDIT TEXT — Click text in the document and type, like editing a Word file. Fix a typo, change a date, update a number. MegaPDF keeps the document's own font where it can, and if a change would shift the rest of the page it tells you rather than quietly moving things.

ADD TEXT — Click a blank line and type on it. Pick the size and the face — sans, serif or monospace — so what you add matches the rest of the form. Drag it into place, or double-click it later to change it.

REDACT — Drag over a name, an address or a picture, or select the text, and MegaPDF takes it out of the file rather than covering it over. What was underneath is gone: no other app can copy it or search for it. Whiteout, which only covers, says so.

CHECK BOXES — Click an empty square and it becomes a checked box. Forms that were never meant to be filled digitally, filled digitally.

APPLY SIGNATURES — Keep a small personal library of signature images. Pick one and click where it goes. Move it, nudge it, resize it until it sits exactly right.

SAVE — Save overwrites, Save As creates a copy. No export wizards, no "flatten" dialogs, no surprises. Save As can also write the document's text — headings, lists, the values you filled in — as a Markdown file, leaving the PDF untouched.

Also included, because real documents need them: find any word with Ctrl+F (every match highlighted, Enter to step through them), print your PDF, and shrink oversized scans for email with one click (image downsampling and JPEG recompression). Documents open as tabs in one window, and a PDF double-clicked in File Explorer joins the window you already have.

PRIVATE BY DESIGN — No account. No cloud. No subscription. No telemetry. Every document is processed entirely on your device and never uploaded anywhere. Ideal for contracts, medical forms, and anything else you'd rather not hand to someone else's server.

MegaPDF is open source (Apache-2.0): github.com/SlyWombat/MegaPDF""",
    "features": [
        "Edit the document's own text by clicking and typing — like a Word document",
        "Redact names, addresses and pictures: taken out of the file, not just covered over",
        "Add text on any blank line, in the size and face that matches the form",
        "Click empty squares to check boxes on any form",
        "Place a signature from your personal library with a click, then nudge and resize it",
        "Find any word in the document with Ctrl+F — every match highlighted, Enter steps through them",
        "Save overwrites, Save As copies — no export wizards or flatten dialogs",
        "Open several PDFs as tabs in one window — a PDF double-clicked in File Explorer joins the window you already have",
        "Save As writes the document's text as Markdown: headings, lists and filled-in values, with the PDF untouched",
        "Shrink oversized scans for email with one click",
        "Print directly from the app",
        "100% local processing: no account, no cloud, no subscription, no telemetry",
    ],
    "terms": ["PDF editor", "edit PDF", "sign PDF", "fill PDF form", "PDF signature", "compress PDF", "lightweight PDF"],
    "copyright": "© 2026 Electric RV. Licensed under Apache-2.0.",
    "captions": [
        ("01-edit-text.png", "Click the text and type — editing a PDF like a Word file."),
        ("02-checkbox.png", "Click an empty square to check it. Drawn checkboxes too, not just real form fields."),
        ("03-signature.png", "Drop a saved signature on the line, then nudge it into place."),
        ("04-shrink.png", "Shrink oversized scans to email-friendly sizes in one click."),
        ("05-add-text.png", "Type on a blank line, in the size and face that matches the form."),
        ("06-redact.png", "Redact, and it really is gone."),
    ],
}

MS_FR_CA = {
    "short": "L'éditeur PDF léger pour Windows. Ouvrir. Corriger. Enregistrer. Terminé.",
    "description": """MegaPDF est un éditeur PDF gratuit et léger, conçu pour ceux qui trouvent les grandes suites PDF trop lourdes et trop compliquées. Il fait six choses exceptionnellement bien, et volontairement rien d'autre.

MODIFIER LE TEXTE — Cliquez sur le texte du document et tapez, comme dans un document Word. Corrigez une coquille, changez une date, mettez un montant à jour. MegaPDF garde la police du document quand il le peut, et si une modification devait déplacer le reste de la page, il vous le dit plutôt que de déplacer les choses en silence.

AJOUTER DU TEXTE — Cliquez sur une ligne vide et écrivez dessus. Choisissez la taille et la police (sans empattement, avec empattement ou à chasse fixe) pour que votre ajout s'accorde au reste du formulaire. Glissez-le en place, ou double-cliquez dessus plus tard pour le modifier.

CAVIARDER — Glissez sur un nom, une adresse ou une image, ou sélectionnez le texte, et MegaPDF le retire du fichier au lieu de le recouvrir. Ce qui était dessous n'y est plus : aucune autre application ne peut le copier ni le retrouver par une recherche. Le correcteur, qui ne fait que recouvrir, le dit.

COCHER DES CASES — Cliquez sur un carré vide et il devient une case cochée. Des formulaires jamais conçus pour être remplis à l'écran, remplis à l'écran.

APPOSER DES SIGNATURES — Gardez une petite bibliothèque personnelle d'images de signature. Choisissez-en une et cliquez à l'endroit voulu. Déplacez-la, ajustez-la, redimensionnez-la jusqu'à ce qu'elle soit exactement à sa place.

ENREGISTRER — Enregistrer remplace le fichier, Enregistrer sous en crée une copie. Pas d'assistant d'exportation, pas de boîte de dialogue d'« aplatissement », pas de surprise. Enregistrer sous peut aussi écrire le texte du document (titres, listes, les valeurs que vous avez remplies) dans un fichier Markdown, sans toucher au PDF.

Aussi inclus, parce que les vrais documents en ont besoin : rechercher un mot avec Ctrl+F (chaque résultat surligné, Entrée pour passer au suivant), imprimer le PDF, et réduire les numérisations trop lourdes pour le courriel en un clic (sous-échantillonnage des images et recompression JPEG). Les documents s'ouvrent dans des onglets d'une même fenêtre, et un PDF double-cliqué dans l'Explorateur de fichiers rejoint la fenêtre que vous avez déjà.

CONFIDENTIEL PAR CONCEPTION — Pas de compte. Pas d'infonuagique. Pas d'abonnement. Pas de télémétrie. Chaque document est traité entièrement sur votre appareil et n'est jamais téléversé nulle part. Idéal pour les contrats, les formulaires médicaux et tout ce que vous préférez ne pas confier au serveur de quelqu'un d'autre.

MegaPDF est un logiciel libre (Apache-2.0) : github.com/SlyWombat/MegaPDF""",
    "features": [
        "Modifiez le texte du document lui-même en cliquant et en tapant, comme dans un document Word",
        "Caviardez noms, adresses et images : retirés du fichier, pas seulement recouverts",
        "Ajoutez du texte sur une ligne vide, dans la taille et la police du formulaire",
        "Cliquez sur les carrés vides pour cocher les cases de n'importe quel formulaire",
        "Apposez une signature de votre bibliothèque personnelle d'un clic, puis ajustez-la et redimensionnez-la",
        "Trouvez n'importe quel mot avec Ctrl+F : chaque résultat surligné, Entrée pour passer au suivant",
        "Enregistrer remplace, Enregistrer sous copie : pas d'assistant d'exportation ni de dialogue d'aplatissement",
        "Ouvrez plusieurs PDF dans les onglets d'une même fenêtre : un PDF double-cliqué dans l'Explorateur de fichiers rejoint la fenêtre que vous avez déjà",
        "Enregistrer sous écrit le texte du document en Markdown : titres, listes et valeurs remplies, sans toucher au PDF",
        "Réduisez les numérisations trop lourdes pour le courriel en un clic",
        "Imprimez directement depuis l'application",
        "Traitement 100 % local : pas de compte, pas d'infonuagique, pas d'abonnement, pas de télémétrie",
    ],
    "terms": ["éditeur PDF", "modifier PDF", "signer PDF", "remplir formulaire PDF", "signature PDF", "compresser PDF", "PDF léger"],
    "copyright": "© 2026 Electric RV. Sous licence Apache-2.0.",
    "captions": [
        ("01-edit-text.png", "Cliquez sur le texte et tapez : modifier un PDF comme un document Word."),
        ("02-checkbox.png", "Cliquez sur un carré vide pour le cocher. Les cases dessinées aussi, pas seulement les vrais champs de formulaire."),
        ("03-signature.png", "Déposez une signature enregistrée sur la ligne, puis ajustez-la en place."),
        ("04-shrink.png", "Réduisez les numérisations trop lourdes à une taille adaptée au courriel en un clic."),
        ("05-add-text.png", "Écrivez sur une ligne vide, dans la taille et la police qui correspondent au formulaire."),
        ("06-redact.png", "Caviardez, et c'est parti pour de bon."),
    ],
}

# --------------------------------------------------------------------------
# App Store (iOS)
# --------------------------------------------------------------------------

AS_EN = {
    "name": "MegaPDF",
    "subtitle": "Fill, check & sign PDFs",
    "promo": "Someone emailed you a PDF to sign? Open it, tap the boxes, drop in your signature, save. Done in under a minute — no account, no subscription.",
    "description": """Open. Fix. Save. Done.

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

MegaPDF is deliberately simple. It doesn't rearrange pages, run OCR, or bury you in toolbars. It opens, it fixes, it saves. Done.""",
    "keywords": "pdf,sign,signature,fill,form,checkbox,esign,editor,search,document,annotate,fill and sign",
    "whatsnew": release_block("app-store.md", "English (Canada)"),
    "captions": [
        ("viewer", "Checked and signed in under a minute"),
        ("text-edit", "Fix a typo in the document itself"),
        ("redact", "Redact removes it. It does not just cover it."),
        ("text", "Type on the blank line — your size, your font"),
        ("search", "Find any word, on every page"),
        ("sign", "Your signatures, saved on your device"),
        ("draw", "Draw it once, use it everywhere"),
        ("home", "No account. No cloud. No tracking."),
    ],
}

AS_FR_CA = {
    "name": "MegaPDF",
    "subtitle": "Remplir, cocher, signer",
    "promo": "On vous a envoyé un PDF à signer? Ouvrez-le, cochez les cases, apposez votre signature, enregistrez. Fait en moins d'une minute, sans compte ni abonnement.",
    "description": """Ouvrir. Corriger. Enregistrer. Terminé.

MegaPDF fait la seule chose que la plupart des gens ont vraiment à faire avec un PDF : quelqu'un vous a envoyé un formulaire, et vous devez le renvoyer rempli, coché et signé. Pas de compte. Pas d'abonnement. Pas d'infonuagique. Tout se passe sur votre appareil.

Cochez n'importe quelle case
Touchez une case et elle est cochée : les vrais champs de formulaire interactifs comme les simples carrés imprimés. MegaPDF reconnaît les cases dessinées que d'autres applications prennent pour de la décoration.

Écrivez sur n'importe quelle ligne
Touchez l'endroit où va la réponse et tapez-la. Choisissez la taille et la police (sans empattement, avec empattement ou à chasse fixe) pour que votre ajout s'accorde au formulaire. Glissez-le en place, ou touchez-le de nouveau pour corriger une coquille. Tout ce que vous ajoutez est du vrai texte, dans lequel on peut chercher, pas un autocollant posé sur la page.

Corrigez le texte du document
Mauvaise date? Nom mal orthographié? Touchez la ligne et retapez-la. MegaPDF garde la police du document quand il le peut et vous prévient quand il a dû en utiliser une semblable. Si une modification devait perturber le reste de la page, il vous le dit plutôt que de déplacer les choses en silence. Annuler remet l'original exactement.

Caviardez, et c'est parti pour de bon
Marquez ce qui doit disparaître — un nom, une adresse, une image — et MegaPDF le retire du fichier au lieu de le recouvrir. Ce qui était dessous n'y est plus : aucune autre application ne peut le copier ni le retrouver par une recherche.

Signez pour de vrai
Dessinez votre signature du doigt, ou photographiez celle sur papier : le fond blanc disparaît automatiquement. Vos signatures restent dans une bibliothèque privée sur votre appareil; déposez-en une sur n'importe quel document, déplacez-la et redimensionnez-la jusqu'à ce qu'elle soit bien sur la ligne.

Enregistrez sans crainte
Enregistrer écrit dans le fichier original, en toute sécurité. MegaPDF vérifie chaque document avant de toucher à votre original : un enregistrement raté ne peut jamais corrompre le fichier qu'on vous a envoyé. Ou gardez l'original et enregistrez une copie. Exporter en Markdown écrit le texte du document (titres, listes, les valeurs que vous avez remplies) dans un fichier Markdown, et laisse le PDF tel quel.

Trouvez n'importe quel mot
Cherchez dans tout le document à mesure que vous tapez. Chaque résultat s'allume et le compteur vous dit combien il y en a, pour que la seule clause dont vous avez besoin dans un bail de quarante pages soit à quelques touches.

Confidentiel par conception
MegaPDF ne demande aucune permission et n'établit aucune connexion réseau. Vos documents et votre signature ne quittent jamais votre appareil : il n'y a aucun serveur où ils pourraient aller. L'application est un logiciel libre; n'importe qui peut le vérifier.

Compatible avec tout
Ouvrez un PDF depuis Mail, Fichiers, iCloud Drive ou toute application qui en partage un (MegaPDF fait partie des applications qu'elles proposent pour l'ouvrir) et renvoyez-le avec Partager. Les documents remplis et signés ici sont des PDF standard : ils s'ouvrent parfaitement dans toute autre application PDF.

MegaPDF est volontairement simple. Il ne réorganise pas les pages, ne fait pas de reconnaissance de caractères et ne vous noie pas sous les barres d'outils. Il ouvre, il corrige, il enregistre. Terminé.""",
    "keywords": "pdf,signer,signature,remplir,formulaire,case,cocher,éditeur,recherche,document,annoter",
    "whatsnew": release_block("app-store.md", "Français (Canada)"),
    "captions": [
        ("viewer", "Coché et signé en moins d'une minute"),
        ("text-edit", "Corrigez une coquille dans le document même"),
        ("redact", "Caviardez : c'est retiré, pas seulement couvert."),
        ("text", "Écrivez sur la ligne vide : votre taille, votre police"),
        ("search", "Trouvez n'importe quel mot, sur chaque page"),
        ("sign", "Vos signatures, enregistrées sur votre appareil"),
        ("draw", "Dessinez-la une fois, utilisez-la partout"),
        ("home", "Pas de compte. Pas d'infonuagique. Pas de suivi."),
    ],
}

# --------------------------------------------------------------------------
# Mac App Store — the same record's MAC_OS platform. Only the promotional text and
# the description differ: the iPhone copy says "tap" and "finger" and names iCloud
# Drive, which App Review reads against the Mac app (2.3). Name, subtitle and
# keywords are shared with iOS on the record's app info.
# --------------------------------------------------------------------------

AS_MAC_EN = {
    "promo": "Someone emailed you a PDF to sign? Open it, click the boxes, drop in your signature, save. Done in under a minute — no account, no subscription.",
    "description": """Open. Fix. Save. Done.

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

MegaPDF is deliberately simple. It doesn't rearrange pages, run OCR, or bury you in toolbars. It opens, it fixes, it saves. Done.""",
}

AS_MAC_FR_CA = {
    "promo": "On vous a envoyé un PDF à signer? Ouvrez-le, cliquez sur les cases, apposez votre signature, enregistrez. Fait en moins d'une minute, sans compte ni abonnement.",
    "description": """Ouvrir. Corriger. Enregistrer. Terminé.

MegaPDF fait la seule chose que la plupart des gens ont vraiment à faire avec un PDF : quelqu'un vous a envoyé un formulaire, et vous devez le renvoyer rempli, coché et signé. Pas de compte. Pas d'abonnement. Pas d'infonuagique. Tout se passe sur votre Mac.

Cochez n'importe quelle case
Cliquez sur une case et elle est cochée : les vrais champs de formulaire interactifs comme les simples carrés imprimés. MegaPDF reconnaît les cases dessinées que d'autres applications prennent pour de la décoration.

Écrivez sur n'importe quelle ligne
Cliquez à l'endroit où va la réponse et tapez-la. Choisissez la taille et la police (sans empattement, avec empattement ou à chasse fixe) pour que votre ajout s'accorde au formulaire. Glissez-le en place, ou double-cliquez dessus pour corriger une coquille. Tout ce que vous ajoutez est du vrai texte, dans lequel on peut chercher, pas un autocollant posé sur la page.

Corrigez le texte du document
Mauvaise date? Nom mal orthographié? Cliquez sur la ligne et retapez-la. MegaPDF garde la police du document quand il le peut et vous prévient quand il a dû en utiliser une semblable. Si une modification devait perturber le reste de la page, il vous le dit plutôt que de déplacer les choses en silence. Annuler remet l'original exactement.

Caviardez, et c'est parti pour de bon
Marquez ce qui doit disparaître — un nom, une adresse, une image — et MegaPDF le retire du fichier au lieu de le recouvrir. Ce qui était dessous n'y est plus : aucune autre application ne peut le copier ni le retrouver par une recherche. Masquer est là aussi, quand il suffit de cacher quelque chose sur la page, et il précise qu'il ne fait que recouvrir.

Signez pour de vrai
Dessinez votre signature au pavé tactile ou à la souris, tapez-la, ou utilisez une photo de celle sur papier : le fond blanc disparaît automatiquement. Vos signatures restent dans une bibliothèque privée sur votre Mac; déposez-en une sur n'importe quel document, déplacez-la et redimensionnez-la jusqu'à ce qu'elle soit bien sur la ligne.

Enregistrez sans crainte
Double-cliquez sur un PDF dans le Finder, et Enregistrer écrit dans ce fichier, en toute sécurité. MegaPDF vérifie chaque document avant de toucher à votre original : un enregistrement raté ne peut jamais corrompre le fichier qu'on vous a envoyé. Ou gardez l'original et enregistrez une copie, ou un fichier Markdown de son texte, le PDF restant tel quel. Fermer ou quitter avec des modifications non enregistrées demande toujours d'abord.

Trouvez n'importe quel mot
Cherchez dans tout le document à mesure que vous tapez. Chaque résultat s'allume et le compteur vous dit combien il y en a, pour que la seule clause dont vous avez besoin dans un bail de quarante pages soit à une touche près.

Protéger, réduire, imprimer
Ajoutez, changez ou retirez le mot de passe d'un document. Enregistrez une copie réduite à envoyer par courriel. Imprimez avec la zone de dialogue d'impression standard de macOS.

Confidentiel par conception
MegaPDF n'établit aucune connexion réseau (son bac à sable ne le lui permet même pas) et n'ouvre que les fichiers que vous choisissez. Vos documents et votre signature ne quittent jamais votre Mac. L'application est un logiciel libre; n'importe qui peut le vérifier.

Chez lui sur le Mac
Chaque commande est dans la barre des menus, avec le raccourci clavier attendu. Les documents s'ouvrent dans les onglets d'une même fenêtre, et un PDF ouvert depuis le Finder rejoint celle que vous avez déjà. VoiceOver lit la barre d'outils et la page, et l'application suit l'apparence claire ou sombre de votre Mac. Les documents remplis et signés ici sont des PDF standard : ils s'ouvrent parfaitement dans Aperçu et dans toute autre application PDF.

MegaPDF est volontairement simple. Il ne réorganise pas les pages, ne fait pas de reconnaissance de caractères et ne vous noie pas sous les barres d'outils. Il ouvre, il corrige, il enregistre. Terminé.""",
}

# --------------------------------------------------------------------------
# Google Play — the App Store description with the Android app names.
# --------------------------------------------------------------------------

# Apple rejects a description that names another platform (2.3.10, c1b0a4a), so
# the App Store copy above names none; Play's "Works with everything" paragraph
# is its own, and may.
WORKS_EN_AS = "Open a PDF from Mail, Files, iCloud Drive or any app that shares one — MegaPDF is among the apps they offer to open it in — and send it back with Share. Documents you fill and sign here are standard PDFs: they open perfectly in any other PDF app."
WORKS_EN_PLAY = "Open a PDF from Files, Google Drive, Gmail or any app that hands one over — MegaPDF is in their Open with list — or through MegaPDF's own file picker, and send it back with Share. Documents you fill and sign here open perfectly in Adobe Acrobat, desktop PDF apps, and MegaPDF for Windows, Mac and iOS — same engine, same result, on every platform."
WORKS_FR_CA_AS = "Ouvrez un PDF depuis Mail, Fichiers, iCloud Drive ou toute application qui en partage un (MegaPDF fait partie des applications qu'elles proposent pour l'ouvrir) et renvoyez-le avec Partager. Les documents remplis et signés ici sont des PDF standard : ils s'ouvrent parfaitement dans toute autre application PDF."
WORKS_FR_CA_PLAY = "Ouvrez un PDF depuis Fichiers, Google Drive, Gmail ou toute application qui en remet un (MegaPDF est dans leur liste Ouvrir avec), ou avec le sélecteur de fichiers de MegaPDF, et renvoyez-le avec Partager. Les documents remplis et signés ici s'ouvrent parfaitement dans Adobe Acrobat, dans les applications PDF de bureau, et dans MegaPDF pour Windows, Mac et iOS : même moteur, même résultat, sur toutes les plateformes."

# What the Android app does differently from the iPhone one, checked against the code
# (2026-09-19 independent Play audit, re-read for 2.1.1): since #376 and #378 both
# phones open a PDF handed over by another app and share one back, so that paragraph
# differs only in which apps it names; signatures come from the photo picker rather
# than the camera, can be typed, and a merged signature-level androidx permission is
# never asked for; the Markdown export is a type choice in Save a copy's own picker
# (#386), not a separate "Export as Markdown" row. Each pair must match exactly once.
PLAY_EN_SWAPS = [
    (WORKS_EN_AS, WORKS_EN_PLAY),
    ("Draw your signature with a finger, or photograph the one on paper — the white background disappears automatically.",
     "Draw your signature with a finger, type your name, or use a photo of the one on paper — the white background disappears automatically."),
    ("Save writes back to the original file — safely. MegaPDF verifies every document before it touches your original, so a failed save can never corrupt the file someone sent you. Or keep the original and save a copy. Export as Markdown writes the document's text — headings, lists, the values you filled in — as a Markdown file, and leaves the PDF as it was.",
     "Save writes back to the original file — safely. MegaPDF checks every save before it touches your original, so a failed save can never corrupt the file someone sent you. Or keep the original and save a copy — as a PDF, or as a Markdown file of the document's text (headings, lists, the values you filled in) that leaves the PDF as it was. You can also protect a document with a password, or remove one you know."),
    ("MegaPDF requests zero permissions and makes zero network connections.",
     "MegaPDF asks you for no permissions and makes no network connections."),
]
PLAY_FR_CA_SWAPS = [
    (WORKS_FR_CA_AS, WORKS_FR_CA_PLAY),
    ("Dessinez votre signature du doigt, ou photographiez celle sur papier : le fond blanc disparaît automatiquement.",
     "Dessinez votre signature du doigt, tapez votre nom ou utilisez une photo de celle sur papier : le fond blanc disparaît automatiquement."),
    ("MegaPDF vérifie chaque document avant de toucher à votre original : un enregistrement raté ne peut jamais corrompre le fichier qu'on vous a envoyé. Ou gardez l'original et enregistrez une copie. Exporter en Markdown écrit le texte du document (titres, listes, les valeurs que vous avez remplies) dans un fichier Markdown, et laisse le PDF tel quel.",
     "MegaPDF vérifie chaque enregistrement avant de toucher à votre original : un enregistrement raté ne peut jamais corrompre le fichier qu'on vous a envoyé. Ou gardez l'original et enregistrez une copie, en PDF ou dans un fichier Markdown du texte du document (titres, listes, les valeurs que vous avez remplies) qui laisse le PDF tel quel. Vous pouvez aussi protéger un document par un mot de passe, ou retirer un mot de passe que vous connaissez."),
    ("MegaPDF ne demande aucune permission et n'établit aucune connexion réseau.",
     "MegaPDF ne vous demande aucune permission et n'établit aucune connexion réseau."),
]


def swapped(text: str, swaps: list) -> str:
    for old, new in swaps:
        assert text.count(old) == 1, f"Play swap does not match exactly once: {old[:60]}"
        text = text.replace(old, new)
    return text


PLAY_EN = {
    "title": "MegaPDF: Fill & Sign PDFs",
    "short": "Fill, check and sign a PDF. No account, no cloud.",
    "description": swapped(AS_EN["description"], PLAY_EN_SWAPS),
    "notes": release_block("google-play.md", "English (Canada)"),
}

PLAY_FR_CA = {
    "title": "MegaPDF : remplir et signer",
    "short": "Remplir, cocher et signer un PDF. Pas de compte, pas d'infonuagique.",
    "description": swapped(AS_FR_CA["description"], PLAY_FR_CA_SWAPS),
    "notes": release_block("google-play.md", "Français (Canada)"),
}


# --------------------------------------------------------------------------
# Rendering
# --------------------------------------------------------------------------

def block(text: str) -> str:
    return "```\n" + text.rstrip("\n") + "\n```\n"


def respace(copy: dict, punctuation: str) -> dict:
    """Every value of a French copy dictionary with its spacing rule applied."""
    out = {}
    for k, v in copy.items():
        if isinstance(v, str):
            out[k] = space_before(v, punctuation)
        elif isinstance(v, list) and v and isinstance(v[0], tuple):
            out[k] = [(f, space_before(c, punctuation)) for f, c in v]
        else:
            out[k] = [space_before(x, punctuation) for x in v]
    return out


def canada(copy: dict) -> dict:
    """Quebec's rule: U+00A0 before ":" only."""
    return respace(copy, ":")


def derive(copy: dict) -> dict:
    """The France variant of a Canadian copy dictionary."""
    out = {}
    for k, v in copy.items():
        if isinstance(v, str):
            out[k] = to_france(v)
        elif isinstance(v, list) and v and isinstance(v[0], tuple):
            out[k] = [(f, to_france(c)) for f, c in v]
        else:
            out[k] = [to_france(x) for x in v]
    return respace(out, ":?!;")


def ms_section(title: str, tag: str, c: dict, note: str = "") -> str:
    parts = [f"### {title} — `{tag}`\n", note]
    parts.append("**Short description / first line**\n" + block(c["short"]))
    parts.append("**Description** (plain text; the blank lines are paragraph breaks)\n" + block(c["description"]))
    parts.append(f"**App features** — one line per box, {len(c['features'])} boxes\n" + block("\n".join(c["features"])))
    parts.append("**Search terms** — one per box, seven boxes\n" + block("\n".join(c["terms"])))
    parts.append("**Copyright**\n" + block(c["copyright"]))
    parts.append("**Screenshot captions** — same six files, same order\n"
                 + block("\n".join(f"{f}: {cap}" for f, cap in c["captions"])))
    return "\n".join(p for p in parts if p)


def as_section(title: str, tag: str, c: dict, note: str = "") -> str:
    parts = [f"### {title} — `{tag}`\n", note]
    parts.append(f"**Name** [30] ({len(c['name'])})\n" + block(c["name"]))
    parts.append(f"**Subtitle** [30] ({len(c['subtitle'])})\n" + block(c["subtitle"]))
    parts.append(f"**Promotional text** [170] ({len(c['promo'])})\n" + block(c["promo"]))
    parts.append(f"**Description** [4000] ({len(c['description'])})\n" + block(c["description"]))
    parts.append(f"**Keywords** [100] ({len(c['keywords'])})\n" + block(c["keywords"]))
    parts.append(f"**What's new** [4000] ({len(c['whatsnew'])}) — {NOTES_VERSION}, from `docs/release-notes/{NOTES_VERSION}/app-store.md`\n"
                 + block(c["whatsnew"]))
    parts.append("**Screenshot captions** (optional overlay text), iPhone 6.9\" and iPad 13\" in this order\n"
                 + block("\n".join(f"{f}: {cap}" for f, cap in c["captions"])))
    return "\n".join(p for p in parts if p)


def mac_section(title: str, tag: str, c: dict) -> str:
    """The Mac platform's own fields, as a '### … — `mac-<locale>`' section that
    tools/asc_publish.py lays over the shared copy when ASC_PLATFORM=MAC_OS."""
    parts = [f"### Mac App Store — {title} — `mac-{tag}`\n",
             "*The Mac version's description and promotional text. Name, subtitle and "
             "keywords are the shared ones above.*\n"]
    parts.append(f"**Promotional text** [170] ({len(c['promo'])})\n" + block(c["promo"]))
    parts.append(f"**Description** [4000] ({len(c['description'])})\n" + block(c["description"]))
    return "\n".join(parts)


def play_section(title: str, tag: str, c: dict, note: str = "") -> str:
    parts = [f"### {title} — `{tag}`\n", note]
    parts.append(f"**Title** [30] ({len(c['title'])})\n" + block(c["title"]))
    parts.append(f"**Short description** [80] ({len(c['short'])})\n" + block(c["short"]))
    parts.append(f"**Full description** [4000] ({len(c['description'])})\n" + block(c["description"]))
    parts.append(f"**Release notes** [500] ({len(c['notes'])}) — {NOTES_VERSION}, from `docs/release-notes/{NOTES_VERSION}/google-play.md`\n"
                 + block(c["notes"]))
    return "\n".join(p for p in parts if p)


LIMITS_OK = [
    (len(MS_EN["short"]) <= 100 and len(MS_FR_CA["short"]) <= 100, "MS short"),
    (all(len(f) <= 200 for f in MS_EN["features"] + MS_FR_CA["features"]), "MS features ≤200"),
    (len(MS_EN["features"]) <= 20 and len(MS_FR_CA["features"]) <= 20, "MS at most 20 features"),
    (len(MS_EN["description"]) <= 10000 and len(MS_FR_CA["description"]) <= 10000, "MS description ≤10000"),
    (all(len(t) <= 30 for t in MS_EN["terms"] + MS_FR_CA["terms"]), "MS terms ≤30"),
    (all(len(c) <= 200 for _, c in MS_EN["captions"] + MS_FR_CA["captions"]), "MS captions ≤200"),
    (len(AS_EN["subtitle"]) <= 30 and len(AS_FR_CA["subtitle"]) <= 30, "AS subtitle ≤30"),
    (len(AS_EN["promo"]) <= 170 and len(AS_FR_CA["promo"]) <= 170, "AS promo ≤170"),
    (len(AS_EN["keywords"]) <= 100 and len(AS_FR_CA["keywords"]) <= 100, "AS keywords ≤100"),
    (len(AS_EN["description"]) <= 4000 and len(AS_FR_CA["description"]) <= 4000, "AS description ≤4000"),
    (len(PLAY_EN["title"]) <= 30 and len(PLAY_FR_CA["title"]) <= 30, "Play title ≤30"),
    (len(AS_MAC_EN["promo"]) <= 170 and len(AS_MAC_FR_CA["promo"]) <= 170, "Mac promo ≤170"),
    (len(AS_MAC_EN["description"]) <= 4000 and len(AS_MAC_FR_CA["description"]) <= 4000, "Mac description ≤4000"),
    (len(PLAY_EN["short"]) <= 80 and len(PLAY_FR_CA["short"]) <= 80, "Play short ≤80"),
    (len(PLAY_EN["description"]) <= 4000 and len(PLAY_FR_CA["description"]) <= 4000, "Play full description ≤4000"),
    (len(PLAY_EN["notes"]) <= 500 and len(PLAY_FR_CA["notes"]) <= 500, "Play notes ≤500"),
]

REVIEW_NOTE = ("*French copy was translated by the build assistant against "
               "`docs/localisation-glossary.md` and signed off by a francophone reviewer for 2.0 "
               "(2026-09-18, #242); new copy gets the same read before it goes live. "
               "The France variant is derived from the Canadian one by `tools/gen_listing_copy.py`.*\n")

MS_BODY = "\n".join([
    "## Copy by language\n",
    "Partner Center asks for one listing per language declared in the package: "
    "**English (United States)**, **English (Canada)**, **French (Canada)** and "
    "**French (France)**. Every field for every language is below as a block that "
    "pastes as-is — copy from inside the block only. The URLs, category and "
    "contact under *Other fields* are the same for all four.\n",
    REVIEW_NOTE,
    ms_section("English (United States)", "en-US", MS_EN),
    ms_section("English (Canada)", "en-CA", MS_EN,
               "*Identical to en-US — the copy carries no US-vs-CA spelling. Repeated so this section pastes on its own.*\n"),
    ms_section("Français (Canada)", "fr-CA", canada(MS_FR_CA)),
    ms_section("Français (France)", "fr-FR", derive(MS_FR_CA)),
])

AS_BODY = "\n".join([
    "## Copy by language\n",
    "App Store Connect → the version page → **Localizations**: **English (Canada)** "
    "is the primary language; add **French (Canada)** and **French**. Every field "
    "for every language is below as a block that pastes as-is. Character limits are "
    "in brackets with the actual count beside them.\n",
    REVIEW_NOTE,
    as_section("English (Canada)", "en-CA", AS_EN),
    as_section("Français (Canada)", "fr-CA", canada(AS_FR_CA)),
    # The France What's new is read from the release file, not derived here, so that a
    # hand-made France wording there (#242) survives; since 2.1.1 that file's own
    # check_copy.py derives it the same way this script derives the rest.
    as_section("Français", "fr", {**derive(AS_FR_CA), "whatsnew": release_block("app-store.md", "Français (France)")}),
    mac_section("English (Canada)", "en-CA", AS_MAC_EN),
    mac_section("Français (Canada)", "fr-CA", canada(AS_MAC_FR_CA)),
    mac_section("Français", "fr", derive(AS_MAC_FR_CA)),
])

PLAY_BODY = "\n".join([
    "## Play listing — copy by language\n",
    "Main store listing → **Manage translations** → add **French (Canada) – fr-CA** "
    "and **French (France) – fr-FR** beside the default **English (Canada) – en-CA**. "
    "`tools/play_submit.py` uploads binaries only; this text is pasted by hand. "
    "Every field for every language is below as a block that pastes as-is. "
    "Screenshots: the **Android Screenshots** workflow captures once per language "
    "(artifacts `play-screenshots-en`, `-fr-CA`, `-fr-FR`); the French runs set the "
    "app's per-app locale on the emulator and open the French demo agreement.\n",
    REVIEW_NOTE,
    play_section("English (Canada)", "en-CA", PLAY_EN),
    play_section("Français (Canada)", "fr-CA", canada(PLAY_FR_CA)),
    play_section("Français (France)", "fr-FR", {**derive(PLAY_FR_CA), "notes": release_block("google-play.md", "Français (France)")}),
])

START, END = "<!-- copy-by-language -->", "<!-- /copy-by-language -->"


def splice(path: Path, body: str) -> None:
    text = path.read_text(encoding="utf-8")
    if START not in text or END not in text:
        raise SystemExit(f"{path}: markers {START} / {END} not found")
    head, rest = text.split(START, 1)
    _, tail = rest.split(END, 1)
    path.write_text(head + START + "\n" + body + "\n" + END + tail, encoding="utf-8", newline="\n")
    print(f"{path.relative_to(ROOT)}: copy section written")


def main() -> int:
    failed = [name for ok, name in LIMITS_OK if not ok]
    if failed:
        raise SystemExit("over a store limit: " + ", ".join(failed))
    splice(ROOT / "docs/microsoft-store-listing.md", MS_BODY)
    splice(ROOT / "docs/app-store-listing.md", AS_BODY)
    splice(ROOT / "android/RELEASING.md", PLAY_BODY)
    return 0


if __name__ == "__main__":
    sys.exit(main())
