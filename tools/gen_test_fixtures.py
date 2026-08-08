#!/usr/bin/env python3
"""Generate shared engine-test fixture PDFs (SDD §6.2 parity fixtures).

Usage: python3 tools/gen_test_fixtures.py <outdir>
Writes:
  fixture.pdf - 2 pages, US Letter; page 1 has text and a stroked 12x12pt
                square at (72,600)-(84,612): a drawn-checkbox candidate.
  forms.pdf   - 1 page with one AcroForm checkbox widget "agree",
                rect (100,600)-(115,615), initially /Off.

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


def main():
    outdir = sys.argv[1]
    os.makedirs(outdir, exist_ok=True)
    for name, data in (("fixture.pdf", gen_fixture()), ("forms.pdf", gen_forms())):
        path = os.path.join(outdir, name)
        with open(path, "wb") as f:
            f.write(data)
        print("wrote %s (%d bytes)" % (path, len(data)))


if __name__ == "__main__":
    main()
