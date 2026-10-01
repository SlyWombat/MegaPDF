#!/usr/bin/env python3
"""Generate shared engine-test fixture PDFs (SDD §6.2 parity fixtures).

Usage: python3 tools/gen_test_fixtures.py <outdir>
Writes:
  fixture.pdf - 2 pages, US Letter; page 1 has text and a stroked 12x12pt
                square at (72,600)-(84,612): a drawn-checkbox candidate.
  forms.pdf   - 1 page with one AcroForm checkbox widget "agree",
                rect (100,600)-(115,615), initially /Off.
  formtext.pdf- 1 page with one AcroForm TEXT field "fullname",
                rect (100,600)-(300,620), initially empty. Separate from
                forms.pdf so that shared fixture (and its committed copy under
                android/) does not drift for a desktop-only test.
  stamped.pdf - 1 page with two MegaPDF-style stamp annots (the SDD 6.2
                MegaPDF_Id contract): "sig:interop-1" at (100,500)-(190,560)
                and "mark:interop-2" at (72,600)-(84,612), each with an /AP
                appearance stream. Used by the iOS PDFKit spike (ADR-001)
                and future cross-platform interop tests.

  demo-pages.pdf, demo-fr-pages.pdf, demo-fr-FR-pages.pdf
              - the same filled agreement as page 1, followed by the four sections
                it refers to and a landscape rate schedule: six pages, per
                language. For the listing shots that need a document with more
                than one page in it — reading mode's page number and the Pages
                sidebar (#613). Page 1 is identical to demo*.pdf's.
  secure-source.pdf - the document tools/gen_security_fixtures.sh encrypts eight
                ways for #241: two pages of text, a filled text field "fullname"
                and a checked checkbox "agree", and a red square and a sticky note
                carrying MegaPDF_Ids. Removing protection must leave all of it alone.
  unused-tail.pdf - a document whose highest object number is one nothing refers
                to (#246): the shape under which PDFium's writer numbered a new
                encryption dictionary differently from the trailer naming it.
  cropped.pdf - CropBox [0 100 612 700] on a 612x792 MediaBox (#28/#30): the
                offset that makes user-space and rendered coordinates disagree.
  userunit.pdf - /UserUnit 2 with a CropBox offset (#150): cropped.pdf drawn in
                2-point units, so every size and coordinate the core reports is
                twice the user space value. Page 612x600 pt; "Hello MegaPDF" at
                36 pt with its baseline at 550 pt in crop space; a stroked 10x10 pt
                square at (100,400)-(110,410); a text field "fullname" at
                (100,300)-(300,320); a checkbox "agree" at (100,260)-(115,275); a 2x2 px image placed 100x50 pt at (300,100).
  textbox.pdf - a MegaPDFTextBox-marked text object with an id property (#34),
                so every platform can prove it reads boxes written elsewhere.
  doubled.pdf - lines drawn twice (fake bold, fill + stroke, shadow, a two-run
                line) whose second copy PDFium's text layer hides (#136).
  doubled-far.pdf - a line whose copies are drawn before it and far after it,
                hidden character by character rather than object by object (#136).
  softmask.pdf - paths, an image, a form and text under luminosity and alpha soft
                masks beneath a scaled, flipped page CTM (#140).
  pagecolours.pdf - the #509 page-tint fixture: every kind of pixel the sepia and
                night post-passes have to get right, at coordinates a 1:1 612x792
                render turns into exact pixel addresses. Page 612x792, one page,
                everything black-on-white or a flat known colour so a pixel is a
                value and not a sample of a gradient.

Deterministic output; both platforms' engine tests assert against these.
"""
import hashlib
import os
import sys


def build(objects):
    out = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
    offsets = [0]
    for i, body in enumerate(objects, start=1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % i + body + b"\nendobj\n"
    xref_pos = len(out)
    out += b"xref\n0 %d\n" % (len(objects) + 1)
    out += b"0000000000 65535 f \n"
    for off in offsets[1:]:
        out += b"%010d 00000 n \n" % off
    catalog = next(i for i, b in enumerate(objects, start=1) if b.startswith(b"<< /Type /Catalog"))
    out += (b"trailer\n<< /Size %d /Root %d 0 R >>\nstartxref\n%d\n%%%%EOF\n"
            % (len(objects) + 1, catalog, xref_pos))
    return bytes(out)


def stream(dict_extra, content):
    return b"<< %s /Length %d >>\nstream\n%s\nendstream" % (dict_extra, len(content), content)


def gen_fixture():
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]

    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    c1 = (b"BT /F1 24 Tf 72 720 Td (MegaPDF engine fixture - page 1) Tj ET\n"
          b"BT /F1 12 Tf 72 690 Td (The square below is a drawn checkbox candidate.) Tj ET\n"
          b"1 w 0.13 0.13 0.13 RG 72 600 12 12 re S\n")
    content1 = add(stream(b"", c1))
    content2 = add(stream(b"", b"BT /F1 24 Tf 72 720 Td (Page 2) Tj ET\n"))
    pages_num = len(objs) + 3
    page1 = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
                b"/Resources << /Font << /F1 %d 0 R >> >> /Contents %d 0 R >>"
                % (pages_num, font, content1))
    page2 = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
                b"/Resources << /Font << /F1 %d 0 R >> >> /Contents %d 0 R >>"
                % (pages_num, font, content2))
    pages = add(b"<< /Type /Pages /Kids [%d 0 R %d 0 R] /Count 2 >>" % (page1, page2))
    assert pages == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages)
    return build(objs)


def gen_forms():
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]

    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    content = add(stream(
        b"", b"BT /F1 14 Tf 72 720 Td (Tap the checkbox below.) Tj ET\n"))
    # Appearance streams for the widget's two states.
    ap_yes = add(stream(
        b"/Type /XObject /Subtype /Form /BBox [0 0 15 15]",
        b"0.13 0.13 0.13 RG 1 w 0.5 0.5 14 14 re S 1.6 w 3 3 m 12 12 l S 3 12 m 12 3 l S\n"))
    ap_off = add(stream(
        b"/Type /XObject /Subtype /Form /BBox [0 0 15 15]",
        b"0.13 0.13 0.13 RG 1 w 0.5 0.5 14 14 re S\n"))
    pages_num = len(objs) + 3
    widget = len(objs) + 2
    page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
               b"/Resources << /Font << /F1 %d 0 R >> >> /Contents %d 0 R "
               b"/Annots [%d 0 R] >>"
               % (pages_num, font, content, widget))
    w = add(b"<< /Type /Annot /Subtype /Widget /FT /Btn /T (agree) /V /Off /AS /Off "
            b"/Rect [100 600 115 615] /F 4 /P %d 0 R "
            b"/AP << /N << /Yes %d 0 R /Off %d 0 R >> >> >>"
            % (page, ap_yes, ap_off))
    assert w == widget
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    assert pages == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R /AcroForm << /Fields [%d 0 R] >> >>"
        % (pages, widget))
    return build(objs)


