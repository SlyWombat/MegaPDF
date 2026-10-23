#!/usr/bin/env python3
"""Staging documents for the Microsoft Store screenshots.

Usage: python3 tools/screenshots-windows/gen_store_docs.py [outdir] [--lang fr-CA|fr-FR|es-419|es-ES]

--lang fr-CA / fr-FR writes the same two documents in French (a rental agreement,
so the French screenshots show French chrome over a French form, #91). "fr" alone
means fr-CA. The customer is a French name with accents per listing language (#146):
fr-CA Hélène Bélanger, fr-FR Céline Lefèvre, and the on-camera fix is the missing
accent ("Belanger" -> "Bélanger", "Lefevre" -> "Lefèvre") instead of English's
"Whitfeld" -> "Whitfield". fr-FR is its own text, not the Quebec page under a
French name (#310, Dave 2026-09-26): week-end for fin de semaine, enlèvement for
ramassage, metres and kilograms for feet and pounds, € for $. The
layout — every y coordinate, the box rows, the signature line — is identical,
so the harness coordinates read off the English frame still land.

--lang es-419 / es-ES writes them in Spanish (#724), the same rental agreement on the
same layout. The customer is Sofía Ramírez (es-419) or Lucía Fernández (es-ES), the
names Dave chose on #724, and the on-camera fix is again the missing accent
("Ramirez" -> "Ramírez", "Fernandez" -> "Fernández"). "es" alone means es-419, as the
neutral Spanish does on the desktops. The title, the opening paragraph, the three
checkbox labels and the signature line are the demo fixture's own wording
(tools/gen_test_fixtures.py DEMO_TEXT["es"] / ["es-ES"]), so the store images and the
Linux and phone demo documents say the same thing. Spain's page is es-419's with the
words that read as Latin American rewritten (alquiler for arrendamiento, recogida for
recolección, fianza, euros), by the substitution table below, plus its own metric
lines, so the two variants cannot drift apart in anything but those words.


blank-agreement.pdf — the Equipment Rental Agreement from tools/gen_test_fixtures.py
    gen_demo(), but *unfilled*: empty checkboxes, empty signature line, and a
    misspelled customer name to fix on camera. The text, sign and redact slots
    build up on this one file.
scanned-receipt.pdf — the same page rendered as a 300 DPI "scan" (one fat JPEG), so
    Shrink for email has something to actually shrink; the corpus PDFs have no images.
rental-terms.pdf — twelve pages of the same company's terms (#613). The one-page
    agreement cannot carry the three slots the 2.2 set added: reading mode's floating
    bar reads "1 / 1" on it and says nothing, the Pages pane shows a single tile
    rather than the grid Windows draws, and Find highlights one hit. Twelve numbered
    sections of prose give the bar a position worth showing ("4 / 12"), the pane a
    grid to fill, and Find a page with several hits on it at once. Its text is
    deliberately dull: the picture is the app, not the document.
"""
import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, os.path.join(REPO, "tools"))
from gen_test_fixtures import DEMO_TEXT, build, stream  # noqa: E402

from PIL import Image, ImageDraw, ImageFont  # noqa: E402

# Default alongside the shots themselves; both are build output, not fixtures.
ARGS = [a for a in sys.argv[1:] if not a.startswith("--")]
LANG_ARG = sys.argv[sys.argv.index("--lang") + 1] if "--lang" in sys.argv else "en"
if LANG_ARG == "es":
    LANG_ARG = "es-419"      # the neutral Spanish is Latin American on the desktops
if LANG_ARG not in ("en", "fr", "fr-CA", "fr-FR", "es-419", "es-ES"):
    sys.exit(f"unknown --lang {LANG_ARG}: use en, fr-CA, fr-FR, es-419 or es-ES")
IS_ES = LANG_ARG in ("es-419", "es-ES")
ES_NAME = {"es-ES": ("Lucía Fernandez", "Lucía Fernández")}.get(LANG_ARG, ("Sofía Ramirez", "Sofía Ramírez"))
FR_NAME = {"fr-FR": ("Céline Lefevre", "Céline Lefèvre")}.get(LANG_ARG, ("Hélène Belanger", "Hélène Bélanger"))
OUT = ARGS[0] if ARGS else os.path.join(REPO, "artifacts", "store", "screenshots")
os.makedirs(OUT, exist_ok=True)
ARIAL = "/mnt/c/Windows/Fonts/arial.ttf"
ARIALBD = "/mnt/c/Windows/Fonts/arialbd.ttf"

BODY_EN = [
    ("b", 22, 716, "Equipment Rental Agreement"),
    ("r", 11, 688, "This agreement is made between Sunrise Tool Rental and the customer named"),
    ("r", 11, 672, "below, covering the rental equipment, delivery options, and insurance terms"),
    ("r", 11, 656, "described in sections 1 through 4 of this document."),
    ("b", 13, 620, "Customer"),
    ("r", 12, 596, "Name: Jane Whitfeld"),
    ("r", 12, 576, "Rental period: March 14 to March 18"),
    ("b", 13, 540, "Options"),
    ("b", 13, 420, "Equipment"),
    ("r", 11, 396, "1 x 6000 lb telescopic forklift, propane, with side shift"),
    ("r", 11, 380, "2 x scaffold tower sections, 6 ft, with guard rails"),
    ("r", 11, 364, "1 x trailer, 12 ft, tandem axle, ramps included"),
    ("b", 13, 330, "Terms"),
    ("r", 11, 306, "Equipment is rented as inspected and is returned in the same condition,"),
    ("r", 11, 290, "normal wear excepted. Fuel is billed at cost on return. Late returns are"),
    ("r", 11, 274, "charged at the daily rate. Damage insurance, where accepted above, caps"),
    ("r", 11, 258, "the customer's liability at $500 per incident."),
    ("b", 13, 220, "Customer signature"),
]
BOXES_EN = [(508, "Include delivery and pickup"), (482, "Damage insurance accepted"),
            (456, "Extended weekend rate")]
