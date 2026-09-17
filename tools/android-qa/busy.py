#!/usr/bin/env python3
"""Captures the busy indicators (#145), which need work slow enough to show them.

    python3 busy.py --serial emulator-5560 --lang fr-CA --out /work/out/busy

Nothing quicker than half a second shows an indicator at all, which is the
design — and which means the capture matrix never caught one: opening a
10,000-page file and searching it are both under the threshold on this hardware.
So these run against the multi-GB fixtures instead, and shoot while the work is
still going.

Needs `push-fixtures.sh --large` to have run.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from adbui import Device  # noqa: E402
from strings import Strings  # noqa: E402

# The busy labels, and what each one is captured from.
WANTED = ["busy_opening", "saving", "busy_verifying_save", "busy_searching",
          "busy_checking_page", "busy_applying"]


class Shooter(threading.Thread):
    """Screencaps on a fixed cadence while something slow runs, and records what
    was on screen with each one.

    Sampling the hierarchy from the main thread instead missed four of the six
    labels: an indicator lives for a second or two and a `uiautomator dump` takes
    about one, so by the time the dump came back the work had finished. Reading
    it here, beside each shot, means the evidence and the label come from the
    same moment."""

    def __init__(self, device: Device, out: str, tag: str, every: float, count: int):
        super().__init__(daemon=True)
        self.d, self.out, self.tag = device, out, tag
        self.every, self.count = every, count
        self.shots: list[str] = []
        self.strings_seen: set[str] = set()

    def run(self) -> None:
        for index in range(self.count):
            path = os.path.join(self.out, f"{self.tag}-{index:02d}.png")
            try:
                self.d.screencap(path)
                self.shots.append(path)
            except Exception:
                pass
            try:
                for node in self.d.nodes():
                    for key in ("text", "content-desc"):
                        value = node.get(key)
                        if value:
                            self.strings_seen.add(value)
            except Exception:
                pass
            time.sleep(self.every)


def labels_in(seen: set[str], strings: Strings) -> list[str]:
    """Which busy labels appeared. The page spinner has no visible text — its
    label is only a content description — so both are searched."""
    return [key for key in WANTED if strings[key] in seen]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--serial", default="emulator-5560")
    parser.add_argument("--lang", default="en", choices=["en", "fr-CA", "fr-FR"])
    parser.add_argument("--out", default="/work/out/busy")
    args = parser.parse_args()

    out = os.path.join(args.out, args.lang)
    os.makedirs(out, exist_ok=True)
    d = Device(args.serial)
    s = Strings(args.lang)
    d.wait_boot()
    d.set_app_locale(args.lang)
    d.set_dark_mode(False)
    d.set_font_scale(1.0)
    time.sleep(2)

    seen: dict[str, str] = {}

    def note(shooter: Shooter, where: str) -> None:
        for key in labels_in(shooter.strings_seen, s):
            seen.setdefault(key, where)

    def pick(name: str) -> None:
        d.tap(text=s["open_pdf"])
        time.sleep(2.5)
        if not d.exists(text=name):
            d.tap(desc="Search", settle=1.5)
            d.type_text(name.replace(".pdf", ""))
            d.press("KEYCODE_ENTER", settle=3.0)
        node = d.wait_for(text=name)
        left, top, right, bottom = d._bounds(node)
        d.tap_xy((left + right) // 2, (top + bottom) // 2, settle=0.0)

    # --- Opening…, on the file that takes longest to open ------------------
    d.stop_app()
    d.launch(wait=4)
    pick("huge-2_5gb.pdf")
    shooter = Shooter(d, out, "opening", 0.5, 12)
    shooter.start()
    shooter.join()
    note(shooter, "opening huge-2_5gb.pdf")
    d.wait_for(desc=s.format("page_n", 1), timeout=180)

    # --- Searching…, over 10,000 pages ------------------------------------
    d.stop_app()
    d.launch(wait=4)
    pick("deep-10000.pdf")
    d.wait_for(desc=s.format("page_n", 1), timeout=180)
    d.tap(desc=s["search"], settle=2.0)
    d.type_text("lighthouse", settle=0.0)
    shooter = Shooter(d, out, "searching", 0.4, 14)
    shooter.start()
    shooter.join()
    note(shooter, "searching deep-10000.pdf")
    d.tap(desc=s["close_search"], settle=1.5)

    # --- Checking this page… and Applying…, on 20,000 text objects --------
    d.stop_app()
    d.launch(wait=4)
    pick("many-objects.pdf")
    d.wait_for(desc=s.format("page_n", 1), timeout=180)
    for _ in range(3):
        d.scroll_down(0.8, ms=150)
    time.sleep(2)
    shooter = Shooter(d, out, "page-work", 0.3, 20)
    shooter.start()
    d.tap_fraction(0.4, 0.35, settle=0.0)
    shooter.join()
    note(shooter, "tapping a line on many-objects.pdf")

    # --- Saving… and Checking the saved file…, on 2.5 GB -------------------
    d.stop_app()
    d.launch(wait=4)
    pick("huge-2_5gb.pdf")
    d.wait_for(desc=s.format("page_n", 1), timeout=180)
    d.tap(desc=s["add_text"], settle=1.5)
    d.tap_fraction(0.4, 0.4, settle=2.5)
    if d.exists(text=s["add"]):
        d.type_text("MegaPDF QA 146")
        d.tap(text=s["add"], settle=12.0)
    d.tap(desc=s["more_options"], settle=1.2)
    d.tap(text=s["save_a_copy"], settle=4.0)
    node = d.find(klass="android.widget.EditText")
    left, top, right, bottom = d._bounds(node)
    d.tap_xy((left + right) // 2, (top + bottom) // 2, settle=0.9)
    d.press("KEYCODE_MOVE_END", settle=0.3)
    d.shell("; ".join(["input keyevent KEYCODE_DEL"] * 64))
    d.type_text("qa-busy.pdf")
    shooter = Shooter(d, out, "saving", 0.7, 40)
    d.tap(contains="SAVE", settle=0.0)
    shooter.start()
    shooter.join()
    note(shooter, "saving a copy of huge-2_5gb.pdf")

    missing = [key for key in WANTED if key not in seen]
    report = {"lang": args.lang, "seen": seen, "missing": missing,
              "labels": {key: s[key] for key in WANTED}}
    with open(os.path.join(out, "busy.json"), "w") as handle:
        json.dump(report, handle, indent=2, ensure_ascii=False)
    print(json.dumps(report, ensure_ascii=False, indent=2), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
