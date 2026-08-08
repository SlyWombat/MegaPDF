#!/usr/bin/env python3
"""Renders assets/branding/icon.svg's design as the 1024x1024 iOS app icon.

Faithful PIL re-draw of the SVG geometry (scaled 256->1024, supersampled 2x):
diagonal gradient tile, white document with folded corner, two text lines,
and the calligraphic check stroke. Output is opaque (iOS masks its own
corners). Usage: gen_ios_icon.py <out.png>
"""
import sys

from PIL import Image, ImageDraw


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


S = 8  # 256 -> 2048 supersample, downscaled to 1024


def pt(x, y):
    return (x * S, y * S)


def main(out_path):
    size = 256 * S
    img = Image.new("RGB", (size, size))
    top, bottom = (0x0A, 0x5B, 0xC4), (0x0F, 0xA8, 0xC6)
    px = img.load()
    for y in range(size):
        for x in range(0, size, 8):  # gradient varies slowly; fill 8-px runs
            t = (x + y) / (2 * size)
            c = lerp(top, bottom, t)
            for dx in range(8):
                px[x + dx, y] = c
    d = ImageDraw.Draw(img)

    # Document body (rounded rect 78..186 x 52..204, radius 14) + fold
    white = (255, 255, 255)
    fold = (0xD6, 0xE7, 0xF8)
    d.rounded_rectangle([pt(78, 52), pt(186, 204)], radius=14 * S, fill=white)
    # Fold: notch the top-right corner then draw the fold triangle
    d.polygon([pt(150, 52), pt(186, 52), pt(186, 88)],
              fill=lerp(top, bottom, (150 + 52) / 512.0))
    d.polygon([pt(150, 52), pt(186, 88), pt(150, 88)], fill=white)
    d.polygon([pt(150, 52), pt(186, 88), pt(164, 88), pt(150, 74)], fill=fold)

    # Text lines
    line = (0xC9, 0xDC, 0xEF)
    for x0, x1, y in ((96, 138, 96), (96, 166, 118)):
        d.line([pt(x0, y), pt(x1, y)], fill=line, width=9 * S)
        d.ellipse([x0 * S - 4 * S, y * S - 4 * S, x0 * S + 4 * S, y * S + 4 * S], fill=line)
        d.ellipse([x1 * S - 4 * S, y * S - 4 * S, x1 * S + 4 * S, y * S + 4 * S], fill=line)

    # Check stroke: cubic (92,148) c(100,152)(110,161)(122,172) then to (168,104)
    def cubic(p0, p1, p2, p3, n=64):
        pts = []
        for i in range(n + 1):
            t = i / n
            mt = 1 - t
            x = mt**3 * p0[0] + 3 * mt**2 * t * p1[0] + 3 * mt * t**2 * p2[0] + t**3 * p3[0]
            y = mt**3 * p0[1] + 3 * mt**2 * t * p1[1] + 3 * mt * t**2 * p2[1] + t**3 * p3[1]
            pts.append((x, y))
        return pts

    path = cubic((92, 148), (100, 152), (110, 161), (122, 172)) + [(168, 104)]
    c0, c1 = (0x0E, 0x6F, 0xD8), (0x18, 0xB6, 0xC8)
    total = len(path) - 1
    r = 6 * S
    for i in range(total):
        t = i / total
        color = lerp(c0, c1, t)
        a, b = path[i], path[i + 1]
        d.line([pt(*a), pt(*b)], fill=color, width=12 * S)
        d.ellipse([a[0] * S - r, a[1] * S - r, a[0] * S + r, a[1] * S + r], fill=color)
    last = path[-1]
    d.ellipse([last[0] * S - r, last[1] * S - r, last[0] * S + r, last[1] * S + r],
              fill=c1)

    img = img.resize((1024, 1024), Image.LANCZOS)
    img.save(out_path, "PNG")
    print("wrote", out_path)


if __name__ == "__main__":
    main(sys.argv[1])