LABELS_EN = {"sign": "Sign above the line", "date": "Date", "typo": "Whitfeld", "fixed": "Whitfield"}

BODY_FR = [
    ("b", 22, 716, "Contrat de location d'équipement"),
    ("r", 11, 688, "Le présent contrat est conclu entre Location d'outils Soleil Levant et le client"),
    ("r", 11, 672, "nommé ci-dessous et couvre l'équipement loué, les options de livraison et les"),
    ("r", 11, 656, "conditions d'assurance décrites aux sections 1 à 4 du présent document."),
    ("b", 13, 620, "Client"),
    ("r", 12, 596, "Nom : " + FR_NAME[0]),
    ("r", 12, 576, "Période de location : du 14 au 18 mars"),
    ("b", 13, 540, "Options"),
    ("b", 13, 420, "Équipement"),
    ("r", 11, 396, "1 x chariot télescopique 6000 lb, propane, avec déport latéral"),
    ("r", 11, 380, "2 x sections d'échafaudage, 6 pi, avec garde-corps"),
    ("r", 11, 364, "1 x remorque 12 pi, essieu tandem, rampes incluses"),
    ("b", 13, 330, "Conditions"),
    ("r", 11, 306, "L'équipement est loué tel qu'inspecté et rendu dans le même état, usure"),
    ("r", 11, 290, "normale exceptée. Le carburant est facturé au prix coûtant au retour. Les"),
    ("r", 11, 274, "retours tardifs sont facturés au tarif quotidien. L'assurance dommages, si"),
    ("r", 11, 258, "acceptée ci-dessus, limite la responsabilité du client à 500 $ par incident."),
    ("b", 13, 220, "Signature du client"),
]
BOXES_FR = [(508, "Livraison et ramassage inclus"), (482, "Assurance dommages acceptée"),
            (456, "Tarif fin de semaine prolongée")]
LABELS_FR = {"sign": "Signez au-dessus de la ligne", "date": "Date", "typo": FR_NAME[0], "fixed": FR_NAME[1]}

# France (#310): the Quebec page with the lines that read as Quebec rewritten, keyed
# by their y coordinate so nothing moves. Metric sizes near the imperial ones (6000 lb
# ~ 2.7 t, 6 ft ~ 1.8 m, 12 ft ~ 3.7 m), rounded the way a French hire catalogue would.
FR_FR_LINES = {
    396: "1 x chariot télescopique 3 000 kg, propane, avec déport latéral",
    380: "2 x sections d'échafaudage, 2 m, avec garde-corps",
    364: "1 x remorque 3,5 m, essieu tandem, rampes incluses",
    258: "acceptée ci-dessus, limite la responsabilité du client à 500 € par incident.",
}
BODY_FR_FR = [(kind, size, y, FR_FR_LINES.get(y, text)) for kind, size, y, text in BODY_FR]
BOXES_FR_FR = [(508, "Livraison et enlèvement inclus"), (482, "Assurance dommages acceptée"),
               (456, "Tarif week-end prolongé")]

# Spanish (#724). es-419 is written here; Spain's is es-419 put through ES_ES_WORDS,
# except the lines that differ in more than a word (ES_ES_LINES, keyed by y like France's).
# The title, opening paragraph, checkbox labels and signature line come from the demo
# fixture, which hand-writes both variants.
ES_T, ES_ES_T = DEMO_TEXT["es"], DEMO_TEXT["es-ES"]
BODY_ES = [
    ("b", 22, 716, ES_T["title"]),
    ("r", 11, 688, ES_T["p1"]),
    ("r", 11, 672, ES_T["p2"]),
    ("r", 11, 656, ES_T["p3"]),
    ("b", 13, 620, "Cliente"),
    ("r", 12, 596, "Nombre: " + ES_NAME[0]),
    ("r", 12, 576, "Periodo de arrendamiento: del 14 al 18 de marzo"),
    ("b", 13, 540, "Opciones"),
    ("b", 13, 420, "Equipo"),
    ("r", 11, 396, "1 x montacargas telescópico de 2,700 kg, propano, con desplazamiento lateral"),
    ("r", 11, 380, "2 x secciones de andamio, 1.8 m, con barandales"),
    ("r", 11, 364, "1 x remolque de 3.7 m, doble eje, rampas incluidas"),
    ("b", 13, 330, "Términos"),
    ("r", 11, 306, "El equipo se arrienda tal como fue inspeccionado y se devuelve en el mismo"),
    ("r", 11, 290, "estado, salvo el desgaste normal. El combustible se factura al costo en la"),
    ("r", 11, 274, "devolución. Las devoluciones tardías se cobran a la tarifa diaria. El seguro"),
    ("r", 11, 258, "contra daños, si se acepta arriba, limita la responsabilidad del cliente a $500."),
    ("b", 13, 220, ES_T["sig"]),
]
BOXES_ES = [(508, ES_T["box1"]), (482, ES_T["box2"]), (456, ES_T["box3"])]
LABELS_ES = {"sign": ES_T["line"], "date": "Fecha", "typo": ES_NAME[0], "fixed": ES_NAME[1]}

