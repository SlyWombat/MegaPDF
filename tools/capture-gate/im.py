"""ImageMagick, wrapped thinly, plus the byte-scanning the checks are built on.

The house already depends on the ImageMagick CLI for capture work — `compare`
and `identify` in `tools/android-qa/compare.py` — so this adds no dependency
anyone has to install to look at a set. Deliberately no Pillow and no numpy:
the gate has to run wherever the captures land, including a Mac mini and a
Windows laptop that only ever installed the app.

Everything here returns plain `bytes` and reads them with `bytes.count` and
`bytes.find`, which run in C. A 2064x2752 iPad shot is 5.7 million pixels; a
per-pixel Python loop over one takes seconds and there are a hundred images in
a full gate. Scanning runs with `find` takes milliseconds.

Optional: `tesseract`. Without it the text checks say so and stand down rather
than passing quietly; see README.md § What needs OCR.
"""
from __future__ import annotations

import functools
import shutil
import subprocess


class MissingTool(RuntimeError):
    pass


def require(tool: str = "convert") -> str:
    path = shutil.which(tool)
    if not path:
        raise MissingTool(
            f"{tool} is not on PATH. The gate needs ImageMagick: "
            "apt install imagemagick / brew install imagemagick.")
    return path


def have(tool: str) -> bool:
    return shutil.which(tool) is not None


def _run(args: list[str]) -> bytes:
    proc = subprocess.run(args, capture_output=True)
    if proc.returncode != 0:
        raise RuntimeError(
            f"{args[0]} failed ({proc.returncode}): "
            f"{proc.stderr.decode('utf-8', 'replace').strip()[:300]}")
    return proc.stdout


class Raster:
    """One 8-bit plane: `data[y * w + x]`, and the row helpers built on it."""

    __slots__ = ("w", "h", "data", "_hist")

    def __init__(self, w: int, h: int, data: bytes):
        self.w, self.h, self.data = w, h, data
        self._hist: list[int] | None = None

    def row(self, y: int) -> bytes:
        return self.data[y * self.w:(y + 1) * self.w]

    def col(self, x: int) -> bytes:
        return self.data[x::self.w]

    def at(self, x: int, y: int) -> int:
        return self.data[y * self.w + x]

    def count(self, value: int) -> int:
        return self.data.count(bytes([value]))


def size(path: str) -> tuple[int, int]:
    require("identify")
    w, h = _run(["identify", "-format", "%w %h", f"{path}[0]"]).split()
    return int(w), int(h)


@functools.lru_cache(maxsize=64)
def gray(path: str, width: int | None = None) -> Raster:
    """The image as one luminance plane, optionally scaled to `width`.

    Scaled rasters are what the layout checks compare: at 200 px wide a
    paragraph is a band of grey whose *position* still means something and
    whose exact glyphs no longer do, which is the distinction between a layout
    difference and a translation being longer.
    """
    require()
    args = [f"{path}[0]", "-colorspace", "Gray", "-depth", "8"]
    if width:
        args += ["-resize", f"{width}x"]
    args += ["gray:-"]
    data = _run(["convert"] + args)
    if width:
        w = width
        h = len(data) // w
    else:
        w, h = size(path)
    if len(data) != w * h:                       # -resize rounds; trust the bytes
        h = len(data) // w
    return Raster(w, h, data)


