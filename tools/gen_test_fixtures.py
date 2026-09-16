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
    },
}


def _winansi(text):
    """A PDF string literal in WinAnsi (cp1252), so accents render in the base-14 faces."""
    raw = text.encode("cp1252")
    return raw.replace(b"\\", b"\\\\").replace(b"(", b"\\(").replace(b")", b"\\)")


def gen_demo(lang="en", filled=True):
    """One-page 'filled agreement' used for App Store screenshots: real
    MegaPDF-style artifacts (mark:/sig: annots tagged MegaPDF_Id).
    `lang` picks the page's language (DEMO_TEXT); the layout is identical.
    `filled=False` writes the same page with nothing on it — no ticks, no
    signature — for the preview video, which fills it in on camera."""
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

    pages_num = len(objs) + 5
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
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
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


def main():
    outdir = sys.argv[1]
    os.makedirs(outdir, exist_ok=True)
    for name, data in (("fixture.pdf", gen_fixture()), ("forms.pdf", gen_forms()),
                       ("stamped.pdf", gen_stamped()), ("demo.pdf", gen_demo()),
                       ("demo-fr.pdf", gen_demo("fr")),
                       ("demo-blank.pdf", gen_demo(filled=False)),
                       ("demo-fr-blank.pdf", gen_demo("fr", filled=False)),
                       ("formtext.pdf", gen_formtext()),
                       ("cropped.pdf", gen_cropped()),
                       ("userunit.pdf", gen_userunit()),
                       ("textbox.pdf", gen_textbox()),
                       ("doubled.pdf", gen_doubled()),
                       ("doubled-far.pdf", gen_doubled_far()),
                       ("softmask.pdf", gen_softmask()),
                       ("encrypted.pdf", gen_encrypted())):
        path = os.path.join(outdir, name)
        with open(path, "wb") as f:
            f.write(data)
        print("wrote %s (%d bytes)" % (path, len(data)))


if __name__ == "__main__":
    main()