def gen_formtext():
    """One AcroForm TEXT field, which forms.pdf deliberately does not have.

    A separate fixture rather than an extra widget on forms.pdf: that file is a
    SDD 6.2 shared fixture with a committed copy under android/, so changing it
    risks silently diverging from Android's asserts for the sake of a
    desktop-only test.
    """
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]

    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    content = add(stream(
        b"", b"BT /F1 14 Tf 72 720 Td (Write your name in the box.) Tj ET\n"))
    ap = add(stream(
        b"/Type /XObject /Subtype /Form /BBox [0 0 200 20]",
        b"0.13 0.13 0.13 RG 1 w 0.5 0.5 199 19 re S\n"))
    pages_num = len(objs) + 3
    widget = len(objs) + 2
    page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
               b"/Resources << /Font << /F1 %d 0 R >> >> /Contents %d 0 R "
               b"/Annots [%d 0 R] >>"
               % (pages_num, font, content, widget))
    w = add(b"<< /Type /Annot /Subtype /Widget /FT /Tx /T (fullname) /V () "
            b"/DA (/Helv 12 Tf 0 g) /Rect [100 600 300 620] /F 4 /P %d 0 R "
            b"/AP << /N %d 0 R >> >>"
            % (page, ap))
    assert w == widget
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    assert pages == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R /AcroForm << /Fields [%d 0 R] "
        b"/DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv %d 0 R >> >> >> >>"
        % (pages, widget, font))
    return build(objs)


def gen_stamped():
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]

    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    content = add(stream(
        b"", b"BT /F1 14 Tf 72 720 Td (MegaPDF_Id interop fixture.) Tj ET\n"
             b"1 w 0.13 0.13 0.13 RG 72 600 12 12 re S\n"))
    # Signature-style stamp appearance: a solid dark block.
    ap_sig = add(stream(
        b"/Type /XObject /Subtype /Form /BBox [0 0 90 60]",
        b"0.13 0.19 0.56 rg 5 5 80 50 re f\n"))
    # Check-mark-style appearance: the X strokes.
    ap_mark = add(stream(
        b"/Type /XObject /Subtype /Form /BBox [0 0 12 12]",
        b"0.13 0.13 0.13 RG 1.3 w 1.2 1.2 m 10.8 10.8 l S 1.2 10.8 m 10.8 1.2 l S\n"))
    pages_num = len(objs) + 4
    sig = len(objs) + 2
    mark = len(objs) + 3
    page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
               b"/Resources << /Font << /F1 %d 0 R >> >> /Contents %d 0 R "
               b"/Annots [%d 0 R %d 0 R] >>"
               % (pages_num, font, content, sig, mark))
    s = add(b"<< /Type /Annot /Subtype /Stamp /Rect [100 500 190 560] /F 4 "
            b"/MegaPDF_Id (sig:interop-1) /AP << /N %d 0 R >> >>" % ap_sig)
    m = add(b"<< /Type /Annot /Subtype /Stamp /Rect [72 600 84 612] /F 4 "
            b"/MegaPDF_Id (mark:interop-2) /AP << /N %d 0 R >> >>" % ap_mark)
    assert (s, m) == (sig, mark)
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    assert pages == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages)
    return build(objs)


def _squiggle_ops(x0, y0, w, h):
    """Cursive-ish signature stroke as PDF path segments."""
    import math
    ops = []
    n = 60
    pts = []
    for i in range(n + 1):
        t = i / n
        x = x0 + w * (t + 0.04 * math.sin(6 * math.pi * t))
        y = (y0 + h * 0.5
             + h * 0.42 * math.sin(2 * math.pi * (1.7 * t + 0.1))
             * (1 - 0.5 * t)
             + h * 0.18 * math.sin(2 * math.pi * (5 * t)))
        pts.append((x, y))
    ops.append(b"%.1f %.1f m" % pts[0])
    for p in pts[1:]:
        ops.append(b"%.1f %.1f l" % p)
    ops.append(b"S")
    return b" ".join(ops)