def gray_box(path: str, box: tuple[int, int, int, int]) -> Raster:
    """A crop as a luminance plane. `box` is (x, y, w, h)."""
    require()
    x, y, w, h = box
    data = _run(["convert", f"{path}[0]", "-crop", f"{w}x{h}+{x}+{y}", "+repage",
                 "-colorspace", "Gray", "-depth", "8", "gray:-"])
    return Raster(w, len(data) // w if w else 0, data)


@functools.lru_cache(maxsize=64)
def colour_mask(path: str, colour: str, fuzz: int = 12) -> Raster:
    """0 where the pixel matches `colour` within `fuzz` %, 255 where it does not.

    Done with `-transparent` + `-alpha extract` rather than the obvious
    `-opaque`/`+opaque` pair, which cannot tell a pixel it just painted white
    from a pixel that was white to begin with — and a document page is white,
    so that version matched most of every screenshot.
    """
    require()
    data = _run(["convert", f"{path}[0]", "-fuzz", f"{fuzz}%", "-transparent", colour,
                 "-alpha", "extract", "-depth", "8", "gray:-"])
    w, h = size(path)
    return Raster(w, len(data) // w, data)


def matched(mask: Raster) -> int:
    return mask.data.count(0)


def runs(row: bytes, hit) -> list[tuple[int, int]]:
    """[(start, length)] of consecutive bytes satisfying `hit`.

    `hit` is a set of byte values (fast path, scanned with translate/find) or a
    predicate. Used on mask rows, where "consecutive" is what tells a glyph
    stroke (a few pixels, repeated) from a window border (the whole edge).
    """
    out: list[tuple[int, int]] = []
    start = None
    for i, value in enumerate(row):
        if (value in hit) if isinstance(hit, (set, frozenset, bytes)) else hit(value):
            if start is None:
                start = i
        elif start is not None:
            out.append((start, i - start))
            start = None
    if start is not None:
        out.append((start, len(row) - start))
    return out


def histogram(raster: Raster) -> list[int]:
    """Cached on the raster: it costs 256 passes over the buffer, and three
    checks want it for the same image."""
    if raster._hist is None:
        raster._hist = [raster.data.count(bytes([v])) for v in range(256)]
    return raster._hist


def modal(raster: Raster) -> int:
    counts = histogram(raster)
    return counts.index(max(counts))


def background_values(raster: Raster, min_share: float = 0.004,
                      delta: int = 12) -> set[int]:
    """The flat tones this image is mostly made of, widened by `delta`.

    A screenshot is a handful of flat colours — the page, the canvas behind it,
    the toolbar, the title bar — plus a little text and iconography. Anything
    holding a few tenths of a percent of the pixels is one of those flats, and
    calling all of them "background" is what lets one ink profile work on a
    light window and a dark one without being told which it is looking at.
    """
    counts = histogram(raster)
    floor = raster.w * raster.h * min_share
    flats = {v for v, n in enumerate(counts) if n >= floor}
    out: set[int] = set()
    for v in flats:
        out.update(range(max(0, v - delta), min(255, v + delta) + 1))
    return out


def ink_profile(raster: Raster, background: set[int] | None = None) -> list[float]:
    """Per row, the fraction of pixels that are not one of the flat tones."""
    if background is None:
        background = background_values(raster)
    table = bytes(0 if v in background else 1 for v in range(256))
    return [sum(raster.row(y).translate(table)) / raster.w
            for y in range(raster.h)]


def blocks(profile: list[float], threshold: float = 0.01,
           gap: int = 2) -> list[tuple[int, int]]:
    """Contiguous inked row ranges, closing gaps of `gap` rows.

    These are what a layout *is*, reduced to one dimension: a toolbar, a
    banner, a heading, a paragraph, a status line. Two captures of the same
    screen in different languages have the same blocks in the same places; the
    amount of ink inside a block is where translation shows up.
    """
    out: list[tuple[int, int]] = []
    start = None
    empty = 0
    for y, value in enumerate(profile):
        if value >= threshold:
            if start is None:
                start = y
            empty = 0
        elif start is not None:
            empty += 1
            if empty > gap:
                out.append((start, y - empty))
                start = None
    if start is not None:
        out.append((start, len(profile) - 1))
    return out


def ink_mask(raster: Raster, background: set[int] | None = None) -> bytes:
    """The raster as one byte per pixel: 1 where there is ink, 0 where there is
    not.

    Comparing *ink* rather than pixels is what makes a status bar comparable
    across screens: the same clock, signal and battery are drawn over a white
    list on one screen and a grey toolbar on the next, and only the ink is the
    same in both.
    """
    if background is None:
        background = background_values(raster, min_share=0.02)
    table = bytes(0 if v in background else 1 for v in range(256))
    return raster.data.translate(table)


def mask_difference(a: bytes, b: bytes) -> int:
    """How many pixels two ink masks disagree about.

    XOR as one big integer: `bit_count` on a 130 000-byte mask is microseconds,
    where the obvious generator expression is tens of milliseconds and this is
    called for every pair in a set. A count rather than a fraction, because the
    denominator that matters is the ink in the band and not its area — a badge
    over the Wi-Fi icon is a tenth of the bar's ink and a thousandth of its
    pixels.
    """
    if len(a) != len(b) or not a:
        return max(len(a), len(b))
    return (int.from_bytes(a, "big") ^ int.from_bytes(b, "big")).bit_count()


def difference(a: str, b: str, fuzz: int = 1) -> float | None:
    """The fraction of pixels two images disagree about, or None if their
    geometries differ.

    `compare -metric AE`, the same measurement `tools/android-qa/compare.py`
    makes between light and dark cells — here between a new set and the one
    that was certified, which is the only way to notice something that is
    *plausible* in every image and simply not what was signed off: a different
    signature in the library, a fixture that was regenerated, a theme that
    moved a shade.
    """
    require("compare")
    if size(a) != size(b):
        return None
    proc = subprocess.run(
        ["compare", "-metric", "AE", "-fuzz", f"{fuzz}%", a, b, "null:"],
        capture_output=True)
    text = proc.stderr.decode("utf-8", "replace").strip().split()[0]
    try:
        differing = float(text.replace(",", ""))
    except ValueError:
        return None
    w, h = size(a)
    return differing / (w * h)


def digest(raster: Raster) -> str:
    import hashlib
    return hashlib.sha1(raster.data).hexdigest()[:12]


def thumbnail(path: str, width: int) -> bytes:
    require()
    return _run(["convert", f"{path}[0]", "-resize", f"{width}x", "-strip",
                 "-quality", "78", "jpeg:-"])


def top_band(path: str, depth: int, tolerance: int = 6,
             share: float = 0.08) -> int:
    """How deep the strip at the top of the image is, in pixels.

    Measured by its own background rather than by where the ink stops: the
    canvas starts a few pixels under the last icon, so an ink-gap reading
    counts the top edge of the page as a second row of buttons. Used for both
    "is this the one-row 2.0 toolbar" and "read the zoom off the toolbar" —
    the second of which read 10 % out of a redaction banner until it stopped
    at the band's edge.

    The test is whether the toolbar's background is *present at all* in a row,
    not whether it is the majority of it. In a narrow window the buttons fill
    the bar and leave the background a minority of every row, which is how an
    earlier version reported a four-pixel toolbar on every 800 px capture.
    """
    raster = gray(path)
    depth = min(depth, raster.h)
    base = modal(gray_box(path, (0, 0, raster.w, 4)))
    table = bytes(1 if abs(v - base) <= tolerance else 0 for v in range(256))
    run = 0
    for y in range(depth):
        if sum(raster.row(y).translate(table)) / raster.w < share:
            run += 1
            if run >= 3:
                return y - 2
        else:
            run = 0
    return depth


def column_clusters(path: str, height: int, top: int = 0, join: int = 10,
                    smallest: int = 12) -> list[tuple[int, int]]:
    """The x ranges of the separate things drawn in the top `height` pixels.

    A toolbar OCRs as nothing: tesseract's layout analysis throws away a row of
    icons with three-letter labels under them, and reading the whole band as
    one line returns junk. Cut into one crop per control and read each as a
    single line, the same band gives up "100 %" every time.
    """
    band = gray_box(path, (0, top, size(path)[0], height))
    table = bytes(0 if v in background_values(band, min_share=0.02) else 1
                  for v in range(256))
    ink = band.data.translate(table)
    columns = bytes(1 if any(ink[y * band.w + x] for y in range(band.h)) else 0
                    for x in range(band.w))
    out: list[tuple[int, int]] = []
    for start, length in runs(columns, {1}):
        if out and start - (out[-1][0] + out[-1][1]) < join:
            out[-1] = (out[-1][0], start + length - out[-1][0])
        else:
            out.append((start, length))
    return [c for c in out if c[1] >= smallest]


def ocr(path: str, box: tuple[int, int, int, int] | None = None,
        languages: str = "eng+fra", psm: int = 4) -> str | None:
    """The text tesseract can find, or None if tesseract is not installed.

    None is not an empty string: the caller has to report "not checked" rather
    than "nothing wrong", which is the whole difference between a gate and a
    green tick.
    """
    if not have("tesseract"):
        return None
    require()
    args = [f"{path}[0]"]
    if box:
        x, y, w, h = box
        args += ["-crop", f"{w}x{h}+{x}+{y}", "+repage"]
    # Upscale and threshold: tesseract reads UI text at 12 px badly and at
    # 36 px well, and screenshots are already crisp, so nothing is invented.
    args += ["-resize", "200%", "-colorspace", "Gray", "-depth", "8", "png:-"]
    png = _run(["convert"] + args)
    proc = subprocess.run(
        ["tesseract", "stdin", "stdout", "-l", languages, "--psm", str(psm)],
        input=png, capture_output=True)
    if proc.returncode != 0:
        return ""
    return proc.stdout.decode("utf-8", "replace")
