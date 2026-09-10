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

# --------------------------------------------------------------------------
# Microsoft Store
# --------------------------------------------------------------------------

MS_EN = {
    "short": "The lightweight PDF editor for Windows. Open. Fix. Save. Done.",
    "description": """Mega PDF is a free, lightweight PDF editor built for people who find the big PDF suites too bloated and complex. It does five things exceptionally well — and deliberately nothing else.

EDIT TEXT — Click any text in the document and type, like editing a Word file. Fix a typo, change a date, update a number. Done.

ADD TEXT — Click a blank line and type on it. Pick the size and the face — sans, serif or monospace — so what you add matches the rest of the form. Drag it into place, or double-click it later to change it.

CHECK BOXES — Click an empty square and it becomes a checked box. Forms that were never meant to be filled digitally, filled digitally.

APPLY SIGNATURES — Keep a small personal library of signature images. Pick one and click where it goes. Move it, nudge it, resize it until it sits exactly right.

SAVE — Save overwrites, Save As creates a copy. No export wizards, no "flatten" dialogs, no surprises.

Also included, because real documents need them: find any word with Ctrl+F (every match highlighted, Enter to step through them), print your PDF, and shrink oversized scans for email with one click (image downsampling and JPEG recompression).

PRIVATE BY DESIGN — No account. No cloud. No subscription. No telemetry. Every document is processed entirely on your device and never uploaded anywhere. Ideal for contracts, medical forms, and anything else you'd rather not hand to someone else's server.

Mega PDF is open source (Apache-2.0): github.com/SlyWombat/MegaPDF""",
    "features": [
        "Edit text in any PDF by clicking and typing — like a Word document",
        "Add text on any blank line, in the size and face that matches the form",
        "Click empty squares to check boxes on any form",
        "Place a signature from your personal library with a click, then nudge and resize it",
        "Find any word in the document with Ctrl+F — every match highlighted, Enter steps through them",
        "Save overwrites, Save As copies — no export wizards or flatten dialogs",
        "Shrink oversized scans for email with one click",
        "Print directly from the app",
        "100% local processing: no account, no cloud, no subscription, no telemetry",
    ],
    "terms": ["PDF editor", "edit PDF", "sign PDF", "fill PDF form", "PDF signature", "compress PDF", "lightweight PDF"],
    "copyright": "© 2026 Electric RV. Licensed under Apache-2.0.",
    "captions": [
        ("01-edit-text.png", "Click any text and type — editing a PDF like a Word file."),
        ("02-checkbox.png", "Click an empty square to check it. Drawn checkboxes too, not just real form fields."),
        ("03-signature.png", "Drop a saved signature on the line, then nudge it into place."),
        ("04-shrink.png", "Shrink oversized scans to email-friendly sizes in one click."),
        ("05-add-text.png", "Type on a blank line, in the size and face that matches the form."),
    ],
}

