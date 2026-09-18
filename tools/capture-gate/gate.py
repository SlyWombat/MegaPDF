#!/usr/bin/env python3
"""The capture gate: read a shot set, check it, and print what to look at.

    python3 tools/capture-gate/gate.py --store mac artifacts/store/mac -o /tmp/gate

Writes a contact sheet (`sheet-<store>.html`) and `results.json` beside each
other in the output directory, and prints a verdict per language set. The sheet
is one page per store: every image in listing order, each with its slot, its
pixel size, its pose and its check results, and each one tappable for the full
image.

The point is to make the final capture review a confirmation rather than a
hunt. The tool cannot tell whether a set looks inviting, and it says so; what
it can do is measure the things a person is bad at measuring across sixty
images — sizes, clipping, chrome left on, a pose that moved, a name in the
wrong language — so the only images anyone opens are the ones it could not
clear.

Nothing here uploads anything, and nothing is written into the set: the sheet
and its thumbnails go to the output directory, which should not be the
captures' own folder if that folder is about to be zipped for a store.
"""
from __future__ import annotations

import argparse
import base64
import collections
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import checks                                                    # noqa: E402
import im                                                        # noqa: E402
import sheet as sheet_mod                                        # noqa: E402
import stores                                                    # noqa: E402


class Shot:
    """One capture, its measurements, and what the checks said about it."""

    def __init__(self, path, lang, device, pose, profile):
        self.path = path
        self.name = os.path.basename(path)
        self.lang = lang
        self.device = device
        self.pose = pose
        self.profile = profile
        self.findings: list[checks.Finding] = []
        self.size = im.size(path)
        raster = im.gray(path)
        # Blocks for the cross-language comparison close gaps of about 0.7 % of
        # the height, so that a line of text sitting a few pixels closer to the
        # one under it in French does not read as a different layout.
        self.blocks = im.blocks(im.ink_profile(raster), 0.004,
                                gap=max(2, round(self.size[1] * 0.007)))
        # One byte per row: is there ink on it? This is the shape of the
        # screen with the words taken out, and it is what the cross-language
        # comparison works on.
        self.ink_rows = bytes(1 if v >= 0.004 else 0
                              for v in im.ink_profile(raster))
        self.digest = im.digest(raster)
        self.accent_px = im.matched(im.colour_mask(path, stores.ACCENT))
        spec_band = profile.get("status_band") or {}
        band = spec_band.get("by_device", {}).get(
            device, spec_band.get("height_frac"))
        w, h = self.size
        if band:
            # Flat on a screenshot, smeared on a still cut out of a clip: the
            # video profile widens each background tone so that H.264 ringing
            # along the clock does not read as ink (im.ink_mask).
            tolerance = profile.get("ink_tolerance", 0)
            crop = im.gray_box(path, (0, 0, w, max(1, int(h * band))))
            self.status_ink = im.ink_mask(crop, tolerance=tolerance)
            right = im.gray_box(path, (int(w * 0.6), 0, w - int(w * 0.6),
                                       max(1, int(h * band))))
            self.status_right = im.ink_mask(right, tolerance=tolerance)
            # Which flat tone the bar is drawn on. The clock and the battery
            # are the same ink over a white list and over a grey toolbar, but
            # their antialiasing is not, so only bars on the same background
            # are worth comparing inside one language.
            self.status_bg = im.modal(crop) // 8
        else:
            self.status_ink = None
            self.status_right = None
            self.status_bg = None
        top = im.gray_box(path, (0, 0, w, max(1, int(h * 0.06))))
        self.top_mean = sum(top.data) / max(1, len(top.data))
        # The lightest tone the top of the window is *painted* in — the
        # toolbar's own background, in practice. Not the average, which a
        # narrow French toolbar changes by dropping its labels, and not the
        # most common tone, which flips between the bar and its buttons when
        # the buttons get wider. A modal dims every tone, so the lightest one
        # moving is the thing worth noticing.
        hist = im.histogram(top)
        floor = top.w * top.h * 0.1
        flats = [v for v, count in enumerate(hist) if count >= floor]
        self.top_tone = max(flats) if flats else im.modal(top)
        spec = profile.get("toolbar")
        # Where the toolbar's background runs on into the canvas (Mica on
        # Windows), there is no edge to find and the profile gives the depth.
        self.toolbar_end = (None if not spec else spec["depth"] if spec.get("fixed")
                            else im.top_band(path, spec["depth"]))

    @property
    def pose_id(self):
        # Appearance belongs in the identity of a pose: the same screen shot
        # light and dark shares nothing a cross-language check can compare, and
        # putting them in one group made every dark shot disagree with every
        # light one about the accent.
        return (self.device, self.pose, self.appearance)

    @property
    def appearance(self) -> str:
        """Light or dark, measured rather than read off the file name.

        A dark shot and a light one of the same screen share nothing a
        set-wide check can compare, so they are never each other's siblings.
        """
        return "dark" if self.top_mean < 128 else "light"

    @property
    def verdict(self):
        if any(f.status == "flag" for f in self.findings):
            return "flag"
        if all(f.status == "skip" for f in self.findings):
            return "skip"
        return "pass"

    def as_dict(self):
        return {
            "path": self.path, "language": self.lang, "device": self.device,
            "pose": self.pose, "size": list(self.size),
            "verdict": self.verdict,
            "findings": [f._asdict() for f in self.findings],
        }


