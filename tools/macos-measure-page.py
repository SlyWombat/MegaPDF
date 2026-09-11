#!/usr/bin/env python3
"""Where the page is in a screenshot of the Mac app's content area.

Prints `left top scale`: the page's left edge and top edge in pixels of the
shot, and pixels per PDF point (page width / 612). The widest white run on
any of several rows is the page (text breaks the runs on some rows, and the
page may be short at a small zoom); the top edge is found up the left margin,
which no text crosses, starting below the toolbar whose light buttons would
otherwise read as page. Reads the PNG through sips, which writes a top-down
BMP — the row order is honoured either way.

    python3 tools/macos-measure-page.py /tmp/shot.png
"""
import struct
import subprocess
import sys

shot = sys.argv[1]
bmp = shot.rsplit(".", 1)[0] + ".bmp"
subprocess.run(["sips", "-s", "format", "bmp", shot, "--out", bmp], capture_output=True, check=True)
d = open(bmp, "rb").read()
off = struct.unpack_from("<I", d, 10)[0]
w = struct.unpack_from("<i", d, 18)[0]
hraw = struct.unpack_from("<i", d, 22)[0]
h = abs(hraw)
bpp = struct.unpack_from("<H", d, 28)[0] // 8
row = ((w * bpp) + 3) // 4 * 4
topdown = hraw < 0


def px(x, y):
    p = off + (y if topdown else h - 1 - y) * row + x * bpp
    return d[p], d[p + 1], d[p + 2]


def white(x, y):
    return min(px(x, y)) >= 250


best = None
for y in range(150, 1000, 50):
    runs, x = [], 0
    while x < w:
        if white(x, y):
            x0 = x
            while x < w and white(x, y):
                x += 1
            runs.append((x0, x))
        x += 1
    if runs:
        r = max(runs, key=lambda r: r[1] - r[0])
        if best is None or r[1] - r[0] > best[1] - best[0]:
            best = r
if best is None or best[1] - best[0] < 400:
    sys.exit(f"no page found in {shot}: widest white run {best}")
left, right = best
top = next(yy for yy in range(70, h) if white(left + 8, yy))
print(left, top, (right - left) / 612.0)
