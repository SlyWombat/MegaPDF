#!/usr/bin/env python3
"""#482: a one-page PDF laid out the way Japanese vertical writing is -- top-to-bottom columns
that march RIGHT TO LEFT across the page (tb-rl).

    tools/gen_vertical_tbrl_fixture.py tests/MegaPDF.Core.Tests/Fixtures/structure/vertical-tbrl.pdf

No font is embedded and nothing but the standard library is needed, so the output is committed
(the same doctrine as tools/gen_vertical_cid_fixture.py, #126) and CI never regenerates it.

## Why this is not vertical-cid.pdf all over again

`vertical-cid.pdf` (#444/#451) is a COMPOSITE font with the predefined vertical CMap
/Identity-V: the font itself declares that its glyphs advance downwards, and #451's fix made
BuildWords honour that one character pair at a time. This fixture is the other, far more common
producer: an ordinary simple font with ordinary horizontal metrics, positioned one glyph at a
time down the page by the layout engine. That is what LibreOffice writes for a `tb-rl`
paragraph, and #471 part 2's vertical-Japanese sample (which #482 was filed from) is made of
exactly it -- measured there: a simple TrueType subset, not a Type0/Identity-V font.

## What it is for

#451 got the WORDS right and stopped there. Everything above BuildWords still assumed a page
reads left-to-right along a horizontal line: BuildLines clusters words on a shared vertical
centre and then sorts them by LEFT EDGE, and the XY-cut looks for horizontal bands before
vertical gutters. On a tb-rl page that reads every column in exactly the wrong order --
left-to-right instead of right-to-left -- while losing not one character, which is why the
corpus token-fidelity measure never saw it and only a reading-order measure does.

So the columns here spell a sentence that is only a sentence when the page is read right to
left. The golden records the block order; if the frame detection (`ReadsVertically`,
core/megapdf_structure.cpp) stops firing, the golden's words come out backwards and the diff
says so in words rather than in coordinates.

The content stream emits the columns in reading order (rightmost first), exactly as a real
tb-rl producer does, so the fixture does not let the engine off by making stream order and
geometric order the same thing.
"""
import sys
import zlib

# Right to left: COLUMNS[0] is the RIGHTMOST column and the first one read.
COLUMNS = ["VERTICAL", "COLUMNS", "ARE", "READ", "RIGHT", "TOLEFT", "TOPDOWN"]

PAGE_W, PAGE_H = 320, 260
FONT_SIZE = 16
STEP = 20          # one glyph to the next, down the column (1.25 em: a real line-height)
COLUMN_PITCH = 34  # one column to the next, leftwards
RIGHT_X = PAGE_W - 40
TOP_Y = PAGE_H - 40


def content_stream():
    out = []
    for i, word in enumerate(COLUMNS):
        x = RIGHT_X - i * COLUMN_PITCH
        out.append("BT")
        out.append("/F1 %d Tf" % FONT_SIZE)
        out.append("1 0 0 1 %d %d Tm" % (x, TOP_Y))
        for j, ch in enumerate(word):
            if j:
                out.append("0 -%d Td" % STEP)
            out.append("(%s) Tj" % ch)
        out.append("ET")
    return "\n".join(out).encode("ascii")


def build():
    stream = zlib.compress(content_stream())
    objs = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 %d %d] /Resources << /Font << /F1 5 0 R >> >>"
         " /Contents 4 0 R >>" % (PAGE_W, PAGE_H)).encode("ascii"),
        b"<< /Length %d /Filter /FlateDecode >>\nstream\n" % len(stream) + stream + b"\nendstream",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    ]
    out = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
    offsets = []
    for n, body in enumerate(objs, start=1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % n + body + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n" % (len(objs) + 1)
    out += b"0000000000 65535 f \n"
    for off in offsets:
        out += b"%010d 00000 n \n" % off
    out += b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objs) + 1, xref)
    return bytes(out)


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit("usage: gen_vertical_tbrl_fixture.py <out.pdf>")
    with open(sys.argv[1], "wb") as fh:
        fh.write(build())
    print("wrote %s" % sys.argv[1])