DEMO_TEXT = {
    # The English is the original; the French is the same page translated
    # (#91), same geometry, so the screenshot tap points and the search term's
    # three hits ("location": title, "Location d'outils", "de location") line up.
    #
    # "fr" is Canadian French, the hand-written listing language; "fr-FR" is
    # France (#310): the same page in France usage, written here rather than
    # derived, because gen_strings.py's table is for the app's chrome. Both keep
    # the paragraph the Redact poses key on ("sections 1 à 4", #415) on its own
    # line, and "location" three times. Dave, 2026-09-26: week-end, metric, €.
    "en": {
        "title": "Equipment Rental Agreement",
        "p1": "This agreement is made between Sunrise Tool Rental and the customer named",
        "p2": "below, covering the rental equipment, delivery options, and insurance terms",
        "p3": "described in sections 1 through 4 of this document.",
        "options": "Options",
        "box1": "Include delivery and pickup",
        "box2": "Damage insurance accepted",
        "box3": "Extended weekend rate",
        "sig": "Customer signature",
        "line": "Sign above the line",
        "sections": [
            {"title": "Section 1 \u2014 The equipment",
             "lines": ["The customer confirms receiving the equipment listed below in good",
                       "working order, and agrees to return it in the same condition.",
                       "",
                       "10 in. mitre saw, with blade and fence",
                       "6 gallon portable compressor",
                       "Rolling scaffold, two sections",
                       "3,500 W petrol generator",
                       "",
                       "Anything missing on return is billed at the replacement rate."]},
            {"title": "Section 2 \u2014 Delivery and pickup",
             "lines": ["Delivery and pickup are included within 15 miles of the counter.",
                       "Beyond that, a mileage charge applies.",
                       "",
                       "The customer provides a clear 6 ft approach and somebody on site",
                       "at the agreed hour."],
             "note": ["Unattended pickup is refused: the equipment stays the",
                      "customer's responsibility until it is collected."]},
            {"title": "Section 3 \u2014 The insurance",
             "lines": ["Damage insurance covers accidental breakage up to $5,000 per",
                       "agreement, with a $250 deductible.",
                       "",
                       "It does not cover theft without evidence of forced entry, use",
                       "beyond the load limits on the machine's own plate, or any loss",
                       "caused by freezing.",
                       "",
                       "A customer who declines the insurance stays liable for the full",
                       "replacement value."]},
            {"title": "Section 4 \u2014 The payment",
             "lines": ["The rate runs from the hour of delivery to the hour of pickup,",
                       "weekends included.",
                       "",
                       "A $500 deposit is held on the card when the equipment leaves,",
                       "and released within five business days of its return."]},
            {"title": "Rate schedule",
             "table": [("Item", "Day", "Weekend", "Week"),
                       ("10 in. mitre saw", "$32", "$58", "$128"),
                       ("6 gallon compressor", "$28", "$49", "$110"),
                       ("Scaffold, 2 sections", "$45", "$80", "$175"),
                       ("3,500 W generator", "$60", "$108", "$240")]},
        ],
    },
    "fr": {
        "title": "Contrat de location d'\u00e9quipement",
        "p1": "Le pr\u00e9sent contrat est conclu entre Location d'outils Soleil Levant et le client",
        "p2": "nomm\u00e9 ci-dessous et couvre l'\u00e9quipement de location, les options de livraison",
        "p3": "et les conditions d'assurance d\u00e9crites aux sections 1 \u00e0 4 du pr\u00e9sent document.",
        "options": "Options",
        "box1": "Livraison et ramassage inclus",
        "box2": "Assurance dommages accept\u00e9e",
        "box3": "Tarif fin de semaine prolong\u00e9e",
        "sig": "Signature du client",
        "line": "Signez au-dessus de la ligne",
        "sections": [
            {"title": "Section 1 \u2014 L'\u00e9quipement",
             "lines": ["Le locataire reconna\u00eet avoir re\u00e7u l'\u00e9quipement d\u00e9crit ci-dessous en bon",
                       "\u00e9tat de marche et s'engage \u00e0 le rendre dans le m\u00eame \u00e9tat.",
                       "",
                       "Scie \u00e0 onglets 10 po, avec lame et guide",
                       "Compresseur portatif 6 gallons",
                       "\u00c9chafaudage roulant, deux sections",
                       "G\u00e9n\u00e9ratrice 3 500 W, essence",
                       "",
                       "Tout manque constat\u00e9 au retour est factur\u00e9 au tarif de remplacement."]},
            {"title": "Section 2 \u2014 Livraison et ramassage",
             "lines": ["La livraison et le ramassage sont inclus dans un rayon de 25 km du",
                       "comptoir. Au-del\u00e0, des frais de kilom\u00e9trage s'appliquent.",
                       "",
                       "Le locataire doit pr\u00e9voir un acc\u00e8s d\u00e9gag\u00e9 de 6 pi de largeur et",
                       "une personne sur place \u00e0 l'heure convenue."],
             "note": ["Le ramassage non assist\u00e9 est refus\u00e9 : l'\u00e9quipement reste sous",
                      "la responsabilit\u00e9 du locataire jusqu'\u00e0 sa reprise."]},
            {"title": "Section 3 \u2014 L'assurance",
             "lines": ["L'assurance dommages couvre la casse accidentelle jusqu'\u00e0 5 000 $",
                       "par contrat, franchise de 250 $.",
                       "",
                       "Elle ne couvre pas le vol sans effraction constat\u00e9e, l'usage hors",
                       "des limites de charge indiqu\u00e9es sur la plaque de l'appareil, ni",
                       "les pertes attribuables au gel.",
                       "",
                       "Le locataire qui refuse l'assurance demeure responsable de la",
                       "valeur de remplacement int\u00e9grale."]},
            {"title": "Section 4 \u2014 Le paiement",
             "lines": ["Le tarif court du moment de la livraison au moment du ramassage,",
                       "fins de semaine comprises.",
                       "",
                       "Un d\u00e9p\u00f4t de 500 $ est port\u00e9 \u00e0 la carte au d\u00e9part de l'\u00e9quipement",
                       "et remis dans les cinq jours ouvrables suivant le retour."]},
            {"title": "Tarifs \u2014 annexe",
             "table": [("Article", "Jour", "Fin de semaine", "Semaine"),
                       ("Scie \u00e0 onglets 10 po", "32 $", "58 $", "128 $"),
                       ("Compresseur 6 gallons", "28 $", "49 $", "110 $"),
                       ("\u00c9chafaudage, 2 sections", "45 $", "80 $", "175 $"),
                       ("G\u00e9n\u00e9ratrice 3 500 W", "60 $", "108 $", "240 $")]},
        ],
    },
    "fr-FR": {
        "title": "Contrat de location d'\u00e9quipement",
        "p1": "Le pr\u00e9sent contrat est conclu entre Location d'outils Soleil Levant et le client",
        "p2": "nomm\u00e9 ci-dessous et couvre l'\u00e9quipement de location, les options de livraison",
        "p3": "et les conditions d'assurance d\u00e9crites aux sections 1 \u00e0 4 du pr\u00e9sent document.",
        "options": "Options",
        "box1": "Livraison et enl\u00e8vement inclus",
        "box2": "Assurance dommages accept\u00e9e",
        "box3": "Tarif week-end prolong\u00e9",
        "sig": "Signature du client",
        "line": "Signez au-dessus de la ligne",
        "sections": [
            {"title": "Section 1 \u2014 Le mat\u00e9riel",
             "lines": ["Le locataire reconna\u00eet avoir re\u00e7u le mat\u00e9riel d\u00e9crit ci-dessous en",
                       "bon \u00e9tat de marche et s'engage \u00e0 le rendre dans le m\u00eame \u00e9tat.",
                       "",
                       "Scie \u00e0 onglets 250 mm, avec lame et guide",
                       "Compresseur portatif 24 litres",
                       "\u00c9chafaudage roulant, deux sections",
                       "Groupe \u00e9lectrog\u00e8ne 3 500 W, essence",
                       "",
                       "Tout manque constat\u00e9 au retour est factur\u00e9 au tarif de remplacement."]},
            {"title": "Section 2 \u2014 Livraison et enl\u00e8vement",
             "lines": ["La livraison et l'enl\u00e8vement sont inclus dans un rayon de 25 km du",
                       "comptoir. Au-del\u00e0, des frais kilom\u00e9triques s'appliquent.",
                       "",
                       "Le locataire doit pr\u00e9voir un acc\u00e8s d\u00e9gag\u00e9 de 2 m de largeur et",
                       "une personne sur place \u00e0 l'heure convenue."],
             "note": ["L'enl\u00e8vement non assist\u00e9 est refus\u00e9 : le mat\u00e9riel reste sous",
                      "la responsabilit\u00e9 du locataire jusqu'\u00e0 sa reprise."]},
            {"title": "Section 3 \u2014 L'assurance",
             "lines": ["L'assurance dommages couvre la casse accidentelle jusqu'\u00e0 5 000 \u20ac",
                       "par contrat, franchise de 250 \u20ac.",
                       "",
                       "Elle ne couvre pas le vol sans effraction constat\u00e9e, l'usage hors",
                       "des limites de charge indiqu\u00e9es sur la plaque de l'appareil, ni",
                       "les pertes attribuables au gel.",
                       "",
                       "Le locataire qui refuse l'assurance demeure responsable de la",
                       "valeur de remplacement int\u00e9grale."]},
            {"title": "Section 4 \u2014 Le paiement",
             "lines": ["Le tarif court de l'heure de livraison \u00e0 l'heure de l'enl\u00e8vement,",
                       "week-ends compris.",
                       "",
                       "Un d\u00e9p\u00f4t de 500 \u20ac est bloqu\u00e9 sur la carte au d\u00e9part du mat\u00e9riel",
                       "et lib\u00e9r\u00e9 dans les cinq jours ouvr\u00e9s suivant le retour."]},
            {"title": "Tarifs \u2014 annexe",
             "table": [("Article", "Jour", "Week-end", "Semaine"),
                       ("Scie \u00e0 onglets 250 mm", "32 \u20ac", "58 \u20ac", "128 \u20ac"),
                       ("Compresseur 24 litres", "28 \u20ac", "49 \u20ac", "110 \u20ac"),
                       ("\u00c9chafaudage, 2 sections", "45 \u20ac", "80 \u20ac", "175 \u20ac"),
                       ("Groupe \u00e9lectrog\u00e8ne", "60 \u20ac", "108 \u20ac", "240 \u20ac")]},
        ],
    },
}


def _winansi(text):
    """A PDF string literal in WinAnsi (cp1252), so accents render in the base-14 faces."""
    raw = text.encode("cp1252")
    return raw.replace(b"\\", b"\\\\").replace(b"(", b"\\(").replace(b")", b"\\)")


