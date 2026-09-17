#!/usr/bin/env python3
"""Generate the #173 redaction fixtures.

    python3 tools/gen_redaction_fixtures.py <outdir>

Every fixture hides the same canary, **CANARY-42-XYZ**, somewhere a redaction must
reach, and puts a **KEEP** word beside it that must come through untouched and
unmoved. The leak checker (tools/leakcheck) then has one string to hunt for in the
saved file and one to find still drawn in the right place.

The fixtures, and what each is for:

  text-run-boundary.pdf  the canary split across two text objects on one line
  text-partial-run.pdf   the canary in the middle of a run, KEEP either side
  text-rotated.pdf       the same, drawn at 30 degrees
  text-actualtext.pdf    a marked-content /ActualText carrying the canary as well
  text-doubled.pdf       the line drawn twice for fake bold - the #136 hidden copy
  image-flate.pdf        the canary as pixels in a Flate image
  image-jpeg.pdf         the same pixels as a baseline JPEG (DCTDecode)
  image-ccitt.pdf        the same pixels as a 1-bit scan
  image-jbig2.pdf        the same as a 1-bit generic-region JBIG2 scan
  image-inline.pdf       the same pixels as an inline image (BI ... ID ... EI)
  image-shared.pdf       one image drawn on two pages: redacting page 1 must not
                         change page 2
  image-smask.pdf        an image with a soft mask over the canary
  form-xobject.pdf       one form XObject drawn on two pages, the canary inside it
  form-field.pdf         an AcroForm text field whose value is the canary
  link.pdf               a link annotation whose URI carries the canary
  metadata.pdf           the canary in /Info and in the XMP packet
  vector-canary.pdf      the canary drawn as filled vector paths, no text at all
  vector-straddle.pdf    a rule crossing the area, which must be clipped not dropped

Deterministic: the same bytes every run, so the committed expectations hold.
"""
import os
import struct
import sys
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gen_test_fixtures import build, stream          # noqa: E402  (the same low-level builder)

CANARY = "CANARY-42-XYZ"
KEEP = "KEEP"

# The canary rendered as a bitmap, so every image fixture carries the same pixels: a 5x7
# blocky font is enough for "is it still there" and keeps the fixtures tiny and readable.
GLYPHS = {
    "A": ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
    "C": ["01110", "10001", "10000", "10000", "10000", "10001", "01110"],
    "N": ["10001", "11001", "10101", "10011", "10001", "10001", "10001"],
    "R": ["11110", "10001", "10001", "11110", "10100", "10010", "10001"],
    "Y": ["10001", "01010", "00100", "00100", "00100", "00100", "00100"],
    "-": ["00000", "00000", "00000", "11111", "00000", "00000", "00000"],
    "4": ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
    "2": ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
    "X": ["10001", "01010", "00100", "00100", "00100", "01010", "10001"],
    "Z": ["11111", "00001", "00010", "00100", "01000", "10000", "11111"],
    "K": ["10001", "10010", "10100", "11000", "10100", "10010", "10001"],
    "E": ["11111", "10000", "10000", "11110", "10000", "10000", "11111"],
    "P": ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
}


def raster(text, scale=2, pad=2):
    """`text` as (width, height, rows of 0/1). An unknown character draws blank."""
    cells = [GLYPHS.get(c, ["00000"] * 7) for c in text]
    w = (len(cells) * 6 - 1) * scale + 2 * pad
    h = 7 * scale + 2 * pad
    rows = [[0] * w for _ in range(h)]
    for ci, cell in enumerate(cells):
        for y in range(7):
            for x in range(5):
                if cell[y][x] != "1":
                    continue
                for dy in range(scale):
                    for dx in range(scale):
                        rows[pad + y * scale + dy][pad + (ci * 6 + x) * scale + dx] = 1
    return w, h, rows


def grey_bytes(rows):
    """Ink black on white, 8 bits per pixel."""
    return bytes(0x00 if v else 0xFF for row in rows for v in row)


