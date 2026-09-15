#!/usr/bin/env python3
"""Generate large synthetic test PDFs for the 2.0 QA pass: wide, deep and big.

Usage: python3 tools/gen_large_fixtures.py <out-dir> [--only name,...] [--skip-huge]

Writes (the outputs are not committed; several are hundreds of MB to GBs):

  Width
    wide-poster.pdf      one 14,400 x 1,200 pt page (the 200-inch PDF 1.x limit):
                         a century timeline of text, vector drawing and images.
    wide-userunit.pdf    a 10 m x 1 m banner: 14,173 x 1,417 units at /UserUnit 2.
    tall-receipt.pdf     216 x 14,400 pt: a 3-inch, 200-inch-long till receipt.
    mixed-sizes.pdf      40 pages: A4/Letter portrait and landscape, /Rotate
                         90/180/270, a 72 pt page, A0, off-origin MediaBoxes and
                         CropBoxes smaller than the MediaBox.
  Depth
    deep-2000.pdf        2,000 text-heavy pages, nested outline, page labels
                         (roman, decimal, "A-" appendix), a form block every 50 pages.
    deep-10000.pdf       10,000 light pages.
    many-fields.pdf      201 pages, 5,000 AcroForm fields (flat and hierarchical).
    many-objects.pdf     100,000 path objects on one page, 20,000 text objects on another.
  Size
    big-scan-250mb.pdf   100 pages of 300-600 dpi RGB "scans" with an invisible OCR layer.
    big-1gb.pdf          400 report pages, each with a large figure image.
    huge-2_5gb.pdf       1,000 report pages, past the 2 GB / int32 offset line.
                         Skipped with --skip-huge.
    huge-image-page.pdf  one page holding one 20,000 x 15,000 px image.
  Combined
    wide-deep.pdf        500 engineering drawings at 48 x 36 in.

Every file opens with a block that says what it is, and carries body text in the
standard 14 fonts (search and text edits), AcroForm text fields and check boxes,
printed check boxes and a "Sign above the line" line. The search canary word
"lighthouse" appears a known number of times, stated on the first page.

Output is streamed: images and content are compressed band by band and written as
they are made, so the multi-GB files need tens of MB of RAM. Stdlib only. Output
is deterministic for a given zlib build (image sizes are steered by measured
compressed size, so another zlib version can shift them slightly).
"""
import hashlib
import math
import os
import random
import sys
import time
import zlib

CANARY = b"lighthouse"
MB = 1024 * 1024

# --------------------------------------------------------------------------------------
# Streaming writer
# --------------------------------------------------------------------------------------


class PdfWriter:
    """Writes objects straight to disk, remembering offsets for a classic xref table."""

    def __init__(self, path, version=b"1.7"):
        self.path = path
        self.f = open(path, "wb", buffering=16 * MB)
        self.pos = 0
        self.offsets = [None]
        self.canary = 0
        self._write(b"%PDF-" + version + b"\n%\xe2\xe3\xcf\xd3\n")

    def _write(self, data):
        self.f.write(data)
        self.pos += len(data)

    def alloc(self):
        self.offsets.append(None)
        return len(self.offsets) - 1

    def _begin(self, num):
        if num is None:
            num = self.alloc()
        assert self.offsets[num] is None, "object %d written twice" % num
        self.offsets[num] = self.pos
        self._write(b"%d 0 obj\n" % num)
        return num

    def obj(self, body, num=None):
        num = self._begin(num)
        self._write(body)
        self._write(b"\nendobj\n")
        return num

    def stream(self, extra, data, num=None, compress=True):
        if compress and len(data) > 200:
            data = zlib.compress(data, 6)
            extra += b" /Filter /FlateDecode"
        num = self._begin(num)
        self._write(b"<< %s /Length %d >>\nstream\n" % (extra, len(data)))
        self._write(data)
        self._write(b"\nendstream\nendobj\n")
        return num

    def content(self, data, num=None):
        """A page or form content stream; counts the search canary before compressing."""
        self.canary += data.lower().count(CANARY)  # search is case-insensitive
        return self.stream(b"", data, num)

    def open_stream(self, extra, num=None, level=1):
        num = self._begin(num)
        length = self.alloc()
        self._write(b"<< %s /Filter /FlateDecode /Length %d 0 R >>\nstream\n" % (extra, length))
        return FlateSink(self, length, level)

    def finish(self, root, info):
        missing = [i for i, off in enumerate(self.offsets) if i and off is None]
        assert not missing, "objects allocated but never written: %r" % missing[:10]
        xref = self.pos
        n = len(self.offsets)
        self._write(b"xref\n0 %d\n0000000000 65535 f \n" % n)
        chunk = []
        for off in self.offsets[1:]:
            chunk.append(b"%010d 00000 n \n" % off)
            if len(chunk) == 10000:
                self._write(b"".join(chunk))
                chunk = []
        self._write(b"".join(chunk))
        ident = hashlib.md5(os.path.basename(self.path).encode()).hexdigest().encode()
        self._write(b"trailer\n<< /Size %d /Root %d 0 R /Info %d 0 R /ID [<%s> <%s>] >>\n"
                    b"startxref\n%d\n%%%%EOF\n" % (n, root, info, ident, ident, xref))
        self.f.close()
        return self.pos


class FlateSink:
    def __init__(self, writer, length_num, level):
        self.w = writer
        self.length_num = length_num
        self.comp = zlib.compressobj(level)
        self.n = 0

    def write(self, raw):
        c = self.comp.compress(raw)
        if c:
            self.w._write(c)
            self.n += len(c)
        return len(c)

    def close(self):
        c = self.comp.flush()
        self.w._write(c)
        self.n += len(c)
        self.w._write(b"\nendstream\nendobj\n")
        self.w.obj(b"%d" % self.n, num=self.length_num)
        return self.n


# --------------------------------------------------------------------------------------
# Text: WinAnsi strings, Helvetica metrics, deterministic prose
# --------------------------------------------------------------------------------------

def pdfstr(text):
    raw = text.encode("cp1252")
    return raw.replace(b"\\", b"\\\\").replace(b"(", b"\\(").replace(b")", b"\\)")


_HELV = dict(zip(" !\"#$%&'()*+,-./0123456789:;<=>?@", [278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278] + [556] * 10 + [278, 278, 584, 584, 584, 556, 1015]))
_HELV.update(zip("ABCDEFGHIJKLMNOPQRSTUVWXYZ", [667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611]))
_HELV.update(zip("abcdefghijklmnopqrstuvwxyz", [556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500]))


def text_width(text, size, font=b"F1"):
    if font == b"F4":  # Courier
        return len(text) * 0.6 * size
    w = sum(_HELV.get(c, 556) for c in text) * size / 1000.0
    return w * (1.06 if font == b"F2" else 1.0)


WORDS = ("the of and to in a is that for it as with be by on not this are or from at which "
         "an have all can will one each other their there when use more these some into time "
         "system report measure value water signal record sample survey result method review "
         "valve panel pressure flow station north river bridge harbour meadow copper timber "
         "orchard granite ledger invoice freight route schedule quarter annual regional survey "
         "inspection clause section schedule appendix figure table index model series battery "
         "circuit sensor module average margin balance capacity density voltage traffic "
         "rainfall summer winter autumn spring morning evening careful steady gentle bright "
         "quiet simple formal public local final early later second third primary general "
         "noted listed shown given built placed checked tested signed approved returned "
         "measured recorded filed updated opened closed moved kept held made found left").split()


class Prose:
    def __init__(self, seed, canary_rate=0.0):
        self.rng = random.Random(seed)
        self.canary_rate = canary_rate

    def sentence(self):
        n = self.rng.randint(7, 17)
        words = [self.rng.choice(WORDS) for _ in range(n)]
        if self.canary_rate and self.rng.random() < self.canary_rate:
            words[self.rng.randrange(n)] = "lighthouse"
        if self.rng.random() < 0.15:
            words.insert(self.rng.randrange(n), str(self.rng.randint(2, 9800)))
        words[0] = words[0].capitalize()
        return " ".join(words) + self.rng.choice([".", ".", ".", ";", ","])

    def paragraph(self, sentences=None):
        return " ".join(self.sentence() for _ in range(sentences or self.rng.randint(2, 6)))

    def words(self, n):
        return " ".join(self.rng.choice(WORDS) for _ in range(n))


def wrap(text, width, size, font=b"F1"):
    lines, cur, cur_w = [], [], 0.0
    space = text_width(" ", size, font)
    for word in text.split():
        ww = text_width(word, size, font)
        if cur and cur_w + space + ww > width:
            lines.append(" ".join(cur))
            cur, cur_w = [word], ww
        else:
            cur_w += (space if cur else 0) + ww
            cur.append(word)
    if cur:
        lines.append(" ".join(cur))
    return lines


def text_block(x, y, size, leading, lines, font=b"F1", color=b"0 g"):
    """One BT..ET with a Tj per line. Returns the bytes."""
    if not lines:
        return b""
    out = [b"BT %s /%s %g Tf %g TL %.2f %.2f Td (%s) Tj" % (color, font, size, leading, x, y, pdfstr(lines[0]))]
    out += [b"T* (%s) Tj" % pdfstr(line) for line in lines[1:]]
    out.append(b"ET\n")
    return b" ".join(out)


def text_line(x, y, size, text, font=b"F1", color=b"0 g"):
    return b"BT %s /%s %g Tf %.2f %.2f Td (%s) Tj ET\n" % (color, font, size, x, y, pdfstr(text))


# --------------------------------------------------------------------------------------
# Document: fonts, page tree, forms, outline, page labels, info block
# --------------------------------------------------------------------------------------

FONT_NAMES = [(b"F1", b"Helvetica"), (b"F2", b"Helvetica-Bold"), (b"F3", b"Times-Roman"),
              (b"F4", b"Courier"), (b"F5", b"Times-Bold")]