def _section_ops(section, helv, bold):
    """One continuation page of the demo agreement, as content-stream operators.

    Three shapes, because the point of these pages is to be told apart as
    *thumbnails*: a heading over body lines, the same with a framed note under
    it, and a ruled table on a landscape page. At 104 px wide — the width of a
    row in the Pages sidebar (PageViewModel.ThumbnailBoxWidth) — the words are
    long gone and the shape is all that is left, so a strip of five pages that
    were laid out identically would read as one page copied five times.

    Returns (operators, width, height) in points.
    """
    if "table" in section:
        # Landscape, and that is the second thing the strip says: a page can
        # arrive the other way up, which is what Rotate is for.
        width, height = 792.0, 612.0
        ops = [b"BT /F2 20 Tf 72 520 Td (%s) Tj ET" % _winansi(section["title"])]
        rows = section["table"]
        columns = (72, 360, 480, 600)
        top = 480
        for r, row in enumerate(rows):
            y = top - r * 26
            face = b"/F2 11" if r == 0 else b"/F1 11"
            for x, cell in zip(columns, row):
                ops.append(b"BT %s Tf %d %d Td (%s) Tj ET" % (face, x, y, _winansi(cell)))
            # A rule under the heading row and under each line of the body.
            ops.append(b"%.1f w 0.6 0.6 0.6 RG 72 %d m 720 %d l S"
                       % (1.2 if r == 0 else 0.4, y - 8, y - 8))
        return ops, width, height

    width, height = 612.0, 792.0
    ops = [b"BT /F2 20 Tf 72 700 Td (%s) Tj ET" % _winansi(section["title"]),
           b"1.2 w 0.6 0.6 0.6 RG 72 688 m 540 688 l S"]
    y = 652
    for line in section["lines"]:
        if line:
            ops.append(b"BT /F1 11 Tf 72 %d Td (%s) Tj ET" % (y, _winansi(line)))
        y -= 18
    if "note" in section:
        note_top = y - 18
        depth = 18 * len(section["note"]) + 20
        ops.append(b"0.8 w 0.4 0.4 0.4 RG 72 %.0f 468 %d re S"
                   % (note_top - depth, depth))
        for i, line in enumerate(section["note"]):
            ops.append(b"BT /F1 11 Tf 88 %.0f Td (%s) Tj ET"
                       % (note_top - 28 - i * 18, _winansi(line)))
    return ops, width, height


def gen_demo(lang="en", filled=True, sections=False):
    """One-page 'filled agreement' used for App Store screenshots: real
    MegaPDF-style artifacts (mark:/sig: annots tagged MegaPDF_Id).
    `lang` picks the page's language (DEMO_TEXT); the layout is identical.
    `filled=False` writes the same page with nothing on it — no ticks, no
    signature — for the preview video, which fills it in on camera.

    `sections=True` keeps that same filled page as page 1 and adds the four
    sections it refers to ("sections 1 through 4") plus a landscape rate
    schedule — six pages. Page 1 is byte-for-byte the page `sections=False`
    writes, which is the point: the reading-mode and page-tools listing shots
    photograph the same agreement every other shot in the set does, in a
    document that has enough pages for a thumbnail strip and a page number to
    mean anything (#613). Only with `filled`: the blank variant is the preview
    video's, and the video fills in one page."""
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]
    t = DEMO_TEXT[lang]

    helv = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
    bold = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>")
    body = [
        b"BT /F2 22 Tf 72 716 Td (%s) Tj ET" % _winansi(t["title"]),
        b"BT /F1 11 Tf 72 688 Td (%s) Tj ET" % _winansi(t["p1"]),
        b"BT /F1 11 Tf 72 672 Td (%s) Tj ET" % _winansi(t["p2"]),
        b"BT /F1 11 Tf 72 656 Td (%s) Tj ET" % _winansi(t["p3"]),
        b"BT /F2 13 Tf 72 616 Td (%s) Tj ET" % _winansi(t["options"]),
        # three drawn checkboxes
        b"1 w 0.13 0.13 0.13 RG 72 584 13 13 re S",
        b"BT /F1 12 Tf 94 587 Td (%s) Tj ET" % _winansi(t["box1"]),
        b"1 w 0.13 0.13 0.13 RG 72 558 13 13 re S",
        b"BT /F1 12 Tf 94 561 Td (%s) Tj ET" % _winansi(t["box2"]),
        b"1 w 0.13 0.13 0.13 RG 72 532 13 13 re S",
        b"BT /F1 12 Tf 94 535 Td (%s) Tj ET" % _winansi(t["box3"]),
        b"BT /F2 13 Tf 72 484 Td (%s) Tj ET" % _winansi(t["sig"]),
        b"0.6 w 0.4 0.4 0.4 RG 72 400 m 320 400 l S",
        b"BT /F1 9 Tf 72 388 Td (%s) Tj ET" % _winansi(t["line"]),
    ]
    content = add(stream(b"", b"\n".join(body) + b"\n"))

    if not filled:
        pages_num = len(objs) + 2
        page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
                   b"/Resources << /Font << /F1 %d 0 R /F2 %d 0 R >> >> /Contents %d 0 R >>"
                   % (pages_num, helv, bold, content))
        pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
        assert pages == pages_num
        add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages)
        return build(objs)

    def mark_ap(size):
        inset = size * 0.10
        lw = max(1.2, size * 0.11)
        return stream(
            b"/Type /XObject /Subtype /Form /BBox [0 0 %.1f %.1f]" % (size, size),
            b"0.13 0.13 0.13 RG %.2f w %.1f %.1f m %.1f %.1f l S %.1f %.1f m %.1f %.1f l S\n"
            % (lw, inset, inset, size - inset, size - inset,
               inset, size - inset, size - inset, inset))

    ap1 = add(mark_ap(13.0))
    ap2 = add(mark_ap(13.0))
    # Handwritten "MegaWoman" signature: the pre-rendered JPEG (white background
    # is invisible over the white page) embedded as an image XObject; falls back
    # to the parametric squiggle if the asset is missing.
    sig_asset = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                             "assets", "megawoman-sig.jpg")
    if os.path.exists(sig_asset):
        jpg = open(sig_asset, "rb").read()
        img = add(b"<< /Type /XObject /Subtype /Image /Width 495 /Height 149 "
                  b"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode "
                  b"/Length %d >>\nstream\n" % len(jpg) + jpg + b"\nendstream")
        sig_ap = add(stream(
            b"/Type /XObject /Subtype /Form /BBox [0 0 233 70] "
            b"/Resources << /XObject << /Im1 %d 0 R >> >>" % img,
            b"q 233 0 0 70 0 0 cm /Im1 Do Q\n"))
    else:
        sig_ap = add(stream(
            b"/Type /XObject /Subtype /Form /BBox [0 0 233 70]",
            b"0.10 0.12 0.35 RG 2.0 w " + _squiggle_ops(8, 8, 210, 52) + b"\n"))

    # The continuation pages' content streams first, so the page objects that
    # point at them are numbered together with page 1 — the arithmetic below is
    # the only thing holding this file's object numbers together.
    extra = [(add(stream(b"", b"\n".join(ops) + b"\n")), w, h)
             for ops, w, h in (_section_ops(s, helv, bold)
                               for s in (t["sections"] if sections else []))]

    pages_num = len(objs) + 5 + len(extra)
    a1, a2, a3 = len(objs) + 2, len(objs) + 3, len(objs) + 4
    page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
               b"/Resources << /Font << /F1 %d 0 R /F2 %d 0 R >> >> /Contents %d 0 R "
               b"/Annots [%d 0 R %d 0 R %d 0 R] >>"
               % (pages_num, helv, bold, content, a1, a2, a3))
    m1 = add(b"<< /Type /Annot /Subtype /Stamp /Rect [72 584 85 597] /F 4 "
             b"/MegaPDF_Id (mark:demo-1) /AP << /N %d 0 R >> >>" % ap1)
    m2 = add(b"<< /Type /Annot /Subtype /Stamp /Rect [72 558 85 571] /F 4 "
             b"/MegaPDF_Id (mark:demo-2) /AP << /N %d 0 R >> >>" % ap2)
    sg = add(b"<< /Type /Annot /Subtype /Stamp /Rect [80 402 313 472] /F 4 "
             b"/MegaPDF_Id (sig:demo-1) /AP << /N %d 0 R >> >>" % sig_ap)
    assert (m1, m2, sg) == (a1, a2, a3)
    kids = [page]
    for body, w, h in extra:
        kids.append(add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 %.0f %.0f] "
                        b"/Resources << /Font << /F1 %d 0 R /F2 %d 0 R >> >> /Contents %d 0 R >>"
                        % (pages_num, w, h, helv, bold, body)))
    pages = add(b"<< /Type /Pages /Kids [%s] /Count %d >>"
                % (b" ".join(b"%d 0 R" % k for k in kids), len(kids)))
    assert pages == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages)
    return build(objs)