MS_FR_CA = {
    "short": "L'éditeur PDF léger pour Windows. Ouvrir. Corriger. Enregistrer. Terminé.",
    "description": """Mega PDF est un éditeur PDF gratuit et léger, conçu pour ceux qui trouvent les grandes suites PDF trop lourdes et trop compliquées. Il fait cinq choses exceptionnellement bien, et volontairement rien d'autre.

MODIFIER LE TEXTE — Cliquez sur n'importe quel texte du document et tapez, comme dans un document Word. Corrigez une coquille, changez une date, mettez un montant à jour. Terminé.

AJOUTER DU TEXTE — Cliquez sur une ligne vide et écrivez dessus. Choisissez la taille et la police (sans empattement, avec empattement ou à chasse fixe) pour que votre ajout s'accorde au reste du formulaire. Glissez-le en place, ou double-cliquez dessus plus tard pour le modifier.

COCHER DES CASES — Cliquez sur un carré vide et il devient une case cochée. Des formulaires jamais conçus pour être remplis à l'écran, remplis à l'écran.

APPOSER DES SIGNATURES — Gardez une petite bibliothèque personnelle d'images de signature. Choisissez-en une et cliquez à l'endroit voulu. Déplacez-la, ajustez-la, redimensionnez-la jusqu'à ce qu'elle soit exactement à sa place.

ENREGISTRER — Enregistrer remplace le fichier, Enregistrer sous en crée une copie. Pas d'assistant d'exportation, pas de boîte de dialogue d'« aplatissement », pas de surprise.

Aussi inclus, parce que les vrais documents en ont besoin : rechercher un mot avec Ctrl+F (chaque résultat surligné, Entrée pour passer au suivant), imprimer le PDF, et réduire les numérisations trop lourdes pour le courriel en un clic (sous-échantillonnage des images et recompression JPEG).

CONFIDENTIEL PAR CONCEPTION — Pas de compte. Pas d'infonuagique. Pas d'abonnement. Pas de télémétrie. Chaque document est traité entièrement sur votre appareil et n'est jamais téléversé nulle part. Idéal pour les contrats, les formulaires médicaux et tout ce que vous préférez ne pas confier au serveur de quelqu'un d'autre.

Mega PDF est un logiciel libre (Apache-2.0) : github.com/SlyWombat/MegaPDF""",
    "features": [
        "Modifiez le texte de n'importe quel PDF en cliquant et en tapant, comme dans un document Word",
        "Ajoutez du texte sur une ligne vide, dans la taille et la police du formulaire",
        "Cliquez sur les carrés vides pour cocher les cases de n'importe quel formulaire",
        "Apposez une signature de votre bibliothèque personnelle d'un clic, puis ajustez-la et redimensionnez-la",
        "Trouvez n'importe quel mot avec Ctrl+F : chaque résultat surligné, Entrée pour passer au suivant",
        "Enregistrer remplace, Enregistrer sous copie : pas d'assistant d'exportation ni de dialogue d'aplatissement",
        "Réduisez les numérisations trop lourdes pour le courriel en un clic",
        "Imprimez directement depuis l'application",
        "Traitement 100 % local : pas de compte, pas d'infonuagique, pas d'abonnement, pas de télémétrie",
    ],
    "terms": ["éditeur PDF", "modifier PDF", "signer PDF", "remplir formulaire PDF", "signature PDF", "compresser PDF", "PDF léger"],
    "copyright": "© 2026 Electric RV. Sous licence Apache-2.0.",
    "captions": [
        ("01-edit-text.png", "Cliquez sur n'importe quel texte et tapez : modifier un PDF comme un document Word."),
        ("02-checkbox.png", "Cliquez sur un carré vide pour le cocher. Les cases dessinées aussi, pas seulement les vrais champs de formulaire."),
        ("03-signature.png", "Déposez une signature enregistrée sur la ligne, puis ajustez-la en place."),
        ("04-shrink.png", "Réduisez les numérisations trop lourdes à une taille adaptée au courriel en un clic."),
        ("05-add-text.png", "Écrivez sur une ligne vide, dans la taille et la police qui correspondent au formulaire."),
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

Sign like you mean it
Draw your signature with a finger, or photograph the one on paper — the white background disappears automatically. Your signatures stay in a private library on your device; drop one onto any document, move and resize it until it sits right on the line.

Save without fear
Save writes back to the original file — safely. MegaPDF verifies every document before it touches your original, so a failed save can never corrupt the file someone sent you. Or keep the original and save a copy.

Find any word
Search the whole document as you type. Every match lights up and the counter tells you how many there are, so the one clause you need in a forty-page lease is a few taps away.

Private by design
MegaPDF requests zero permissions and makes zero network connections. Your documents and your signature never leave your device — there is no server for them to go to. The app is open source, so anyone can verify that.

Works with everything
Open PDFs from Mail, Files, iCloud Drive, Google Drive, or any app that shares files. Documents you fill and sign here open perfectly in Adobe Acrobat, desktop PDF apps, and MegaPDF for Windows and Android — same engine, same result, on every platform.

MegaPDF is deliberately simple. It doesn't rearrange pages, run OCR, or bury you in toolbars. It opens, it fixes, it saves. Done.""",
    "keywords": "pdf,sign,signature,fill,form,checkbox,esign,editor,search,document,annotate,fill and sign",
    "whatsnew": "MegaPDF now speaks French. The app follows your device language; you can also pick one under Settings → MegaPDF → Language.",
    "captions": [
        ("viewer", "Checked and signed in under a minute"),
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

Signez pour de vrai
Dessinez votre signature du doigt, ou photographiez celle sur papier : le fond blanc disparaît automatiquement. Vos signatures restent dans une bibliothèque privée sur votre appareil; déposez-en une sur n'importe quel document, déplacez-la et redimensionnez-la jusqu'à ce qu'elle soit bien sur la ligne.

Enregistrez sans crainte
Enregistrer écrit dans le fichier original, en toute sécurité. MegaPDF vérifie chaque document avant de toucher à votre original : un enregistrement raté ne peut jamais corrompre le fichier qu'on vous a envoyé. Ou gardez l'original et enregistrez une copie.

Trouvez n'importe quel mot
Cherchez dans tout le document à mesure que vous tapez. Chaque résultat s'allume et le compteur vous dit combien il y en a, pour que la seule clause dont vous avez besoin dans un bail de quarante pages soit à quelques touches.

Confidentiel par conception
MegaPDF ne demande aucune permission et n'établit aucune connexion réseau. Vos documents et votre signature ne quittent jamais votre appareil : il n'y a aucun serveur où ils pourraient aller. L'application est un logiciel libre; n'importe qui peut le vérifier.

Compatible avec tout
Ouvrez des PDF depuis Mail, Fichiers, iCloud Drive, Google Drive ou toute application qui partage des fichiers. Les documents remplis et signés ici s'ouvrent parfaitement dans Adobe Acrobat, dans les applications PDF de bureau, et dans MegaPDF pour Windows et Android : même moteur, même résultat, sur toutes les plateformes.

MegaPDF est volontairement simple. Il ne réorganise pas les pages, ne fait pas de reconnaissance de caractères et ne vous noie pas sous les barres d'outils. Il ouvre, il corrige, il enregistre. Terminé.""",
    "keywords": "pdf,signer,signature,remplir,formulaire,case,cocher,éditeur,recherche,document,annoter",
    "whatsnew": "MegaPDF parle maintenant français. L'application suit la langue de votre appareil; vous pouvez aussi la choisir dans Réglages → MegaPDF → Langue.",
    "captions": [
        ("viewer", "Coché et signé en moins d'une minute"),
        ("text", "Écrivez sur la ligne vide : votre taille, votre police"),
        ("search", "Trouvez n'importe quel mot, sur chaque page"),
        ("sign", "Vos signatures, enregistrées sur votre appareil"),
        ("draw", "Dessinez-la une fois, utilisez-la partout"),
        ("home", "Pas de compte. Pas d'infonuagique. Pas de suivi."),
    ],
}

# --------------------------------------------------------------------------
# Google Play — the App Store description with the Android app names.
# --------------------------------------------------------------------------

PLAY_EN = {
    "title": "MegaPDF",
    "short": "Fill, check and sign a PDF. No account, no cloud.",
    "description": AS_EN["description"]
        .replace("Mail, Files, iCloud Drive, Google Drive", "Gmail, Files, Google Drive")
        .replace("MegaPDF for Windows and Android", "MegaPDF for Windows and iOS"),
    "notes": "MegaPDF now speaks French. The app follows your device language; on Android 13 and later you can also pick one under Settings → Apps → MegaPDF → Language.",
}

PLAY_FR_CA = {
    "title": "MegaPDF",
    "short": "Remplir, cocher et signer un PDF. Pas de compte, pas d'infonuagique.",
    "description": AS_FR_CA["description"]
        .replace("Mail, Fichiers, iCloud Drive, Google Drive", "Gmail, Fichiers, Google Drive")
        .replace("MegaPDF pour Windows et Android", "MegaPDF pour Windows et iOS"),
    "notes": "MegaPDF parle maintenant français. L'application suit la langue de votre appareil; sur Android 13 et plus, vous pouvez aussi la choisir dans Paramètres → Applications → MegaPDF → Langue.",
}


# --------------------------------------------------------------------------
# Rendering
# --------------------------------------------------------------------------

def block(text: str) -> str:
    return "```\n" + text.rstrip("\n") + "\n```\n"


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
    return out


def ms_section(title: str, tag: str, c: dict, note: str = "") -> str:
    parts = [f"### {title} — `{tag}`\n", note]
    parts.append("**Short description / first line**\n" + block(c["short"]))
    parts.append("**Description** (plain text; the blank lines are paragraph breaks)\n" + block(c["description"]))
    parts.append("**App features** — one line per box, nine boxes\n" + block("\n".join(c["features"])))
    parts.append("**Search terms** — one per box, seven boxes\n" + block("\n".join(c["terms"])))
    parts.append("**Copyright**\n" + block(c["copyright"]))
    parts.append("**Screenshot captions** — same five files, same order\n"
                 + block("\n".join(f"{f}: {cap}" for f, cap in c["captions"])))
    return "\n".join(p for p in parts if p)


def as_section(title: str, tag: str, c: dict, note: str = "") -> str:
    parts = [f"### {title} — `{tag}`\n", note]
    parts.append(f"**Name** [30] ({len(c['name'])})\n" + block(c["name"]))
    parts.append(f"**Subtitle** [30] ({len(c['subtitle'])})\n" + block(c["subtitle"]))
    parts.append(f"**Promotional text** [170] ({len(c['promo'])})\n" + block(c["promo"]))
    parts.append(f"**Description** [4000] ({len(c['description'])})\n" + block(c["description"]))
    parts.append(f"**Keywords** [100] ({len(c['keywords'])})\n" + block(c["keywords"]))
    parts.append("**What's new** — for the release that first carries the language\n" + block(c["whatsnew"]))
    parts.append("**Screenshot captions** (optional overlay text), iPhone 6.9\" and iPad 13\" in this order\n"
                 + block("\n".join(f"{f}: {cap}" for f, cap in c["captions"])))
    return "\n".join(p for p in parts if p)


def play_section(title: str, tag: str, c: dict, note: str = "") -> str:
    parts = [f"### {title} — `{tag}`\n", note]
    parts.append(f"**Title** [30] ({len(c['title'])})\n" + block(c["title"]))
    parts.append(f"**Short description** [80] ({len(c['short'])})\n" + block(c["short"]))
    parts.append(f"**Full description** [4000] ({len(c['description'])})\n" + block(c["description"]))
    parts.append(f"**Release notes** [500] ({len(c['notes'])})\n" + block(c["notes"]))
    return "\n".join(p for p in parts if p)


LIMITS_OK = [
    (len(MS_EN["short"]) <= 100 and len(MS_FR_CA["short"]) <= 100, "MS short"),
    (all(len(f) <= 200 for f in MS_EN["features"] + MS_FR_CA["features"]), "MS features ≤200"),
    (all(len(t) <= 30 for t in MS_EN["terms"] + MS_FR_CA["terms"]), "MS terms ≤30"),
    (all(len(c) <= 200 for _, c in MS_EN["captions"] + MS_FR_CA["captions"]), "MS captions ≤200"),
    (len(AS_EN["subtitle"]) <= 30 and len(AS_FR_CA["subtitle"]) <= 30, "AS subtitle ≤30"),
    (len(AS_EN["promo"]) <= 170 and len(AS_FR_CA["promo"]) <= 170, "AS promo ≤170"),
    (len(AS_EN["keywords"]) <= 100 and len(AS_FR_CA["keywords"]) <= 100, "AS keywords ≤100"),
    (len(AS_EN["description"]) <= 4000 and len(AS_FR_CA["description"]) <= 4000, "AS description ≤4000"),
    (len(PLAY_EN["short"]) <= 80 and len(PLAY_FR_CA["short"]) <= 80, "Play short ≤80"),
    (len(PLAY_EN["notes"]) <= 500 and len(PLAY_FR_CA["notes"]) <= 500, "Play notes ≤500"),
]

REVIEW_NOTE = ("*French copy was translated by the build assistant against "
               "`docs/localisation-glossary.md`; have a francophone read it before it goes live. "
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
    ms_section("Français (Canada)", "fr-CA", MS_FR_CA),
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
    as_section("Français (Canada)", "fr-CA", AS_FR_CA),
    as_section("Français", "fr", derive(AS_FR_CA)),
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
    play_section("Français (Canada)", "fr-CA", PLAY_FR_CA),
    play_section("Français (France)", "fr-FR", derive(PLAY_FR_CA)),
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
