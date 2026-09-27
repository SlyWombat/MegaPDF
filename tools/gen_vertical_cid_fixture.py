#!/usr/bin/env python3
"""#444: a one-page PDF whose text is drawn through a composite (Type0/CIDFontType2) font
with the predefined VERTICAL CMap /Identity-V, the way a CJK document (or, per #444, veraPDF's
own composite-font/CMap conformance fixtures) lays out text top-to-bottom in columns rather
than left-to-right along a line.

    tools/gen_vertical_cid_fixture.py tests/MegaPDF.Core.Tests/Fixtures/structure/vertical-cid.pdf [font.ttf]

fontTools is not on the CI runners, so the output is committed (same doctrine as
tools/gen_cid_font_fixture.py, #126).

Two five-letter words, "Hello" and "World", are drawn as two separate vertical runs (columns),
side by side, each with the font's default vertical metrics (/DW2 [880 -1000]: every glyph
after the first steps one full em straight down the page, with no /W2 override). Nothing about
this file is non-conforming -- Identity-V is a predefined CMap name every composite-font-capable
reader recognises without an embedded CMap stream, and #444's own diagnosis found PDFium places
these characters exactly as spec'd (origin_y stepping down by one em per glyph, the glyph's own
rendering matrix staying upright). What #444 found broken is downstream of that: contract 9's
BuildWords (core/megapdf_structure.cpp) assumed a character's advance is always along its LOCAL
+x, which holds for horizontal text and for a #363 matrix-rotated run but not for a WMode-1
composite font, so every consecutive pair failed the baseline test and "Hello"/"World" came out
as ten one-glyph words instead of two five-letter ones. This fixture is the regression case for
the fix, registered in core/tests/core_tests.cpp's `cases` list (test name "vertical-cid");
the two veraPDF documents that actually surfaced #444 are conformance-suite fixtures under a
CC BY 4.0 licence from an external corpus (github.com/veraPDF/veraPDF-corpus), not something
this repository's own fixture set commits a copy of.
"""
import io
import sys
import zlib

from fontTools import subset
from fontTools.ttLib import TTFont

out_path = sys.argv[1]
font_path = sys.argv[2] if len(sys.argv) > 2 else "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
WORD_A = "Hello"
WORD_B = "World"
KEEP = WORD_A + WORD_B

full = TTFont(font_path)
upem = full["head"].unitsPerEm
cmap = full.getBestCmap()
hmtx = full["hmtx"]
cids = {ch: full.getGlyphID(cmap[ord(ch)]) for ch in sorted(set(KEEP))}

options = subset.Options()
options.notdef_outline = True
options.name_IDs = []
options.layout_features = []
options.retain_gids = True
sub = subset.Subsetter(options)
sub.populate(text=KEEP)
font = TTFont(font_path)
sub.subset(font)
buf = io.BytesIO()
font.save(buf)
program = buf.getvalue()

head = font["head"]
bbox = [round(v * 1000 / upem) for v in (head.xMin, head.yMin, head.xMax, head.yMax)]
widths = " ".join("%d [%d]" % (cid, round(hmtx[cmap[ord(ch)]][0] * 1000 / upem))
                  for ch, cid in sorted(cids.items(), key=lambda kv: kv[1]))
hex_a = "".join("%04X" % cids[ch] for ch in WORD_A)
hex_b = "".join("%04X" % cids[ch] for ch in WORD_B)
# Two columns, 60pt apart, both starting at the same y so BuildLines' vertical-centre-overlap
# rule has reason to cluster them into one line once BuildWords stops shredding each into
# one-glyph words (#444) -- the fixture would still show the bug as two separate one-column
# "lines" (one word each) without the fix, since each vertical run's word-height alone changed;
# the fidelity/token measure this bug was found by does not care about line grouping, only
# about the words themselves, so the golden below is the authority on what "fixed" means here.
content = ("BT /F1 24 Tf 72 700 Td <%s> Tj ET BT /F1 24 Tf 132 700 Td <%s> Tj ET"
           % (hex_a, hex_b)).encode()
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
    b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
    b"<< /Type /Font /Subtype /Type0 /BaseFont /ABCDEF+DejaVuSans /Encoding /Identity-V /DescendantFonts [6 0 R] /ToUnicode 9 0 R >>",
    stream(content),
    ("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /ABCDEF+DejaVuSans "
     "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
     "/FontDescriptor 7 0 R /DW 1000 /DW2 [880 -1000] /W [%s] /CIDToGIDMap /Identity >>" % widths).encode(),
    ("<< /Type /FontDescriptor /FontName /ABCDEF+DejaVuSans /Flags 32 /FontBBox [%s] /ItalicAngle 0 /Ascent %d "
     "/Descent %d /CapHeight %d /StemV 80 /FontFile2 8 0 R >>"
     % (" ".join(map(str, bbox)), bbox[3], bbox[1], bbox[3])).encode(),
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