def gen_cropped():
    """A page whose CropBox does not start at the MediaBox origin (#28).

    Viewers render and measure the CropBox, but pdfium reports page content in user
    space, whose origin is the MediaBox. Where they differ every reported coordinate
    is out by that offset, so search highlights and tap targets land on the wrong
    part of the page. Text sits at 72,650 -- 50pt below the crop top.
    """
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]
    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    content = add(stream(b"", b"BT /F1 36 Tf 72 650 Td (Hello MegaPDF) Tj ET\n"))
    pages_num = len(objs) + 2
    page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
               b"/CropBox [0 100 612 700] "
               b"/Resources << /Font << /F1 %d 0 R >> >> /Contents %d 0 R >>"
               % (pages_num, font, content))
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    assert pages == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages)
    return build(objs)


def gen_userunit():
    """cropped.pdf in 2-point units: /UserUnit 2 on a 306x396 MediaBox (#150).

    PDFium reports user space units; a viewer that ignores /UserUnit shows the page
    at half size and puts taps, marks and text at half their distance from the corner.
    CropBox [0 50 306 350] keeps the #28 offset in play: crop space is (user - crop
    origin) x 2. The square is 5x5 units (10 pt), under the 6 pt a checkbox needs
    unless the unit is applied.
    """
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]
    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    image = add(stream(b"/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB "
                       b"/BitsPerComponent 8", b"\xff\x00\x00\x00\xff\x00\x00\x00\xff\xff\xff\x00"))
    content = add(stream(b"", b"BT /F1 18 Tf 36 325 Td (Hello MegaPDF) Tj ET\n"
                               b"0.5 w 0.13 0.13 0.13 RG 50 250 5 5 re S\n"
                               b"q 50 0 0 25 150 100 cm /Im1 Do Q\n"))
    ap = add(stream(b"/Type /XObject /Subtype /Form /BBox [0 0 100 10]",
                    b"0.45 0.5 0.6 RG 0.5 w 0.25 0.25 99.5 9.5 re S\n"))
    box_on = add(stream(b"/Type /XObject /Subtype /Form /BBox [0 0 7.5 7.5]",
                        b"0.13 0.13 0.13 RG 0.5 w 0.25 0.25 7 7 re S 0.8 w 1.5 1.5 m 6 6 l S 1.5 6 m 6 1.5 l S\n"))
    box_off = add(stream(b"/Type /XObject /Subtype /Form /BBox [0 0 7.5 7.5]",
                         b"0.13 0.13 0.13 RG 0.5 w 0.25 0.25 7 7 re S\n"))
    pages_num = len(objs) + 4
    widget = len(objs) + 2
    checkbox = len(objs) + 3
    page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 306 396] "
               b"/CropBox [0 50 306 350] /UserUnit 2 "
               b"/Resources << /Font << /F1 %d 0 R >> /XObject << /Im1 %d 0 R >> >> /Contents %d 0 R /Annots [%d 0 R %d 0 R] >>"
               % (pages_num, font, image, content, widget, checkbox))
    w = add(b"<< /Type /Annot /Subtype /Widget /FT /Tx /T (fullname) /DA (/Helv 6 Tf 0 g) "
            b"/Rect [50 200 150 210] /F 4 /P %d 0 R /AP << /N %d 0 R >> >>" % (page, ap))
    assert w == widget
    c = add(b"<< /Type /Annot /Subtype /Widget /FT /Btn /T (agree) /V /Off /AS /Off "
            b"/Rect [50 180 57.5 187.5] /F 4 /P %d 0 R "
            b"/AP << /N << /Yes %d 0 R /Off %d 0 R >> >> >>" % (page, box_on, box_off))
    assert c == checkbox
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    assert pages == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R /AcroForm << /Fields [%d 0 R %d 0 R] "
        b"/DR << /Font << /Helv %d 0 R >> >> /DA (/Helv 0 Tf 0 g) >> >>" % (pages, widget, checkbox, font))
    return build(objs)


def gen_doubled():
    """Lines drawn twice, the way producers fake bold, outlines and shadows (#136).

    PDFium's text layer gives the characters of two identical overlapping text objects
    to the first one; the second extracts as empty text and so is never a run. A delete
    or an edit that only touched the run left the second copy on the page. In content
    order (every text object is one Helvetica 14 pt BT..ET):
      0  "A plain body line"                      a normal line, drawn once
      1  "Fake bold heading"  2  its copy 0.3 pt to the right
      3  "Filled then stroked" 4 its copy in render mode 1 (stroke) on top
      5  "Shadowed line" in grey 1 pt right and down  6  the same text in black
      7  "Two runs"  8  "drawn twice"  9  copy of 7  10  copy of 8   (one line, two runs)
      11 "The closing line"                       a normal line after them all
    """
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]
    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
    text = lambda x, y, s: b"BT /F1 14 Tf %s %s Td (%s) Tj ET\n" % (x, y, s)
    body = (text(b"72", b"720", b"A plain body line")
            + text(b"72", b"680", b"Fake bold heading")
            + text(b"72.3", b"680", b"Fake bold heading")
            + text(b"72", b"640", b"Filled then stroked")
            + b"q 0.4 w BT 1 Tr /F1 14 Tf 72 640 Td (Filled then stroked) Tj ET Q\n"
            + b"q 0.6 g " + text(b"73", b"599", b"Shadowed line") + b"Q\n"
            + text(b"72", b"600", b"Shadowed line")
            + text(b"72", b"560", b"Two runs")
            + text(b"150", b"560", b"drawn twice")
            + text(b"72.3", b"560", b"Two runs")
            + text(b"150.3", b"560", b"drawn twice")
            + text(b"72", b"520", b"The closing line"))
    content = add(stream(b"", body))
    pages_num = len(objs) + 2
    page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
               b"/Resources << /Font << /F1 %d 0 R >> >> /Contents %d 0 R >>"
               % (pages_num, font, content))
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    assert pages == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages)
    return build(objs)