def discover(root: str, profile: dict, only: str | None = None) -> list[Shot]:
    """Every capture under `root`, at whatever depth it was filed.

    The language of a shot is the one its file name carries (the iOS sets put
    it there), else the nearest folder above it that names one, else nothing —
    a single-language re-shoot arrives as a flat directory and is read as one
    unnamed set rather than refused.
    """
    shots = []
    for folder, subdirs, files in os.walk(root):
        subdirs.sort()
        if only and not any(part in os.path.abspath(folder)
                            for part in only.split(",")):
            continue
        # The whole path, not just the part under the root: pointing the gate
        # at one language's folder is a normal thing to do, and the language is
        # then above the root rather than under it.
        folder_lang = None
        folder_device = None
        for part in os.path.abspath(folder).split(os.sep):
            found = stores.language_of(part)
            if found:
                folder_lang = found
            # A set may file its device above its languages — the Play gate set
            # is phone/<language>/ — in which case the slot is a folder and not
            # part of the file name.
            if part in profile["slots"]:
                folder_device = part
        for entry in sorted(files):
            lower = entry.lower()
            if not lower.endswith(".png") or lower.endswith(".small.png"):
                continue                   # .small.png: a set's own thumbnails
            parsed = profile["parse"](entry)
            if not parsed:
                continue
            device, pose, file_lang = parsed
            shots.append(Shot(os.path.join(folder, entry),
                              file_lang or folder_lang or "\u2014",
                              device or folder_device or "", pose, profile))
    return shots


def language_pairs(shots: list) -> list[dict]:
    """For each pair of languages, how many poses are the same image.

    Not a check — a fact worth seeing. The Mac's fr-CA and fr-FR sets are
    byte-identical in five of six poses because the demo person is the only
    string that differs between the two catalogues on any listing screen. That
    is correct, and it is also a decision someone may want to revisit, so the
    sheet says it out loud rather than leaving it to be noticed.
    """
    by_lang = collections.defaultdict(dict)
    for shot in shots:
        by_lang[shot.lang][(shot.device, shot.pose)] = shot.digest
    out = []
    langs = sorted(by_lang)
    for i, a in enumerate(langs):
        for b in langs[i + 1:]:
            shared = set(by_lang[a]) & set(by_lang[b])
            if not shared:
                continue
            same = sum(1 for k in shared if by_lang[a][k] == by_lang[b][k])
            out.append({"a": a, "b": b, "same": same, "of": len(shared)})
    return out


def constants(shots: list) -> list:
    """One shot per device and appearance — the ones to look at by eye.

    Every check in the gate compares an image with something: a slot size, a
    sibling in another language, a certified set. Anything that is the same in
    *every* image of a set has nothing to be compared against and is invisible
    here. The launcher taskbar that reached all 24 Play tablet captures was
    exactly that, and this tool does not find it even now that it is known
    about — the fixed set and the broken one both come back clean.

    So the sheet names one image per device and appearance and says: look at
    this one properly. It is where the constants live.
    """
    seen = {}
    for shot in shots:
        seen.setdefault((shot.device, shot.appearance), shot)
    return list(seen.values())


def order_key(shot: Shot, profile: dict):
    order = profile["order"]
    rank = order.index(shot.pose) if shot.pose in order else len(order)
    return (shot.device, rank, shot.name)


