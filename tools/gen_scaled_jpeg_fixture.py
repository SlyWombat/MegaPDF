#!/usr/bin/env python3
"""#152: a three-page PDF of JPEG images that a small render decodes at a reduced size (PDFium
patches 0021 and 0022).

    tools/gen_scaled_jpeg_fixture.py tests/MegaPDF.Core.Tests/Fixtures/scaled-jpeg.pdf <lossless.jpg>

Pillow cannot write lossless JPEG. The lossless image comes from libjpeg-turbo's
jpeg_enable_lossless() (any SOF3 grayscale JPEG of 661 x 855 with a dark bar and frame on mid gray
will do), so the output is committed.

PDFium sizes a reduced decode against the whole render bitmap, so each page is filled by one
661 x 855 px image, and the test renders a page at 80 x 103 px: eight times smaller, which asks for
a 1/4 decode. The pages:
  1. a progressive CMYK JPEG of odd size (Adobe inverted CMYK, /Decode [1 0 1 0 1 0 1 0]);
  2. a baseline RGB JPEG of odd size;
  3. the lossless grayscale JPEG. libjpeg decodes a lossless JPEG only at full size, so a reduced
     decode must fall back to full size instead of failing (0022; at patch 21 the page stayed blank).
Every image is mid gray with a dark bar and a dark frame: most of a render is neither white nor black.
"""
import io
import sys

from PIL import Image, ImageDraw

out_path, lossless_path = sys.argv[1], sys.argv[2]
W, H = 661, 855


def picture(mode):
    img = Image.new("L", (W, H), 128)
    d = ImageDraw.Draw(img)
    d.rectangle([0, H // 3, W - 1, H // 2], fill=20)
    d.rectangle([0, 0, W - 1, H - 1], outline=40, width=W // 20)
    return img.convert(mode)


def jpeg(mode, progressive):
    buf = io.BytesIO()
    picture(mode).save(buf, "JPEG", quality=90, progressive=progressive)
    return buf.getvalue()


images = [
    (jpeg("CMYK", True), b"/DeviceCMYK", b" /Decode [1 0 1 0 1 0 1 0]"),
    (jpeg("RGB", False), b"/DeviceRGB", b""),
    (open(lossless_path, "rb").read(), b"/DeviceGray", b""),
]
content = b"q 612 0 0 792 0 0 cm /Im1 Do Q"
count = len(images)
# Objects: 1 catalog, 2 pages, then per page: page, content, image.
kids = b" ".join(b"%d 0 R" % (3 + 3 * k) for k in range(count))
objs = [b"<< /Type /Catalog /Pages 2 0 R >>", b"<< /Type /Pages /Kids [" + kids + b"] /Count %d >>" % count]
for k, (data, cs, decode) in enumerate(images):
    page, stream, image = 3 + 3 * k, 4 + 3 * k, 5 + 3 * k
    objs.append(b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im1 %d 0 R >> >> /Contents %d 0 R >>"
                % (image, stream))
    objs.append(b"<< /Length %d >>\nstream\n" % len(content) + content + b"\nendstream")
    objs.append(b"<< /Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace %s /BitsPerComponent 8 /Filter /DCTDecode%s /Length %d >>\nstream\n"
                % (W, H, cs, decode, len(data)) + data + b"\nendstream")
pdf = b"%PDF-1.4\n"
offsets = []
for n, body in enumerate(objs, 1):
    offsets.append(len(pdf))
    pdf += b"%d 0 obj\n" % n + body + b"\nendobj\n"
xref = len(pdf)
pdf += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objs) + 1)
for off in offsets:
    pdf += b"%010d 00000 n \n" % off
pdf += b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objs) + 1, xref)
open(out_path, "wb").write(pdf)
print(out_path, len(pdf), "bytes")