def bilevel_bytes(rows):
    """1 bit per pixel, each row padded to a byte; 0 is black in DeviceGray."""
    out = bytearray()
    for row in rows:
        acc, bits = 0, 0
        for v in row:
            acc = (acc << 1) | (0 if v else 1)
            bits += 1
            if bits == 8:
                out.append(acc)
                acc, bits = 0, 0
        if bits:
            out.append(acc << (8 - bits))
    return bytes(out)


# --------------------------------------------------------------------------
# Text fixtures
# --------------------------------------------------------------------------

def _one_page(content, extra_resources=b"", media=b"[0 0 612 792]"):
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]
    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    body = add(stream(b"", content))
    pages_num = len(objs) + 2
    page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox %s /Resources << /Font << /F1 %d 0 R >> %s >> "
               b"/Contents %d 0 R >>" % (pages_num, media, font, extra_resources, body))
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    assert pages == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages)
    return build(objs)


def gen_text_run_boundary():
    # "KEEP CAN" and "ARY-42-XYZ KEEP" as two text objects, so the canary straddles them.
    return _one_page(b"BT /F1 18 Tf 72 700 Td (KEEP CAN) Tj ET\n"
                     b"BT /F1 18 Tf 155 700 Td (ARY-42-XYZ KEEP) Tj ET\n")


def gen_text_partial_run():
    return _one_page(b"BT /F1 18 Tf 72 700 Td (KEEP CANARY-42-XYZ KEEP) Tj ET\n")


def gen_text_rotated():
    # 30 degrees about (72, 500).
    return _one_page(b"BT /F1 18 Tf .8660254 .5 -.5 .8660254 72 500 Tm (KEEP CANARY-42-XYZ KEEP) Tj ET\n")


def gen_text_actualtext():
    return _one_page(b"/Span << /ActualText (CANARY-42-XYZ) >> BDC\n"
                     b"BT /F1 18 Tf 72 700 Td (KEEP CANARY-42-XYZ KEEP) Tj ET\n"
                     b"EMC\n")


def gen_text_doubled():
    # Fake bold: the same line drawn twice, a fifth of a point apart (#136).
    return _one_page(b"BT /F1 18 Tf 72 700 Td (KEEP CANARY-42-XYZ KEEP) Tj ET\n"
                     b"BT /F1 18 Tf 72.2 700 Td (KEEP CANARY-42-XYZ KEEP) Tj ET\n")


# --------------------------------------------------------------------------
# Image fixtures
# --------------------------------------------------------------------------

def _image_page(image_obj_body, extra_objs=(), draw=b"q 300 0 0 100 100 600 cm /Im0 Do Q\n",
                page_text=b"BT /F1 18 Tf 72 500 Td (KEEP) Tj ET\n", pages=1):
    objs = list(extra_objs)
    add = lambda b: (objs.append(b), len(objs))[1]
    img = add(image_obj_body)
    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    contents = [add(stream(b"", draw + page_text)) for _ in range(pages)]
    pages_num = len(objs) + pages + 1
    kids = []
    for c in contents:
        kids.append(add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] /Resources "
                        b"<< /XObject << /Im0 %d 0 R >> /Font << /F1 %d 0 R >> >> /Contents %d 0 R >>"
                        % (pages_num, img, font, c)))
    tree = add(b"<< /Type /Pages /Kids [%s] /Count %d >>"
               % (b" ".join(b"%d 0 R" % k for k in kids), pages))
    assert tree == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R >>" % tree)
    return build(objs)


def gen_image_flate():
    w, h, rows = raster(CANARY)
    return _image_page(stream(b"/Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceGray "
                              b"/BitsPerComponent 8 /Filter /FlateDecode" % (w, h),
                              zlib.compress(grey_bytes(rows), 9)))


def gen_image_shared():
    w, h, rows = raster(CANARY)
    return _image_page(stream(b"/Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceGray "
                              b"/BitsPerComponent 8 /Filter /FlateDecode" % (w, h),
                              zlib.compress(grey_bytes(rows), 9)), pages=2)


