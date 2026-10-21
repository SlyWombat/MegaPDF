#!/usr/bin/env python3
"""Drift test for the store screenshot slots (#656).

tools/store_slots.py is the one copy of each store's listing order. The capture
gate, the submission tools and the caption tables import it; the capture rigs
(shell and workflow YAML) cannot, so this reads their literal lists and compares
them. Run directly - stdlib only, no ImageMagick, no pytest:

    python3 tools/test_store_slots.py

The Windows rig (tools/screenshots-windows/Shoot-Set.ps1) is deliberately not
covered: msstore_submit.py compares the files on disk with the captions at submit
time and refuses a mismatch.
"""
from __future__ import annotations

import ast
import os
import re
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(HERE, "capture-gate"))

# play_submit.py writes a temp file from PLAY_SA_JSON when it is imported. This
# test never reads or prints either value, so keep them out of the import.
os.environ.pop("PLAY_SA_JSON", None)
os.environ.pop("PLAY_SA_PATH", None)

import store_slots                                                # noqa: E402
import stores                                                     # noqa: E402
import asc_publish                                                # noqa: E402
import play_listing                                               # noqa: E402
import gen_listing_copy as glc                                    # noqa: E402

SCANNED = ["tools/asc_publish.py", "tools/play_listing.py", "tools/gen_listing_copy.py",
           "tools/msstore_submit.py", "tools/capture-gate/stores.py",
           "tools/capture-gate/gate.py", "tools/capture-gate/test_gate.py"]


def _read(rel: str) -> str:
    with open(os.path.join(ROOT, rel), encoding="utf-8") as fh:
        return fh.read()


def _found(rel: str, matches):
    if not matches:
        raise AssertionError(f"{rel}: no slot list found; was the rig rewritten into another "
                             "shape? Update tools/test_store_slots.py to read the new shape.")
    return matches