class Doc:
    LEAF = 32

    def __init__(self, path, title, version=b"1.7"):
        self.w = PdfWriter(path, version)
        self.title = title
        self.fonts = {}
        for key, base in FONT_NAMES:
            self.fonts[key] = self.w.obj(b"<< /Type /Font /Subtype /Type1 /BaseFont /%s /Encoding /WinAnsiEncoding >>" % base)
        self.zadb = self.w.obj(b"<< /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats >>")
        self.font_res = b"/Font << " + b" ".join(b"/%s %d 0 R" % (k, v) for k, v in self.fonts.items()) + b" >>"
        self.leaves = []          # [leaf_num, [page nums]]
        self.fields = []          # top-level AcroForm fields
        self.outline = []         # (title, page_num, children)
        self.page_labels = None
        self.catalog_extra = b""
        self.info_block = None    # (stream num, x, top, width, heading, lines)
        self.field_count = 0
        self.images = {}

    # ---- pages
    def new_page(self):
        if not self.leaves or len(self.leaves[-1][1]) == self.LEAF:
            self.leaves.append([self.w.alloc(), []])
        num = self.w.alloc()
        self.leaves[-1][1].append(num)
        return num, self.leaves[-1][0]

    @property
    def page_count(self):
        return sum(len(k) for _, k in self.leaves)

    def write_page(self, num, parent, media, contents, xobjects=None, annots=None, extra=b""):
        res = self.font_res
        if xobjects:
            res += b" /XObject << " + b" ".join(b"/%s %d 0 R" % (k, v) for k, v in xobjects.items()) + b" >>"
        if isinstance(contents, int):
            contents = [contents]
        body = (b"<< /Type /Page /Parent %d 0 R /MediaBox [%s] /Resources << %s >> /Contents [%s]"
                % (parent, b" ".join(b"%g" % v for v in media), res, b" ".join(b"%d 0 R" % c for c in contents)))
        if annots:
            body += b" /Annots [%s]" % b" ".join(b"%d 0 R" % a for a in annots)
        self.w.obj(body + extra + b" >>", num=num)

    def page(self, media, content, xobjects=None, annots_fn=None, extra=b""):
        """Convenience: allocate, add annotations (annots_fn(page_num) -> [nums]), write."""
        num, parent = self.new_page()
        annots = annots_fn(num) if annots_fn else None
        c = self.w.content(content)
        self.write_page(num, parent, media, c, xobjects, annots, extra)
        return num

    def _write_tree(self):
        level = [(num, kids, len(kids)) for num, kids in self.leaves]
        parent_of = {}
        written = []
        while len(level) > 1:
            nxt = []
            for i in range(0, len(level), self.LEAF):
                group = level[i:i + self.LEAF]
                num = self.w.alloc()
                for child in group:
                    parent_of[child[0]] = num
                nxt.append((num, [g[0] for g in group], sum(g[2] for g in group)))
            written.extend(level)
            level = nxt
        written.extend(level)
        for num, kids, count in written:
            parent = b" /Parent %d 0 R" % parent_of[num] if num in parent_of else b""
            self.w.obj(b"<< /Type /Pages%s /Kids [%s] /Count %d >>"
                       % (parent, b" ".join(b"%d 0 R" % k for k in kids), count), num=num)
        return level[0][0]

    # ---- forms
    def text_field(self, page, name, rect, value=None, multiline=False, parent=None, size=10):
        x0, y0, x1, y1 = rect
        w, h = x1 - x0, y1 - y0
        ap = b"/Tx BMC q 0.96 0.97 1 rg 0 0 %.2f %.2f re f 0.45 0.5 0.6 RG 0.8 w 0.4 0.4 %.2f %.2f re S " % (w, h, w - 0.8, h - 0.8)
        if value:
            base = h - size - 2 if multiline else (h - size * 0.7) / 2
            ap += b"q 1 1 %.2f %.2f re W n BT /Helv %g Tf 0 g 3 %.2f Td (%s) Tj ET Q " % (w - 2, h - 2, size, base, pdfstr(value))
        ap += b"Q EMC"
        apn = self.w.stream(b"/Type /XObject /Subtype /Form /BBox [0 0 %.2f %.2f] /Resources << /Font << /Helv %d 0 R >> >>"
                            % (w, h, self.fonts[b"F1"]), ap)
        body = (b"<< /Type /Annot /Subtype /Widget /FT /Tx /T (%s) /DA (/Helv %g Tf 0 g) /Rect [%.2f %.2f %.2f %.2f] "
                b"/F 4 /P %d 0 R /MK << /BC [0.45 0.5 0.6] /BG [0.96 0.97 1] >> /AP << /N %d 0 R >>"
                % (pdfstr(name), size, x0, y0, x1, y1, page, apn))
        if value:
            body += b" /V (%s)" % pdfstr(value)
        if multiline:
            body += b" /Ff 4096"
        if parent:
            body += b" /Parent %d 0 R" % parent
        num = self.w.obj(body + b" >>")
        if not parent:
            self.fields.append(num)
        self.field_count += 1
        return num

    def check_box(self, page, name, rect, checked=False, parent=None):
        x0, y0, x1, y1 = rect
        s = x1 - x0
        border = b"0.96 0.97 1 rg 0 0 %.2f %.2f re f 0.2 0.2 0.25 RG 1 w 0.5 0.5 %.2f %.2f re S" % (s, s, s - 1, s - 1)
        inset = s * 0.22
        on = self.w.stream(b"/Type /XObject /Subtype /Form /BBox [0 0 %.2f %.2f]" % (s, s),
                           border + b" 0.1 0.1 0.1 RG %.2f w %.2f %.2f m %.2f %.2f l S %.2f %.2f m %.2f %.2f l S"
                           % (max(1.2, s * 0.1), inset, inset, s - inset, s - inset, inset, s - inset, s - inset, inset))
        off = self.w.stream(b"/Type /XObject /Subtype /Form /BBox [0 0 %.2f %.2f]" % (s, s), border)
        state = b"/Yes" if checked else b"/Off"
        body = (b"<< /Type /Annot /Subtype /Widget /FT /Btn /T (%s) /V %s /AS %s /DA (/ZaDb 0 Tf 0 g) "
                b"/Rect [%.2f %.2f %.2f %.2f] /F 4 /P %d 0 R /MK << /CA (8) /BC [0.2 0.2 0.25] /BG [0.96 0.97 1] >> "
                b"/AP << /N << /Yes %d 0 R /Off %d 0 R >> >>"
                % (pdfstr(name), state, state, x0, y0, x1, y1, page, on, off))
        if parent:
            body += b" /Parent %d 0 R" % parent
        num = self.w.obj(body + b" >>")
        if not parent:
            self.fields.append(num)
        self.field_count += 1
        return num

    # ---- images
    def pattern_image(self, key, w, h, seed):
        """A small procedural RGB image (plasma plus rings), shared by key."""
        if key in self.images:
            return self.images[key]
        rng = random.Random(seed)
        fx, fy, fr = rng.uniform(3, 9), rng.uniform(2, 7), rng.uniform(10, 30)
        rows = []
        for y in range(h):
            row = bytearray(w * 3)
            for x in range(w):
                u, v = x / w, y / h
                a = math.sin(u * fx + v * 2.1) + math.cos(v * fy - u * 1.3)
                d = math.hypot(u - 0.5, v - 0.5)
                row[x * 3] = int(128 + 60 * a) & 255
                row[x * 3 + 1] = int(128 + 90 * math.sin(d * fr)) & 255
                row[x * 3 + 2] = int(160 + 50 * math.cos(a + d * 6)) & 255
            rows.append(bytes(row))
        num = self.w.stream(b"/Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceRGB /BitsPerComponent 8" % (w, h),
                            b"".join(rows))
        self.images[key] = num
        return num

    # ---- the "what is this file" block, rendered at finish with the real numbers
    def reserve_info(self, x, top, width, heading, purpose, size=11):
        num = self.w.alloc()
        self.info_block = (num, x, top, width, heading, purpose, size)
        return num

    def _write_info(self):
        num, x, top, width, heading, purpose, size = self.info_block
        lead = size * 1.35
        out = [text_line(x, top - size * 2, size * 2, heading, b"F2")]
        y = top - size * 2 - lead * 1.6
        paras = list(purpose) + [
            "Pages: %d. AcroForm fields: %d. Approximate size when this line was written: %.1f MB."
            % (self.page_count, self.field_count, self.w.pos / MB),
            "Search canary: the word lighthouse appears %d times in this file, including this line."
            % (self.w.canary + 1),
            "Generated by tools/gen_large_fixtures.py in the MegaPDF repository. Synthetic content only; "
            "the words are random and mean nothing.",
        ]
        for p in paras:
            lines = wrap(p, width, size)
            out.append(text_block(x, y, size, lead, lines))
            y -= lead * (len(lines) + 0.6)
        self.w.content(b"".join(out), num=num)

    # ---- finish
    def _write_outline(self):
        if not self.outline:
            return None
        root = self.w.alloc()

        def write_level(items, parent):
            nums = [self.w.alloc() for _ in items]
            total = 0
            for i, (title, page, children) in enumerate(items):
                body = b"<< /Title (%s) /Parent %d 0 R /Dest [%d 0 R /XYZ null null null]" % (pdfstr(title), parent, page)
                if i > 0:
                    body += b" /Prev %d 0 R" % nums[i - 1]
                if i < len(items) - 1:
                    body += b" /Next %d 0 R" % nums[i + 1]
                if children:
                    first, last, count = write_level(children, nums[i])
                    body += b" /First %d 0 R /Last %d 0 R /Count -%d" % (first, last, count)
                self.w.obj(body + b" >>", num=nums[i])
                total += 1
            return nums[0], nums[-1], total

        first, last, count = write_level(self.outline, root)
        self.w.obj(b"<< /Type /Outlines /First %d 0 R /Last %d 0 R /Count %d >>" % (first, last, count), num=root)
        return root

    def finish(self):
        if self.info_block:
            self._write_info()
        pages = self._write_tree()
        outline = self._write_outline()
        cat = b"<< /Type /Catalog /Pages %d 0 R /Lang (en-US)" % pages
        if outline:
            cat += b" /Outlines %d 0 R /PageMode /UseOutlines" % outline
        if self.page_labels:
            cat += b" /PageLabels << /Nums [%s] >>" % self.page_labels
        if self.fields:
            fields = b" ".join(b"%d 0 R" % f for f in self.fields)
            cat += (b" /AcroForm << /Fields [%s] /DA (/Helv 10 Tf 0 g) /DR << /Font << /Helv %d 0 R /ZaDb %d 0 R >> >> >>"
                    % (fields, self.fonts[b"F1"], self.zadb))
        root = self.w.obj(cat + self.catalog_extra + b" >>")
        info = self.w.obj(b"<< /Title (%s) /Producer (MegaPDF tools/gen_large_fixtures.py) /Creator (MegaPDF synthetic fixtures) >>"
                          % pdfstr(self.title))
        return self.w.finish(root, info)