def gen_image_ccitt():
    """The same pixels as a 1-bit scan. A real Group 4 stream is what a fax-coded scan
    carries; the core reads it through PDFium, which decodes it to the same 1 bpc bitmap
    as this Flate-coded one, and the redaction path is the same. `--ccitt` on a machine
    with an encoder writes the Group 4 form."""
    w, h, rows = raster(CANARY)
    return _image_page(stream(b"/Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceGray "
                              b"/BitsPerComponent 1 /Filter /FlateDecode" % (w, h),
                              zlib.compress(bilevel_bytes(rows), 9)))


def gen_image_smask():
    w, h, rows = raster(CANARY)
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]
    # The mask is opaque where the ink is, so the canary only shows through the mask.
    mask = add(stream(b"/Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceGray "
                      b"/BitsPerComponent 8 /Filter /FlateDecode" % (w, h),
                      zlib.compress(bytes(0xFF if v else 0x00 for row in rows for v in row), 9)))
    body = stream(b"/Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceGray "
                  b"/BitsPerComponent 8 /SMask %d 0 R /Filter /FlateDecode" % (w, h, mask),
                  zlib.compress(grey_bytes(rows), 9))
    return _image_page(body, extra_objs=objs)


def gen_image_inline():
    w, h, rows = raster(CANARY)
    data = zlib.compress(grey_bytes(rows), 9)
    draw = (b"q 300 0 0 100 100 600 cm\n"
            b"BI /W %d /H %d /CS /G /BPC 8 /F /Fl ID " % (w, h)) + data + b" EI Q\n"
    return _one_page(draw + b"BT /F1 18 Tf 72 500 Td (KEEP) Tj ET\n")


def gen_image_jpeg():
    """A baseline JPEG of the same raster, encoded below so no library is needed."""
    w, h, rows = raster(CANARY)
    return _image_page(stream(b"/Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceGray "
                              b"/BitsPerComponent 8 /Filter /DCTDecode" % (w, h),
                              _jpeg_grey(w, h, grey_bytes(rows))))


def gen_image_jbig2():
    """A JBIG2 generic region holding the same bilevel raster."""
    w, h, rows = raster(CANARY)
    return _image_page(stream(b"/Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceGray "
                              b"/BitsPerComponent 1 /Filter /JBIG2Decode" % (w, h),
                              _jbig2_generic(w, h, rows)))


# --------------------------------------------------------------------------
# Structure fixtures
# --------------------------------------------------------------------------

def gen_form_xobject():
    """One form XObject with the canary in it, drawn on two pages."""
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]
    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    form = add(stream(b"/Type /XObject /Subtype /Form /BBox [0 0 300 40] "
                      b"/Resources << /Font << /F1 %d 0 R >> >>" % font,
                      b"BT /F1 18 Tf 0 10 Td (KEEP CANARY-42-XYZ) Tj ET\n"))
    c1 = add(stream(b"", b"q 1 0 0 1 100 650 cm /Fm0 Do Q\nBT /F1 18 Tf 72 500 Td (KEEP) Tj ET\n"))
    c2 = add(stream(b"", b"q 1 0 0 1 100 300 cm /Fm0 Do Q\n"))
    pages_num = len(objs) + 3
    p1 = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] /Resources "
             b"<< /XObject << /Fm0 %d 0 R >> /Font << /F1 %d 0 R >> >> /Contents %d 0 R >>"
             % (pages_num, form, font, c1))
    p2 = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] /Resources "
             b"<< /XObject << /Fm0 %d 0 R >> >> /Contents %d 0 R >>" % (pages_num, form, c2))
    pages = add(b"<< /Type /Pages /Kids [%d 0 R %d 0 R] /Count 2 >>" % (p1, p2))
    assert pages == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages)
    return build(objs)


