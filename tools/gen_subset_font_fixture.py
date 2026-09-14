#!/usr/bin/env python3
"""#130: a one-page PDF whose heading is in an embedded TrueType *subset* that lacks
some letters, the case a read-back check cannot see.

    tools/gen_subset_font_fixture.py tests/MegaPDF.Core.Tests/Fixtures/subset-font.pdf [font.ttf]

fontTools is not on the CI runners, so the output is committed (#130).

The subset holds only the glyphs of "Hello World" plus space. The font dictionary uses
WinAnsiEncoding with FirstChar 32 / LastChar 126 and real widths for every code, as
subsetting tools write them, so PDFium maps any ASCII character to a code; only the font
program knows which glyphs exist.
"""
import io
import sys
import zlib

from fontTools import subset
from fontTools.ttLib import TTFont

out_path = sys.argv[1]
font_path = sys.argv[2] if len(sys.argv) > 2 else "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
KEEP = "Hello World"

full = TTFont(font_path)
upem = full["head"].unitsPerEm
cmap = full.getBestCmap()
hmtx = full["hmtx"]
widths = []
for code in range(32, 127):
    glyph = cmap.get(code)
    widths.append(round(hmtx[glyph][0] * 1000 / upem) if glyph else 0)

options = subset.Options()
options.notdef_outline = True
options.name_IDs = []
options.layout_features = []
sub = subset.Subsetter(options)
sub.populate(text=KEEP + " ")
font = TTFont(font_path)
sub.subset(font)
buf = io.BytesIO()
font.save(buf)
program = buf.getvalue()

head = font["head"]
bbox = [round(v * 1000 / upem) for v in (head.xMin, head.yMin, head.xMax, head.yMax)]
content = b"BT /F1 24 Tf 72 700 Td (Hello World) Tj ET BT /F2 12 Tf 72 660 Td (Body line under it) Tj ET"
program_z = zlib.compress(program)

objects = [
    b"<< /Type /Catalog /Pages 2 0 R >>",
    b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
    b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R /F2 7 0 R >> >> /Contents 5 0 R >>",
    ("<< /Type /Font /Subtype /TrueType /BaseFont /ABCDEF+DejaVuSans /FirstChar 32 /LastChar 126 /Widths [%s] "
     "/Encoding /WinAnsiEncoding /FontDescriptor 6 0 R >>" % " ".join(map(str, widths))).encode(),
    b"<< /Length %d >>\nstream\n" % len(content) + content + b"\nendstream",
    ("<< /Type /FontDescriptor /FontName /ABCDEF+DejaVuSans /Flags 32 /FontBBox [%s] /ItalicAngle 0 /Ascent %d "
     "/Descent %d /CapHeight %d /StemV 80 /FontFile2 8 0 R >>"
     % (" ".join(map(str, bbox)), bbox[3], bbox[1], bbox[3])).encode(),
    b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
    b"<< /Length %d /Length1 %d /Filter /FlateDecode >>\nstream\n" % (len(program_z), len(program)) + program_z + b"\nendstream",
]
pdf = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
offsets = []
for i, body in enumerate(objects, 1):
    offsets.append(len(pdf))
    pdf += b"%d 0 obj\n" % i + body + b"\nendobj\n"
xref = len(pdf)
pdf += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objects) + 1)
for off in offsets:
    pdf += b"%010d 00000 n \n" % off
pdf += b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objects) + 1, xref)
open(out_path, "wb").write(pdf)
print(f"{out_path}: {len(pdf)} bytes, subset program {len(program)} bytes, glyphs {len(font.getGlyphOrder())}")
