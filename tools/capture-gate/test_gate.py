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


@unittest.skipUnless(shutil.which("convert"), "ImageMagick is not installed")
class MacTabUnderline(unittest.TestCase):
    """#400: the Mac profile knows the tab strip (#348). Its active-tab
    underline is a thin accent rule under the toolbar, and it is in every
    document pose of the 2.1.1 set; the gate used to read it as a selection
    left on. A rule on those rows passes by name, and accent anywhere else on
    the same image is still a flag."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.shots = os.path.join(self.tmp.name, "en")
        os.makedirs(self.shots)
        rows = stores.STORES["mac"]["tab_underline"]["rows"]
        # As measured on the 2.1.1 set: two rows, from x=13, as wide as the
        # tab's title.
        underline = f"rectangle 13,{rows[0]} 240,{rows[1] - 1}"
        subprocess.run(["convert", "-size", "1440x900", "xc:white",
                        "-fill", stores.ACCENT, "-draw", underline,
                        os.path.join(self.shots, "light-01-viewer.png")],
                       check=True, capture_output=True)
        # The same, plus a selection border around a word on the page.
        subprocess.run(["convert", "-size", "1440x900", "xc:white",
                        "-fill", stores.ACCENT, "-draw", underline,
                        "-fill", "none", "-stroke", stores.ACCENT,
                        "-draw", "rectangle 400,500 520,530",
                        os.path.join(self.shots, "light-02-text.png")],
                       check=True, capture_output=True)

    def _accent(self, name):
        with tempfile.TemporaryDirectory() as out:
            result = gate.run(self.tmp.name, "mac", out, thumb_width=32)
        image = next(i for i in result["images"]
                     if os.path.basename(i["path"]) == name)
        return next(f for f in image["findings"] if f["check"] == "accent")

    def test_the_underline_alone_passes_by_name(self):
        finding = self._accent("light-01-viewer.png")
        self.assertEqual(finding["status"], "pass", finding["note"])
        self.assertIn("active tab's underline", finding["note"])
        self.assertNotIn("elsewhere", finding["note"])

    def test_a_selection_beside_it_is_still_flagged(self):
        finding = self._accent("light-02-text.png")
        self.assertEqual(finding["status"], "flag", finding["note"])
        self.assertIn("elsewhere", finding["note"])


@unittest.skipUnless(shutil.which("convert"), "ImageMagick is not installed")
class LinuxTabUnderline(unittest.TestCase):
    """#310: the Linux profile never got the tab strip's (#348) underline
    rows the way Mac's did (#400), so the 2026-10-01 re-shoot's viewer, text,
    search and sign slots — whose only accent pixels are this underline —
    came back from the gate flagged as a stray selection. Same fixture shape
    as MacTabUnderline, at the Linux listing set's own size and file names
    (`01-viewer` was dropped as a slot by #613; `03-sign` has the same
    underline and nothing else in the accent)."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.shots = os.path.join(self.tmp.name, "en")
        os.makedirs(self.shots)
        rows = stores.STORES["linux"]["tab_underline"]["rows"]
        # As measured on the 2026-10-01 set: two rows, from x=13, as wide as
        # the tab's title (234 px for "Rental Agreement.pdf").
        underline = f"rectangle 13,{rows[0]} 247,{rows[1] - 1}"
        subprocess.run(["convert", "-size", "1280x800", "xc:white",
                        "-fill", stores.ACCENT, "-draw", underline,
                        os.path.join(self.shots, "03-sign.png")],
                       check=True, capture_output=True)
        # The same, plus a selection border around a word on the page.
        subprocess.run(["convert", "-size", "1280x800", "xc:white",
                        "-fill", stores.ACCENT, "-draw", underline,
                        "-fill", "none", "-stroke", stores.ACCENT,
                        "-draw", "rectangle 400,500 520,530",
                        os.path.join(self.shots, "02-text.png")],
                       check=True, capture_output=True)

    def _accent(self, name):
        with tempfile.TemporaryDirectory() as out:
            result = gate.run(self.tmp.name, "linux", out, thumb_width=32)
        image = next(i for i in result["images"]
                     if os.path.basename(i["path"]) == name)
        return next(f for f in image["findings"] if f["check"] == "accent")

    def test_the_underline_alone_passes_by_name(self):
        finding = self._accent("03-sign.png")
        self.assertEqual(finding["status"], "pass", finding["note"])
        self.assertIn("active tab's underline", finding["note"])
        self.assertNotIn("elsewhere", finding["note"])

    def test_a_selection_beside_it_is_still_flagged(self):
        finding = self._accent("02-text.png")
        self.assertEqual(finding["status"], "flag", finding["note"])
        self.assertIn("elsewhere", finding["note"])