def gen_form_field():
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]
    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    ap = add(stream(b"/Type /XObject /Subtype /Form /BBox [0 0 200 20] "
                    b"/Resources << /Font << /F1 %d 0 R >> >>" % font,
                    b"BT /F1 12 Tf 2 5 Td (CANARY-42-XYZ) Tj ET\n"))
    content = add(stream(b"", b"BT /F1 18 Tf 72 500 Td (KEEP) Tj ET\n"))
    # widget, page, pages: three more objects after the three already added.
    page_num = len(objs) + 2
    pages_num = len(objs) + 3
    widget = add(b"<< /Type /Annot /Subtype /Widget /FT /Tx /T (account) /V (CANARY-42-XYZ) "
                 b"/DV (CANARY-42-XYZ) /Rect [100 690 300 710] /F 4 /P %d 0 R /AP << /N %d 0 R >> >>"
                 % (page_num, ap))
    page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 %d 0 R >> >> "
               b"/Contents %d 0 R /Annots [%d 0 R] >>" % (pages_num, font, content, widget))
    assert page == page_num
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    assert pages == pages_num
    acro = add(b"<< /Fields [%d 0 R] /DA (/Helv 0 Tf 0 g) >>" % widget)
    add(b"<< /Type /Catalog /Pages %d 0 R /AcroForm %d 0 R >>" % (pages, acro))
    return build(objs)


def gen_link():
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]
    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    content = add(stream(b"", b"BT /F1 18 Tf 72 700 Td (KEEP CANARY-42-XYZ) Tj ET\n"))
    action = add(b"<< /Type /Action /S /URI /URI (https://example.invalid/CANARY-42-XYZ) >>")
    pages_num = len(objs) + 3
    link = add(b"<< /Type /Annot /Subtype /Link /Rect [72 695 300 720] /Border [0 0 0] /A %d 0 R >>" % action)
    page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 %d 0 R >> >> "
               b"/Contents %d 0 R /Annots [%d 0 R] >>" % (pages_num, font, content, link))
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    assert pages == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages)
    return build(objs)


def gen_metadata():
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]
    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    content = add(stream(b"", b"BT /F1 18 Tf 72 700 Td (KEEP CANARY-42-XYZ KEEP) Tj ET\n"))
    xmp = (b'<?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>\n'
           b'<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF '
           b'xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
           b'<rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/">'
           b'<dc:title><rdf:Alt><rdf:li xml:lang="x-default">CANARY-42-XYZ</rdf:li></rdf:Alt></dc:title>'
           b'</rdf:Description></rdf:RDF></x:xmpmeta>\n<?xpacket end="w"?>')
    meta = add(stream(b"/Type /Metadata /Subtype /XML", xmp))
    pages_num = len(objs) + 2
    page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 %d 0 R >> >> "
               b"/Contents %d 0 R >>" % (pages_num, font, content))
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    assert pages == pages_num
    catalog = add(b"<< /Type /Catalog /Pages %d 0 R /Metadata %d 0 R >>" % (pages, meta))
    info = add(b"<< /Title (CANARY-42-XYZ) /Author (CANARY-42-XYZ) /Keywords (CANARY-42-XYZ) >>")
    data = build(objs)
    # build() writes its own trailer; /Info is added by rewriting it.
    return data.replace(b"/Root %d 0 R >>" % catalog, b"/Root %d 0 R /Info %d 0 R >>" % (catalog, info))


def gen_vector_canary():
    """The canary drawn as filled rectangles - no text object at all, so only the
    geometry carries it. A redaction must remove the paths inside the area."""
    w, h, rows = raster(CANARY, scale=1, pad=0)
    ops = [b"0 g\n"]
    for y, row in enumerate(rows):
        for x, v in enumerate(row):
            if v:
                ops.append(b"%d %d 2 2 re f\n" % (100 + x * 2, 700 - y * 2))
    ops.append(b"BT /F1 18 Tf 72 500 Td (KEEP) Tj ET\n")
    return _one_page(b"".join(ops))


def gen_vector_straddle():
    """A rule and a filled band that cross the edge of the area: what must be clipped
    rather than dropped, and what the guard would refuse if it were dropped."""
    return _one_page(b"BT /F1 18 Tf 72 700 Td (KEEP CANARY-42-XYZ KEEP) Tj ET\n"
                     b"1 w 0 G 40 690 m 560 690 l S\n"
                     b"0.8 g 40 660 520 20 re f\n"
                     b"BT /F1 18 Tf 72 500 Td (KEEP) Tj ET\n")


