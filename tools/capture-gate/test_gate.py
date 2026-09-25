#!/usr/bin/env python3
"""Regression tests for the capture gate's file-name parsing and indexing.

Run directly — no pytest, matching the rest of this repo's standalone tool
scripts:

    python3 tools/capture-gate/test_gate.py

Needs ImageMagick (`convert`), same as the gate itself; a test is skipped
rather than failed if it is not on PATH.
"""
from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import gate                                                       # noqa: E402
import stores                                                     # noqa: E402


def _solid(path: str, size: str, colour: str) -> None:
    subprocess.run(["convert", "-size", size, f"xc:{colour}", path],
                   check=True, capture_output=True)


@unittest.skipUnless(shutil.which("convert"), "ImageMagick is not installed")
class LinuxThemeSlot(unittest.TestCase):
    """#251: light and dark must never collide on (lang, device, pose)."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.shots = os.path.join(self.tmp.name, "shots")
        self.reference = os.path.join(self.tmp.name, "reference")
        os.makedirs(self.shots)
        os.makedirs(self.reference)
        # One pose, one width, light and dark side by side in each of two
        # languages — the exact shape the Linux QA rig writes and the issue's
        # own repro. Colours are as far apart as they can be so a mis-pairing
        # is loud: `im.difference` between white and black is ~100 %, not a
        # rounding error near the 0.1 % "certified" threshold, and the two
        # languages share a colour per theme so a correct cross-language
        # comparison reports them identical.
        names = (("doc_en_light_1280.png", "white"),
                 ("doc_en_dark_1280.png", "black"),
                 ("doc_fr-FR_light_1280.png", "white"),
                 ("doc_fr-FR_dark_1280.png", "black"))
        for name, colour in names:
            _solid(os.path.join(self.shots, name), "1280x800", colour)
        # The reference set only needs the "en" pair: `--against` is what
        # #251 is about.
        for name, colour in names[:2]:
            _solid(os.path.join(self.reference, name), "1280x800", colour)

    def test_parse_keeps_light_and_dark_distinct(self):
        """stores.py: the theme is a captured slot, not thrown away."""
        parse = stores.STORES["linux"]["parse"]
        light = parse("doc_en_light_1280.png")
        dark = parse("doc_en_dark_1280.png")
        self.assertIsNotNone(light)
        self.assertIsNotNone(dark)
        # Same (device, pose, lang) either way — that was never the bug...
        self.assertEqual(light, dark)
        # ...which is exactly why `gate.py` cannot key on the parsed triple
        # alone and has to fall back to the measured appearance below.

    def test_against_pairs_each_theme_with_itself(self):
        """gate.py --against: a light shot must be compared with the light
        reference and a dark shot with the dark one, never the other way
        round, even though both parse to the same (lang, device, pose)."""
        with tempfile.TemporaryDirectory() as out:
            result = gate.run(self.shots, "linux", out, thumb_width=32,
                              against=self.reference)
        by_name = {os.path.basename(img["path"]): img
                  for img in result["images"]}
        for name in ("doc_en_light_1280.png", "doc_en_dark_1280.png"):
            findings = {f["check"]: f for f in by_name[name]["findings"]}
            certified = findings.get("certified")
            self.assertIsNotNone(
                certified, f"{name}: no 'certified' finding at all")
            self.assertEqual(
                certified["status"], "pass",
                f"{name}: expected a match against its own theme's "
                f"reference, got {certified['status']!r} — {certified['note']}")

    def test_language_pairs_keeps_themes_separate(self):
        """gate.py's language_pairs() fact line has the same (device, pose)
        keying and the same blind spot if appearance is left out of it: with
        light and dark both filed under "en" and "fr-FR", a key that drops
        appearance collapses the two themes into one, so the pairing sees one
        shared pose instead of two."""
        with tempfile.TemporaryDirectory() as out:
            result = gate.run(self.shots, "linux", out, thumb_width=32)
        self.assertEqual(len(result["pairs"]), 1)
        pair = result["pairs"][0]
        self.assertEqual((pair["a"], pair["b"]), ("en", "fr-FR"))
        # Both themes should be counted (light and dark are each their own
        # shared pose), and both should match — same colour per theme in both
        # languages.
        self.assertEqual(pair["of"], 2,
                         "expected light and dark counted as two distinct "
                         "shared poses, not collapsed into one")
        self.assertEqual(pair["same"], 2)


if __name__ == "__main__":
    unittest.main()
