#!/usr/bin/env python3
"""Contract 9 fixtures (#142, #353): columns, furniture, lists, headings, a form-XObject
text run, a scanned page and a mixed text/scan document — design §7's list.

    tools/gen_structure_fixtures.py tests/MegaPDF.Core.Tests/Fixtures/structure

Unlike tools/gen_test_fixtures.py (stdlib only, run by CI on every OS on every push), these
fixtures embed real TrueType font programs so their glyph metrics — and so the golden block
files core_tests.cpp compares byte-for-byte — are the same on ubuntu, macos and windows,
which a non-embedded base-14 fixture is not (core/tests/core_tests.cpp's kSameFontsAsExpectation
comment explains why: PDFium substitutes a different system face per platform for an
unembedded standard font). fontTools is not on the CI runners (the same reason
tools/gen_subset_font_fixture.py and tools/gen_cid_font_fixture.py give), so — like those —
this script is not part of core-tests.yml's fixture-generation step; its *output* is
committed here, generated once on a machine that has fontTools and a DejaVu Sans install
(this one: /usr/share/fonts/truetype/dejavu/, Ubuntu's fonts-dejavu-core package).

Both weights are embedded whole (not subsetted to one fixture's own text, unlike
gen_subset_font_fixture.py's deliberately-partial subset for #130): every fixture uses one
shared FONT_REGULAR/FONT_BOLD pair, and subsetting per fixture would mean seven divergent
font programs to keep in sync instead of one.
"""
import io
import os
import sys
import zlib

from fontTools import subset
from fontTools.ttLib import TTFont

REGULAR_PATH = os.environ.get("MEGAPDF_DEJAVU_REGULAR", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf")
BOLD_PATH = os.environ.get("MEGAPDF_DEJAVU_BOLD", "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf")

# Every character any fixture below draws, so one subset serves all seven files.
KEEP_TEXT = (
    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 "
    ".,:;!?()-•\n"
    "Two-ColumnLayoutDemo TpHdingBrqcvw'stMFxjkyz"  # slack for words composed above
)


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


def winansi(text):
    """A PDF string literal in WinAnsi (cp1252): ASCII plus the bullet (U+2022 -> 0x95) and
    the other Latin-1-supplement punctuation the fixtures use, exactly as PDFium's own
    WinAnsiEncoding table maps them."""
    raw = text.encode("cp1252")
    return raw.replace(b"\\", b"\\\\").replace(b"(", b"\\(").replace(b")", b"\\)")


class FontKit:
    """Embeds one TrueType face (subsetted to KEEP_TEXT) as a WinAnsiEncoding-keyed simple
    font, the shape tools/gen_subset_font_fixture.py established for #130: FirstChar 32,
    real widths for every code up to 255 so the bullet and Latin-1 punctuation the fixtures
    use have a real advance, not the 0 a narrower range would silently give them.
    """

    def __init__(self, font_path):
        self.program = self._subset(font_path)
        full = TTFont(io.BytesIO(self.program))
        self.upem = full["head"].unitsPerEm
        self.cmap = full.getBestCmap()
        self.hmtx = full["hmtx"]
        head = full["head"]
        self.bbox = [round(v * 1000 / self.upem) for v in (head.xMin, head.yMin, head.xMax, head.yMax)]
        self.ascent = self.bbox[3]
        self.descent = self.bbox[1]
        self.glyph_count = len(full.getGlyphOrder())

    def _subset(self, font_path):
        options = subset.Options()
        options.notdef_outline = True
        options.name_IDs = []
        options.layout_features = []
        sub = subset.Subsetter(options)
        sub.populate(text=KEEP_TEXT)
        font = TTFont(font_path)
        sub.subset(font)
        buf = io.BytesIO()
        font.save(buf)
        return buf.getvalue()

    def width(self, codepoint):
        glyph = self.cmap.get(codepoint)
        if glyph is None:
            return 0
        return round(self.hmtx[glyph][0] * 1000 / self.upem)

    def add(self, add, base_name):
        codes = [b for b in range(32, 256) if b not in (0x81, 0x8D, 0x8F, 0x90, 0x9D)]
        widths = [self.width(ord(bytes([b]).decode("cp1252"))) if b not in (0x81, 0x8D, 0x8F, 0x90, 0x9D) else 0
                  for b in range(32, 256)]
        program_z = zlib.compress(self.program)
        font_file = add(stream(b"/Length1 %d /Filter /FlateDecode" % len(self.program), program_z))
        descriptor = add(
            ("<< /Type /FontDescriptor /FontName /%s /Flags 32 /FontBBox [%s] /ItalicAngle 0 "
             "/Ascent %d /Descent %d /CapHeight %d /StemV 80 /FontFile2 %d 0 R >>"
             % (base_name, " ".join(map(str, self.bbox)), self.ascent, self.descent, self.ascent, font_file)).encode())
        to_unicode = add(stream(b"", self._to_unicode_cmap(codes)))
        font = add(
            ("<< /Type /Font /Subtype /TrueType /BaseFont /%s /FirstChar 32 /LastChar 255 "
             "/Widths [%s] /Encoding /WinAnsiEncoding /FontDescriptor %d 0 R /ToUnicode %d 0 R >>"
             % (base_name, " ".join(map(str, widths)), descriptor, to_unicode)).encode())
        return font

    @staticmethod
    def _to_unicode_cmap(codes):
        """A ToUnicode CMap mapping every WinAnsi byte code to its real Unicode codepoint.

        Without one, a PDF reader must GUESS a simple TrueType font's per-character Unicode
        from its embedded cmap table, which is exactly the kind of thing that goes quietly
        wrong for a subsetted font: FPDFText_GetUnicode came back 2 (PDFium's "could not
        determine" placeholder) for the hyphen in this fixture's own hyphen-joining test,
        with no ToUnicode present, even though /Encoding /WinAnsiEncoding + /Widths were
        both correct and the glyph rendered fine. An explicit CMap removes the guess.
        """
        # The PDF spec caps a beginbfchar/endbfchar block at 100 entries.
        blocks = b""
        for start in range(0, len(codes), 100):
            chunk = codes[start:start + 100]
            entries = b"".join(b"<%02X> <%04X>\n" % (b, ord(bytes([b]).decode("cp1252"))) for b in chunk)
            blocks += b"%d beginbfchar\n%sendbfchar\n" % (len(chunk), entries)
        return (b"/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n"
                b"/CIDSystemInfo << /Registry (MegaPDF) /Ordering (UCS) /Supplement 0 >> def\n"
                b"/CMapName /MegaPDF-UCS def\n/CMapType 2 def\n"
                b"1 begincodespacerange\n<20> <FF>\nendcodespacerange\n"
                b"%sendcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n" % blocks)


class Doc:
    """One fixture PDF under construction: shared object list, a page list, and the two
    embedded fonts (added once, referenced by every page)."""

    def __init__(self, regular, bold):
        self.objs = []
        self.pages = []
        self.regular_kit = regular
        self.bold_kit = bold
        self.regular = regular.add(self.add, "MegaPDFStructureFixture-Regular")
        self.bold = bold.add(self.add, "MegaPDFStructureFixture-Bold")

    def add(self, body):
        self.objs.append(body)
        return len(self.objs)

    def font_refs(self):
        return b"/F1 %d 0 R /F2 %d 0 R" % (self.regular, self.bold)

    def add_page(self, content, extra_resources=b"", annots=None):
        content_ref = self.add(stream(b"", content))
        placeholder = self.add(b"<< /Type /Page >>")   # patched below once Pages' object number is known
        self.pages.append((placeholder, content_ref, extra_resources, annots))

    def finish(self):
        pages_num = len(self.objs) + 1   # patching below adds nothing; Pages is the next object
        # Patch each page object in place (same object number, real content).
        kids = []
        for placeholder, content_ref, extra_resources, annots in self.pages:
            self.objs[placeholder - 1] = (
                b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
                b"/Resources << /Font << %s >>%s >> /Contents %d 0 R%s >>"
                % (pages_num, self.font_refs(), extra_resources, content_ref,
                   b" /Annots [%s]" % b" ".join(b"%d 0 R" % a for a in annots) if annots else b""))
            kids.append(placeholder)
        pages = self.add(b"<< /Type /Pages /Kids [%s] /Count %d >>"
                         % (b" ".join(b"%d 0 R" % k for k in kids), len(kids)))
        assert pages == pages_num
        self.add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages)
        return build(self.objs)