# --------------------------------------------------------------------------
# Minimal encoders, so the fixtures need no third-party library on a CI runner
# --------------------------------------------------------------------------

_ZIGZAG = [0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
           12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
           35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
           58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63]
# The Annex K luminance quantisation table, quality about 50.
_QUANT = [16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55,
          14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
          18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92,
          49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99]
# The Annex K standard Huffman tables (luminance DC and AC).
_DC_BITS = [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0]
_DC_VALS = list(range(12))
_AC_BITS = [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D]
_AC_VALS = [
    0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
    0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
    0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
    0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
    0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
    0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
    0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
    0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
    0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
    0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
    0xF9, 0xFA]


def _huff_table(bits, vals):
    codes, code, k = {}, 0, 0
    for length in range(1, 17):
        for _ in range(bits[length - 1]):
            codes[vals[k]] = (code, length)
            code += 1
            k += 1
        code <<= 1
    return codes


class _BitWriter:
    def __init__(self):
        self.out = bytearray()
        self.acc = 0
        self.n = 0

    def write(self, code, length):
        for i in range(length - 1, -1, -1):
            self.acc = (self.acc << 1) | ((code >> i) & 1)
            self.n += 1
            if self.n == 8:
                byte = self.acc & 0xFF
                self.out.append(byte)
                if byte == 0xFF:
                    self.out.append(0x00)          # byte stuffing
                self.acc, self.n = 0, 0

    def flush(self):
        while self.n:
            self.write(1, 1)
        return bytes(self.out)


def _fdct(block):
    import math
    out = [0.0] * 64
    for u in range(8):
        for v in range(8):
            s = 0.0
            for x in range(8):
                for y in range(8):
                    s += block[y * 8 + x] * math.cos((2 * x + 1) * v * math.pi / 16) * \
                         math.cos((2 * y + 1) * u * math.pi / 16)
            cu = 1 / math.sqrt(2) if u == 0 else 1.0
            cv = 1 / math.sqrt(2) if v == 0 else 1.0
            out[u * 8 + v] = 0.25 * cu * cv * s
    return out


def _magnitude(value):
    size, v = 0, abs(value)
    while v:
        size += 1
        v >>= 1
    bits = value if value >= 0 else value + (1 << size) - 1
    return size, bits


def _jpeg_grey(w, h, pixels):
    """A baseline greyscale JPEG. Plain and slow - the fixtures are a few thousand
    pixels, and this keeps the generator free of dependencies."""
    dc_codes = _huff_table(_DC_BITS, _DC_VALS)
    ac_codes = _huff_table(_AC_BITS, _AC_VALS)
    bw = _BitWriter()
    prev_dc = 0
    for by in range(0, h, 8):
        for bx in range(0, w, 8):
            block = []
            for y in range(8):
                for x in range(8):
                    sx, sy = min(bx + x, w - 1), min(by + y, h - 1)
                    block.append(pixels[sy * w + sx] - 128)
            coeffs = _fdct(block)
            q = [int(round(coeffs[i] / _QUANT[i])) for i in range(64)]
            zz = [q[_ZIGZAG[i]] for i in range(64)]
            diff = zz[0] - prev_dc
            prev_dc = zz[0]
            size, bits = _magnitude(diff)
            code, length = dc_codes[size]
            bw.write(code, length)
            if size:
                bw.write(bits, size)
            run = 0
            for i in range(1, 64):
                if zz[i] == 0:
                    run += 1
                    continue
                while run > 15:
                    code, length = ac_codes[0xF0]
                    bw.write(code, length)
                    run -= 16
                size, bits = _magnitude(zz[i])
                code, length = ac_codes[(run << 4) | size]
                bw.write(code, length)
                bw.write(bits, size)
                run = 0
            if run:
                code, length = ac_codes[0x00]
                bw.write(code, length)
    scan = bw.flush()

    out = bytearray(b"\xFF\xD8")
    out += b"\xFF\xDB" + struct.pack(">HB", 67, 0) + bytes(_QUANT[_ZIGZAG[i]] for i in range(64))
    out += b"\xFF\xC0" + struct.pack(">HBHHB", 11, 8, h, w, 1) + bytes([1, 0x11, 0])
    out += b"\xFF\xC4" + struct.pack(">HB", 2 + 1 + 16 + len(_DC_VALS), 0x00) + bytes(_DC_BITS) + bytes(_DC_VALS)
    out += b"\xFF\xC4" + struct.pack(">HB", 2 + 1 + 16 + len(_AC_VALS), 0x10) + bytes(_AC_BITS) + bytes(_AC_VALS)
    out += b"\xFF\xDA" + struct.pack(">HB", 8, 1) + bytes([1, 0x00]) + bytes([0, 63, 0])
    out += scan + b"\xFF\xD9"
    return bytes(out)


