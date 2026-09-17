#!/usr/bin/env python3
"""Cross-checks the capture matrix, so the eye is spent where it is needed.

Two comparisons no one should make by hand across 27 cells:

* **light vs dark.** The app is light-only on purpose (`ui/Brand.kt`, #40), so a
  dark capture should differ from its light twin only where the system draws —
  the pickers, toasts and the soft keyboard. A screen of the app's own that
  changes in dark mode is a defect; one that does not is the design working.

* **English vs French.** A French capture that is pixel-identical to its English
  twin is a screen that did not translate. (The reverse is not a defect: two
  screens can differ only in a date format.)

    python3 compare.py /work/out/shots

Prints a table and writes `compare.json` beside the shots.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
from collections import defaultdict


def cells(root: str) -> dict[str, dict[str, str]]:
    """{cell -> {scene -> path}}"""
    out: dict[str, dict[str, str]] = defaultdict(dict)
    for cell in sorted(os.listdir(root)):
        folder = os.path.join(root, cell)
        if not os.path.isdir(folder):
            continue
        for name in sorted(os.listdir(folder)):
            if not name.endswith(".png"):
                continue
            scene = name[len(cell) + 2:-4] if name.startswith(cell) else name[:-4]
            out[cell][scene] = os.path.join(folder, name)
    return out


def difference(a: str, b: str) -> float | None:
    """Fraction of pixels that differ, or None if the two cannot be compared."""
    proc = subprocess.run(
        ["compare", "-metric", "AE", "-fuzz", "1%", a, b, "null:"],
        capture_output=True)
    text = proc.stderr.decode(errors="replace").strip().split()
    if not text:
        return None
    try:
        pixels = float(text[0].replace(",", ""))
    except ValueError:
        return None
    size = subprocess.run(["identify", "-format", "%w %h", a],
                          capture_output=True).stdout.decode().split()
    if len(size) != 2:
        return None
    return pixels / (int(size[0]) * int(size[1]))


def main() -> int:
    root = sys.argv[1] if len(sys.argv) > 1 else "/work/out/shots"
    found = cells(root)
    report: dict[str, list] = {"dark_differs": [], "not_translated": [], "missing": []}

    for cell, scenes in sorted(found.items()):
        if "__light__" not in cell:
            continue
        dark = cell.replace("__light__", "__dark__")
        if dark in found:
            for scene, path in sorted(scenes.items()):
                twin = found[dark].get(scene)
                if not twin:
                    report["missing"].append({"cell": dark, "scene": scene})
                    continue
                delta = difference(path, twin)
                if delta is not None and delta > 0.002:
                    report["dark_differs"].append(
                        {"cell": cell, "scene": scene, "fraction": round(delta, 5)})

    for cell, scenes in sorted(found.items()):
        if "__en__" not in cell:
            continue
        for lang in ("fr-CA", "fr-FR"):
            french = cell.replace("__en__", f"__{lang}__")
            if french not in found:
                continue
            for scene, path in sorted(scenes.items()):
                twin = found[french].get(scene)
                if not twin:
                    report["missing"].append({"cell": french, "scene": scene})
                    continue
                delta = difference(path, twin)
                if delta is not None and delta < 0.0005:
                    report["not_translated"].append(
                        {"cell": french, "scene": scene, "fraction": round(delta, 6)})

    with open(os.path.join(root, "compare.json"), "w") as handle:
        json.dump(report, handle, indent=2)

    print(f"cells: {len(found)}")
    print(f"\ndark differs from light in {len(report['dark_differs'])} screens "
          "(expected: only where the system draws)")
    for row in report["dark_differs"]:
        print(f"  {row['cell']:34s} {row['scene']:34s} {row['fraction']:.4f}")
    print(f"\nFrench identical to English in {len(report['not_translated'])} screens "
          "(expected: only screens with no words of ours)")
    for row in report["not_translated"]:
        print(f"  {row['cell']:34s} {row['scene']}")
    print(f"\nmissing captures: {len(report['missing'])}")
    for row in report["missing"][:40]:
        print(f"  {row['cell']:34s} {row['scene']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
