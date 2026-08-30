#!/usr/bin/env python3
"""Generates MegaPDF.icns from the same geometry as the iOS and Android icons.

The Mac bundle shipped with no CFBundleIconFile at all, so MegaPDF.app showed
the blank generic document in Finder, the Dock and the app switcher (#73).

Why this is not just gen_ios_icon.py with a different filename: **macOS does not
mask app icons.** iOS clips its own squircle, so that generator draws edge to
edge and says so in its docstring. On macOS the rounded shape, the transparent
margin around it and the drop shadow all have to be baked into the 1024 canvas,
or the icon renders square and visibly larger than every neighbour in the Dock.

Apple's grid for a "square" macOS icon: a 1024 canvas, the shape occupying the
middle 824 with a 100px margin all round, corner radius 185.4 (22.5% of 824).
The brand tile's own radius is 58 on 256 — 22.66% — so the shape is the brand
tile at Apple's metrics rather than an approximation of it.

Run this when assets/branding/icon.svg changes; the .icns is committed, the same
way the Android launcher PNGs are, so build-macos-app.sh needs no PIL.

Usage: gen_macos_icon.py [assets/branding/MegaPDF.icns]
"""
import io
import struct
import sys

from PIL import Image, ImageDraw, ImageFilter

# Apple's grid, in canvas units at 1024.
CANVAS = 1024
SHAPE = 824
MARGIN = (CANVAS - SHAPE) // 2
RADIUS = 185.4

S = 4                      # supersample of the 256-unit design space
DESIGN = 256


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def pt(x, y):
    return (x * S, y * S)


def draw_design():
    """The icon.svg design, opaque, at DESIGN*S square. Shared with gen_ios_icon."""
    size = DESIGN * S
    img = Image.new("RGB", (size, size))
    top, bottom = (0x0A, 0x5B, 0xC4), (0x0F, 0xA8, 0xC6)
    px = img.load()
    for y in range(size):
        for x in range(0, size, 8):
            c = lerp(top, bottom, (x + y) / (2 * size))
            for dx in range(8):
                px[x + dx, y] = c
    d = ImageDraw.Draw(img)

    white = (255, 255, 255)
    fold = (0xD6, 0xE7, 0xF8)
    d.rounded_rectangle([pt(78, 52), pt(186, 204)], radius=14 * S, fill=white)
    d.polygon([pt(150, 52), pt(186, 52), pt(186, 88)],
              fill=lerp(top, bottom, (150 + 52) / 512.0))
    d.polygon([pt(150, 52), pt(186, 88), pt(150, 88)], fill=white)
    d.polygon([pt(150, 52), pt(186, 88), pt(164, 88), pt(150, 74)], fill=fold)

    line = (0xC9, 0xDC, 0xEF)
    for x0, x1, y in ((96, 138, 96), (96, 166, 118)):
        d.line([pt(x0, y), pt(x1, y)], fill=line, width=9 * S)
        for x in (x0, x1):
            d.ellipse([x * S - 4 * S, y * S - 4 * S, x * S + 4 * S, y * S + 4 * S], fill=line)

    def cubic(p0, p1, p2, p3, n=64):
        out = []
        for i in range(n + 1):
            t = i / n
            mt = 1 - t
            out.append((
                mt**3 * p0[0] + 3 * mt**2 * t * p1[0] + 3 * mt * t**2 * p2[0] + t**3 * p3[0],
                mt**3 * p0[1] + 3 * mt**2 * t * p1[1] + 3 * mt * t**2 * p2[1] + t**3 * p3[1],
            ))
        return out

    path = cubic((92, 148), (100, 152), (110, 161), (122, 172)) + [(168, 104)]
    c0, c1 = (0x0E, 0x6F, 0xD8), (0x18, 0xB6, 0xC8)
    r = 6 * S
    for i in range(len(path) - 1):
        colour = lerp(c0, c1, i / (len(path) - 1))
        a, b = path[i], path[i + 1]
        d.line([pt(*a), pt(*b)], fill=colour, width=12 * S)
        d.ellipse([a[0] * S - r, a[1] * S - r, a[0] * S + r, a[1] * S + r], fill=colour)
    last = path[-1]
    d.ellipse([last[0] * S - r, last[1] * S - r, last[0] * S + r, last[1] * S + r], fill=c1)
    return img


def build_master():
    """The 1024 canvas: shadow, then the squircle-masked design inside the margin."""
    design = draw_design().resize((SHAPE, SHAPE), Image.LANCZOS)

    mask = Image.new("L", (SHAPE * 4, SHAPE * 4), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, SHAPE * 4 - 1, SHAPE * 4 - 1], radius=int(RADIUS * 4), fill=255)
    mask = mask.resize((SHAPE, SHAPE), Image.LANCZOS)

    canvas = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))

    # A soft drop shadow, so the icon sits beside Apple's own rather than
    # floating flat. Offset down, never sideways — the Dock lights from above.
    shadow = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    shadow.paste((0, 0, 0, 64), (MARGIN, MARGIN + 10), mask)
    canvas = Image.alpha_composite(canvas, shadow.filter(ImageFilter.GaussianBlur(14)))

    tile = Image.new("RGBA", (SHAPE, SHAPE), (0, 0, 0, 0))
    tile.paste(design, (0, 0), mask)
    canvas.alpha_composite(tile, (MARGIN, MARGIN))
    return canvas


# The modern PNG-backed ICNS types. Retina variants are separate entries with
# the same pixel size as a lower nominal one — that is how the format expresses
# @2x, and omitting them leaves Finder scaling a smaller image.
ICNS_TYPES = [
    (b"icp4", 16), (b"icp5", 32), (b"ic11", 32), (b"ic12", 64),
    (b"ic07", 128), (b"ic13", 256), (b"ic08", 256),
    (b"ic14", 512), (b"ic09", 512), (b"ic10", 1024),
]


def write_icns(master, out_path):
    entries = []
    for kind, size in ICNS_TYPES:
        buf = io.BytesIO()
        master.resize((size, size), Image.LANCZOS).save(buf, "PNG")
        data = buf.getvalue()
        entries.append(kind + struct.pack(">I", len(data) + 8) + data)

    body = b"".join(entries)
    with open(out_path, "wb") as f:
        f.write(b"icns" + struct.pack(">I", len(body) + 8) + body)
    print(f"wrote {out_path} ({len(body) + 8} bytes, {len(entries)} representations)")


if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else "assets/branding/MegaPDF.icns"
    write_icns(build_master(), out)
