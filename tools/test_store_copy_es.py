#!/usr/bin/env python3
"""Tests for the Spanish store copy and the tools that carry it (#724).

Run directly, standard library only:

    python3 tools/test_store_copy_es.py

What it holds in place:
  * every Spanish store field, in the Latin American copy and in the Spain copy derived
    from it, follows the Spanish typography (no space before : ; ? !, inverted marks
    balanced, no guillemets) and says nothing the glossary forbids;
  * the Apple descriptions name no other platform (Apple rejects one that does);
  * the submission tools know the Spanish locales, and map each to the folder the
    captures are kept in (App Store Connect has no es-419; Partner Center writes es-mx);
  * tools/play_listing.py reads the es-419 section (its locale is three digits, not two
    letters) and refuses release notes that are still PENDING;
  * the Linux metainfo carries the Spanish name, summary, captions and images.
"""
import os
import re
import shutil
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(HERE, "capture-gate"))

# play_submit.py writes a temp file from PLAY_SA_JSON when it is imported; this test
# never reads or prints either value, so keep them out of the import.
os.environ.pop("PLAY_SA_JSON", None)
os.environ.pop("PLAY_SA_PATH", None)

import asc_publish          # noqa: E402
import gen_listing_copy as glc   # noqa: E402
import msstore_submit       # noqa: E402
import play_listing         # noqa: E402

SPACE_BEFORE = re.compile("[   ]+[:;?!]")
OTHER_PLATFORMS = re.compile(r"Android|Windows|Linux|Google Play|Microsoft", re.I)


def texts(copy):
    """Every string in a copy dictionary, with the name of the field it is in."""
    for key, value in copy.items():
        if isinstance(value, str):
            yield key, value
        elif isinstance(value, dict):
            for sub, text in value.items():
                yield f"{key}.{sub}", text
        else:
            for i, text in enumerate(value):
                yield f"{key}[{i}]", text


def variants():
    """(label, copy) for every Spanish dictionary, es-419 and the Spain derivation."""
    for label, copy in (("MS", glc.MS_ES_419), ("AS", glc.AS_ES_419),
                        ("Mac", glc.AS_MAC_ES_419), ("Play", glc.PLAY_ES_419)):
        yield f"{label} es-419", copy
        yield f"{label} es-ES", glc.spain(copy)


class SpanishCopy(unittest.TestCase):
    def test_typography(self):
        for label, copy in variants():
            for field, text in texts(copy):
                if field.startswith("notes"):   # a PENDING line until the release notes exist
                    continue
                with self.subTest(label=label, field=field):
                    self.assertIsNone(SPACE_BEFORE.search(text), text)
                    self.assertEqual(text.count("¿"), text.count("?"), text)
                    self.assertEqual(text.count("¡"), text.count("!"), text)
                    self.assertNotIn("«", text)
                    self.assertNotIn("»", text)

    def test_no_cipher_words_for_signatures(self):
        # A signature encrypts nothing, and the store copy must not say it does (glossary).
        for label, copy in variants():
            for field, text in texts(copy):
                with self.subTest(label=label, field=field):
                    self.assertIsNone(re.search(r"cifrad|cifrar|encript", text, re.I), text)

    def test_apple_descriptions_name_no_other_platform(self):
        for label, copy in variants():
            if label.startswith(("AS ", "Mac ")):
                for field in ("description", "promo"):
                    with self.subTest(label=label, field=field):
                        self.assertIsNone(OTHER_PLATFORMS.search(copy[field]), copy[field])

    def test_spain_is_derived_not_hand_edited(self):
        self.assertEqual(glc.spain({"a": "Llena el formulario"})["a"], "Rellena el formulario")
        self.assertEqual(glc.spain({"a": "tu computadora"})["a"], "tu ordenador")
        # and applies no French or Spanish punctuation pass: nothing is inserted
        self.assertEqual(glc.spain({"a": "¿Listo? ¡Sí!"})["a"], "¿Listo? ¡Sí!")

    def test_limits_hold_for_both_variants(self):
        failed = [name for ok, name in glc.LIMITS_OK if not ok]
        self.assertEqual(failed, [])
        self.assertTrue(any("(es-ES)" in name for _, name in glc.LIMITS_OK))