# Spain's usage, whole words only (so nothing fires inside another word): the Latin American
# word on the left, Spain's on the right. Applied to the terms text and the body alike.
ES_ES_WORDS = [
    ("Arrendamiento", "Alquiler"), ("arrendamiento", "alquiler"),
    ("arrienda", "alquila"), ("subarrienda", "subalquila"),
    ("Recolección", "Recogida"), ("recolección", "recogida"),
    ("dólares", "euros"), ("concreto", "hormigón"), ("monto", "importe"),
    ("depósito de garantía", "fianza"), ("días calendario", "días naturales"),
    ("proporciona", "facilita"), ("Términos", "Condiciones"), ("costo", "coste"),
]
ES_ES_LINES = {
    716: ES_ES_T["title"], 688: ES_ES_T["p1"], 672: ES_ES_T["p2"], 656: ES_ES_T["p3"],
    396: "1 x carretilla elevadora telescópica de 2.700 kg, propano, con desplazamiento lateral",
    380: "2 x tramos de andamio, 1,8 m, con barandillas",
    364: "1 x remolque de 3,7 m, eje tándem, rampas incluidas",
    258: "contra daños, si se acepta arriba, limita la responsabilidad del cliente a 500 €.",
}


def to_spain(text):
    for latin, spain in ES_ES_WORDS:
        text = re.sub(r"(?<!\w)" + re.escape(latin) + r"(?!\w)", spain, text)
    return text


BODY_ES_ES = [(kind, size, y, ES_ES_LINES.get(y) or to_spain(text)) for kind, size, y, text in BODY_ES]
BOXES_ES_ES = [(508, ES_ES_T["box1"]), (482, ES_ES_T["box2"]), (456, ES_ES_T["box3"])]
LABELS_ES_ES = {"sign": ES_ES_T["line"], "date": "Fecha", "typo": ES_NAME[0], "fixed": ES_NAME[1]}

BODY, BOXES, LABELS = {
    "en": (BODY_EN, BOXES_EN, LABELS_EN),
    "fr-FR": (BODY_FR_FR, BOXES_FR_FR, LABELS_FR),
    "es-419": (BODY_ES, BOXES_ES, LABELS_ES),
    "es-ES": (BODY_ES_ES, BOXES_ES_ES, LABELS_ES_ES),
}.get(LANG_ARG, (BODY_FR, BOXES_FR, LABELS_FR))
SIG_LINE_Y = 150

# -------------------------------------------------------------------- rental-terms
# Twelve pages of terms, for the reading, pages and search slots (#613). Each entry
# is a section heading and its paragraph lines, already wrapped: the page is 612 pt
# wide with a 72 pt margin, which is about 84 characters of 11 pt Helvetica.
#
# The find term is the ordinary word of the trade — "equipment" in English,
# "location" in both French variants — so the search slot shows a page with several
# hits on it rather than one lonely highlight. It is also a word with no accent in
# it, because the term is typed into the find box by the harness and text piped to
# powershell.exe from WSL arrives in the OEM code page (README, "Type the accents").
TERMS_TITLE_EN = "Rental Terms and Conditions"
TERMS_TITLE_FR = "Conditions de location"
TERMS_TITLE_ES = "Condiciones de arrendamiento"
# "equipo" in Spanish: the ordinary word of the trade, no accent, and on every page in
# both variants (the Spanish page says alquiler or arrendamiento, which differ).
FIND_TERM = {"en": "equipment", "es-419": "equipo", "es-ES": "equipo"}.get(LANG_ARG, "location")