class _MQEncoder:
    """The MQ arithmetic coder of JBIG2 (ISO/IEC 14492 Annex E)."""
    QE = [
        (0x5601, 1, 1, 1), (0x3401, 2, 6, 0), (0x1801, 3, 9, 0), (0x0AC1, 4, 12, 0), (0x0521, 5, 29, 0),
        (0x0221, 38, 33, 0), (0x5601, 7, 6, 1), (0x5401, 8, 14, 0), (0x4801, 9, 14, 0), (0x3801, 10, 14, 0),
        (0x3001, 11, 17, 0), (0x2401, 12, 18, 0), (0x1C01, 13, 20, 0), (0x1601, 29, 21, 0), (0x5601, 15, 14, 1),
        (0x5401, 16, 14, 0), (0x5101, 17, 15, 0), (0x4801, 18, 16, 0), (0x3801, 19, 17, 0), (0x3401, 20, 18, 0),
        (0x3001, 21, 19, 0), (0x2801, 22, 19, 0), (0x2401, 23, 20, 0), (0x2201, 24, 21, 0), (0x1C01, 25, 22, 0),
        (0x1801, 26, 23, 0), (0x1601, 27, 24, 0), (0x1401, 28, 25, 0), (0x1201, 29, 26, 0), (0x1101, 30, 27, 0),
        (0x0AC1, 31, 28, 0), (0x09C1, 32, 29, 0), (0x08A1, 33, 30, 0), (0x0521, 34, 31, 0), (0x0441, 35, 32, 0),
        (0x02A1, 36, 33, 0), (0x0221, 37, 34, 0), (0x0141, 38, 35, 0), (0x0111, 39, 36, 0), (0x0085, 40, 37, 0),
        (0x0049, 41, 38, 0), (0x0025, 42, 39, 0), (0x0015, 43, 40, 0), (0x0009, 44, 41, 0), (0x0005, 45, 42, 0),
        (0x0001, 45, 43, 0), (0x5601, 46, 46, 0)]

    def __init__(self):
        self.a = 0x8000
        self.c = 0
        self.ct = 12
        self.b = []          # the output bytes, so the carry can reach the one before
        self.first = True

    def _byteout(self):
        if self.b and self.b[-1] == 0xFF:
            self.b.append((self.c >> 20) & 0xFF)
            self.c &= 0xFFFFF
            self.ct = 7
            return
        if self.c > 0xFFFFFFF:
            if self.b:
                self.b[-1] = (self.b[-1] + 1) & 0xFF
            self.c &= 0xFFFFFFF
            if self.b and self.b[-1] == 0xFF:
                self.b.append((self.c >> 20) & 0xFF)
                self.c &= 0xFFFFF
                self.ct = 7
                return
        self.b.append((self.c >> 19) & 0xFF)
        self.c &= 0x7FFFF
        self.ct = 8

    def encode(self, cx, mps, ctx, d):
        i = cx[ctx]
        qe, nmps, nlps, switch = self.QE[i]
        if d == mps[ctx]:
            self.a -= qe
            if self.a & 0x8000:
                return
            if self.a < qe:
                self.a = qe
            else:
                self.c += qe
            cx[ctx] = nmps
        else:
            self.a -= qe
            if self.a < qe:
                self.c += qe
            else:
                self.a = qe
            if switch:
                mps[ctx] = 1 - mps[ctx]
            cx[ctx] = nlps
        while True:
            self.a = (self.a << 1) & 0xFFFF
            self.c <<= 1
            self.ct -= 1
            if self.ct == 0:
                self._byteout()
            if self.a & 0x8000:
                break

    def flush(self):
        temp = self.c + self.a
        self.c |= 0xFFFF
        if self.c >= temp:
            self.c -= 0x8000
        self.c <<= self.ct
        self._byteout()
        self.c <<= self.ct
        self._byteout()
        out = bytearray(self.b)
        out += b"\xFF\xAC"
        return bytes(out)