def gen_doubled_far():
    """A line whose copies PDFium hides character by character, not object by object (#136).

    PDFium's object-level check only looks five text objects back, so these copies are
    all out of its reach; its text layer still reads them as empty, because it sorts a
    line's objects left to right and drops each character that repeats one in the same
    font within 0.07 of the font size (0.98 pt at 14 pt). The corpus shapes: a copy
    drawn before its run, and whole lines repeated many objects later. One line of seven
    words, each word its own BT..ET (Helvetica 14 pt), in content order:
      0-6    the words 0.7 pt right and 0.7 pt up    (copies drawn before the line)
      7-13   the words                                (the runs)
      14-20  the words 0.7 pt right                   (copies 7 objects after their run)
      21-27  the words 0.7 pt up                      (copies 14 objects after their run)
      28     "The closing line"                       a normal line after them
    """
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]
    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
    words = [(72, b"One"), (112, b"two"), (152, b"red"), (192, b"fox"), (232, b"ran"), (272, b"far"), (312, b"off")]
    text = lambda x, y, s: b"BT /F1 14 Tf %s %s Td (%s) Tj ET\n" % (x, y, s)
    fmt = lambda v: (b"%.1f" % v).rstrip(b"0").rstrip(b".")
    body = b""
    for dx, dy in ((0.7, 0.7), (0, 0), (0.7, 0), (0, 0.7)):
        for x, w in words:
            body += text(fmt(x + dx), fmt(700 + dy), w)
    body += text(b"72", b"660", b"The closing line")
    content = add(stream(b"", body))
    pages_num = len(objs) + 2
    page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
               b"/Resources << /Font << /F1 %d 0 R >> >> /Contents %d 0 R >>"
               % (pages_num, font, content))
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    assert pages == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages)
    return build(objs)


def gen_pagecolours():
    """The #509 page-colour fixture: black, white, glyphs and images at known points.

    Rendered at 612x792 the page is 1 pt to 1 px and device y is 792 - pdf y, so every
    coordinate below is a pixel address the pixel tests can assert on directly. Nothing
    here is a gradient, a blend or an antialiased interior: each region is a flat value,
    so a pixel assertion is about the tint and never about how PDFium sampled something.

    What each region is for, under MEGAPDF_RENDER_SEPIA and MEGAPDF_RENDER_NIGHT:

      black bar   pdf (72,700)-(272,740), device rows 52..92, cols 72..272
                  Pure black. Sepia must leave it black; night must make it light.
      white gap   nothing is drawn in the page's top-right corner, so device (560, 40)
                  -- pdf (560, 752) -- is bare page. Sepia must warm it to the paper
                  colour; night must take it to #1A1A1A.
      text        36 pt Helvetica black on white at pdf (72,620); stems at that size are
                  several pixels wide, so the band has solid black glyph interiors. This
                  is the one that says black-on-white text reads as light-on-dark.
      blue image  a 1x1 DeviceRGB image of #1E5AC8 stretched over pdf (72,300)-(272,420).
                  1x1 so there is nothing to interpolate: every interior pixel is exactly
                  the source byte, whatever the scaler does at the edges. It carries the
                  #168 decision -- night inverts pictures too -- and the hue check.
      pale image  the same, #F0F0F0, over pdf (300,300)-(500,420): a photograph's
                  highlight, which night must turn dark. A negative, deliberately.
      blue path   a vector fill of the same #1E5AC8 over pdf (72,150)-(272,210). The
                  post-pass cannot tell a path from an image, and this proves it: the
                  path pixel and the image pixel come out identical. That is the whole
                  content of the trade-off FPDF_COLORSCHEME was rejected over.
    """
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]
    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    # 1x1 images: a source pixel each, so scaling cannot change the interior colour.
    blue = add(stream(b"/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB "
                      b"/BitsPerComponent 8", b"\x1e\x5a\xc8"))
    pale = add(stream(b"/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB "
                      b"/BitsPerComponent 8", b"\xf0\xf0\xf0"))
    content = add(stream(b"",
                         b"0 g 72 700 200 40 re f\n"
                         b"BT /F1 36 Tf 0 g 72 620 Td (Reading mode) Tj ET\n"
                         b"q 200 0 0 120 72 300 cm /Im1 Do Q\n"
                         b"q 200 0 0 120 300 300 cm /Im2 Do Q\n"
                         # 30/90/200 over 255, the bytes /Im1 carries, so the path and the
                         # image are the same colour before the tint and must stay the same
                         # colour after it.
                         b"0.117647 0.352941 0.784314 rg 72 150 200 60 re f\n"))
    pages_num = len(objs) + 2
    page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
               b"/Resources << /Font << /F1 %d 0 R >> /XObject << /Im1 %d 0 R /Im2 %d 0 R >> >> "
               b"/Contents %d 0 R >>"
               % (pages_num, font, blue, pale, content))
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    assert pages == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages)
    return build(objs)