TERMS_EN = [
    ("1. Scope of this agreement", [
        "These terms apply to every rental of equipment from Sunrise Tool Rental, and",
        "form part of the rental agreement signed by the customer. Where a signed",
        "agreement and these terms disagree, the signed agreement governs the rental",
        "period, the rates and the list of equipment, and these terms govern everything",
        "else. Nothing in this document varies a right the customer has by law.",
    ]),
    ("2. The equipment supplied", [
        "Equipment is supplied as inspected at the depot. The customer is invited to",
        "inspect every item before it leaves, and a note of any damage found at that",
        "point is written on the agreement by both parties. Equipment not inspected at",
        "the depot is taken to have left in the condition described in the agreement.",
        "Substitute equipment of an equivalent capacity may be supplied where an item",
        "is unavailable, and the rate does not change when it is.",
    ]),
    ("3. Delivery and collection", [
        "Delivery and collection are charged separately from the rental and are shown",
        "on the agreement where they were accepted. The customer provides clear access",
        "for a flatbed of up to nine metres and a firm surface to unload onto. Where",
        "access is refused or unsafe, the equipment returns to the depot and the",
        "delivery charge stands.",
    ]),
    ("4. The rental period", [
        "The rental period begins when the equipment leaves the depot and ends when it",
        "is back at the depot, not when the customer stops using it. A day is a",
        "twenty-four hour period. A week is seven of them. Equipment returned after the",
        "period on the agreement is charged at the daily rate for every further day,",
        "including the day of return.",
    ]),
    ("5. Use of the equipment", [
        "The customer uses the equipment only for the work it is made for, only within",
        "its rated capacity, and only by people competent to operate it. The equipment",
        "is not sublet, moved to another site, or taken out of the province without",
        "written agreement. Operating instructions and guards supplied with the",
        "equipment stay with it.",
    ]),
    ("6. Fuel, consumables and cleaning", [
        "Fuel is billed at cost on return, on the reading taken at the depot. Blades,",
        "bits, abrasives and other consumables are charged as used. Equipment returned",
        "with concrete, mortar or soil on it is cleaned at the depot and the cleaning is",
        "charged at the posted rate.",
    ]),
    ("7. Breakdown and repair", [
        "A breakdown is reported to the depot the same day. The customer does not",
        "attempt a repair, and does not instruct anybody else to attempt one. Where",
        "equipment fails through fair wear, a replacement is supplied and the rental",
        "continues on the replacement; no rental is charged for the time the customer",
        "was without working equipment.",
    ]),
    ("8. Damage and loss", [
        "The customer is responsible for the equipment from the moment it leaves the",
        "depot until it is back. That responsibility covers damage, theft and loss,",
        "however caused, except fair wear. Damage insurance, where it was accepted on",
        "the agreement, caps that responsibility at five hundred dollars for each",
        "incident; without it, the customer is responsible for the full cost of repair",
        "or replacement.",
    ]),
    ("9. Insurance the customer carries", [
        "The customer carries public liability insurance for the work the equipment is",
        "used for. Damage insurance offered on the agreement is not public liability",
        "insurance and is not a substitute for it: it covers the equipment itself and",
        "nothing else.",
    ]),
    ("10. Charges and payment", [
        "Rental, delivery, fuel, consumables and cleaning are invoiced on return and are",
        "payable within thirty days. An account not settled within that period carries",
        "interest at the posted rate. Where a deposit was taken, it is applied to the",
        "invoice and the balance returned.",
    ]),
    ("11. Ending the rental early", [
        "The customer may end a rental early by returning the equipment to the depot;",
        "the rental is then charged to the day of return, subject to any minimum period",
        "on the agreement. Sunrise Tool Rental may end a rental where the equipment is",
        "used outside these terms, and may collect it.",
    ]),
    ("12. Notices and governing law", [
        "A notice under these terms is given in writing, to the address on the",
        "agreement, and takes effect on delivery. This agreement is governed by the law",
        "of the province in which the depot is situated, and the courts of that province",
        "have jurisdiction over it.",
    ]),
]

TERMS_FR = [
    ("1. Portée du présent contrat", [
        "Les présentes conditions s'appliquent à toute location d'équipement auprès de",
        "Location d'outils Soleil Levant et font partie du contrat de location signé par",
        "le client. En cas de divergence, le contrat signé régit la période de location,",
        "les tarifs et la liste de l'équipement, et les présentes conditions régissent le",
        "reste. Rien ici ne retire au client un droit que la loi lui accorde.",
    ]),
    ("2. L'équipement fourni", [
        "L'équipement est fourni tel qu'inspecté au dépôt. Le client est invité à",
        "inspecter chaque article avant son départ, et toute avarie constatée à ce",
        "moment est inscrite au contrat par les deux parties. L'équipement non inspecté",
        "au dépôt est réputé être parti dans l'état décrit au contrat. Un équipement de",
        "remplacement de capacité équivalente peut être fourni si un article n'est pas",
        "disponible, et le tarif de la location ne change pas pour autant.",
    ]),
    ("3. Livraison et ramassage", [
        "La livraison et le ramassage sont facturés séparément de la location et",
        "figurent au contrat lorsqu'ils ont été acceptés. Le client fournit un accès",
        "dégagé pour un camion plateau de neuf mètres et une surface ferme pour le",
        "déchargement. Si l'accès est refusé ou dangereux, l'équipement retourne au",
        "dépôt et les frais de livraison restent dus.",
    ]),
    ("4. La période de location", [
        "La période de location commence au départ de l'équipement du dépôt et se",
        "termine à son retour au dépôt, et non lorsque le client cesse de s'en servir.",
        "Une journée est une période de vingt-quatre heures. Une semaine en compte sept.",
        "L'équipement rendu après la période prévue au contrat est facturé au tarif",
        "quotidien pour chaque journée supplémentaire, y compris celle du retour.",
    ]),
    ("5. Utilisation de l'équipement", [
        "Le client n'utilise l'équipement que pour les travaux auxquels il est destiné,",
        "dans les limites de sa capacité nominale, et seulement par des personnes",
        "compétentes pour le faire fonctionner. L'équipement n'est ni sous-loué, ni",
        "déplacé sur un autre chantier, ni sorti de la province sans accord écrit. Les",
        "consignes et les protecteurs fournis avec l'équipement restent avec lui.",
    ]),
    ("6. Carburant, consommables et nettoyage", [
        "Le carburant est facturé au prix coûtant au retour, selon le relevé pris au",
        "dépôt. Les lames, les mèches, les abrasifs et les autres consommables sont",
        "facturés à l'usage. L'équipement rendu couvert de béton, de mortier ou de terre",
        "est nettoyé au dépôt et le nettoyage est facturé au tarif affiché.",
    ]),
    ("7. Panne et réparation", [
        "Toute panne est signalée au dépôt le jour même. Le client ne tente aucune",
        "réparation et n'en fait tenter aucune par un tiers. Si l'équipement cesse de",
        "fonctionner par usure normale, un équipement de remplacement est fourni et la",
        "location se poursuit sur celui-ci; aucune location n'est facturée pour le temps",
        "pendant lequel le client a été privé d'un équipement en état de marche.",
    ]),
    ("8. Avaries et perte", [
        "Le client est responsable de l'équipement dès son départ du dépôt et jusqu'à",
        "son retour. Cette responsabilité couvre les avaries, le vol et la perte, quelle",
        "qu'en soit la cause, sauf l'usure normale. L'assurance dommages, lorsqu'elle a",
        "été acceptée au contrat, limite cette responsabilité à cinq cents dollars par",
        "incident; sans elle, le client répond du coût complet de la réparation ou du",
        "remplacement.",
    ]),
    ("9. L'assurance que le client détient", [
        "Le client détient une assurance de responsabilité civile pour les travaux",
        "auxquels l'équipement sert. L'assurance dommages offerte au contrat n'est pas",
        "une assurance de responsabilité civile et ne la remplace pas : elle couvre",
        "l'équipement lui-même et rien d'autre.",
    ]),
    ("10. Frais et paiement", [
        "La location, la livraison, le carburant, les consommables et le nettoyage sont",
        "facturés au retour et payables dans les trente jours. Un compte non réglé dans",
        "ce délai porte intérêt au taux affiché. Lorsqu'un dépôt de garantie a été versé,",
        "il est appliqué à la facture et le solde est remis au client.",
    ]),
    ("11. Fin anticipée de la location", [
        "Le client peut mettre fin à une location par anticipation en rapportant",
        "l'équipement au dépôt; la location est alors facturée jusqu'au jour du retour,",
        "sous réserve de toute période minimale prévue au contrat. Location d'outils",
        "Soleil Levant peut mettre fin à une location si l'équipement est utilisé en",
        "dehors des présentes conditions, et peut le reprendre.",
    ]),
    ("12. Avis et droit applicable", [
        "Un avis donné en vertu des présentes conditions est écrit, adressé à l'adresse",
        "figurant au contrat, et prend effet à sa remise. Le présent contrat est régi par",
        "le droit de la province où se trouve le dépôt, et les tribunaux de cette",
        "province ont compétence à son égard.",
    ]),
]

