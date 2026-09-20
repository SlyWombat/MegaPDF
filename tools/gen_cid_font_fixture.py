#!/usr/bin/env python3
"""#126: a one-page PDF whose heading is in an embedded CID font, the way most producers
write any text outside WinAnsi (Word, Chrome, LibreOffice and every CJK document).

    tools/gen_cid_font_fixture.py tests/MegaPDF.Core.Tests/Fixtures/cid-font.pdf [font.ttf]

fontTools is not on the CI runners, so the output is committed (#126).

The heading is a Type0 font, Encoding /Identity-H, over a CIDFontType2 descendant whose
FontFile2 is a DejaVuSans subset holding only the glyphs of "Hello World" plus space.
Glyph ids are kept (retain_gids), so every CID is the full font's glyph id and the
CIDToGIDMap is /Identity. /W carries the widths of the kept glyphs and the ToUnicode
CMap maps only those CIDs, as subsetting producers write it: nothing in the document
says which CID an x or a capital O would be.
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
cids = {ch: full.getGlyphID(cmap[ord(ch)]) for ch in sorted(set(KEEP + " "))}

options = subset.Options()
options.notdef_outline = True
options.name_IDs = []
options.layout_features = []
options.retain_gids = True
sub = subset.Subsetter(options)
sub.populate(text=KEEP + " ")
font = TTFont(font_path)
sub.subset(font)
buf = io.BytesIO()
font.save(buf)
program = buf.getvalue()

head = font["head"]
bbox = [round(v * 1000 / upem) for v in (head.xMin, head.yMin, head.xMax, head.yMax)]
widths = " ".join("%d [%d]" % (cid, round(hmtx[cmap[ord(ch)]][0] * 1000 / upem))
                  for ch, cid in sorted(cids.items(), key=lambda kv: kv[1]))
heading = "".join("%04X" % cids[ch] for ch in KEEP)
content = ("BT /F1 24 Tf 72 700 Td <%s> Tj ET BT /F2 12 Tf 72 660 Td (Body line under it) Tj ET" % heading).encode()
to_unicode = (
    "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n"
    "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n"
    "/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n"
    "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n"
    "%d beginbfchar\n%s\nendbfchar\n"
    "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend"
    % (len(cids), "\n".join("<%04X> <%04X>" % (cid, ord(ch)) for ch, cid in sorted(cids.items(), key=lambda kv: kv[1])))
).encode()
program_z = zlib.compress(program)


def stream(body, extra=b""):
    return b"<< /Length %d%s >>\nstream\n" % (len(body), extra) + body + b"\nendstream"


objects = [
    b"<< /Type /Catalog /Pages 2 0 R >>",
    b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
    b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R /F2 8 0 R >> >> /Contents 5 0 R >>",
    b"<< /Type /Font /Subtype /Type0 /BaseFont /ABCDEF+DejaVuSans /Encoding /Identity-H /DescendantFonts [6 0 R] /ToUnicode 10 0 R >>",
    stream(content),
    ("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /ABCDEF+DejaVuSans "
     "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
     "/FontDescriptor 7 0 R /DW 1000 /W [%s] /CIDToGIDMap /Identity >>" % widths).encode(),
    ("<< /Type /FontDescriptor /FontName /ABCDEF+DejaVuSans /Flags 32 /FontBBox [%s] /ItalicAngle 0 /Ascent %d "
     "/Descent %d /CapHeight %d /StemV 80 /FontFile2 9 0 R >>"
     % (" ".join(map(str, bbox)), bbox[3], bbox[1], bbox[3])).encode(),
    b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
    b"<< /Length %d /Length1 %d /Filter /FlateDecode >>\nstream\n" % (len(program_z), len(program)) + program_z + b"\nendstream",
    stream(to_unicode),
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
print(f"{out_path}: {len(pdf)} bytes, subset program {len(program)} bytes, CIDs {sorted(cids.values())}")

// trigger structural scan