class StoreSlots(unittest.TestCase):

    def test_every_listing_fits_its_store(self):
        for store, spec in store_slots.SLOTS.items():
            for device in store_slots.devices(store):
                poses = store_slots.listing_poses(store, device)
                with self.subTest(store=store, device=device):
                    self.assertTrue(0 < len(poses) <= spec["max"],
                                    f"{len(poses)} poses, store takes {spec['max']}")
                    self.assertEqual(len(poses), len(set(poses)), "duplicate pose")
                    self.assertFalse(set(poses) & set(spec["extra"]),
                                     "an extra pose is also a listing pose")

    def test_the_ios_devices_show_the_same_poses_in_a_different_order(self):
        phone = store_slots.listing_poses("ios", "iphone-6_9")
        pad = store_slots.listing_poses("ios", "ipad-13")
        self.assertEqual(set(phone), set(pad))
        self.assertNotEqual(phone, pad)
        # Two sequences stay a {device: [...]} dict at the gate, which order_key reads.
        self.assertIsInstance(store_slots.gate_order("ios"), dict)

    def test_the_gate_profiles_read_the_shared_order(self):
        for store in store_slots.SLOTS:
            with self.subTest(store=store):
                self.assertEqual(stores.STORES[store]["order"], store_slots.gate_order(store))
                for device in store_slots.devices(store):
                    self.assertIn(device, stores.STORES[store]["slots"])

    def test_every_listing_file_parses_back_to_its_pose(self):
        for store in store_slots.SLOTS:
            parse = stores.STORES[store]["parse"]
            for device in store_slots.devices(store):
                poses = store_slots.listing_poses(store, device)
                files = store_slots.listing_files(store, device)
                for pose, name in zip(poses, files):
                    with self.subTest(store=store, device=device, file=name):
                        got = parse(name)
                        self.assertIsNotNone(got, f"the gate does not read {name}")
                        self.assertEqual(got[1], pose)
                        if store == "ios":
                            self.assertEqual(got[0], device)

    def test_the_submission_tools_name_the_shared_devices(self):
        for store in ("mac", "ios"):
            self.assertEqual(set(asc_publish.SHOT_SETS_BY_STORE[store]),
                             set(store_slots.devices(store)), store)
        self.assertEqual(set(play_listing.IMAGE_TYPES), set(store_slots.devices("play")))

    def test_play_listing_wants_exactly_the_listing_files(self):
        names = store_slots.listing_files("play", "phone")
        with tempfile.TemporaryDirectory() as tmp:
            folder = os.path.join(tmp, "phone", "en")
            os.makedirs(folder)
            for name in names:
                open(os.path.join(folder, name), "wb").close()
            self.assertEqual(play_listing.shots(tmp, "phone", "en"),
                             [os.path.join(folder, n) for n in names])
            os.remove(os.path.join(folder, names[3]))
            with self.assertRaises(SystemExit):
                play_listing.shots(tmp, "phone", "en")

    def test_the_captions_cover_exactly_the_listing(self):
        ms = set(store_slots.listing_poses("microsoft", "desktop"))
        self.assertEqual(set(glc.MS_EN["captions"]), ms)
        self.assertEqual(set(glc.MS_FR_CA["captions"]), ms)
        for table in (glc.AS_EN, glc.AS_FR_CA):
            self.assertEqual(set(table["captions_iphone"]),
                             set(store_slots.listing_poses("ios", "iphone-6_9")))
            self.assertEqual(set(table["captions_ipad"]),
                             set(store_slots.listing_poses("ios", "ipad-13")))

    def test_the_capture_rigs_shoot_the_shared_order(self):
        play = store_slots.listing_poses("play", "phone")
        ios = [store_slots.listing_poses("ios", d) for d in ("iphone-6_9", "ipad-13")]

        rel = "android/scripts/capture-screenshots.sh"
        hits = _found(rel, re.findall(r'^for state in ([a-z -]+); do$', _read(rel), re.M))
        self.assertEqual(len(hits), 1, rel)
        self.assertEqual(hits[0].split(), play, rel)

        rel = "tools/ios-screenshots.sh"
        hits = _found(rel, re.findall(r'STATES=\(([^)]*)\)', _read(rel)))
        self.assertEqual([h.split() for h in hits], ios, rel)

        rel = ".github/workflows/ios-screenshots.yml"
        hits = _found(rel, re.findall(r'(iphone-6_9|ipad-13)\)\s+STATES=\(([^)]*)\)', _read(rel)))
        self.assertEqual({d for d, _ in hits}, {"iphone-6_9", "ipad-13"}, rel)
        for device, poses in hits:
            self.assertEqual(poses.split(),
                             store_slots.listing_poses("ios", device) + store_slots.SLOTS["ios"]["extra"],
                             f"{rel} {device}")

        rel = "tools/macos-store-captures.sh"
        text = _read(rel)
        want = [f"{n:02d}-{p}" for n, p in
                enumerate(store_slots.listing_poses("mac", "desktop"), 1)]
        self.assertEqual(_found(rel, re.findall(r'^shoot (\d\d-[a-z-]+)\s', text, re.M)), want, rel)
        block = _found(rel, re.findall(r'^slots = \[([^\]]*)\]', text, re.M))
        self.assertEqual(re.findall(r'"([^"]+)"', block[0]), want, rel + " check heredoc")

    def test_no_tool_keeps_its_own_copy_of_an_order(self):
        poses = {p for spec in store_slots.SLOTS.values()
                 for seq in list(spec["listing"].values()) + [spec["extra"]] for p in seq}
        norm = re.compile(r'^(light-)?\d\d-|\.png$')
        hits = []
        for rel in SCANNED:
            tree = ast.parse(_read(rel), filename=rel)
            for node in ast.walk(tree):
                if not isinstance(node, (ast.List, ast.Tuple, ast.Set)) or len(node.elts) < 5:
                    continue
                if not all(isinstance(e, ast.Constant) and isinstance(e.value, str)
                           for e in node.elts):
                    continue
                if all(norm.sub("", e.value) in poses for e in node.elts):
                    hits.append(f"{rel}:{node.lineno}")
        self.assertEqual(hits, [], "a pose list outside tools/store_slots.py: " + ", ".join(hits))


if __name__ == "__main__":
    unittest.main()