TERMS_ES = [
    ("1. Alcance de este contrato", [
        "Estas condiciones se aplican a todo arrendamiento de equipo de Arrendamiento de",
        "Herramientas Amanecer y forman parte del contrato de arrendamiento firmado por el",
        "cliente. Si el contrato firmado y estas condiciones no coinciden, rige el contrato",
        "firmado en cuanto al periodo, las tarifas y la lista de equipo, y estas condiciones",
        "rigen todo lo demás. Nada aquí limita un derecho que la ley otorga al cliente.",
    ]),
    ("2. El equipo suministrado", [
        "El equipo se suministra tal como fue inspeccionado en el almacén. Se invita al",
        "cliente a revisar cada artículo antes de que salga, y ambas partes anotan en el",
        "contrato cualquier daño que se encuentre en ese momento. El equipo que no se",
        "inspeccione en el almacén se considera salido en el estado que describe el contrato.",
        "Si un artículo no está disponible, puede suministrarse equipo sustituto de capacidad",
        "equivalente, y la tarifa no cambia por ello.",
    ]),
    ("3. Entrega y recolección", [
        "La entrega y la recolección se cobran aparte del arrendamiento y constan en el",
        "contrato cuando se aceptaron. El cliente proporciona acceso libre para una",
        "plataforma de hasta nueve metros y una superficie firme donde descargar. Si se",
        "niega el acceso o este es inseguro, el equipo vuelve al almacén y el cargo por",
        "entrega se mantiene.",
    ]),
    ("4. El periodo de arrendamiento", [
        "El periodo de arrendamiento comienza cuando el equipo sale del almacén y termina",
        "cuando regresa a él, no cuando el cliente deja de usarlo. Un día es un periodo de",
        "veinticuatro horas. Una semana son siete de ellos. El equipo devuelto después del",
        "periodo indicado en el contrato se cobra a la tarifa diaria por cada día adicional,",
        "incluido el día de la devolución.",
    ]),
    ("5. Uso del equipo", [
        "El cliente usa el equipo solo para el trabajo para el que fue fabricado, solo dentro",
        "de su capacidad nominal y solo mediante personas competentes para operarlo. El equipo",
        "no se subarrienda, no se traslada a otra obra ni se saca de la jurisdicción sin",
        "acuerdo por escrito. Las instrucciones de operación y las protecciones que acompañan",
        "al equipo permanecen con él.",
    ]),
    ("6. Combustible, consumibles y limpieza", [
        "El combustible se factura al costo en la devolución, según la lectura tomada en el",
        "almacén. Las cuchillas, las brocas, los abrasivos y otros consumibles se cobran",
        "según el uso. El equipo devuelto con concreto, mortero o tierra se limpia en el",
        "almacén y la limpieza se cobra a la tarifa publicada.",
    ]),
    ("7. Averías y reparaciones", [
        "Toda avería se avisa al almacén el mismo día. El cliente no intenta ninguna",
        "reparación ni encarga a otra persona que la intente. Si el equipo falla por desgaste",
        "normal, se suministra un reemplazo y el arrendamiento continúa con él; no se cobra",
        "arrendamiento por el tiempo en que el cliente estuvo sin equipo en funcionamiento.",
    ]),
    ("8. Daños y pérdida", [
        "El cliente es responsable del equipo desde que sale del almacén hasta que regresa.",
        "Esa responsabilidad abarca daños, robo y pérdida, sea cual sea su causa, salvo el",
        "desgaste normal. El seguro contra daños, si se aceptó en el contrato, limita esa",
        "responsabilidad a quinientos dólares por incidente; sin él, el cliente responde por",
        "el costo total de la reparación o de la reposición.",
    ]),
    ("9. El seguro que contrata el cliente", [
        "El cliente contrata un seguro de responsabilidad civil para el trabajo en el que se",
        "usa el equipo. El seguro contra daños que ofrece el contrato no es un seguro de",
        "responsabilidad civil ni lo sustituye: cubre el equipo mismo y nada más.",
    ]),
    ("10. Cargos y pago", [
        "El arrendamiento, la entrega, el combustible, los consumibles y la limpieza se",
        "facturan en la devolución y se pagan en un plazo de treinta días. Una cuenta no",
        "saldada en ese plazo genera intereses a la tasa publicada. Si se cobró un depósito",
        "de garantía, se aplica a la factura y se devuelve la diferencia.",
    ]),
    ("11. Terminación anticipada", [
        "El cliente puede terminar un arrendamiento antes de tiempo devolviendo el equipo al",
        "almacén; el arrendamiento se cobra entonces hasta el día de la devolución, sujeto a",
        "cualquier periodo mínimo indicado en el contrato. Arrendamiento de Herramientas",
        "Amanecer puede terminar un arrendamiento si el equipo se usa fuera de estas",
        "condiciones, y puede recogerlo.",
    ]),
    ("12. Avisos y ley aplicable", [
        "Todo aviso conforme a estas condiciones se da por escrito, a la dirección que figura",
        "en el contrato, y surte efecto al entregarse. Este contrato se rige por la ley de la",
        "jurisdicción en la que se encuentra el almacén, y los tribunales de esa jurisdicción",
        "son competentes respecto de él.",
    ]),
]