# --------------------------------------------------------------------------------------
# Drawing helpers (return content bytes)
# --------------------------------------------------------------------------------------

def circle(cx, cy, r):
    k = 0.5523 * r
    return (b"%.2f %.2f m %.2f %.2f %.2f %.2f %.2f %.2f c %.2f %.2f %.2f %.2f %.2f %.2f c "
            b"%.2f %.2f %.2f %.2f %.2f %.2f c %.2f %.2f %.2f %.2f %.2f %.2f c h"
            % (cx + r, cy, cx + r, cy + k, cx + k, cy + r, cx, cy + r,
               cx - k, cy + r, cx - r, cy + k, cx - r, cy,
               cx - r, cy - k, cx - k, cy - r, cx, cy - r,
               cx + k, cy - r, cx + r, cy - k, cx + r, cy))


def printed_checkboxes(x, y, labels, size=11, gap=20):
    """Drawn (not AcroForm) squares with labels: detect-checkbox candidates."""
    out = [b"q 0.13 0.13 0.13 RG 1 w"]
    for i, label in enumerate(labels):
        yy = y - i * gap
        out.append(b"%.2f %.2f %.2f %.2f re S" % (x, yy, size, size))
    out.append(b"Q\n")
    for i, label in enumerate(labels):
        out.append(text_line(x + size + 8, y - i * gap + 2, 10, label))
    return b" ".join(out)


def sign_line(x, y, length=240, label="Sign above the line"):
    return (b"q 0.6 w 0.35 0.35 0.35 RG %.2f %.2f m %.2f %.2f l S Q\n" % (x, y, x + length, y)
            + text_line(x, y - 12, 9, label, color=b"0.3 g"))


def bar_chart(x, y, w, h, rng, bars=12):
    out = [b"q 0.4 w 0.2 g 0.2 G %.2f %.2f m %.2f %.2f l %.2f %.2f l S" % (x, y + h, x, y, x + w, y)]
    bw = w / bars
    for i in range(bars):
        v = rng.uniform(0.1, 0.95)
        out.append(b"%.3f %.3f %.3f rg %.2f %.2f %.2f %.2f re f"
                   % (0.2 + 0.05 * (i % 4), 0.4 + 0.04 * (i % 7), 0.7 - 0.03 * (i % 5), x + i * bw + bw * 0.15, y, bw * 0.7, h * v))
    pts = [(x + i * w / 24, y + h * (0.5 + 0.35 * math.sin(i / 3.0 + rng.random()))) for i in range(25)]
    out.append(b"0.8 0.2 0.1 RG 1.2 w %.2f %.2f m" % pts[0])
    for i in range(1, 25):
        (ax, ay), (bx, by) = pts[i - 1], pts[i]
        out.append(b"%.2f %.2f %.2f %.2f %.2f %.2f c" % (ax + (bx - ax) / 3, ay, bx - (bx - ax) / 3, by, bx, by))
    out.append(b"S Q\n")
    return b" ".join(out)


# --------------------------------------------------------------------------------------
# Procedural raster images, streamed band by band
# --------------------------------------------------------------------------------------
# A grey level is built per pixel from a class byte (top 3 bits, the structure: paper,
# ink, photo tones) OR'd with 5 bits of hash noise, mapped through a 256-entry table.
# Everything happens in C (hashlib, bytes.translate, big-int OR, bytearray slices), so
# a 600 dpi page takes about a second rather than an hour of per-pixel Python.

_LOW5 = bytes(v & 31 for v in range(256))


def _noise(seed, n):
    return hashlib.shake_256(seed).digest(n).translate(_LOW5)


def _or_bytes(a, b):
    n = len(a)
    return (int.from_bytes(a, "big") | int.from_bytes(b, "big")).to_bytes(n, "big")


def _grey_lut(tone_shift=0, speckle=True, photo_amp=3):
    lut = bytearray(256)
    for v in range(256):
        cls, n = v >> 5, v & 31
        if cls == 0:      # paper, rare speckle
            lut[v] = 246 if (not speckle or n < 31) else 215
        elif cls == 1:    # ink, mostly solid so text lines stay cheap
            lut[v] = 38 if n < 28 else 60 + n * 3
        else:             # photo tones 2..7, noisy
            base = 45 + (cls - 2) * 36 + tone_shift
            lut[v] = max(0, min(255, base + (n - 16) * photo_amp))
    return bytes(lut)


def _rgb(grey, red_lut, blue_lut):
    out = bytearray(len(grey) * 3)
    out[0::3] = grey.translate(red_lut)
    out[1::3] = grey
    out[2::3] = grey.translate(blue_lut)
    return out


_WARM_R = bytes(min(255, v + 4) for v in range(256))
_WARM_B = bytes(max(0, v - 10) for v in range(256))