def text_ops(font_ref, size, x, y, text):
    return b"BT /%s %s Tf %s %s Td (%s) Tj ET\n" % (font_ref, str(size).encode(), str(x).encode(), str(y).encode(),
                                                     winansi(text))


# ---------------------------------------------------------------------------

def gen_columns(regular, bold):
    d = Doc(regular, bold)
    body = text_ops(b"F2", 20, 72, 730, "Two-Column Layout Demo")
    left = ["Left column line one.", "Left column line two.", "Left column line three.",
            "Left column line four.", "Left column line five."]
    right = ["Right column line one.", "Right column line two.", "Right column line three.",
             "Right column line four.", "Right column line five."]
    y = 680
    for line in left:
        body += text_ops(b"F1", 12, 72, y, line)
        y -= 20
    y = 680
    for line in right:
        body += text_ops(b"F1", 12, 330, y, line)
        y -= 20
    d.add_page(body)
    return d.finish()


def gen_furniture(regular, bold):
    d = Doc(regular, bold)
    for n in range(1, 4):
        body = text_ops(b"F1", 10, 72, 760, "MegaPDF Furniture Fixture")
        body += text_ops(b"F1", 12, 72, 400, "This is the content of page %d." % n)
        body += text_ops(b"F1", 10, 260, 40, "Page %d of 3" % n)
        d.add_page(body)
    return d.finish()