@unittest.skipUnless(shutil.which("convert"), "ImageMagick is not installed")
class LinuxReadingPose(unittest.TestCase):
    """#613: reading mode (#505) is a listing slot now, and it is the first
    pose the gate has met that has no chrome at all — no toolbar, no tab
    strip, no status bar. The toolbar check measured the whole 200 px search
    depth as the band and flagged it ("the toolbar band is 200 px; expected
    32-90"). The profile names the pose instead, and the check is inverted
    there: a band found in this pose means reading mode did not turn on and
    the slot is wearing an ordinary viewer shot."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        # Two sets of one image, because both are the *same* slot — the pose a
        # file name parses to is what the profile keys on, so the good and the
        # bad reading shot cannot share a folder without becoming each other's
        # siblings.
        self.good = os.path.join(self.tmp.name, "good", "en")
        self.bad = os.path.join(self.tmp.name, "bad", "en")
        os.makedirs(self.good)
        os.makedirs(self.bad)
        # The mode as it is: the page host to the top of the window, with the
        # page's own ink on it and nothing above it.
        subprocess.run(["convert", "-size", "1280x800", "xc:white",
                        "-fill", "black", "-draw", "rectangle 320,120 740,150",
                        os.path.join(self.good, "01-reading.png")],
                       check=True, capture_output=True)
        # The failure it has to catch: the same slot with the toolbar still on
        # screen — a band of chrome across the top, as every other pose has.
        subprocess.run(["convert", "-size", "1280x800", "xc:white",
                        "-fill", "#cccccc", "-draw", "rectangle 0,0 1279,99",
                        "-fill", "black", "-draw", "rectangle 320,300 740,330",
                        os.path.join(self.bad, "01-reading.png")],
                       check=True, capture_output=True)

    def _toolbar(self, root):
        with tempfile.TemporaryDirectory() as out:
            result = gate.run(root, "linux", out, thumb_width=32)
        image = next(i for i in result["images"]
                     if os.path.basename(i["path"]) == "01-reading.png")
        return next(f for f in image["findings"] if f["check"] == "toolbar")

    def test_no_chrome_is_what_this_pose_is(self):
        finding = self._toolbar(self.good)
        self.assertEqual(finding["status"], "pass", finding["note"])
        self.assertIn("no chrome band", finding["note"])

    def test_chrome_left_on_is_flagged(self):
        """The pose is named in the profile, so the check still has teeth: it
        is the presence of a band that is the defect here, not its height."""
        finding = self._toolbar(self.bad)
        self.assertEqual(finding["status"], "flag", finding["note"])
        self.assertIn("should have none", finding["note"])

    def test_the_pose_name_is_the_one_the_rig_writes(self):
        """tools/linux/store-captures.sh writes 01-reading.png, and the
        profile's exemption is keyed on the parsed pose, not the file name."""
        parsed = stores.STORES["linux"]["parse"]("01-reading.png")
        self.assertIsNotNone(parsed)
        self.assertEqual(parsed[1], "reading")
        self.assertIn(
            "reading",
            stores.STORES["linux"]["toolbar"].get("chromeless_poses", ()))


@unittest.skipUnless(shutil.which("convert"), "ImageMagick is not installed")
class MacReadingPose(unittest.TestCase):
    """#613: reading mode (#505) leads the Mac listing now, and it is the one
    pose with no chrome at all — no toolbar, no tab strip, no status bar. The
    toolbar check measured the whole 160 px search depth as the band. The Mac
    profile takes the rule the Linux profile took for the same slot and the
    same app: the pose is named, and the check is inverted there, because a
    band found here means reading mode did not turn on and slot 1 is wearing an
    ordinary viewer shot."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        # One image per folder: the pose a file name parses to is what the
        # profile keys on, so the good and the bad reading shot cannot share a
        # folder without becoming each other's siblings.
        self.good = os.path.join(self.tmp.name, "good", "en")
        self.bad = os.path.join(self.tmp.name, "bad", "en")
        os.makedirs(self.good)
        os.makedirs(self.bad)
        # The mode as it is: the page host to the top of the window, with the
        # page's own ink on it and nothing above it.
        subprocess.run(["convert", "-size", "1440x900", "xc:white",
                        "-fill", "black", "-draw", "rectangle 360,140 830,170",
                        os.path.join(self.good, "light-01-reading.png")],
                       check=True, capture_output=True)
        # The failure it has to catch: the same slot with the toolbar still on
        # screen — a band of chrome across the top, as every other pose has.
        subprocess.run(["convert", "-size", "1440x900", "xc:white",
                        "-fill", "#cccccc", "-draw", "rectangle 0,0 1439,79",
                        "-fill", "black", "-draw", "rectangle 360,320 830,350",
                        os.path.join(self.bad, "light-01-reading.png")],
                       check=True, capture_output=True)

    def _toolbar(self, root):
        with tempfile.TemporaryDirectory() as out:
            result = gate.run(root, "mac", out, thumb_width=32)
        image = next(i for i in result["images"]
                     if os.path.basename(i["path"]) == "light-01-reading.png")
        return next(f for f in image["findings"] if f["check"] == "toolbar")

    def test_no_chrome_is_what_this_pose_is(self):
        finding = self._toolbar(self.good)
        self.assertEqual(finding["status"], "pass", finding["note"])
        self.assertIn("no chrome band", finding["note"])

    def test_chrome_left_on_is_flagged(self):
        finding = self._toolbar(self.bad)
        self.assertEqual(finding["status"], "flag", finding["note"])
        self.assertIn("should have none", finding["note"])

    def test_the_slot_names_are_the_ones_the_rig_writes(self):
        """tools/macos-store-captures.sh writes light-01-reading.png and
        light-04-pages.png, and the profile's rules are keyed on the parsed
        pose rather than on the file name."""
        profile = stores.STORES["mac"]
        for name, pose in (("light-01-reading.png", "reading"),
                           ("light-04-pages.png", "pages")):
            parsed = profile["parse"](name)
            self.assertIsNotNone(parsed, name)
            self.assertEqual(parsed[1], pose)
        self.assertIn("reading", profile["toolbar"]["chromeless_poses"])
        self.assertIn("pages", profile["accent_poses"])

    def test_the_listing_order_is_the_order_the_rig_shoots(self):
        """The seven slots #613 settled, in order, ahead of the pose that is no
        longer a listing slot."""
        self.assertEqual(
            stores.STORES["mac"]["order"][:7],
            ["reading", "text", "sign", "pages", "search", "redact", "home"])


if __name__ == "__main__":
    unittest.main()