class ImageBands:
    """Feeds rows to a FlateSink; tracks compressed bytes spent on photo vs other rows."""

    def __init__(self, sink, width, seed, lut, red=_WARM_R, blue=_WARM_B):
        self.sink, self.width, self.seed, self.lut = sink, width, seed, lut
        self.red, self.blue = red, blue
        self.band = 0
        self.photo_bytes = self.photo_px = self.other_bytes = self.other_px = 0

    def rows(self, structure, count, photo=False):
        """`count` rows sharing one class row `structure` (bytes of length width)."""
        while count > 0:
            k = min(count, max(1, (4 * MB) // self.width))
            self.band += 1
            noise = _noise(b"%s/%d" % (self.seed, self.band), self.width * k)
            grey = _or_bytes(structure * k, noise).translate(self.lut)
            emitted = self.sink.write(bytes(_rgb(grey, self.red, self.blue)))
            if photo:
                self.photo_bytes += emitted
                self.photo_px += self.width * k
            else:
                self.other_bytes += emitted
                self.other_px += self.width * k
            count -= k


def photo_structure(width, band, x0, x1, margin_cls=0):
    """Class row for a photo band: a stepped tone gradient between x0 and x1."""
    row = bytearray([margin_cls << 5]) * width
    span = max(1, x1 - x0)
    step = span // 6 + 1
    for i in range(6):
        cls = 2 + (i + band) % 6
        a = x0 + i * step
        b = min(x1, a + step)
        if a < b:
            row[a:b] = bytes([cls << 5]) * (b - a)
    return bytes(row)


class SizeSteer:
    """Picks how much of each page is 'photo' so the file lands near a byte target."""

    def __init__(self, target, pages):
        self.target, self.pages = target, pages
        self.photo_cost = 1.6   # compressed bytes per photo pixel (RGB), refined as we go
        self.other_cost = 0.02

    def learn(self, bands):
        if bands.photo_px > 10000:
            self.photo_cost = bands.photo_bytes / bands.photo_px
        if bands.other_px > 10000:
            self.other_cost = bands.other_bytes / bands.other_px

    def fraction(self, written, done, page_px, photo_px_max):
        remaining = max(1, self.pages - done)
        budget = (self.target - written) / remaining
        need = budget - self.other_cost * page_px
        return max(0.0, min(1.0, need / max(1.0, (self.photo_cost - self.other_cost) * photo_px_max)))


# --------------------------------------------------------------------------------------
# Generators
# --------------------------------------------------------------------------------------

LETTER = (612, 792)
A4 = (595.28, 841.89)


def info_page_fields(doc, page, x, y):
    """The standard QA block: a text field, a check box, printed boxes and a sign line."""
    annots = [doc.text_field(page, "qa_tester", (x + 90, y - 4, x + 330, y + 16)),
              doc.check_box(page, "qa_opened", (x, y - 36, x + 14, y - 22))]
    content = (text_line(x, y + 2, 11, "Tester name:")
               + text_line(x + 22, y - 33, 11, "Opened and scrolled on this platform (AcroForm check box)")
               + printed_checkboxes(x, y - 70, ["Search found the canary", "Edited a line of body text", "Saved and reopened"])
               + sign_line(x, y - 170))
    return annots, content


def gen_wide_poster(path):
    W, H = 14400, 1200
    doc = Doc(path, "Wide poster: a 200-inch timeline")
    prose = Prose(101, canary_rate=0.03)
    num, parent = doc.new_page()
    info = doc.reserve_info(60, H - 60, 720, "wide-poster.pdf", [
        "One 14,400 x 1,200 pt page (200 x 16.7 in), the largest page PDF 1.x allows without /UserUnit. "
        "A century timeline runs left to right: era bands, a year axis, event cards, trend curves and images.",
        "Stresses: fit-to-width zoom far below 10%, the render clamp at high zoom, horizontal scrolling, "
        "search hits thousands of points apart, and form fields and a signature line far from the origin.",
    ])
    rng = random.Random(7)
    c = [b"q 0.97 0.97 0.95 rg 0 0 %d %d re f Q\n" % (W, H)]
    # Era bands and the axis.
    for i in range(10):
        c.append(b"q %.3f %.3f %.3f rg %d 480 1344 120 re f Q\n" % (0.75 + 0.02 * (i % 3), 0.82 - 0.02 * (i % 4), 0.9 - 0.03 * (i % 2), 900 + i * 1344))
        c.append(text_line(920 + i * 1344, 530, 28, "Era %d: %d to %d" % (i + 1, 1900 + i * 10, 1909 + i * 10), b"F2", b"0.25 g"))
    c.append(b"q 2 w 0.1 G 900 470 m 14340 470 l S Q\n")
    for year in range(1900, 2001):
        x = 900 + (year - 1900) * 134.4
        tall = 30 if year % 10 == 0 else 12
        c.append(b"q 0.8 w 0.1 G %.1f 470 m %.1f %d l S Q\n" % (x, x, 470 - tall))
        if year % 5 == 0:
            c.append(text_line(x - 14, 420, 14 if year % 10 else 20, str(year), b"F2" if year % 10 == 0 else b"F1"))
    # Event cards above and below the axis.
    for i in range(86):
        x = 920 + i * 140
        above = i % 2 == 0
        y0 = 640 + (i % 4) * 70 if above else 60 + (i % 3) * 40
        c.append(b"q 0.5 w 0.3 G %.1f %d m %.1f %d l S Q\n" % (x + 60, 470 if above else 400, x + 60, y0 if above else y0 + 250))
        c.append(b"q 1 1 1 rg 0.6 G 0.8 w %.1f %d 128 250 re B Q\n" % (x - 4, y0))
        c.append(text_line(x, y0 + 232, 10, "Event %d, %d" % (i + 1, 1900 + int(i * 1.05)), b"F2"))
        c.append(text_block(x, y0 + 216, 7.5, 9.5, wrap(prose.paragraph(3), 118, 7.5)[:22]))
    # Trend curves across the whole poster.
    for k, colour in enumerate([b"0.8 0.2 0.1", b"0.1 0.4 0.75", b"0.2 0.6 0.3"]):
        pts = [(900 + i * 134.4, 1000 + 70 * math.sin(i / (6.0 + k * 2)) + 40 * math.sin(i / 2.3 + k)) for i in range(101)]
        seg = [b"q %s RG 3 w %.1f %.1f m" % (colour, pts[0][0], pts[0][1] - k * 60)]
        for i in range(1, 101):
            (ax, ay), (bx, by) = pts[i - 1], pts[i]
            seg.append(b"%.1f %.1f %.1f %.1f %.1f %.1f c" % (ax + 45, ay - k * 60, bx - 45, by - k * 60, bx, by - k * 60))
        c.append(b" ".join(seg) + b" S Q\n")
    # Images placed along the top.
    xobj = {}
    for i in range(6):
        key = b"Im%d" % i
        xobj[key] = doc.pattern_image(key, 240, 160, 300 + i)
        c.append(b"q 360 0 0 240 %d 900 cm /%s Do Q\n" % (1800 + i * 2100, key))
    # Review row: fields and printed boxes near the right end; sign line at the far right.
    rx = 13000
    c.append(text_line(rx, 380, 14, "Review row (AcroForm fields far from the origin)", b"F2"))
    annots = []
    for i in range(5):
        annots.append(doc.text_field(num, "review_note%d" % (i + 1), (rx + i * 260, 330, rx + i * 260 + 230, 352),
                                     value="Note %d" % (i + 1) if i % 2 else None))
        annots.append(doc.check_box(num, "review_ok%d" % (i + 1), (rx + i * 260, 300, rx + i * 260 + 14, 314), checked=i == 2))
        c.append(text_line(rx + i * 260 + 22, 303, 10, "Section %d reviewed" % (i + 1)))
    c.append(printed_checkboxes(rx, 260, ["Colours checked", "Dates checked", "Spelling checked"]))
    c.append(sign_line(14000, 200, 300))
    fa, fc = info_page_fields(doc, num, 60, 520)
    main = doc.w.content(b"".join(c) + fc)
    doc.write_page(num, parent, (0, 0, W, H), [main, info], xobj, annots + fa)
    return doc


def gen_wide_userunit(path):
    unit = 2.0
    W, H = 10000 / 25.4 * 72 / unit, 1000 / 25.4 * 72 / unit  # 10 m x 1 m in 2-pt units
    doc = Doc(path, "Wide banner: 10 m x 1 m with UserUnit", b"1.7")
    prose = Prose(202, canary_rate=0.05)
    num, parent = doc.new_page()
    info = doc.reserve_info(40, H - 40, 520, "wide-userunit.pdf", [
        "One page, 10 m x 1 m. The MediaBox is %.1f x %.1f units with /UserUnit 2, so each unit is 2 pt; "
        "a viewer that ignores UserUnit shows it at half size (5 m x 0.5 m). A metre ruler with 10 cm ticks runs along the bottom; "
        "the label under each metre mark says where it should be." % (W, H),
        "Stresses: UserUnit handling in page size, zoom and hit testing (fields and the signature line are in units, "
        "not points), and a page beyond the 200-inch limit.",
    ], size=9)
    per_m = W / 10
    c = [b"q 0.99 0.98 0.93 rg 0 0 %.2f %.2f re f Q\n" % (W, H)]
    c.append(b"q 1.5 w 0.1 G 0 60 m %.2f 60 l S Q\n" % W)
    for cm in range(0, 1001):
        x = cm * per_m / 100
        tick = 40 if cm % 100 == 0 else (20 if cm % 10 == 0 else 6)
        if cm % 2 == 0 or tick > 6:
            c.append(b"q 0.5 w 0.1 G %.2f 60 m %.2f %d l S Q\n" % (x, x, 60 + tick))
        if cm % 100 == 0:
            c.append(text_line(x + 3, 20, 16, "%d.00 m" % (cm // 100), b"F2"))
        elif cm % 10 == 0:
            c.append(text_line(x + 2, 104 if cm % 20 else 88, 5, "%.1f" % (cm / 100)))
    for m in range(1, 10):
        x = m * per_m
        c.append(text_line(x + 20, H - 60, 30, "Metre %d" % m, b"F5"))
        c.append(text_block(x + 20, H - 90, 9, 12, wrap(prose.paragraph(6), per_m - 60, 9)[:14], b"F3"))
        c.append(b"q 0.2 0.35 0.6 RG 2 w %s S Q\n" % circle(x + per_m / 2, 250, 60 + 10 * (m % 3)))
        c.append(b"q 0.85 0.4 0.2 rg %.2f 140 %.2f 30 re f Q\n" % (x + 20, per_m * 0.8 * (m / 10)))
    annots, fc = info_page_fields(doc, num, 40, 600)
    annots.append(doc.text_field(num, "banner_far_right", (W - 400, 200, W - 150, 220), value="9.9 m mark"))
    annots.append(doc.check_box(num, "banner_far_right_ok", (W - 400, 170, W - 386, 184)))
    c.append(text_line(W - 380, 173, 9, "Checked at the far end"))
    c.append(sign_line(W - 400, 130, 250))
    main = doc.w.content(b"".join(c) + fc)
    doc.write_page(num, parent, (0, 0, round(W, 2), round(H, 2)), [main, info], None, annots, b" /UserUnit 2")
    return doc


def gen_tall_receipt(path):
    W, H = 216, 14400
    doc = Doc(path, "Tall receipt: 3 x 200 inches")
    prose = Prose(303, canary_rate=0.0)
    rng = random.Random(33)
    num, parent = doc.new_page()
    info = doc.reserve_info(12, H - 10, 192, "tall-receipt.pdf", [
        "One 216 x 14,400 pt page (3 x 200 in): a till receipt with about 1,100 item lines in Courier.",
        "Stresses: fit-to-height far below 10%, vertical scrolling inside one page, per-page text "
        "extraction and search over a single very long page, and a tip field and signature line at the very bottom.",
    ], size=7)
    c = []
    y = H - 330
    c.append(text_line(40, y, 14, "MEGA MART", b"F2"))
    y -= 14
    c.append(text_line(30, y, 7, "Store 0042  Register 3  Receipt 0000911", b"F4"))
    y -= 20
    total = 0
    item = 0
    while y > 700:
        item += 1
        name = prose.words(2)[:18]
        price = rng.randint(49, 4999)
        total += price
        canary = item % 97 == 0
        label = "lighthouse lamp" if canary else name
        c.append(b"BT /F4 7 Tf 10 %d Td (%s) Tj ET\n" % (y, pdfstr("%04d %-18s %7.2f" % (item, label[:18], price / 100))))
        y -= 12
        if item % 50 == 0:
            c.append(b"q 0.3 w 0.5 G 10 %d m 206 %d l S Q\n" % (y + 6, y + 6))
            c.append(b"BT /F4 7 Tf 10 %d Td (%s) Tj ET\n" % (y - 4, pdfstr("   subtotal after %d items %9.2f" % (item, total / 100))))
            y -= 16
    c.append(b"BT /F2 10 Tf 10 %d Td (%s) Tj ET\n" % (y - 10, pdfstr("TOTAL  %.2f" % (total / 100))))
    annots = [doc.text_field(num, "receipt_tip", (60, 560, 200, 580)),
              doc.check_box(num, "receipt_card", (10, 520, 24, 534), checked=True),
              doc.check_box(num, "receipt_cash", (110, 520, 124, 534))]
    c.append(text_line(10, 566, 9, "Tip:"))
    c.append(text_line(30, 523, 8, "Card"))
    c.append(text_line(130, 523, 8, "Cash"))
    c.append(printed_checkboxes(10, 470, ["Customer copy", "Merchant copy"], size=9, gap=16))
    c.append(sign_line(10, 360, 190))
    # A barcode.
    c.append(b"q 0 g" + b"".join(b" %d 200 %d 80 re" % (14 + i * 3, 1 + (i * 7) % 3) for i in range(60)) + b" f Q\n")
    main = doc.w.content(b"".join(c))
    doc.write_page(num, parent, (0, 0, W, H), [info, main], None, annots)
    return doc


def gen_mixed_sizes(path):
    doc = Doc(path, "Mixed page sizes, rotations and boxes")
    A0 = (2383.94, 3370.39)
    specs = [
        ("Letter portrait", (0, 0, 612, 792), 0, None),
        ("A4 portrait", (0, 0) + A4, 0, None),
        ("Letter landscape", (0, 0, 792, 612), 0, None),
        ("A4 landscape", (0, 0, A4[1], A4[0]), 0, None),
        ("Letter portrait, /Rotate 90", (0, 0, 612, 792), 90, None),
        ("Letter portrait, /Rotate 180", (0, 0, 612, 792), 180, None),
        ("Letter portrait, /Rotate 270", (0, 0, 612, 792), 270, None),
        ("A4 landscape, /Rotate 90", (0, 0, A4[1], A4[0]), 90, None),
        ("A4 portrait, /Rotate 270", (0, 0) + A4, 270, None),
        ("Tiny 72 x 72 pt (1 inch)", (0, 0, 72, 72), 0, None),
        ("A0 portrait", (0, 0) + A0, 0, None),
        ("Letter, MediaBox origin (-300, -400)", (-300, -400, 312, 392), 0, None),
        ("Letter, MediaBox origin (1000, 2000)", (1000, 2000, 1612, 2792), 0, None),
        ("Letter, CropBox inset 72 pt on every side", (0, 0, 612, 792), 0, (72, 72, 540, 720)),
        ("Letter, CropBox offset [0 100 612 700]", (0, 0, 612, 792), 0, (0, 100, 612, 700)),
        ("Letter, off-origin MediaBox and a CropBox, /Rotate 90", (-200, 300, 412, 1092), 90, (-150, 350, 362, 1042)),
        ("Tiny 72 x 72 pt, /Rotate 270", (0, 0, 72, 72), 270, None),
        ("A0 landscape, /Rotate 180", (0, 0, A0[1], A0[0]), 180, None),
        ("Legal portrait", (0, 0, 612, 1008), 0, None),
        ("Tabloid landscape, CropBox inset", (0, 0, 1224, 792), 0, (36, 36, 1188, 756)),
    ]
    pages = [specs[0]] + [specs[1 + i % (len(specs) - 1)] for i in range(39)]
    prose = Prose(404, canary_rate=0.02)
    for idx, (label, media, rot, crop) in enumerate(pages):
        num, parent = doc.new_page()
        x0, y0, x1, y1 = media
        bx0, by0, bx1, by1 = crop or media
        w, h = bx1 - bx0, by1 - by0
        s = max(0.08, min(1.0, min(w, h) / 612.0)) if min(w, h) < 612 else min(4.0, min(w, h) / 612.0)
        annots, extra, contents = [], b"", []
        if idx == 0:
            info = doc.reserve_info(54, 750, 500, "mixed-sizes.pdf", [
                "40 pages cycling through page sizes, rotations and page boxes. Every page names itself, frames its "
                "visible box in blue, labels its corners in unrotated page space (TL, TR, BL, BR) and draws an UP arrow, "
                "so a wrong rotation or box is visible at a glance.",
                "Stresses: thumbnails and scroll layout with mixed sizes, /Rotate for rendering, hit testing, form fields "
                "and text edits, MediaBox origins away from (0, 0), CropBox smaller than MediaBox, a 1-inch page and an A0 page.",
            ], size=10)
            fa, fc = info_page_fields(doc, num, 54, 420)
            annots += fa
            contents.append(info)
            c = [fc]
        else:
            c = [b"q 0.2 0.4 0.8 RG %.2f w %.2f %.2f %.2f %.2f re S Q\n" % (2 * s, bx0 + 4 * s, by0 + 4 * s, w - 8 * s, h - 8 * s)]
            fs = max(3.0, 9 * s)
            for tag, tx, ty in (("TL", bx0 + 10 * s, by1 - 20 * s), ("TR", bx1 - 30 * s, by1 - 20 * s),
                                ("BL", bx0 + 10 * s, by0 + 12 * s), ("BR", bx1 - 30 * s, by0 + 12 * s)):
                c.append(text_line(tx, ty, fs * 1.4, tag, b"F2", b"0.8 0.1 0.1 rg"))
            cx, cy = bx0 + w / 2, by0 + h / 2
            c.append(b"q 0.1 0.5 0.2 rg %.2f %.2f m %.2f %.2f l %.2f %.2f l h f Q\n"
                     % (cx, cy + 60 * s, cx - 25 * s, cy + 20 * s, cx + 25 * s, cy + 20 * s))
            c.append(text_line(cx - 8 * s, cy + 2 * s, fs * 1.2, "UP", b"F2"))
            c.append(text_line(bx0 + 20 * s, by1 - 45 * s, max(3.0, 15 * s), "Page %d: %s" % (idx + 1, label), b"F2"))
            if w >= 300:
                c.append(text_block(bx0 + 20 * s, by1 - 65 * s, 10 * s, 13 * s,
                                    wrap(prose.paragraph(4), w - 40 * s, 10 * s)[:8]))
                fy = by0 + 150 * s
                annots.append(doc.text_field(num, "page%02d_note" % (idx + 1), (bx0 + 20 * s, fy, bx0 + 220 * s, fy + 20 * s), size=10 * s))
                annots.append(doc.check_box(num, "page%02d_ok" % (idx + 1), (bx0 + 20 * s, fy - 30 * s, bx0 + 34 * s, fy - 16 * s)))
                c.append(text_line(bx0 + 40 * s, fy - 27 * s, 9 * s, "Page looks right"))
                c.append(printed_checkboxes(bx0 + 250 * s, fy + 6 * s, ["Rotation right", "Box right"], size=10 * s, gap=18 * s)
                         if s == 1.0 else b"")
                c.append(sign_line(bx0 + 20 * s, by0 + 60 * s, 220 * s))
            else:
                c.append(text_line(bx0 + 6, by0 + h * 0.3, max(2.5, 5 * s), label[:28]))
        if rot:
            extra += b" /Rotate %d" % rot
        if crop:
            extra += b" /CropBox [%s]" % b" ".join(b"%g" % v for v in crop)
        if "Tabloid" in label:
            extra += b" /TrimBox [54 54 1170 738] /BleedBox [45 45 1179 747]"
        main = doc.w.content(b"".join(c))
        doc.write_page(num, parent, media, [main] + contents, None, annots, extra)
    return doc


def text_page_body(doc, prose, rng, heading, y_top=730, y_bottom=90, x=72, width=468, chart=False, image=None):
    """A text-heavy page body. Returns (bytes, xobjects)."""
    c = [text_line(x, y_top, 16, heading, b"F2")]
    y = y_top - 26
    xobj = {}
    if chart:
        c.append(bar_chart(x, y - 170, width, 160, rng))
        c.append(text_line(x, y - 186, 9, "Figure: %s" % prose.words(6), b"F3"))
        y -= 210
    if image:
        key, img = image
        xobj[key] = img
        c.append(b"q 200 0 0 133 %.2f %.2f cm /%s Do Q\n" % (x + width - 200, y - 133, key))
        body = wrap(prose.paragraph(5), width - 215, 10)
        lines = body[:10]
        c.append(text_block(x, y - 10, 10, 13, lines))
        y -= max(150, 13 * len(lines) + 20)
    while y > y_bottom + 26:
        lines = wrap(prose.paragraph(), width, 10)
        room = int((y - y_bottom) // 13)
        lines = lines[:room]
        if not lines:
            break
        c.append(text_block(x, y, 10, 13, lines))
        y -= 13 * len(lines) + 8
    return b"".join(c), xobj


def gen_deep(path, pages, light, title, name):
    doc = Doc(path, title)
    prose = Prose(500 + pages, canary_rate=0.004 if not light else 0.02)
    rng = random.Random(pages)
    imgs = [(b"Im%d" % i, None) for i in range(6)]
    front = 12 if not light else 1
    appendix = pages - 88 if not light else pages
    chapter_len = 50 if not light else 1000
    section_len = 10 if not light else 100
    outline_ch = None
    for p in range(pages):
        num, parent = doc.new_page()
        annots, contents, xobj = [], [], {}
        if p == 0:
            info = doc.reserve_info(72, 740, 468, name, [
                ("%d text-heavy Letter pages (about 45 lines each), a two-level outline (a chapter every 50 pages, "
                 "a section every 10), page labels (i-xii front matter, 1-1900, then A-1 to A-88), a chart every 5 pages, "
                 "an image every 10 and a form block (text field, check box, printed boxes, sign line) every 50, starting at page 26." % pages)
                if not light else
                ("%d light Letter pages: a heading, a short paragraph and a small drawing. A two-level outline "
                 "(every 1,000 and every 100 pages), decimal page labels and a form block every 1,000 pages, starting at page 501." % pages),
                "Stresses: open time and memory for a deep page tree, thumbnail and scroll virtualisation, whole-document "
                "search time, outline and page-label navigation, save time for a large document.",
            ])
            fa, fc = info_page_fields(doc, num, 72, 380)
            annots += fa
            contents.append(info)
            body = fc
        elif light:
            form = p % 1000 == 500
            if form:
                annots.append(doc.text_field(num, "milestone_p%05d_initials" % (p + 1), (160, 196, 300, 216)))
                annots.append(doc.check_box(num, "milestone_p%05d_done" % (p + 1), (320, 199, 334, 213), checked=p % 2000 == 500))
            body = b""
            if form:
                body = (text_line(72, 202, 10, "Initials:") + text_line(340, 202, 10, "Checked")
                        + printed_checkboxes(400, 202, ["Pass", "Fail"], size=10, gap=16) + sign_line(72, 130))
            body += (text_line(72, 720, 18, "Section %d, page %d" % (p // 100 + 1, p + 1), b"F2")
                    + text_block(72, 690, 11, 14, wrap(prose.paragraph(2), 468, 11)[:6])
                    + b"q %.3f %.3f %.3f RG 2 w %s S Q\n" % (0.2 + (p % 5) * 0.1, 0.3, 0.6, circle(306, 400, 40 + p % 60))
                    + b"q 0.9 0.9 0.92 rg 72 %d %d 12 re f Q\n" % (300, 40 + (p * 37) % 420))
        else:
            chart = p % 5 == 3
            image = None
            if p % 10 == 7:
                key = imgs[(p // 10) % 6][0]
                image = (key, doc.pattern_image(key, 240, 160, 900 + (p // 10) % 6))
            heading = "Chapter %d. Section %d.%d" % (p // chapter_len + 1, p // chapter_len + 1, (p % chapter_len) // section_len + 1)
            if p < front:
                heading = "Front matter, page %d" % (p + 1)
            elif p >= appendix:
                heading = "Appendix A, page %d" % (p - appendix + 1)
            form = p % 50 == 25
            body, xobj = text_page_body(doc, prose, rng, heading, y_bottom=260 if form else 90, chart=chart, image=image)
            if form:
                body += text_line(72, 236, 12, "Checkpoint form for page %d" % (p + 1), b"F2")
                annots.append(doc.text_field(num, "checkpoint_p%04d_initials" % (p + 1), (160, 196, 300, 216), value="QA" if p % 100 == 25 else None))
                annots.append(doc.check_box(num, "checkpoint_p%04d_done" % (p + 1), (320, 199, 334, 213), checked=p % 200 == 25))
                body += text_line(72, 202, 10, "Initials:") + text_line(340, 202, 10, "Checked")
                body += printed_checkboxes(400, 202, ["Pass", "Fail"], size=10, gap=16)
                body += sign_line(72, 130)
        body += text_line(270, 40, 8, "physical page %d of %d" % (p + 1, pages), color=b"0.4 g")
        main = doc.w.content(body)
        doc.write_page(num, parent, (0, 0, 612, 792), [main] + contents, xobj, annots)
        # Outline
        if p >= front and (p - front) % chapter_len == 0 and p < appendix:
            outline_ch = ("Chapter %d" % (p // chapter_len + 1) if not light else "Pages %d to %d" % (p + 1, min(pages, p + chapter_len)), num, [])
            doc.outline.append(outline_ch)
        if outline_ch and p >= front and p < appendix and (p - front) % section_len == 0:
            outline_ch[2].append(("Section at page %d" % (p + 1), num, []))
        if p == 0:
            doc.outline.append(("About this file", num, []))
        if p == appendix:
            doc.outline.append(("Appendix A", num, []))
    if not light:
        doc.page_labels = b"0 << /S /r >> %d << /S /D >> %d << /S /D /P (A-) >>" % (front, appendix)
    else:
        doc.page_labels = b"0 << /S /D >>"
    return doc


def gen_many_fields(path):
    doc = Doc(path, "Many fields: 5,000 AcroForm fields")
    prose = Prose(606, canary_rate=0.05)
    num, parent = doc.new_page()
    info = doc.reserve_info(72, 740, 468, "many-fields.pdf", [
        "An info page plus 200 form pages of 25 fields each (8 rows of name, amount and approved, plus a multi-line notes box): "
        "5,000 fields in all. Pages 1-100 use flat top-level names (form_p001_r1_name); pages 101-200 use a real field "
        "hierarchy (a parent per page, a parent per row, terminal kids). About a quarter of the text fields and every "
        "fifth check box start filled.",
        "Stresses: form-field enumeration at open, per-page field regions, Tab traversal, fill and save time with a "
        "large /Fields array, and printed check boxes next to real ones.",
    ])
    fa, fc = info_page_fields(doc, num, 72, 420)
    doc.write_page(num, parent, (0, 0, 612, 792), [doc.w.content(fc), info], None, fa)
    for p in range(1, 201):
        num, parent = doc.new_page()
        hier = p > 100
        c = [text_line(72, 740, 16, "Claim form, page %d" % p, b"F2"),
             text_block(72, 718, 9, 11, wrap(prose.paragraph(3), 468, 9)[:3])]
        c.append(text_line(72, 668, 9, "Name", b"F2") + text_line(292, 668, 9, "Amount", b"F2") + text_line(420, 668, 9, "Approved", b"F2"))
        annots = []
        page_parent = row_parent = None
        page_kids = []
        if hier:
            page_parent = doc.w.alloc()
            doc.fields.append(page_parent)
        for r in range(1, 9):
            y = 640 - (r - 1) * 44
            c.append(text_line(50, y + 5, 9, "%d." % r))
            if hier:
                row_parent = doc.w.alloc()
                page_kids.append(row_parent)
                n1, n2, n3 = "name", "amount", "approved"
            else:
                n1, n2, n3 = ["form_p%03d_r%d_%s" % (p, r, k) for k in ("name", "amount", "approved")]
            filled = (p + r) % 4 == 0
            kids = [doc.text_field(num, n1, (72, y, 280, y + 20), value=("Claimant %d-%d" % (p, r)) if filled else None, parent=row_parent),
                    doc.text_field(num, n2, (292, y, 400, y + 20), value=("%d.00" % (p * r)) if filled else None, parent=row_parent),
                    doc.check_box(num, n3, (430, y + 3, 444, y + 17), checked=(p + r) % 5 == 0, parent=row_parent)]
            annots += kids
            c.append(printed_checkboxes(470, y + 4, ["paid"], size=10))
            if hier:
                doc.w.obj(b"<< /T (r%d) /Parent %d 0 R /Kids [%s] >>" % (r, page_parent, b" ".join(b"%d 0 R" % k for k in kids)), num=row_parent)
        notes_parent = page_parent if hier else None
        annots.append(doc.text_field(num, "notes" if hier else "form_p%03d_notes" % p, (72, 180, 540, 280), multiline=True,
                                     value=("Notes for page %d" % p) if p % 3 == 0 else None, parent=notes_parent))
        if hier:
            page_kids.append(annots[-1])
            doc.w.obj(b"<< /T (p%03d) /Kids [%s] >>" % (p, b" ".join(b"%d 0 R" % k for k in page_kids)), num=page_parent)
        c.append(text_line(72, 286, 9, "Notes", b"F2"))
        c.append(sign_line(72, 110, 260))
        c.append(text_line(270, 40, 8, "page %d of 201" % (p + 1), color=b"0.4 g"))
        doc.write_page(num, parent, (0, 0, 612, 792), doc.w.content(b"".join(c)), None, annots)
    return doc


def gen_many_objects(path):
    doc = Doc(path, "Many objects: 100,000 paths and 20,000 text objects")
    num, parent = doc.new_page()
    info = doc.reserve_info(72, 740, 468, "many-objects.pdf", [
        "Page 2: 100,000 separate filled path objects (a 250 x 400 grid of tiny coloured squares) under a normal "
        "text heading. Page 3: 20,000 separate text objects (a BT..ET each, 80 columns x 250 rows of short words). "
        "Page 4: a normal text page for comparison.",
        "Stresses: render time and memory with huge object counts, page-object enumeration (text lines, check-box "
        "detection, whiteouts), the text-edit layout guard and #139 content regeneration timings on a page with "
        "100,000 objects, and search over 20,000 text objects.",
    ])
    fa, fc = info_page_fields(doc, num, 72, 420)
    doc.write_page(num, parent, (0, 0, 612, 792), [doc.w.content(fc), info], None, fa)
    # 100k paths.
    W, H = 792, 1224
    num, parent = doc.new_page()
    c = [text_line(40, H - 40, 16, "100,000 path objects: every square below is its own path", b"F2"),
         text_line(40, H - 58, 10, "Edit this heading to time content regeneration on a crowded page.")]
    cw, ch = (W - 80) / 250.0, (H - 120) / 400.0
    for j in range(400):
        row = []
        for i in range(250):
            r = (i * 7 + j * 3) % 256 / 255.0
            g = (i * 3 + j * 11) % 256 / 255.0
            row.append(b"%.2f %.2f %.2f rg %.2f %.2f %.2f %.2f re f" % (r, g, 0.5, 40 + i * cw, 40 + j * ch, cw * 0.8, ch * 0.8))
        c.append(b"\n".join(row) + b"\n")
    doc.write_page(num, parent, (0, 0, W, H), doc.w.content(b"".join(c)))
    # 20k text objects.
    W, H = 1224, 1584
    num, parent = doc.new_page()
    rng = random.Random(77)
    c = [text_line(40, H - 40, 16, "20,000 text objects: every word below is its own BT..ET", b"F2")]
    cw, ch = (W - 80) / 80.0, (H - 100) / 250.0
    for j in range(250):
        for i in range(80):
            word = rng.choice(WORDS)[:5] if (i * 13 + j * 7) % 1999 else "lighthouse"
            c.append(b"BT /F1 5 Tf %.2f %.2f Td (%s) Tj ET\n" % (40 + i * cw, 40 + j * ch, pdfstr(word)))
    doc.write_page(num, parent, (0, 0, W, H), doc.w.content(b"".join(c)))
    prose = Prose(708, canary_rate=0.02)
    num, parent = doc.new_page()
    body, _ = text_page_body(doc, prose, rng, "A normal page for comparison", y_bottom=200)
    fa = [doc.text_field(num, "compare_note", (72, 150, 300, 170))]
    doc.write_page(num, parent, (0, 0, 612, 792), doc.w.content(body + sign_line(320, 150)), None, fa)
    return doc


def gen_big_scan(path, target=250 * MB, pages=100):
    doc = Doc(path, "Big scan: 100 pages of 300-600 dpi RGB scans")
    prose = Prose(808, canary_rate=0.01)
    rng = random.Random(88)
    steer = SizeSteer(target, pages)
    dpis = [300, 400, 600, 300, 450]
    for p in range(pages):
        num, parent = doc.new_page()
        pw, ph = (LETTER if p % 3 else A4)
        dpi = dpis[p % len(dpis)]
        W, H = int(pw * dpi / 72), int(ph * dpi / 72)
        margin = dpi  # one inch
        # Layout in pixel rows: text lines (with OCR words) and one photo block.
        photo_px_max = (W - 2 * margin) * (H - 2 * margin)
        frac = steer.fraction(doc.w.pos, p, W * H, photo_px_max)
        photo_rows = int((H - 2 * margin) * frac)
        img = doc.w.alloc()
        sink = doc.w.open_stream(b"/Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceRGB /BitsPerComponent 8" % (W, H), num=img)
        bands = ImageBands(sink, W, b"scan%d" % p, _grey_lut(tone_shift=(p * 7) % 30))
        paper = bytes(W)
        ocr = [b"q BT 3 Tr"]
        scale = 72.0 / dpi
        bands.rows(paper, margin)
        y = margin
        text_rows = H - 2 * margin - photo_rows
        photo_at = margin + (text_rows // 3 if p % 2 else 0)
        line_h = int(dpi * 14 / 72)          # 14 pt leading
        glyph_h = int(dpi * 7 / 72)          # x-height band
        size_pt = 10
        photo_done = False
        while y < H - margin:
            if not photo_done and y >= photo_at and photo_rows > 0:
                photo_rows = min(photo_rows, H - margin - y)
                band = 0
                left = photo_rows
                while left > 0:
                    k = min(left, max(1, dpi // 4))
                    bands.rows(photo_structure(W, band, margin, W - margin), k, photo=True)
                    band += 1
                    left -= k
                y += photo_rows
                photo_done = True
                continue
            if y + line_h > H - margin:
                bands.rows(paper, H - margin - y)
                y = H - margin
                break
            words = prose.sentence().split()
            row = bytearray(W)
            x = margin
            placed = []
            for word in words:
                wpx = int(text_width(word, size_pt) / scale)
                if x + wpx > W - margin:
                    break
                row[x:x + wpx] = b"\x20" * wpx
                placed.append((x, word))
                x += wpx + int(text_width(" ", size_pt) / scale)
            gap = (line_h - glyph_h) // 2
            bands.rows(paper, gap)
            bands.rows(bytes(row), glyph_h)
            bands.rows(paper, line_h - gap - glyph_h)
            base_pt = ph - (y + gap + glyph_h) * scale
            for gx, word in placed:
                ocr.append(b"/F1 %d Tf 1 0 0 1 %.2f %.2f Tm (%s) Tj" % (size_pt, gx * scale, base_pt, pdfstr(word)))
            y += line_h
        bands.rows(paper, margin)
        sink.close()
        steer.learn(bands)
        ocr.append(b"ET Q\n")
        c = b"q %.2f 0 0 %.2f 0 0 cm /Scan Do Q\n" % (pw, ph) + b" ".join(ocr)
        annots, contents = [], []
        if p == 0:
            info = doc.reserve_info(30, ph - 20, pw - 60, "big-scan-250mb.pdf", [
                "100 pages of procedurally generated RGB page scans at 300, 400, 450 and 600 dpi (Letter and A4), "
                "Flate-compressed, with an invisible OCR text layer (text render mode 3) over the ink lines, and a photo "
                "block sized so the file lands near 250 MB. No DCT: a pure-Python JPEG encoder would take hours here.",
                "Stresses: image decode per render and per zoom (#94), memory for 100 MB decoded pages, open and save of a "
                "250 MB file, search in an invisible text layer, and the shrink-for-email decode path.",
            ], size=9)
            contents.append(info)
            c = b"q 1 1 1 rg 20 %.2f %.2f 150 re f Q\n" % (ph - 170, pw - 40) + c
        if p % 10 == 5:
            annots.append(doc.text_field(num, "scan_p%03d_batch" % (p + 1), (pw - 200, 30, pw - 40, 50), value="Batch %d" % (p // 10 + 1)))
            annots.append(doc.check_box(num, "scan_p%03d_legible" % (p + 1), (pw - 230, 33, pw - 216, 47)))
            c += text_line(40, 36, 9, "Page %d legible?" % (p + 1), color=b"0.8 0 0 rg") + sign_line(40, 70, 180)
        main = doc.w.content(c)
        doc.write_page(num, parent, (0, 0, pw, ph), [main] + contents, {b"Scan": img}, annots)
        progress(path, p + 1, pages, doc.w.pos)
    return doc


def gen_big_report(path, target, pages, name, info_extra):
    doc = Doc(path, "Big report: %d pages with large figures" % pages)
    prose = Prose(target % 99991, canary_rate=0.004)
    rng = random.Random(pages)
    steer = SizeSteer(target, pages)
    IW, IH = 2400, 1600   # figure pixels, drawn at 6 x 4 in (400 dpi)
    for p in range(pages):
        num, parent = doc.new_page()
        frac = steer.fraction(doc.w.pos, p, IW * IH, IW * IH)
        img = doc.w.alloc()
        sink = doc.w.open_stream(b"/Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceRGB /BitsPerComponent 8" % (IW, IH), num=img)
        bands = ImageBands(sink, IW, b"fig%d/%d" % (pages, p), _grey_lut(tone_shift=(p * 11) % 40, speckle=False, photo_amp=4),
                           red=bytes(min(255, v + (p % 5) * 6) for v in range(256)), blue=bytes(max(0, v - (p % 7) * 5) for v in range(256)))
        photo_rows = int(IH * frac)
        band, y = 0, 0
        while y < IH:
            k = min(IH - y, 40)
            if y < photo_rows:
                bands.rows(photo_structure(IW, band, 0, IW), k, photo=True)
            else:
                row = bytearray(IW)
                for i in range(0, IW, 400):  # grid of faint cells below the photo part
                    row[i:i + 4] = b"\x20" * 4
                bands.rows(bytes(row) if band % 10 else b"\x20" * IW, k)
            band += 1
            y += k
        sink.close()
        steer.learn(bands)
        chapter = p // 20 + 1
        # Figure on the upper half, body text below.
        c = [b"q 432 0 0 288 90 400 cm /Fig Do Q\n",
             text_line(90, 386, 9, "Figure %d.%d: %s" % (chapter, p % 20 + 1, prose.words(7)), b"F3")]
        if p:  # page 1's heading space holds the info block instead
            c.append(text_line(72, 730, 16, "Chapter %d, page %d" % (chapter, p + 1), b"F2"))
        y = 360
        while y > 110:
            lines = wrap(prose.paragraph(), 468, 10)[: int((y - 100) // 13)]
            if not lines:
                break
            c.append(text_block(72, y, 10, 13, lines))
            y -= 13 * len(lines) + 8
        annots, contents = [], []
        if p == 0:
            info = doc.reserve_info(72, 780, 468, name, info_extra, size=9)
            contents.append(info)
            c = [b"q 1 1 1 rg 60 690 492 100 re f Q\n"] + c
        if p % 25 == 12:
            annots.append(doc.text_field(num, "report_p%04d_reviewer" % (p + 1), (380, 50, 540, 70)))
            annots.append(doc.check_box(num, "report_p%04d_figure_ok" % (p + 1), (72, 53, 86, 67), checked=p % 50 == 12))
            c.append(text_line(92, 56, 9, "Figure renders correctly") + text_line(330, 56, 9, "Reviewer:"))
            c.append(printed_checkboxes(72, 90, ["Colour ok"], size=9))
            c.append(sign_line(200, 90, 150))
        c.append(text_line(270, 30, 8, "page %d of %d" % (p + 1, pages), color=b"0.4 g"))
        main = doc.w.content(b"".join(c))
        doc.write_page(num, parent, (0, 0, 612, 792), [main] + contents, {b"Fig": img}, annots)
        progress(path, p + 1, pages, doc.w.pos)
    return doc


def gen_huge_image_page(path):
    IW, IH = 20000, 15000
    PW, PH = 4800, 3600   # 300 dpi
    doc = Doc(path, "Huge image page: one 20,000 x 15,000 px image")
    num, parent = doc.new_page()
    img = doc.w.alloc()
    sink = doc.w.open_stream(b"/Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceRGB /BitsPerComponent 8" % (IW, IH),
                             num=img, level=6)
    # Column buckets (0..249) with grid lines every 1,000 px as bucket 255.
    col = bytearray(IW)
    for x in range(IW):
        col[x] = 255 if x % 1000 < 6 else (x * 250) // IW
    col = bytes(col)
    for band in range(250):
        rows = IH // 250
        yb = band
        luts = []
        for ch in range(3):
            lut = bytearray(256)
            for xb in range(250):
                u, v = xb / 250.0, yb / 250.0
                d = math.hypot(u - 0.5, (v - 0.5) * 0.75)
                val = [128 + 110 * math.sin(u * 9 + v * 3), 128 + 110 * math.sin(d * 40), 128 + 110 * math.cos(v * 7 - u * 2)][ch]
                lut[xb] = int(max(0, min(255, val)))
            lut[255] = 20
            luts.append(bytes(lut))
        grid = (band * rows) % 1000 < rows  # a horizontal grid line in this band
        row = bytearray(IW * 3)
        row[0::3] = col.translate(luts[0])
        row[1::3] = col.translate(luts[1])
        row[2::3] = col.translate(luts[2])
        row = bytes(row)
        if grid:
            sink.write(b"\x14" * (IW * 3 * 6))
            sink.write(row * (rows - 6))
        else:
            sink.write(row * rows)
    sink.close()
    info = doc.reserve_info(80, PH - 60, 1500, "huge-image-page.pdf", [
        "One 4,800 x 3,600 pt page (66.7 x 50 in) holding a single 20,000 x 15,000 px RGB image (300 dpi, 900 MB "
        "decoded, Flate-compressed to a few MB): a colour field with a grid line every 1,000 px.",
        "Stresses: the render clamp (#93) and image decode memory: a naive full-resolution decode needs 900 MB, "
        "and zooming in must fit the raster to the clamp rather than allocate the ideal bitmap. Also the "
        "shrink-for-email path, which must refuse or downsample rather than decode blindly.",
    ], size=28)
    fa, fc = info_page_fields(doc, num, 80, 2900)
    c = b"q %d 0 0 %d 0 0 cm /Big Do Q\n" % (PW, PH)
    c += b"q 1 1 1 rg 40 2600 1600 960 re f Q\n" + fc
    main = doc.w.content(c)
    doc.write_page(num, parent, (0, 0, PW, PH), [main, info], {b"Big": img}, fa)
    return doc


def gen_wide_deep(path, pages=500):
    W, H = 3456, 2592
    doc = Doc(path, "Wide and deep: 500 engineering drawings at 48 x 36 in")
    prose = Prose(1001, canary_rate=0.01)
    for p in range(pages):
        rng = random.Random(5000 + p)
        num, parent = doc.new_page()
        c = [b"q 2 w 0 G 36 36 %d %d re S 0.8 w 60 60 %d %d re S Q\n" % (W - 72, H - 72, W - 120, H - 120)]
        for i in range(12):
            x = 60 + (i + 0.5) * (W - 120) / 12
            c.append(text_line(x, 42, 14, str(i + 1), b"F2") + text_line(x, H - 56, 14, str(i + 1), b"F2"))
            c.append(b"q 0.8 w 0 G %.1f 36 m %.1f 60 l S %.1f %d m %.1f %d l S Q\n" % (x + 144, x + 144, x + 144, H - 60, x + 144, H - 36))
        for i in range(8):
            y = 60 + (i + 0.5) * (H - 120) / 8
            c.append(text_line(42, y, 14, "ABCDEFGH"[7 - i], b"F2") + text_line(W - 54, y, 14, "ABCDEFGH"[7 - i], b"F2"))
        # Plate outline with holes, centre lines, dimensions.
        px, py, pw_, ph_ = 300, 700, 1700, 1300
        c.append(b"q 3 w 0 G %d %d %d %d re S Q\n" % (px, py, pw_, ph_))
        c.append(b"q 0.6 w 0.2 G [40 8 8 8] 0 d %d %d m %d %d l S %d %d m %d %d l S Q\n"
                 % (px - 60, py + ph_ // 2, px + pw_ + 60, py + ph_ // 2, px + pw_ // 2, py - 60, px + pw_ // 2, py + ph_ + 60))
        holes = []
        for k in range(rng.randint(12, 40)):
            hx, hy, r = rng.uniform(px + 80, px + pw_ - 80), rng.uniform(py + 80, py + ph_ - 80), rng.choice([12, 18, 25, 40])
            holes.append(circle(hx, hy, r))
            c.append(text_line(hx + r + 6, hy + r + 4, 10, "%s%d" % ("\xd8", r * 2)))
        c.append(b"q 1.5 w 0 G " + b" ".join(holes) + b" S Q\n")
        # Bolt circle and gear.
        gx, gy = px + pw_ // 2, py + ph_ // 2
        teeth = 24 + p % 30
        pts = []
        for t in range(teeth * 4):
            ang = 2 * math.pi * t / (teeth * 4)
            rr = 260 if (t % 4) in (0, 1) else 228
            pts.append(b"%.2f %.2f %s" % (gx + rr * math.cos(ang), gy + rr * math.sin(ang), b"m" if t == 0 else b"l"))
        c.append(b"q 1.2 w 0.1 0.1 0.4 RG " + b" ".join(pts) + b" h S " + circle(gx, gy, 80) + b" S Q\n")
        # Hatched section view, clipped.
        sx, sy = 2150, 1300
        hatch = [b"q %d %d 700 700 re W n 0.5 w 0 G" % (sx, sy)]
        for k in range(-700, 700, 14):
            hatch.append(b"%d %d m %d %d l" % (sx + k, sy, sx + k + 700, sy + 700))
        c.append(b" ".join(hatch) + b" S Q q 2 w 0 G %d %d 700 700 re S Q\n" % (sx, sy))
        c.append(text_line(sx, sy - 30, 20, "SECTION A-A", b"F2"))
        # Dimension lines with arrowheads.
        for dy, (a, b_) in enumerate([(px, px + pw_), (px, gx), (gx, px + pw_)]):
            yy = py - 120 - dy * 60
            c.append(b"q 0.7 w 0 G %d %d m %d %d l S %d %d m %d %d l %d %d l h f %d %d m %d %d l %d %d l h f Q\n"
                     % (a, yy, b_, yy, a, yy, a + 20, yy + 6, a + 20, yy - 6, b_, yy, b_ - 20, yy + 6, b_ - 20, yy - 6))
            c.append(text_line((a + b_) / 2 - 30, yy + 8, 16, "%.1f mm" % ((b_ - a) * 0.2)))
        # Random polyline route (piping) with many segments.
        seg = [b"q 1 w 0.6 0.1 0.1 RG %d %d m" % (2150, 800)]
        x, y = 2150, 800
        for k in range(600):
            if k % 2:
                x = max(2100, min(3300, x + rng.choice([-40, -20, 20, 40])))
            else:
                y = max(500, min(1200, y + rng.choice([-30, 30])))
            seg.append(b"%d %d l" % (x, y))
        c.append(b" ".join(seg) + b" S Q\n")
        # Notes block.
        notes = ["NOTES:"] + ["%d. %s" % (i + 1, prose.sentence().upper()[:90]) for i in range(10)]
        c.append(text_block(300, 560, 14, 18, notes, b"F4"))
        # Title block.
        tx, ty = W - 1260, 60
        c.append(b"q 1.5 w 0 G %d %d 1200 360 re S %d %d m %d %d l S %d %d m %d %d l S %d %d m %d %d l S Q\n"
                 % (tx, ty, tx, ty + 240, tx + 1200, ty + 240, tx, ty + 120, tx + 1200, ty + 120, tx + 600, ty, tx + 600, ty + 360))
        c.append(text_line(tx + 20, ty + 300, 40, "DRAWING WD-%04d" % (p + 1), b"F2"))
        c.append(text_line(tx + 620, ty + 300, 24, "SHEET %d OF %d" % (p + 1, pages), b"F2"))
        c.append(text_line(tx + 20, ty + 180, 20, "PART: %s" % prose.words(3).upper()))
        c.append(text_line(tx + 620, ty + 180, 20, "SCALE 1:%d   MATERIAL %s" % (rng.choice([1, 2, 5, 10]), rng.choice(["STEEL", "ALUMINIUM", "BRASS"]))))
        c.append(printed_checkboxes(tx + 20, ty + 80, ["Checked", "Released"], size=14, gap=30))
        c.append(sign_line(tx + 620, ty + 50, 520, "Approved: sign above the line"))
        annots, contents = [], []
        if p % 10 == 0:
            annots.append(doc.text_field(num, "drawing_%04d_revision" % (p + 1), (tx + 300, ty + 130, tx + 580, ty + 160), value="A" if p % 20 else None, size=16))
            annots.append(doc.check_box(num, "drawing_%04d_released" % (p + 1), (tx + 200, ty + 20, tx + 220, ty + 40), checked=p % 30 == 0))
            c.append(text_line(tx + 230, ty + 140, 14, "REV"))
        if p == 0:
            info = doc.reserve_info(300, H - 120, 1500, "wide-deep.pdf", [
                "500 pages at 3,456 x 2,592 pt (48 x 36 in, ARCH E): engineering drawings with borders and zone "
                "labels, a plate with holes, a gear outline of up to 212 segments, a clipped hatched section, "
                "dimensions, a 600-segment route, numbered notes in Courier and a title block with printed check "
                "boxes and an approval line. Every tenth sheet has a revision field and a released check box.",
                "Stresses: the combination of large pages and a deep document: open time, thumbnail and "
                "scroll layout, render time per sheet at fit and at the clamp, whole-document search, save.",
            ], size=24)
            fa, fc = info_page_fields(doc, num, 2300, 2420)
            c.append(b"q 1 1 1 rg 290 1950 1560 560 re f Q\n")
            c.append(fc)
            annots += fa
            contents.append(info)
        main = doc.w.content(b"".join(c))
        doc.write_page(num, parent, (0, 0, W, H), [main] + contents, None, annots)
        if p % 50 == 0:
            doc.outline.append(("Sheets %d-%d" % (p + 1, min(pages, p + 50)), num, []))
        progress(path, p + 1, pages, doc.w.pos)
    return doc


# --------------------------------------------------------------------------------------

_last_progress = [0.0]


def progress(path, done, total, pos):
    now = time.time()
    if now - _last_progress[0] > 10 or done == total:
        _last_progress[0] = now
        print("  %s: page %d/%d, %.0f MB" % (os.path.basename(path), done, total, pos / MB), flush=True)


BIG_1GB_INFO = [
    "400 Letter report pages, each with a 2,400 x 1,600 px RGB figure (Flate) whose detail is steered so the file "
    "lands near 1 GB, over body text, a form block every 25 pages and a signature line.",
    "Stresses: open, scroll and save of a 1 GB file, the app's memory while it is open, image decode per page, "
    "and search and edits on pages far into the file.",
]
HUGE_INFO = [
    "1,000 Letter report pages with large figures, about 2.5 GB: past the 2 GB line, so every offset in the "
    "second half of the file, the xref table, the page tree and the catalog is beyond what a signed 32-bit int holds.",
    "Stresses: graceful failure or success for files over 2 GB: open (streamed or memory-mapped, never a single "
    "2 GB+ byte array), save, the share and copy paths, and any code that reads the whole file into memory.",
]

FILES = [
    ("wide-poster.pdf", gen_wide_poster),
    ("wide-userunit.pdf", gen_wide_userunit),
    ("tall-receipt.pdf", gen_tall_receipt),
    ("mixed-sizes.pdf", gen_mixed_sizes),
    ("deep-2000.pdf", lambda p: gen_deep(p, 2000, False, "Deep: 2,000 text-heavy pages", "deep-2000.pdf")),
    ("deep-10000.pdf", lambda p: gen_deep(p, 10000, True, "Deep: 10,000 light pages", "deep-10000.pdf")),
    ("many-fields.pdf", gen_many_fields),
    ("many-objects.pdf", gen_many_objects),
    ("big-scan-250mb.pdf", gen_big_scan),
    ("big-1gb.pdf", lambda p: gen_big_report(p, 1000 * MB, 400, "big-1gb.pdf", BIG_1GB_INFO)),
    ("huge-2_5gb.pdf", lambda p: gen_big_report(p, 2560 * MB, 1000, "huge-2_5gb.pdf", HUGE_INFO)),
    ("huge-image-page.pdf", gen_huge_image_page),
    ("wide-deep.pdf", gen_wide_deep),
]
HUGE = {"huge-2_5gb.pdf"}


def main(argv):
    args = [a for a in argv if not a.startswith("--")]
    if not args:
        print(__doc__)
        return 2
    out = args[0]
    only = None
    if "--only" in argv:
        only = set(argv[argv.index("--only") + 1].split(","))
        args = [a for a in args if a != argv[argv.index("--only") + 1]]
    skip_huge = "--skip-huge" in argv
    os.makedirs(out, exist_ok=True)
    for name, fn in FILES:
        if only and name not in only and name[:-4] not in only:
            continue
        if skip_huge and name in HUGE:
            print("%-22s skipped (--skip-huge)" % name)
            continue
        path = os.path.join(out, name)
        start = time.time()
        doc = fn(path)
        size = doc.finish()
        print("%-22s %10.1f MB  %6d pages  %5d fields  %7.1f s" % (name, size / MB, doc.page_count, doc.field_count, time.time() - start), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
