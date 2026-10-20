#!/usr/bin/env python3
"""#710: the fixture for contract 11, text selection: a two-column page, and a page of
ligatures and line-end hyphens.

    tools/gen_selection_fixture.py tests/MegaPDF.Core.Tests/Fixtures/structure/selection.pdf

Built on tools/gen_structure_fixtures.py's Doc and text helpers, so it embeds the same DejaVu
Sans faces every contract 9 fixture does and its glyph metrics are the same on every CI OS. It
writes one file only: running tools/gen_structure_fixtures.py itself rewrites every structure
fixture (fontTools stamps each subset), which would move goldens this fixture has nothing to do
with. fontTools is not on the CI runners, so the output is committed, like its parent's.

Page 1, two columns. A title across the top, then five lines at x=72 and five at x=330, of
deliberately different lengths, so a test can ask for a point past the end of a short line that
is still inside its column (the caret belongs on that line, not on the longer one below it).
The columns are drawn left column first, but nothing in a selection may depend on that: the
order the core reads them in is the XY-cut's, from the geometry.

Page 2, what the clipboard must get right:

  * "fi", drawn as ONE glyph (the font's U+FB01 ligature, code 0x81 through /Differences)
    whose ToUnicode maps it to the TWO characters "f" "i" -- the common way a producer
    writes a ligature;
  * "fl", drawn as one glyph (U+FB02, code 0x8D) whose ToUnicode maps it to the single
    presentation-form code point U+FB02 -- the less common way, which a copy must still expand;
  * a line that ends in a soft hyphen (WinAnsi 0xAD, U+00AD);
  * a line that ends in a hard hyphen before a lowercase word ("well-" / "known"), the case
    contract 9's hyphen-joining would join and a copy must not.
"""
import importlib.util
import io
import os
import sys
import zlib

from fontTools import subset
from fontTools.ttLib import TTFont

_HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "gen_structure_fixtures", os.path.join(_HERE, "gen_structure_fixtures.py"))
_gsf = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_gsf)

FI_CODE = 0x81   # undefined in WinAnsi, so /Differences can name it without shadowing a real character
FL_CODE = 0x8D
KEEP_TEXT = _gsf.KEEP_TEXT + "­ﬁﬂ"

COLUMN_TITLE = "Selection Columns"
LEFT = ["Left one alpha", "Left two bravo and more", "Left three charlie", "Left four delta", "Left five echo"]
RIGHT = ["Right one golf", "Right two hotel", "Right three india", "Right four juliet", "Right five kilo"]


class LigatureFontKit(_gsf.FontKit):
    """The parent's DejaVu subset, plus the soft hyphen and the fi/fl ligature glyphs, written as
    a WinAnsi font whose /Differences put the two ligatures on codes WinAnsi leaves undefined."""

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

    def add(self, add, base_name):
        undefined = (0x81, 0x8D, 0x8F, 0x90, 0x9D)
        widths = []
        for b in range(32, 256):
            if b == FI_CODE:
                widths.append(self.width(0xFB01))
            elif b == FL_CODE:
                widths.append(self.width(0xFB02))
            elif b in undefined:
                widths.append(0)
            else:
                widths.append(self.width(ord(bytes([b]).decode("cp1252"))))
        program_z = zlib.compress(self.program)
        font_file = add(_gsf.stream(b"/Length1 %d /Filter /FlateDecode" % len(self.program), program_z))
        descriptor = add(
            ("<< /Type /FontDescriptor /FontName /%s /Flags 32 /FontBBox [%s] /ItalicAngle 0 "
             "/Ascent %d /Descent %d /CapHeight %d /StemV 80 /FontFile2 %d 0 R >>"
             % (base_name, " ".join(map(str, self.bbox)), self.ascent, self.descent, self.ascent, font_file)).encode())
        entries = []
        for b in range(32, 256):
            if b == FI_CODE:
                entries.append(b"<%02X> <00660069>\n" % b)      # one glyph, two characters
            elif b == FL_CODE:
                entries.append(b"<%02X> <FB02>\n" % b)          # one glyph, one presentation form
            elif b not in undefined:
                entries.append(b"<%02X> <%04X>\n" % (b, ord(bytes([b]).decode("cp1252"))))
        blocks = b""
        for start in range(0, len(entries), 100):
            chunk = entries[start:start + 100]
            blocks += b"%d beginbfchar\n%sendbfchar\n" % (len(chunk), b"".join(chunk))
        cmap = (b"/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n"
                b"/CIDSystemInfo << /Registry (MegaPDF) /Ordering (UCS) /Supplement 0 >> def\n"
                b"/CMapName /MegaPDF-UCS def\n/CMapType 2 def\n"
                b"1 begincodespacerange\n<20> <FF>\nendcodespacerange\n"
                b"%sendcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n" % blocks)
        to_unicode = add(_gsf.stream(b"", cmap))
        font = add(
            ("<< /Type /Font /Subtype /TrueType /BaseFont /%s /FirstChar 32 /LastChar 255 "
             "/Widths [%s] /Encoding << /Type /Encoding /BaseEncoding /WinAnsiEncoding "
             "/Differences [%d /fi %d /fl] >> /FontDescriptor %d 0 R /ToUnicode %d 0 R >>"
             % (base_name, " ".join(map(str, widths)), FI_CODE, FL_CODE, descriptor, to_unicode)).encode())
        return font


def raw_text_ops(font_ref, size, x, y, raw):
    """text_ops() for a string that is already bytes in the font's encoding (the ligature codes
    are not cp1252 characters, so the parent's winansi() cannot produce them)."""
    raw = raw.replace(b"\\", b"\\\\").replace(b"(", b"\\(").replace(b")", b"\\)")
    return b"BT /%s %d Tf %d %d Td (%s) Tj ET\n" % (font_ref, size, x, y, raw)


def gen_selection(regular, bold):
    d = _gsf.Doc(regular, bold)

    body = _gsf.text_ops(b"F2", 20, 72, 730, COLUMN_TITLE)
    y = 680
    for line in LEFT:
        body += _gsf.text_ops(b"F1", 12, 72, y, line)
        y -= 20
    y = 680
    for line in RIGHT:
        body += _gsf.text_ops(b"F1", 12, 330, y, line)
        y -= 20
    d.add_page(body)

    body = raw_text_ops(b"F1", 12, 72, 700, b"The " + bytes([FI_CODE]) + b"nal " + bytes([FL_CODE]) + b"ow test")
    body += raw_text_ops(b"F1", 12, 72, 680, b"We test hyphen\xad")
    body += raw_text_ops(b"F1", 12, 72, 660, b"ation here.")
    body += raw_text_ops(b"F1", 12, 72, 640, b"A well-")
    body += raw_text_ops(b"F1", 12, 72, 620, b"known word.")
    d.add_page(body)
    return d.finish()


def main():
    if len(sys.argv) != 2:
        sys.exit("usage: gen_selection_fixture.py <out.pdf>")
    regular = LigatureFontKit(_gsf.REGULAR_PATH)
    bold = LigatureFontKit(_gsf.BOLD_PATH)
    data = gen_selection(regular, bold)
    with open(sys.argv[1], "wb") as fh:
        fh.write(data)
    print("wrote %s (%d bytes, 2 pages)" % (sys.argv[1], len(data)))


if __name__ == "__main__":
    main()