def gen_lists(regular, bold):
    # Two spaces between a marker and its body text, not one: design §1.2 needs the gap
    # >= 0.5 em, and a single space glyph's advance in most text faces (DejaVuSans included)
    # falls close enough to that line that whether a given marker (a period's narrow glyph,
    # a wider bullet) clears it becomes a coin flip — measured while building this fixture,
    # where a single space put half the markers just under the threshold.
    d = Doc(regular, bold)
    body = text_ops(b"F2", 16, 72, 740, "Shopping List")
    for i, item in enumerate(["Milk", "Bread", "Eggs"]):
        body += text_ops(b"F1", 12, 72, 700 - i * 20, "•  %s" % item)
    for i, item in enumerate(["First step", "Second step", "Third step"]):
        body += text_ops(b"F1", 12, 72, 620 - i * 20, "%d.  %s" % (i + 1, item))
    body += text_ops(b"F1", 12, 72, 540, "1.  Preparation")
    body += text_ops(b"F1", 12, 100, 520, "a.  Wash vegetables")
    body += text_ops(b"F1", 12, 100, 500, "b.  Chop vegetables")
    body += text_ops(b"F1", 12, 72, 460, "This paragraph demonstrates a hyphen-")
    body += text_ops(b"F1", 12, 72, 445, "ated line ending that should join without a space.")
    body += text_ops(b"F1", 12, 72, 410, "Standards like ISO-")
    body += text_ops(b"F1", 12, 72, 395, "8859 are common encodings.")
    d.add_page(body)
    return d.finish()


def gen_headings(regular, bold):
    d = Doc(regular, bold)
    body = text_ops(b"F2", 24, 72, 750, "Top Level Heading")
    body += text_ops(b"F1", 12, 72, 715, "Body text under the top heading.")
    body += text_ops(b"F2", 20, 72, 670, "Second Level Heading")
    body += text_ops(b"F1", 12, 72, 645, "Body text under the second heading.")
    body += text_ops(b"F2", 16, 72, 610, "Third Level Heading")
    body += text_ops(b"F1", 12, 72, 588, "Body text under the third heading.")
    body += text_ops(b"F2", 14, 72, 555, "Fourth Level Heading")
    body += text_ops(b"F1", 12, 72, 533, "Body text under the fourth heading.")
    body += text_ops(b"F2", 12, 72, 490, "Notice")
    body += text_ops(b"F1", 12, 72, 440, "Text following the bold notice heading.")
    d.add_page(body)
    return d.finish()


def gen_xobject_text(regular, bold):
    d = Doc(regular, bold)
    fm_content = text_ops(b"F1", 14, 0, 0, "Text drawn by a form XObject.")
    fm = d.add(stream(
        b"/Type /XObject /Subtype /Form /BBox [0 0 400 50] "
        b"/Resources << /Font << %s >> >>" % d.font_refs(),
        fm_content))
    page_content = b"q 1 0 0 1 100 700 cm /Fm1 Do Q\n"
    page_content += text_ops(b"F1", 12, 72, 650, "Page-level text above the XObject text.")
    d.add_page(page_content, extra_resources=b" /XObject << /Fm1 %d 0 R >>" % fm)
    return d.finish()


def gen_scan(regular, bold):
    d = Doc(regular, bold)
    image = d.add(stream(
        b"/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8",
        b"\x80\x80\x80\x80\x80\x80\x80\x80\x80\x80\x80\x80"))
    content = b"q 468 0 0 648 72 72 cm /Im1 Do Q\n"
    d.add_page(content, extra_resources=b" /XObject << /Im1 %d 0 R >>" % image)
    return d.finish()


def gen_mixed(regular, bold):
    d = Doc(regular, bold)
    d.add_page(text_ops(b"F1", 14, 72, 700, "First page has text."))
    image = d.add(stream(
        b"/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8",
        b"\x80\x80\x80\x80\x80\x80\x80\x80\x80\x80\x80\x80"))
    d.add_page(b"q 468 0 0 648 72 72 cm /Im1 Do Q\n", extra_resources=b" /XObject << /Im1 %d 0 R >>" % image)
    d.add_page(text_ops(b"F1", 14, 72, 700, "Third page has text too."))
    return d.finish()


def main():
    outdir = sys.argv[1] if len(sys.argv) > 1 else "tests/MegaPDF.Core.Tests/Fixtures/structure"
    os.makedirs(outdir, exist_ok=True)
    regular = FontKit(REGULAR_PATH)
    bold = FontKit(BOLD_PATH)
    print("embedded DejaVu Sans (%d glyphs) and DejaVu Sans Bold (%d glyphs)"
          % (regular.glyph_count, bold.glyph_count))
    generators = (
        ("columns.pdf", gen_columns),
        ("furniture.pdf", gen_furniture),
        ("lists.pdf", gen_lists),
        ("headings.pdf", gen_headings),
        ("xobject-text.pdf", gen_xobject_text),
        ("scan.pdf", gen_scan),
        ("mixed.pdf", gen_mixed),
    )
    for name, gen in generators:
        data = gen(regular, bold)
        path = os.path.join(outdir, name)
        with open(path, "wb") as f:
            f.write(data)
        print("wrote %s (%d bytes)" % (path, len(data)))


if __name__ == "__main__":
    main()