# The clauses that belong to every section of a terms document rather than to one of
# them. Three of the six go on each page, rotated by section number, so that a page is
# a full page of text and no two pages carry the same three. Without them each page
# ran out of words halfway down, and reading mode -- whose whole picture is a page --
# came out as a heading over a half-sheet of white paper.
COMMON_EN = [
    ["Where this section is read with any other, the two are read together, and the",
     "narrower of them governs the point they both cover. A heading is a label and",
     "does not limit what the section under it says.",
     "A term held to be unenforceable is severed and the rest of the agreement stands."],
    ["Time is not of the essence except where this agreement says it is. A day on which",
     "the depot is closed is not counted in a period of less than five days, and a period",
     "of five days or more is counted in calendar days including those on which the depot",
     "is closed."],
    ["Neither party is liable for a failure to perform caused by something outside its",
     "reasonable control, including weather that makes a site unsafe, a strike, or the",
     "closing of a road the equipment has to travel. The party affected tells the other",
     "as soon as it reasonably can, and the rental is suspended for as long as it lasts."],
    ["A variation of this agreement is effective only when it is in writing and signed by",
     "both parties, and a concession given once is not a variation and does not have to be",
     "given again. A right not exercised is not a right given up."],
    ["The customer may not assign this agreement or any right under it without written",
     "consent. Sunrise Tool Rental may assign it on notice, and the customer's rights are",
     "unaffected by an assignment."],
    ["Nothing in this agreement excludes liability for death or personal injury caused by",
     "negligence, or for anything else that cannot be excluded by law. Where a liability",
     "can be limited but not excluded, it is limited to the amount of the rental charged",
     "under the agreement it arises from."],
]

COMMON_FR = [
    ["Lorsque la présente section se lit avec une autre, les deux se lisent ensemble, et",
     "la plus étroite des deux régit le point qu'elles couvrent toutes les deux. Un titre",
     "est une étiquette et ne limite pas la portée de la section qu'il coiffe.",
     "Une clause jugée inapplicable est retranchée et le reste du contrat demeure."],
    ["Les délais ne sont pas de rigueur, sauf là où le contrat le dit. Un jour de",
     "fermeture du dépôt n'est pas compté dans un délai de moins de cinq jours, et un",
     "délai de cinq jours ou plus se compte en jours civils, y compris ceux où le dépôt",
     "est fermé."],
    ["Aucune des parties n'est responsable d'un manquement causé par un événement hors de",
     "son contrôle raisonnable, notamment des conditions météorologiques rendant un",
     "chantier dangereux, une grève ou la fermeture d'une route que l'équipement doit",
     "emprunter. La partie touchée en avise l'autre dès qu'elle le peut raisonnablement,",
     "et la location est suspendue pour la durée de l'événement."],
    ["Une modification du présent contrat n'a d'effet que si elle est écrite et signée par",
     "les deux parties; une tolérance accordée une fois n'est pas une modification et n'a",
     "pas à être accordée de nouveau. Un droit non exercé n'est pas un droit abandonné."],
    ["Le client ne peut céder le présent contrat ni aucun droit qui en découle sans accord",
     "écrit. Location d'outils Soleil Levant peut le céder sur avis, et les droits du",
     "client ne sont pas touchés par une cession."],
    ["Rien dans le présent contrat n'exclut la responsabilité en cas de décès ou de",
     "blessure causés par une négligence, ni pour tout autre cas que la loi ne permet pas",
     "d'exclure. Lorsqu'une responsabilité peut être limitée sans être exclue, elle est",
     "limitée au montant de la location facturée en vertu du contrat dont elle découle."],
]

