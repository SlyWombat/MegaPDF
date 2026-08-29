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
  textbox.pdf - a MegaPDFTextBox-marked text object with an id property (#34),
                so every platform can prove it reads boxes written elsewhere.

Deterministic output; both platforms' engine tests assert against these.
"""
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


def gen_demo():
    """One-page 'filled agreement' used for App Store screenshots: real
    MegaPDF-style artifacts (mark:/sig: annots tagged MegaPDF_Id)."""
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]

    helv = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    bold = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>")
    body = [
        b"BT /F2 22 Tf 72 716 Td (Equipment Rental Agreement) Tj ET",
        b"BT /F1 11 Tf 72 688 Td (This agreement is made between Sunrise Tool Rental and the customer named) Tj ET",
        b"BT /F1 11 Tf 72 672 Td (below, covering the rental equipment, delivery options, and insurance terms) Tj ET",
        b"BT /F1 11 Tf 72 656 Td (described in sections 1 through 4 of this document.) Tj ET",
        b"BT /F2 13 Tf 72 616 Td (Options) Tj ET",
        # three drawn checkboxes
        b"1 w 0.13 0.13 0.13 RG 72 584 13 13 re S",
        b"BT /F1 12 Tf 94 587 Td (Include delivery and pickup) Tj ET",
        b"1 w 0.13 0.13 0.13 RG 72 558 13 13 re S",
        b"BT /F1 12 Tf 94 561 Td (Damage insurance accepted) Tj ET",
        b"1 w 0.13 0.13 0.13 RG 72 532 13 13 re S",
        b"BT /F1 12 Tf 94 535 Td (Extended weekend rate) Tj ET",
        b"BT /F2 13 Tf 72 484 Td (Customer signature) Tj ET",
        b"0.6 w 0.4 0.4 0.4 RG 72 400 m 320 400 l S",
        b"BT /F1 9 Tf 72 388 Td (Sign above the line) Tj ET",
    ]
    content = add(stream(b"", b"\n".join(body) + b"\n"))

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


def main():
    outdir = sys.argv[1]
    os.makedirs(outdir, exist_ok=True)
    for name, data in (("fixture.pdf", gen_fixture()), ("forms.pdf", gen_forms()),
                       ("stamped.pdf", gen_stamped()), ("demo.pdf", gen_demo()),
                       ("formtext.pdf", gen_formtext()),
                              ("cropped.pdf", gen_cropped()),
                       ("textbox.pdf", gen_textbox())):
        path = os.path.join(outdir, name)
        with open(path, "wb") as f:
            f.write(data)
        print("wrote %s (%d bytes)" % (path, len(data)))


if __name__ == "__main__":
    main()
