#!/usr/bin/env python3
"""Staging documents for the Microsoft Store screenshots.

Usage: python3 tools/screenshots-windows/gen_store_docs.py [outdir]


blank-agreement.pdf — the Equipment Rental Agreement from tools/gen_test_fixtures.py
    gen_demo(), but *unfilled*: empty checkboxes, empty signature line, and a
    misspelled customer name to fix on camera. Shots 1-3 build up on this one file.
scanned-receipt.pdf — the same page rendered as a 300 DPI "scan" (one fat JPEG), so
    Shrink for email has something to actually shrink; the corpus PDFs have no images.
"""
import io
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, os.path.join(REPO, "tools"))
from gen_test_fixtures import build, stream  # noqa: E402

from PIL import Image, ImageDraw, ImageFont  # noqa: E402

# Default alongside the shots themselves; both are build output, not fixtures.
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(REPO, "artifacts", "store", "screenshots")
os.makedirs(OUT, exist_ok=True)
ARIAL = "/mnt/c/Windows/Fonts/arial.ttf"
ARIALBD = "/mnt/c/Windows/Fonts/arialbd.ttf"

BODY = [
    ("b", 22, 716, "Equipment Rental Agreement"),
    ("r", 11, 688, "This agreement is made between Sunrise Tool Rental and the customer named"),
    ("r", 11, 672, "below, covering the rental equipment, delivery options, and insurance terms"),
    ("r", 11, 656, "described in sections 1 through 4 of this document."),
    ("b", 13, 620, "Customer"),
    ("r", 12, 596, "Name: Dana Whitfeld"),
    ("r", 12, 576, "Rental period: March 14 to March 18"),
    ("b", 13, 540, "Options"),
    ("b", 13, 420, "Equipment"),
    ("r", 11, 396, "1 x 6000 lb telescopic forklift, propane, with side shift"),
    ("r", 11, 380, "2 x scaffold tower sections, 6 ft, with guard rails"),
    ("r", 11, 364, "1 x trailer, 12 ft, tandem axle, ramps included"),
    ("b", 13, 330, "Terms"),
    ("r", 11, 306, "Equipment is rented as inspected and is returned in the same condition,"),
    ("r", 11, 290, "normal wear excepted. Fuel is billed at cost on return. Late returns are"),
    ("r", 11, 274, "charged at the daily rate. Damage insurance, where accepted above, caps"),
    ("r", 11, 258, "the customer's liability at $500 per incident."),
    ("b", 13, 220, "Customer signature"),
]
BOXES = [(508, "Include delivery and pickup"), (482, "Damage insurance accepted"),
         (456, "Extended weekend rate")]
SIG_LINE_Y = 150
def blank_agreement():
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]
    helv = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    bold = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>")
    ops = []
    for kind, size, y, text in BODY:
        f = b"/F2" if kind == "b" else b"/F1"
        ops.append(b"BT %s %d Tf 72 %d Td (%s) Tj ET" % (f, size, y, text.encode()))
    for y, label in BOXES:
        ops.append(b"1 w 0.13 0.13 0.13 RG 72 %d 13 13 re S" % y)
        ops.append(b"BT /F1 12 Tf 94 %d Td (%s) Tj ET" % (y + 3, label.encode()))
    ops.append(b"0.6 w 0.4 0.4 0.4 RG 72 %d m 320 %d l S" % (SIG_LINE_Y, SIG_LINE_Y))
    ops.append(b"BT /F1 9 Tf 72 %d Td (Sign above the line) Tj ET" % (SIG_LINE_Y - 12))
    ops.append(b"0.6 w 0.4 0.4 0.4 RG 380 %d m 520 %d l S" % (SIG_LINE_Y, SIG_LINE_Y))
    ops.append(b"BT /F1 9 Tf 380 %d Td (Date) Tj ET" % (SIG_LINE_Y - 12))
    content = add(stream(b"", b"\n".join(ops) + b"\n"))
    pages_num = len(objs) + 2
    page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
               b"/Resources << /Font << /F1 %d 0 R /F2 %d 0 R >> >> /Contents %d 0 R >>"
               % (pages_num, helv, bold, content))
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    assert pages == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages)
    return build(objs)


def scan_jpeg(dpi=400):
    """The same page, rendered as if it came off a flatbed: slight gray cast,
    paper speckle, a hair of skew. Big enough that Shrink has real work to do.

    The deliberate "Whitfeld" typo belongs only to the shot-1 edit story; the scan
    spells the name correctly so it can't be read as a typo in the Shrink shot."""
    import random
    random.seed(7)
    w, h = int(8.5 * dpi), int(11 * dpi)
    img = Image.new("RGB", (w, h), (252, 251, 247))
    d = ImageDraw.Draw(img)
    s = dpi / 72.0
    for kind, size, y, text in BODY:
        font = ImageFont.truetype(ARIALBD if kind == "b" else ARIAL, int(size * s))
        d.text((72 * s, (792 - y - size) * s), text.replace("Whitfeld", "Whitfield"),
               font=font, fill=(28, 28, 32))
    for y, label in BOXES:
        d.rectangle([72 * s, (792 - y - 13) * s, 85 * s, (792 - y) * s], outline=(30, 30, 34), width=max(1, int(s)))
        d.text((94 * s, (792 - y - 12) * s), label, font=ImageFont.truetype(ARIAL, int(12 * s)), fill=(28, 28, 32))
    for x0, x1, label in ((72, 320, "Sign above the line"), (380, 520, "Date")):
        d.line([x0 * s, (792 - SIG_LINE_Y) * s, x1 * s, (792 - SIG_LINE_Y) * s], fill=(90, 90, 95), width=max(1, int(s * 0.8)))
        d.text((x0 * s, (792 - SIG_LINE_Y + 12 - 9) * s), label, font=ImageFont.truetype(ARIAL, int(9 * s)), fill=(60, 60, 65))
    px = img.load()
    for _ in range(w * h // 120):                      # scanner speckle
        x, y = random.randrange(w), random.randrange(h)
        v = random.randrange(200, 245)
        px[x, y] = (v, v, v - 3)
    img = img.rotate(-0.35, resample=Image.BICUBIC, fillcolor=(252, 251, 247))
    buf = io.BytesIO()
    img.save(buf, "JPEG", quality=96, subsampling=0)
    return buf.getvalue(), img.size


def scanned_pdf():
    jpg, (iw, ih) = scan_jpeg()
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]
    im = add(b"<< /Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceRGB "
             b"/BitsPerComponent 8 /Filter /DCTDecode /Length %d >>\nstream\n" % (iw, ih, len(jpg))
             + jpg + b"\nendstream")
    content = add(stream(b"", b"q 612 0 0 792 0 0 cm /Im1 Do Q\n"))
    pages_num = len(objs) + 2
    page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
               b"/Resources << /XObject << /Im1 %d 0 R >> >> /Contents %d 0 R >>"
               % (pages_num, im, content))
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    assert pages == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages)
    return build(objs)


for name, data in (("blank-agreement.pdf", blank_agreement()), ("scanned-agreement.pdf", scanned_pdf())):
    p = os.path.join(OUT, name)
    open(p, "wb").write(data)
    print("wrote %s (%.2f MB)" % (name, len(data) / 1024 / 1024))