def gen_softmask():
    """Objects drawn under soft masks beneath a scaled, flipped page CTM (#140).

    PDFium fixes a soft mask's matrix to the CTM in force at its "gs"; a writer that
    replays the ExtGState anywhere else maps the mask in the wrong space. The page's
    content starts with "0.5 0 0 -0.5 0 792 cm", so every coordinate below is in half
    points with y running down. In content order:
      0  a blue box under a luminosity mask (white stripe x 0-400)
      1  a red box under /ca 0.5 and an alpha mask (a 250 x 300 rectangle)
      2  a checker image under a luminosity mask whose group has its own /Matrix
      3  a green form XObject under an alpha mask whose group has its own /Matrix
      4  text under the luminosity mask of 0
      5  a purple box under the mask of 2, set before a further "0.8 0 0 0.8 0 0 cm"
         (the mask's matrix is not the box's)
      6  "Plain line", the object a regeneration marks dirty
    """
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]
    group = b"/Type /XObject /Subtype /Form /BBox [0 0 1224 1584] /Group << /S /Transparency /CS /DeviceGray >>"
    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
    lum = add(stream(group, b"1 g 0 0 400 1584 re f"))
    alpha = add(stream(group, b"0 g 100 500 250 300 re f"))
    lum_moved = add(stream(group + b" /Matrix [1 0 0 1 60 40]", b"1 g 600 0 300 1584 re f 1 g 560 900 300 300 re f"))
    alpha_scaled = add(stream(group + b" /Matrix [0.5 0 0 0.5 300 250]", b"0 g 700 500 300 300 re f"))
    pixels = bytes(255 if ((x // 4 + y // 4) & 1) else 0 for y in range(16) for x in range(16))
    image = add(stream(b"/Type /XObject /Subtype /Image /Width 16 /Height 16 /ColorSpace /DeviceGray /BitsPerComponent 8",
                       pixels))
    form = add(stream(b"/Type /XObject /Subtype /Form /BBox [0 0 400 300]", b"0 0.6 0 rg 0 0 400 300 re f"))
    content = add(stream(b"", b"0.5 0 0 -0.5 0 792 cm\n"
                              b"q /GL gs 0 0 1 rg 100 100 500 300 re f Q\n"
                              b"q /GC gs /GA gs 1 0 0 rg 100 500 500 300 re f Q\n"
                              b"q /GI gs 300 0 0 300 650 100 cm /Im1 Do Q\n"
                              b"q /GF gs 1 0 0 1 650 500 cm /Fm1 Do Q\n"
                              b"q /GL gs 0 0.5 0 rg BT /F1 96 Tf 1 0 0 -1 100 1000 Tm (Masked text) Tj ET Q\n"
                              b"q /GI gs 0.8 0 0 0.8 0 0 cm 0.5 0 0.5 rg 700 1000 400 300 re f Q\n"
                              b"BT /F1 24 Tf 1 0 0 -1 100 1450 Tm (Plain line) Tj ET\n"))
    pages_num = len(objs) + 2
    page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 %d 0 R >> "
               b"/XObject << /Im1 %d 0 R /Fm1 %d 0 R >> "
               b"/ExtGState << /GL << /SMask << /S /Luminosity /G %d 0 R >> >> "
               b"/GA << /SMask << /S /Alpha /G %d 0 R >> >> "
               b"/GI << /SMask << /S /Luminosity /G %d 0 R >> >> "
               b"/GF << /SMask << /S /Alpha /G %d 0 R >> >> "
               b"/GC << /ca 0.5 >> >> >> /Contents %d 0 R >>"
               % (pages_num, font, image, form, lum, alpha, lum_moved, alpha_scaled, content))
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    assert pages == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages)
    return build(objs)


def gen_textbox():
    """A page carrying a MegaPDF *text box* written the way the engines write it
    (#34): a text object wrapped in a marked-content section named
    `MegaPDFTextBox` with an `id` property.

    This is the interop half of the contract. Each platform's own tests prove it
    can write a box and read it back; this fixture proves it can read one written
    somewhere else -- the text-object equivalent of `stamped.pdf` for annots.

    It also carries two boxes that are marked but carry *no* id, which is what
    MegaPDF for Windows wrote before it started stamping one. They must read as
    text boxes and must not collide: an id shared between two objects would make
    removeTextBox delete an arbitrary one.

    One box is deliberately *not* 12 pt Helvetica (#43): 18 pt Times-Roman, with
    the face named in a `font` property beside the id. Every box written before
    #43 carries no `font`, and must still read as Helvetica -- that is what the
    `text:fixture-1` box pins.
    """
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]
    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    times = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman >>")
    content = add(stream(
        b"",
        b"BT /F1 14 Tf 72 720 Td (Ordinary body text, not a text box.) Tj ET\n"
        b"/MegaPDFTextBox << /id (text:fixture-1) >> BDC\n"
        b"BT /F1 12 Tf 100 300 Td (Fixture text box) Tj ET\n"
        b"EMC\n"
        # A box that chose its face and size (#43). The `font` property is the
        # contract: it says what the user picked, independent of whatever name
        # pdfium reports for the resource.
        b"/MegaPDFTextBox << /id (text:fixture-times) /font (Times-Roman) >> BDC\n"
        b"BT /F2 18 Tf 100 180 Td (Eighteen point Times) Tj ET\n"
        b"EMC\n"
        # Two boxes marked but carrying no id: what MegaPDF for Windows wrote
        # before it started stamping one (SDD 6.2 contract 4). They must read as
        # text boxes and still be told apart.
        #
        # BMC, not BDC: BDC takes *two* operands (tag + property list), so a tag
        # with no properties is BMC -- which is also what pdfium emits for a
        # param-less FPDFPageObj_AddMark, making this the faithful legacy form.
        b"/MegaPDFTextBox BMC\n"
        b"BT /F1 12 Tf 100 260 Td (Legacy box one) Tj ET\n"
        b"EMC\n"
        b"/MegaPDFTextBox BMC\n"
        b"BT /F1 12 Tf 100 220 Td (Legacy box two) Tj ET\n"
        b"EMC\n"))
    pages_num = len(objs) + 2
    page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
               b"/Resources << /Font << /F1 %d 0 R /F2 %d 0 R >> >> /Contents %d 0 R >>"
               % (pages_num, font, times, content))
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    assert pages == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages)
    return build(objs)


# #132: a protected document, so every platform can test that a save stays protected
# and reads back. The standard security handler at V1/R2 (RC4, 40-bit) is what the
# standard library can build; the full set of handlers comes from qpdf in #131.
_SECURITY_PAD = bytes([0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
             0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A])


def _rc4(key, data):
    s = list(range(256))
    j = 0
    for i in range(256):
        j = (j + s[i] + key[i % len(key)]) & 0xFF
        s[i], s[j] = s[j], s[i]
    out = bytearray()
    i = j = 0
    for b in data:
        i = (i + 1) & 0xFF
        j = (j + s[i]) & 0xFF
        s[i], s[j] = s[j], s[i]
        out.append(b ^ s[(s[i] + s[j]) & 0xFF])
    return bytes(out)


def gen_encrypted(unlock_text="u123"):
    padded = (unlock_text.encode("latin-1") + _SECURITY_PAD)[:32]
    file_id = bytes((i * 7 + 3) & 0xFF for i in range(16))
    perms = -3904
    o_entry = _rc4(hashlib.md5(padded).digest()[:5], padded)  # owner text == user text
    key = hashlib.md5(padded + o_entry + perms.to_bytes(4, "little", signed=True) + file_id).digest()[:5]
    u_entry = _rc4(key, _SECURITY_PAD)

    def obj_key(num, gen=0):
        return hashlib.md5(key + num.to_bytes(3, "little") + gen.to_bytes(2, "little")).digest()[:10]

    content = b"BT /F1 24 Tf 72 700 Td (MegaPDF encrypted fixture) Tj ET"
    enc = _rc4(obj_key(4), content)
    objs = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
        b"<< /Length %d >>\nstream\n" % len(enc) + enc + b"\nendstream",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        b"<< /Filter /Standard /V 1 /R 2 /O <" + o_entry.hex().encode() + b"> /U <" + u_entry.hex().encode()
        + b"> /P %d >>" % perms,
    ]
    out = bytearray(b"%PDF-1.4\n")
    offsets = []
    for n, body in enumerate(objs, 1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % n + body + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objs) + 1)
    for o in offsets:
        out += b"%010d 00000 n \n" % o
    hid = file_id.hex().encode()
    out += (b"trailer\n<< /Size %d /Root 1 0 R /Encrypt 6 0 R /ID [<" % (len(objs) + 1) + hid + b"> <" + hid
            + b">] >>\nstartxref\n%d\n%%%%EOF\n" % xref)
    return bytes(out)


