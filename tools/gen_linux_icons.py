#!/usr/bin/env python3
"""Generates the Linux hicolor icon set from the same geometry as the iOS,
Android and macOS icons (#158).

Why not just scale one PNG down: at 16 and 24 pixels the check stroke and the
two text lines turn to mush, because a LANCZOS downscale of a 512 render has no
idea which strokes carry the meaning. Every size is drawn at its own scale from
the design geometry, supersampled and reduced once — the same approach
gen_android_icons.py takes for the launcher densities, and for the same reason.

**Not the macOS grid.** gen_macos_icon.py bakes a 100/1024 transparent margin and
a drop shadow into the canvas, because macOS neither masks nor shadows app icons.
Freedesktop is the other way round: the panel, the dock and the app grid each add
their own spacing, so a baked-in margin renders the icon visibly smaller than its
neighbours. These are drawn edge to edge, with the tile's own rounded corners and
transparency outside them.

The PNGs are committed, the way the Android launcher icons and MegaPDF.icns are,
so tools/build-linux-app.sh needs no PIL. Regenerate when the branding changes:

    python3 tools/gen_linux_icons.py [assets/branding/linux]
"""
import sys
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent

# The freedesktop icon theme sizes an application is expected to provide. 16 and
# 24 are the ones that actually get used most — window title bars, the task bar,
# the Open With menu — and they are the ones a naive downscale ruins.
SIZES = (16, 22, 24, 32, 48, 64, 128, 256, 512)

DESIGN = 256                       # the design space icon.svg is drawn in
TILE_RADIUS = 58                   # the brand tile's corner radius at 256
TOP, BOTTOM = (0x0A, 0x5B, 0xC4), (0x0F, 0xA8, 0xC6)


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def draw(size):
    """The icon at `size` px, RGBA, transparent outside the rounded tile."""
    # Supersample enough that even 16px gets a smooth curve, without drawing a
    # 4096-wide canvas for the 512 (which is slow and gains nothing).
    s = max(4, min(16, 512 // size))
    canvas = size * s
    scale = canvas / DESIGN

    def pt(x, y):
        return (x * scale, y * scale)

    # The gradient, then the tile shape as a mask over it: drawing the gradient
    # into a rounded rectangle directly would leave the corners aliased against
    # transparency rather than anti-aliased into it.
    gradient = Image.new("RGB", (canvas, canvas))
    px = gradient.load()
    for y in range(canvas):
        for x in range(0, canvas, 8):
            c = lerp(TOP, BOTTOM, (x + y) / (2 * canvas))
            for dx in range(min(8, canvas - x)):
                px[x + dx, y] = c

    mask = Image.new("L", (canvas, canvas), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, canvas - 1, canvas - 1], radius=TILE_RADIUS * scale, fill=255)

    img = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    img.paste(gradient, (0, 0), mask)
    d = ImageDraw.Draw(img)

    # Document body (rounded rect 78..186 x 52..204, radius 14) and its folded
    # corner — the same numbers as gen_ios_icon.py, which is the point.
    white = (255, 255, 255, 255)
    fold = (0xD6, 0xE7, 0xF8, 255)
    d.rounded_rectangle([pt(78, 52), pt(186, 204)], radius=14 * scale, fill=white)
    d.polygon([pt(150, 52), pt(186, 52), pt(186, 88)],
              fill=lerp(TOP, BOTTOM, (150 + 52) / 512.0) + (255,))
    d.polygon([pt(150, 52), pt(186, 88), pt(150, 88)], fill=white)
    d.polygon([pt(150, 52), pt(186, 88), pt(164, 88), pt(150, 74)], fill=fold)

    # The two text lines. Below 32px they are drawn thicker rather than dropped:
    # a document with nothing on it reads as a blank page, not as a document.
    line = (0xC9, 0xDC, 0xEF, 255)
    width = max(1, round(9 * scale)) if size >= 32 else max(1, round(13 * scale))
    for x0, x1, y in ((96, 138, 96), (96, 166, 118)):
        d.line([pt(x0, y), pt(x1, y)], fill=line, width=width)

    # The check stroke.
    def cubic(p0, p1, p2, p3, n=64):
        pts = []
        for i in range(n + 1):
            t = i / n
            mt = 1 - t
            pts.append((
                mt**3 * p0[0] + 3 * mt**2 * t * p1[0] + 3 * mt * t**2 * p2[0] + t**3 * p3[0],
                mt**3 * p0[1] + 3 * mt**2 * t * p1[1] + 3 * mt * t**2 * p2[1] + t**3 * p3[1],
            ))
        return pts

    path = cubic((92, 148), (100, 152), (110, 161), (122, 172)) + [(168, 104)]
    c0, c1 = (0x0E, 0x6F, 0xD8), (0x18, 0xB6, 0xC8)
    stroke = max(2, round(12 * scale))
    cap = stroke / 2
    for i in range(len(path) - 1):
        colour = lerp(c0, c1, i / (len(path) - 1)) + (255,)
        a, b = path[i], path[i + 1]
        d.line([pt(*a), pt(*b)], fill=colour, width=stroke)
        ax, ay = pt(*a)
        d.ellipse([ax - cap, ay - cap, ax + cap, ay + cap], fill=colour)
    lx, ly = pt(*path[-1])
    d.ellipse([lx - cap, ly - cap, lx + cap, ly + cap], fill=c1 + (255,))

    return img.resize((size, size), Image.LANCZOS)


def main(out_dir):
    out = Path(out_dir)
    for size in SIZES:
        target = out / "hicolor" / f"{size}x{size}" / "apps" / "megapdf.png"
        target.parent.mkdir(parents=True, exist_ok=True)
        draw(size).save(target, "PNG", optimize=True)
        print(f"wrote {target.relative_to(ROOT) if target.is_relative_to(ROOT) else target}")

    # The scalable entry is the brand SVG itself: every renderer that reads this
    # directory can rasterise it, and keeping a copy here means the installed
    # theme is complete without the build script reaching back into assets/.
    scalable = out / "hicolor" / "scalable" / "apps" / "megapdf.svg"
    scalable.parent.mkdir(parents=True, exist_ok=True)
    scalable.write_bytes((ROOT / "assets/branding/icon.svg").read_bytes())
    print(f"wrote {scalable.relative_to(ROOT) if scalable.is_relative_to(ROOT) else scalable}")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else ROOT / "assets/branding/linux")