COMMON_ES = [
    ["Cuando esta sección se lee junto con otra, ambas se leen en conjunto, y la más",
     "específica rige el punto que las dos abarcan. Un título es una etiqueta y no limita",
     "lo que dice la sección que encabeza.",
     "Una cláusula que se declare inaplicable se separa y el resto del contrato sigue vigente."],
    ["El plazo no es esencial, salvo donde este contrato diga que lo es. Un día en que el",
     "almacén está cerrado no se cuenta en un plazo de menos de cinco días, y un plazo de",
     "cinco días o más se cuenta en días calendario, incluidos aquellos en que el almacén",
     "está cerrado."],
    ["Ninguna de las partes responde por un incumplimiento causado por algo fuera de su",
     "control razonable, como el mal tiempo que vuelva inseguro un sitio, una huelga o el",
     "cierre de un camino por el que deba circular el equipo. La parte afectada avisa a la",
     "otra en cuanto razonablemente pueda, y el arrendamiento se suspende mientras dure."],
    ["Una modificación de este contrato solo surte efecto si consta por escrito y la firman",
     "ambas partes; una concesión otorgada una vez no es una modificación y no tiene que",
     "otorgarse de nuevo. Un derecho que no se ejerce no es un derecho al que se renuncia."],
    ["El cliente no puede ceder este contrato ni ningún derecho derivado de él sin",
     "consentimiento por escrito. Arrendamiento de Herramientas Amanecer puede cederlo con",
     "aviso, y los derechos del cliente no se ven afectados por una cesión."],
    ["Nada en este contrato excluye la responsabilidad por muerte o lesiones personales",
     "causadas por negligencia, ni por ninguna otra cosa que la ley no permita excluir.",
     "Cuando una responsabilidad pueda limitarse pero no excluirse, se limita al monto del",
     "arrendamiento cobrado conforme al contrato del que deriva."],
]

# France's own wording, on exactly the same layout (#310): the Quebec words that read
# as Quebec, and the euro. Applied as a substitution so the two French variants cannot
# drift apart in anything but these words.
FR_FR_WORDS = [
    ("ramassage", "enlèvement"), ("Ramassage", "Enlèvement"),
    ("fin de semaine", "week-end"),
    ("cinq cents dollars", "cinq cents euros"),
    ("Location d'outils Soleil Levant", "Location d'outils Soleil Levant"),
]


def terms_pages():
    """(title, [(heading, [paragraph, ...]), ...]) for the language being written.

    Each page is its section's own paragraph followed by four of the six common
    clauses, rotated by the section's number: a full page, and no two pages the same.
    """
    title, sections, common = {
        "en": (TERMS_TITLE_EN, TERMS_EN, COMMON_EN),
        "es-419": (TERMS_TITLE_ES, TERMS_ES, COMMON_ES),
        "es-ES": (TERMS_TITLE_ES, TERMS_ES, COMMON_ES),
    }.get(LANG_ARG, (TERMS_TITLE_FR, TERMS_FR, COMMON_FR))
    pages = []
    for index, (heading, lines) in enumerate(sections):
        rotated = [common[(index + n) % len(common)] for n in range(4)]
        pages.append((heading, [lines] + rotated))
    if LANG_ARG == "fr-FR":
        def fix(text):
            for a, b in FR_FR_WORDS:
                text = text.replace(a, b)
            return text
        pages = [(fix(h), [[fix(l) for l in p] for p in paras]) for h, paras in pages]
    if LANG_ARG == "es-ES":
        title = to_spain(title)
        pages = [(to_spain(h), [[to_spain(l) for l in p] for p in paras]) for h, paras in pages]
    return title, pages


def pdf_text(text):
    """A PDF string literal in WinAnsi (cp1252), so accents render in the base-14 faces."""
    raw = text.encode("cp1252")
    return raw.replace(b"\\", b"\\\\").replace(b"(", b"\\(").replace(b")", b"\\)")
def blank_agreement():
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]
    helv = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
    bold = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>")
    ops = []
    for kind, size, y, text in BODY:
        f = b"/F2" if kind == "b" else b"/F1"
        ops.append(b"BT %s %d Tf 72 %d Td (%s) Tj ET" % (f, size, y, pdf_text(text)))
    for y, label in BOXES:
        ops.append(b"1 w 0.13 0.13 0.13 RG 72 %d 13 13 re S" % y)
        ops.append(b"BT /F1 12 Tf 94 %d Td (%s) Tj ET" % (y + 3, pdf_text(label)))
    ops.append(b"0.6 w 0.4 0.4 0.4 RG 72 %d m 320 %d l S" % (SIG_LINE_Y, SIG_LINE_Y))
    ops.append(b"BT /F1 9 Tf 72 %d Td (%s) Tj ET" % (SIG_LINE_Y - 12, pdf_text(LABELS["sign"])))
    ops.append(b"0.6 w 0.4 0.4 0.4 RG 380 %d m 520 %d l S" % (SIG_LINE_Y, SIG_LINE_Y))
    ops.append(b"BT /F1 9 Tf 380 %d Td (%s) Tj ET" % (SIG_LINE_Y - 12, pdf_text(LABELS["date"])))
    content = add(stream(b"", b"\n".join(ops) + b"\n"))
    pages_num = len(objs) + 2
    page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
               b"/Resources << /Font << /F1 %d 0 R /F2 %d 0 R >> >> /Contents %d 0 R >>"
               % (pages_num, helv, bold, content))
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    assert pages == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages)
    return build(objs)