class Tools(unittest.TestCase):
    def test_app_store_connect_locales(self):
        self.assertIn("es-MX", asc_publish.LOCALES)
        self.assertIn("es-ES", asc_publish.LOCALES)
        # No es-419 in App Store Connect, but the capture folder keeps that name.
        self.assertEqual(asc_publish.REPO_LOCALE["es-MX"], "es-419")
        self.assertEqual(asc_publish.REPO_LOCALE["es-ES"], "es")

    def test_partner_center_locales(self):
        self.assertEqual(msstore_submit.LANGS["es-mx"], {"shots": "es-419", "doc": "es-MX"})
        self.assertEqual(msstore_submit.LANGS["es-es"], {"shots": "es-ES", "doc": "es-ES"})

    def test_play_locales(self):
        self.assertEqual(play_listing.LOCALES["es-419"], "es-419")
        self.assertEqual(play_listing.LOCALES["es-ES"], "es-ES")

    def _play_doc(self, notes):
        """A minimal android/RELEASING.md with the five Play locales, for copy_by_language."""
        def section(tag, notes_text):
            return (f"### X — `{tag}`\n\n**Title** [30] (1)\n```\nT\n```\n"
                    f"**Short description** [80] (1)\n```\nS\n```\n"
                    f"**Full description** [4000] (1)\n```\nF\n```\n"
                    f"**Release notes** [500] (1)\n```\n{notes_text}\n```\n")
        body = "\n".join(section(t, notes if t.startswith("es") else "ok")
                         for t in ("en-CA", "fr-CA", "fr-FR", "es-419", "es-ES"))
        root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, root, True)
        os.makedirs(os.path.join(root, "android"))
        with open(os.path.join(root, "android", "RELEASING.md"), "w", encoding="utf-8") as f:
            f.write("<!-- copy-by-language -->\n" + body + "\n<!-- /copy-by-language -->\n")
        return root

    def test_play_reads_the_three_digit_locale(self):
        old = play_listing.ROOT
        play_listing.ROOT = self._play_doc("Notas")
        try:
            copy = play_listing.copy_by_language()
        finally:
            play_listing.ROOT = old
        self.assertEqual(copy["es-419"]["notes"], "Notas")
        self.assertEqual(copy["es-ES"]["title"], "T")

    def test_play_refuses_pending_notes(self):
        old = play_listing.ROOT
        play_listing.ROOT = self._play_doc("PENDING: written at release time")
        try:
            with self.assertRaises(SystemExit):
                play_listing.copy_by_language()
        finally:
            play_listing.ROOT = old


class Metainfo(unittest.TestCase):
    XML_LANG = "{http://www.w3.org/XML/1998/namespace}lang"

    @classmethod
    def setUpClass(cls):
        path = os.path.join(ROOT, "tools", "linux", "flatpak", "ca.electricrv.MegaPDF.metainfo.xml")
        cls.root = ET.parse(path).getroot()

    def langs(self, nodes):
        return {n.get(self.XML_LANG) for n in nodes}

    def test_name_and_summary(self):
        for tag in ("name", "summary"):
            self.assertTrue({"es", "es-ES"} <= self.langs(self.root.findall(tag)), tag)

    def test_every_screenshot_has_spanish_images_and_captions(self):
        shots = self.root.findall("./screenshots/screenshot")
        self.assertEqual(len(shots), 7)
        for shot in shots:
            self.assertTrue({"es", "es-ES"} <= self.langs(shot.findall("caption")))
            images = {i.get(self.XML_LANG): i.text for i in shot.findall("image")}
            self.assertIn("/linux/es-419/", images["es"])
            self.assertIn("/linux/es-ES/", images["es-ES"])


if __name__ == "__main__":
    unittest.main()