def _jbig2_generic(w, h, rows):
    """An embedded-stream JBIG2 immediate generic region, MMR off, template 0, TPGDON
    off. PDF's JBIG2Decode takes the segment stream without a JBIG2 file header."""
    enc = _MQEncoder()
    cx = [0] * (1 << 16)
    mps = [0] * (1 << 16)

    def pixel(x, y):
        if x < 0 or x >= w or y < 0:
            return 0
        return 1 if rows[y][x] else 0

    for y in range(h):
        for x in range(w):
            # The GB template 0 context, in the bit order the spec builds it.
            ctx = (pixel(x - 1, y) | pixel(x - 2, y) << 1 | pixel(x - 3, y) << 2 | pixel(x - 4, y) << 3 |
                   pixel(x + 2, y - 1) << 4 | pixel(x + 1, y - 1) << 5 | pixel(x, y - 1) << 6 |
                   pixel(x - 1, y - 1) << 7 | pixel(x - 2, y - 1) << 8 |
                   pixel(x + 1, y - 2) << 9 | pixel(x, y - 2) << 10 | pixel(x - 1, y - 2) << 11)
            enc.encode(cx, mps, ctx, 1 if rows[y][x] else 0)
    data = enc.flush()

    region = struct.pack(">IIIIB", w, h, 0, 0, 0)                        # region segment info
    region += bytes([0])                                                  # flags: MMR 0, template 0
    region += bytes([0x03, 0xFD, 0x02, 0xFE, 0x02, 0xFE, 0xF8, 0xFD])     # the template 0 AT pixels
    region += data
    # Segment header: number 0, type 38 (immediate generic region), page association 1.
    header = struct.pack(">I", 0) + bytes([38, 0x00, 1]) + struct.pack(">I", len(region))
    return header + region


FIXTURES = (
    ("text-run-boundary.pdf", gen_text_run_boundary),
    ("text-partial-run.pdf", gen_text_partial_run),
    ("text-rotated.pdf", gen_text_rotated),
    ("text-actualtext.pdf", gen_text_actualtext),
    ("text-doubled.pdf", gen_text_doubled),
    ("image-flate.pdf", gen_image_flate),
    ("image-jpeg.pdf", gen_image_jpeg),
    ("image-ccitt.pdf", gen_image_ccitt),
    ("image-jbig2.pdf", gen_image_jbig2),
    ("image-inline.pdf", gen_image_inline),
    ("image-shared.pdf", gen_image_shared),
    ("image-smask.pdf", gen_image_smask),
    ("form-xobject.pdf", gen_form_xobject),
    ("form-field.pdf", gen_form_field),
    ("link.pdf", gen_link),
    ("metadata.pdf", gen_metadata),
    ("vector-canary.pdf", gen_vector_canary),
    ("vector-straddle.pdf", gen_vector_straddle),
)


def main():
    outdir = sys.argv[1]
    os.makedirs(outdir, exist_ok=True)
    for name, gen in FIXTURES:
        data = gen()
        path = os.path.join(outdir, name)
        with open(path, "wb") as f:
            f.write(data)
        print("wrote %s (%d bytes)" % (path, len(data)))


if __name__ == "__main__":
    main()