def scan_jpeg(dpi=400):
    """The same page, rendered as if it came off a flatbed: slight gray cast,
    paper speckle, a hair of skew. Big enough that Shrink has real work to do.

    The deliberate "Whitfeld" typo belongs only to the shot-1 edit story; the scan
    spells the name correctly so it can't be read as a typo in the Shrink shot."""
    import random
    random.seed(7)
    w, h = int(8.5 * dpi), int(11 * dpi)
    img = Image.new("RGB", (w, h), (252, 251, 247))
    d = ImageDraw.Draw(img)
    s = dpi / 72.0
    for kind, size, y, text in BODY:
        font = ImageFont.truetype(ARIALBD if kind == "b" else ARIAL, int(size * s))
        d.text((72 * s, (792 - y - size) * s), text.replace(LABELS["typo"], LABELS["fixed"]),
               font=font, fill=(28, 28, 32))
    for y, label in BOXES:
        d.rectangle([72 * s, (792 - y - 13) * s, 85 * s, (792 - y) * s], outline=(30, 30, 34), width=max(1, int(s)))
        d.text((94 * s, (792 - y - 12) * s), label, font=ImageFont.truetype(ARIAL, int(12 * s)), fill=(28, 28, 32))
    for x0, x1, label in ((72, 320, LABELS["sign"]), (380, 520, LABELS["date"])):
        d.line([x0 * s, (792 - SIG_LINE_Y) * s, x1 * s, (792 - SIG_LINE_Y) * s], fill=(90, 90, 95), width=max(1, int(s * 0.8)))
        d.text((x0 * s, (792 - SIG_LINE_Y + 12 - 9) * s), label, font=ImageFont.truetype(ARIAL, int(9 * s)), fill=(60, 60, 65))
    px = img.load()
    for _ in range(w * h // 120):                      # scanner speckle
        x, y = random.randrange(w), random.randrange(h)
        v = random.randrange(200, 245)
        px[x, y] = (v, v, v - 3)
    img = img.rotate(-0.35, resample=Image.BICUBIC, fillcolor=(252, 251, 247))
    buf = io.BytesIO()
    img.save(buf, "JPEG", quality=96, subsampling=0)
    return buf.getvalue(), img.size


def scanned_pdf():
    jpg, (iw, ih) = scan_jpeg()
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]
    im = add(b"<< /Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceRGB "
             b"/BitsPerComponent 8 /Filter /DCTDecode /Length %d >>\nstream\n" % (iw, ih, len(jpg))
             + jpg + b"\nendstream")
    content = add(stream(b"", b"q 612 0 0 792 0 0 cm /Im1 Do Q\n"))
    pages_num = len(objs) + 2
    page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
               b"/Resources << /XObject << /Im1 %d 0 R >> >> /Contents %d 0 R >>"
               % (pages_num, im, content))
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    assert pages == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages)
    return build(objs)


def rental_terms():
    """Twelve pages of terms (#613): a title, a section heading and its paragraph.

    One shared font pair and one shared Pages node, so every page is the same
    layout — the Pages pane's grid of thumbnails should read as one document
    rather than as twelve unrelated sheets.
    """
    title, sections = terms_pages()
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]
    helv = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
    bold = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>")

    contents = []
    for index, (heading, paragraphs) in enumerate(sections, start=1):
        ops = [b"BT /F1 9 Tf 72 740 Td (%s) Tj ET" % pdf_text(title),
               b"0.5 w 0.75 0.75 0.75 RG 72 732 m 540 732 l S",
               b"BT /F2 15 Tf 72 690 Td (%s) Tj ET" % pdf_text(heading)]
        y = 656
        for paragraph in paragraphs:
            for line in paragraph:
                ops.append(b"BT /F1 11 Tf 72 %d Td (%s) Tj ET" % (y, pdf_text(line)))
                y -= 18
            y -= 12
        # Bottom *right*, not centred: reading mode's floating bar sits across the
        # middle of the page's lower edge, and a centred "4 / 12" lands directly
        # under the bar's own "Page 4 of 12" -- two page counters stacked on the one
        # image the slot exists to show.
        ops.append(b"BT /F1 9 Tf 505 60 Td (%d / %d) Tj ET" % (index, len(sections)))
        contents.append(add(stream(b"", b"\n".join(ops) + b"\n")))

    pages_num = len(objs) + len(contents) + 1
    kids = [add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
                b"/Resources << /Font << /F1 %d 0 R /F2 %d 0 R >> >> /Contents %d 0 R >>"
                % (pages_num, helv, bold, content)) for content in contents]
    pages = add(b"<< /Type /Pages /Kids [%s] /Count %d >>"
                % (b" ".join(b"%d 0 R" % k for k in kids), len(kids)))
    assert pages == pages_num, (pages, pages_num)
    add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages)
    return build(objs)


for name, data in (("blank-agreement.pdf", blank_agreement()),
                   ("scanned-agreement.pdf", scanned_pdf()),
                   ("rental-terms.pdf", rental_terms())):
    p = os.path.join(OUT, name)
    open(p, "wb").write(data)
    print("wrote %s (%.2f MB)" % (name, len(data) / 1024 / 1024))
