#!/usr/bin/env python3
"""#453/#444: a four-page PDF whose pages carry /Rotate 0, 90, 180 and 270, each drawing the
same four horizontal words, so contract 9's word building is exercised at every page rotation.

    tools/gen_rotated_page_fixture.py tests/MegaPDF.Core.Tests/Fixtures/structure/rotated-pages.pdf

Committed rather than generated at test time, the same doctrine as tools/gen_vertical_cid_fixture.py.

Why this fixture exists
-----------------------
#444 was diagnosed and fixed through a *vertical composite font* (Identity-V), and that is what
`vertical-cid.pdf` locks. But the fix's real-world trigger is this one: an ordinary Type1 font on
a **rotated page**. #363's matrix inversion leaves a rotated page's characters advancing along
their local +y rather than +x, which is the exact condition the #444 fix taught BuildWords to
recognise -- so a plain rotated page hits the same code path that a WMode-1 CJK column does, and
rotated pages are common in real documents where Identity-V text essentially never is.

That gap is what #453 ran into. Two qpdf overlay fixtures (`uo-4.pdf`,
`overlay-copy-annotations.pdf`, both page-rotation test files) were reported as over-counting
tokens 1.9-2.7x against PDFium; the cause was the pre-#444 BuildWords, which shattered every
rotated-page word into one-character tokens. Before the #444 fix this fixture's 90-degree page
reads `A lp h a B ra v o C h a rlieD e lta`; after it, one token per word. Nothing in the
repository covered that, so the fix could have regressed at its most common trigger without a
single test going red.

Known-wrong output this fixture also pins
-----------------------------------------
The golden this produces is a change detector, not an endorsement. Two defects remain visible in
it, both PRE-DATING the #444 fix and both filed separately -- do not "correct" the golden by hand:

  * /Rotate 180 and 270 emit the page's words in REVERSE order
    (`Delta Charlie Bravo Alpha` for `Alpha Bravo Charlie Delta`);
  * /Rotate 90 and 270 lose the inter-word gaps (`AlphaBravoCharlieDelta`).

poppler's `pdftotext -layout` reads all four pages correctly, so the reference and the engine
disagree here and the engine is the one that is wrong. When that is fixed the golden SHOULD
change, and the diff is the proof.
"""
import sys


def build(path):
    text = b"BT /F1 18 Tf 72 700 Td (Alpha Bravo Charlie Delta) Tj ET\n"
    rotations = (0, 90, 180, 270)

    objs = {}
    objs[1] = b"<< /Type /Catalog /Pages 2 0 R >>"
    kids = b" ".join(b"%d 0 R" % (3 + i) for i in range(len(rotations)))
    objs[2] = b"<< /Type /Pages /Kids [" + kids + b"] /Count %d >>" % len(rotations)

    font_obj = 3 + 2 * len(rotations)
    for i, rot in enumerate(rotations):
        page_obj = 3 + i
        content_obj = 3 + len(rotations) + i
        objs[page_obj] = (
            b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Rotate %d "
            b"/Resources << /Font << /F1 %d 0 R >> >> /Contents %d 0 R >>"
            % (rot, font_obj, content_obj))
        objs[content_obj] = (b"<< /Length %d >>\nstream\n" % len(text)) + text + b"endstream"
    objs[font_obj] = b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"

    out = bytearray(b"%PDF-1.4\n")
    offsets = {}
    for num in sorted(objs):
        offsets[num] = len(out)
        out += b"%d 0 obj\n" % num + objs[num] + b"\nendobj\n"

    startxref = len(out)
    count = max(objs) + 1
    out += b"xref\n0 %d\n0000000000 65535 f \n" % count
    for num in sorted(objs):
        out += b"%010d 00000 n \n" % offsets[num]
    out += (b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n"
            % (count, startxref))

    with open(path, "wb") as fh:
        fh.write(bytes(out))
    return len(out)


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__.strip().splitlines()[2].strip())
    size = build(sys.argv[1])
    print("wrote %s (%d bytes, 4 pages at /Rotate 0/90/180/270)" % (sys.argv[1], size))
