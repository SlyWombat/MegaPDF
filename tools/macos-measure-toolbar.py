#!/usr/bin/env python3
"""Where the Mac app's toolbar buttons are in a shot of its content area.

Prints one line — `y x0 x1 x2 …` — the row to click on and the centre of every
button in the one-row 2.0 toolbar, left to right, in pixels of the shot:

    Open  Save │ Sign  Add text  Cover  Redact │ Undo  Redo │ −  100%  +  ⋯

    python3 tools/macos-measure-toolbar.py /tmp/shot.png

Measured rather than written down, because the two things that move these are
both routine. The toolbar was redesigned for 2.0 (#144) and every coordinate
written into `tools/macos-record-demo.sh` for the old one pointed at empty bar.
And the labels are translated, so *Ajouter du texte* is nearly twice the width
of *Add text* and everything to its right shifts — a French clip driven by
English coordinates clicks the wrong buttons. The **order** is the same in
every language, so the caller indexes rather than names.

A button is a light pill, a few levels darker than the bar it sits on. The scan
stops at the first gap wider than 100 px: the groups are separated by about 18,
and anything past that gap is not the toolbar — a notification banner over the
right of the window reads as a perfectly good pill otherwise.

Reads the PNG through sips, which writes a top-down BMP, as
`macos-measure-page.py` does.
"""
import struct
import subprocess
import sys

BAND = 46          # the toolbar's own height; the canvas starts below it
MIN_WIDTH = 16     # narrower than the narrowest button (the zoom steppers)
MAX_GAP = 100      # wider than any gap between groups (measured: 18 px)

shot = sys.argv[1]
bmp = shot.rsplit(".", 1)[0] + "-toolbar.bmp"
subprocess.run(["sips", "-s", "format", "bmp", shot, "--out", bmp],
               capture_output=True, check=True)
d = open(bmp, "rb").read()
off = struct.unpack_from("<I", d, 10)[0]
w = struct.unpack_from("<i", d, 18)[0]
hraw = struct.unpack_from("<i", d, 22)[0]
h = abs(hraw)
bpp = struct.unpack_from("<H", d, 28)[0] // 8
row = ((w * bpp) + 3) // 4 * 4
topdown = hraw < 0


def grey(x, y):
    p = off + (y if topdown else h - 1 - y) * row + x * bpp
    return (d[p] + d[p + 1] + d[p + 2]) // 3


band = min(BAND, h)
column = [sum(grey(x, y) for y in range(band)) / band for x in range(w)]
bar = sorted(column)[w // 2]

runs, start = [], None
for x in range(w):
    pill = column[x] < bar - 2
    if pill and start is None:
        start = x
    elif not pill and start is not None:
        if x - start >= MIN_WIDTH:
            runs.append((start, x - 1))
        start = None
if start is not None and w - start >= MIN_WIDTH:
    runs.append((start, w - 1))

kept = []
for run in runs:
    if kept and run[0] - kept[-1][1] > MAX_GAP:
        break
    kept.append(run)
if len(kept) < 4:
    sys.exit(f"no toolbar found in {shot}: {len(kept)} button(s) on a bar at "
             f"grey {bar:.0f}")

# The row to click: the middle of the pills, found down the first button.
first = (kept[0][0] + kept[0][1]) // 2
rows = [y for y in range(band) if grey(first, y) < bar - 2]
print((rows[0] + rows[-1]) // 2 if rows else band // 2,
      *[(a + b) // 2 for a, b in kept])