def gen_secure_source():
    """The document the #241 protection fixtures are made from.

    Removing protection must leave everything a reader can see exactly as it was, so
    this one fixture carries all three of the things the removal is checked against:
    page content (two pages of text), form fields (a filled text field and a checked
    checkbox) and annotations (a square and a sticky note, each with a MegaPDF_Id so
    the core's stamp contract lists them). tools/gen_security_fixtures.sh encrypts it
    eight ways, six with a classic cross-reference table and two with object streams
    and a cross-reference stream.
    """
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]

    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    c1 = add(stream(
        b"", b"BT /F1 24 Tf 72 720 Td (MegaPDF protection fixture - page 1) Tj ET\n"
             b"BT /F1 12 Tf 72 680 Td (Removing protection must change none of this.) Tj ET\n"
             b"1 w 0.13 0.13 0.13 RG 72 640 12 12 re S\n"))
    c2 = add(stream(
        b"", b"BT /F1 24 Tf 72 720 Td (MegaPDF protection fixture - page 2) Tj ET\n"
             b"BT /F1 12 Tf 72 680 Td (A second page, so the page tree is exercised too.) Tj ET\n"))
    ap_text = add(stream(
        b"/Type /XObject /Subtype /Form /BBox [0 0 200 20] /Resources << /Font << /Helv 1 0 R >> >>",
        b"0.13 0.13 0.13 RG 1 w 0.5 0.5 199 19 re S BT /Helv 12 Tf 0 g 2 5 Td (Ada Lovelace) Tj ET\n"))
    ap_yes = add(stream(
        b"/Type /XObject /Subtype /Form /BBox [0 0 15 15]",
        b"0.13 0.13 0.13 RG 1 w 0.5 0.5 14 14 re S 1.6 w 3 3 m 12 12 l S 3 12 m 12 3 l S\n"))
    ap_off = add(stream(
        b"/Type /XObject /Subtype /Form /BBox [0 0 15 15]",
        b"0.13 0.13 0.13 RG 1 w 0.5 0.5 14 14 re S\n"))
    ap_square = add(stream(
        b"/Type /XObject /Subtype /Form /BBox [0 0 100 50]",
        b"1 0 0 RG 3 w 1.5 1.5 97 47 re S\n"))
    ap_note = add(stream(
        b"/Type /XObject /Subtype /Form /BBox [0 0 20 20]",
        b"0.95 0.80 0.20 rg 1 1 18 18 re f 0.13 0.13 0.13 RG 1 w 1 1 18 18 re S\n"))

    pages_num = len(objs) + 7
    text_field = len(objs) + 3
    checkbox = len(objs) + 4
    square = len(objs) + 5
    note = len(objs) + 6
    page1 = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
                b"/Resources << /Font << /F1 %d 0 R >> >> /Contents %d 0 R "
                b"/Annots [%d 0 R %d 0 R %d 0 R %d 0 R] >>"
                % (pages_num, font, c1, text_field, checkbox, square, note))
    page2 = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
                b"/Resources << /Font << /F1 %d 0 R >> >> /Contents %d 0 R >>"
                % (pages_num, font, c2))
    t = add(b"<< /Type /Annot /Subtype /Widget /FT /Tx /T (fullname) /V (Ada Lovelace) "
            b"/DA (/Helv 12 Tf 0 g) /Rect [100 560 300 580] /F 4 /P %d 0 R "
            b"/AP << /N %d 0 R >> >>" % (page1, ap_text))
    c = add(b"<< /Type /Annot /Subtype /Widget /FT /Btn /T (agree) /V /Yes /AS /Yes "
            b"/Rect [100 520 115 535] /F 4 /P %d 0 R "
            b"/AP << /N << /Yes %d 0 R /Off %d 0 R >> >> >>" % (page1, ap_yes, ap_off))
    s_ = add(b"<< /Type /Annot /Subtype /Square /Rect [72 420 172 470] /C [1 0 0] "
             b"/BS << /W 3 >> /F 4 /P %d 0 R /MegaPDF_Id (note:square) "
             b"/AP << /N %d 0 R >> >>" % (page1, ap_square))
    n_ = add(b"<< /Type /Annot /Subtype /Text /Rect [520 695 540 715] /Contents (A sticky note) "
             b"/Name /Comment /F 4 /P %d 0 R /MegaPDF_Id (note:text) "
             b"/AP << /N %d 0 R >> >>" % (page1, ap_note))
    assert (t, c, s_, n_) == (text_field, checkbox, square, note)
    pages = add(b"<< /Type /Pages /Kids [%d 0 R %d 0 R] /Count 2 >>" % (page1, page2))
    assert pages == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R /AcroForm << /Fields [%d 0 R %d 0 R] "
        b"/DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv %d 0 R >> >> >> >>"
        % (pages, text_field, checkbox, font))
    return build(objs)


def gen_unused_tail():
    """A document whose highest object number is one nothing refers to (#246).

    PDFium's writer only writes the objects it can reach, so for a file like this the
    last object it writes is not the document's last object number -- the condition
    under which the two places that number a new encryption dictionary used to
    disagree, and the protected copy's trailer named an /Encrypt object that was never
    written. Real documents get here through an incremental-update history or a stale
    object; this is the same shape in five objects.
    """
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]

    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    content = add(stream(
        b"", b"BT /F1 18 Tf 72 700 Td (An object nobody refers to follows this one.) Tj ET\n"))
    pages_num = len(objs) + 2
    page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
               b"/Resources << /Font << /F1 %d 0 R >> >> /Contents %d 0 R >>"
               % (pages_num, font, content))
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    assert pages == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages)
    # Listed in the cross-reference table, reachable from nothing.
    add(b"<< /Type /MegaPDFUnused /Note (nothing refers to this object) >>")
    return build(objs)


def main():
    outdir = sys.argv[1]
    os.makedirs(outdir, exist_ok=True)
    for name, data in (("fixture.pdf", gen_fixture()), ("forms.pdf", gen_forms()),
                       ("stamped.pdf", gen_stamped()), ("demo.pdf", gen_demo()),
                       ("demo-fr.pdf", gen_demo("fr")),
                       ("demo-fr-FR.pdf", gen_demo("fr-FR")),
                       ("demo-pages.pdf", gen_demo(sections=True)),
                       ("demo-fr-pages.pdf", gen_demo("fr", sections=True)),
                       ("demo-fr-FR-pages.pdf", gen_demo("fr-FR", sections=True)),
                       ("demo-blank.pdf", gen_demo(filled=False)),
                       ("demo-fr-blank.pdf", gen_demo("fr", filled=False)),
                       ("demo-fr-FR-blank.pdf", gen_demo("fr-FR", filled=False)),
                       ("formtext.pdf", gen_formtext()),
                       ("cropped.pdf", gen_cropped()),
                       ("userunit.pdf", gen_userunit()),
                       ("textbox.pdf", gen_textbox()),
                       ("doubled.pdf", gen_doubled()),
                       ("doubled-far.pdf", gen_doubled_far()),
                       ("softmask.pdf", gen_softmask()),
                       ("pagecolours.pdf", gen_pagecolours()),
                       ("encrypted.pdf", gen_encrypted()),
                       ("secure-source.pdf", gen_secure_source()),
                       ("unused-tail.pdf", gen_unused_tail())):
        path = os.path.join(outdir, name)
        with open(path, "wb") as f:
            f.write(data)
        print("wrote %s (%d bytes)" % (path, len(data)))


if __name__ == "__main__":
    main()