def run(root: str, store: str, out: str, thumb_width: int,
        only: str | None = None, against: str | None = None,
        live_clock: bool = False) -> dict:
    profile = stores.profile(store)
    shots = discover(root, profile, only)
    if not shots:
        raise SystemExit(f"no captures found under {root} that this profile "
                         f"recognises — see README.md § File names")

    for shot in shots:
        for check in checks.PER_IMAGE:
            shot.findings.extend(check(shot, profile))

    if against:
        reference = {(s.lang, s.device, s.pose): s.path
                     for s in discover(against, profile, only)}
        for shot in shots:
            shot.findings.extend(checks.against_reference(
                shot, reference.get((shot.lang, shot.device, shot.pose))))

    # Set-wide: the same pose across languages, and each language's own frame.
    by_pose = collections.defaultdict(list)
    by_lang = collections.defaultdict(list)
    by_frame = collections.defaultdict(list)
    for shot in shots:
        by_pose[shot.pose_id].append(shot)
        by_lang[shot.lang].append(shot)
        # The furniture is only comparable within one language, one device and
        # one appearance: an iPad's status bar is not an iPhone's, and a dark
        # shot is not a light one's sibling.
        by_frame[(shot.lang, shot.device, shot.appearance)].append(shot)
    for group in by_pose.values():
        group.sort(key=lambda s: (s.lang != "en", s.lang))
        for shot, finding in checks.poses_match(group, profile):
            shot.findings.append(finding)
        for shot, finding in checks.accent_consistent(group, profile):
            shot.findings.append(finding)
    for group in by_frame.values():
        for shot, finding in checks.chrome_consistent(group, profile,
                                                      live_clock):
            shot.findings.append(finding)

    os.makedirs(out, exist_ok=True)
    shots.sort(key=lambda s: (s.lang, order_key(s, profile)))
    thumbs = {}
    for shot in shots:
        data = im.thumbnail(shot.path, thumb_width)
        thumbs[shot.path] = "data:image/jpeg;base64," + \
            base64.b64encode(data).decode("ascii")

    result = {
        "store": store,
        "title": profile["title"],
        "root": os.path.abspath(root),
        "ocr": im.have("tesseract"),
        "sets": [],
    }
    for lang, group in sorted(by_lang.items()):
        flagged = [s for s in group if s.verdict == "flag"]
        counted = sum(len(s.findings) for s in group)
        passed = sum(1 for s in group for f in s.findings if f.status == "pass")
        skipped = sum(1 for s in group for f in s.findings if f.status == "skip")
        result["sets"].append({
            "language": lang,
            "images": len(group),
            "checks": counted,
            "passed": passed,
            "skipped": skipped,
            "questionable": [
                {"name": s.name, "pose": s.pose,
                 "why": [f"{f.check}: {f.note}" for f in s.findings
                         if f.status == "flag"]}
                for s in flagged],
        })
    # Two things the sheet says that no single check can: how alike the
    # language sets are, and which images carry everything the checks cannot
    # see.
    result["pairs"] = language_pairs(shots)
    result["constants"] = [
        {"language": s.lang, "device": s.device, "pose": s.pose,
         "name": s.name, "path": s.path}
        for s in constants(shots)]
    result["images"] = [s.as_dict() for s in shots]

    with open(os.path.join(out, "results.json"), "w", encoding="utf-8") as handle:
        json.dump(result, handle, indent=2, ensure_ascii=False)
    sheet_path = os.path.join(out, f"sheet-{store}.html")
    sheet_mod.write(sheet_path, result, shots, thumbs, profile)
    result["sheet"] = sheet_path
    return result


def report(result: dict) -> int:
    print(f"\n{result['title']} — {result['root']}")
    if not result["ocr"]:
        print("  tesseract is not installed: the checks that read text stood "
              "down. See README.md § What needs OCR.")
    worst = 0
    for entry in result["sets"]:
        flagged = entry["questionable"]
        print(f"\n  {entry['language']}: {entry['images']} images, "
              f"{entry['passed']} checks passed, {entry['skipped']} not run, "
              f"{len(flagged)} image(s) to look at")
        for item in flagged:
            print(f"    • {item['name']} ({item['pose']})")
            for why in item["why"]:
                print(f"        {why}")
        worst = max(worst, 1 if flagged else 0)
    print(f"\n  sheet: {result['sheet']}")
    return worst


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        description="Check a store capture set and build its contact sheet.")
    parser.add_argument("root", help="the set: <root>/<language>/<image>.png")
    parser.add_argument("--store", required=True,
                        choices=sorted(stores.STORES),
                        help="which listing's rules to apply")
    parser.add_argument("-o", "--out", default="capture-gate-out",
                        help="where the sheet and results.json go")
    parser.add_argument("--thumb", type=int, default=460,
                        help="contact-sheet thumbnail width in px")
    parser.add_argument("--only", metavar="SEGMENT[,SEGMENT...]",
                        help="only folders whose path contains one of these — "
                             "a shot folder often holds a listing set and a "
                             "review set side by side, and a QA matrix holds "
                             "twenty-seven cells")
    parser.add_argument("--against", metavar="DIR",
                        help="a set that was already signed off: every image "
                             "is compared with its counterpart, which is how a "
                             "re-shoot proves it changed only what it meant to")
    parser.add_argument("--live-clock", action="store_true",
                        help="the set was not shot with the clock posed (a QA "
                             "matrix rather than a store set), so only the "
                             "right of the status bar is compared")
    parser.add_argument("--strict", action="store_true",
                        help="exit non-zero if anything is flagged (for CI)")
    args = parser.parse_args(argv)

    im.require("convert")
    result = run(args.root, args.store, args.out, args.thumb, args.only,
                 args.against, args.live_clock)
    flagged = report(result)
    return flagged if args.strict else 0


if __name__ == "__main__":
    sys.exit(main())
