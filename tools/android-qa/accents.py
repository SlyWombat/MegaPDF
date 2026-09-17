#!/usr/bin/env python3
"""Proves the §3 demo name renders, and survives into the saved PDF (#146 §3).

The name the Android screenshot flow types is the `screenshot_text` string
resource: English Jane Whitfield, fr-CA Hélène Bélanger, fr-FR Céline Lefèvre.
The accents have to be right on screen *and* in the file, and a screenshot alone
only shows the first.

So for each language this drives the app's own `--es screenshot text` state —
which fills the Add text dialog from the catalogue, the same path a capture run
takes — captures the dialog and the stamped page, saves a copy, pulls it and
reads the text back out with pdftotext.

    python3 accents.py --serial emulator-5560 --out /work/out/accents

This is also why the driver never types the name itself: adb's `input text`
goes through KeyCharacterMap and cannot produce a character outside ASCII.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
import unicodedata

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from adbui import Device  # noqa: E402
from strings import Strings  # noqa: E402

LANGS = ["en", "fr-CA", "fr-FR"]


def pdf_text(path: str) -> str:
    out = subprocess.run(["pdftotext", "-enc", "UTF-8", path, "-"],
                         capture_output=True, check=True)
    return out.stdout.decode("utf-8", errors="replace")


def run(d: Device, lang: str, out: str) -> dict:
    s = Strings(lang)
    expected = s["screenshot_text"]
    row: dict = {"lang": lang, "expected": expected,
                 "accented": not expected.isascii()}

    d.stop_app()
    d.set_app_locale(lang)
    time.sleep(2)
    d.launch("text", wait=9)

    # 1. the dialog, filled from the catalogue
    dialog = os.path.join(out, f"{lang}-1-add-text-dialog.png")
    d.screencap(dialog)
    row["dialog_shot"] = os.path.basename(dialog)
    row["dialog_shows_name"] = d.exists(text=expected)

    # 2. stamp it onto the page
    d.tap(text=s["add"], settle=6.0)
    page = os.path.join(out, f"{lang}-2-stamped-page.png")
    d.screencap(page)
    row["page_shot"] = os.path.basename(page)

    # 3. save a copy and read it back
    name = f"accent-{lang}.pdf"
    d.tap(desc=s["more_options"], settle=1.0)
    d.tap(text=s["save_a_copy"], settle=4.0)
    field = d.find(klass="android.widget.EditText")
    left, top, right, bottom = d._bounds(field)
    d.tap_xy((left + right) // 2, (top + bottom) // 2, settle=0.8)
    for _ in range(64):
        d.shell("input keyevent KEYCODE_DEL")
    d.type_text(name)
    d.tap(contains="SAVE", settle=8.0)

    remote = f"/sdcard/Download/{name}"
    local = os.path.join(out, name)
    for _ in range(12):
        if d.shell(f"ls {remote} 2>/dev/null").strip():
            break
        time.sleep(2)
    d.adb("pull", remote, local, timeout=300)
    text = pdf_text(local)
    row["in_saved_pdf"] = expected in text
    # A "ready-made" é (U+00E9) and an "e + combining acute" look the same and are
    # not the same string; say which one came back.
    row["nfc_match"] = unicodedata.normalize("NFC", expected) in \
        unicodedata.normalize("NFC", text)
    row["saved_pdf"] = os.path.basename(local)
    row["ok"] = row["dialog_shows_name"] and row["in_saved_pdf"]
    return row


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--serial", default="emulator-5560")
    parser.add_argument("--out", default="/work/out/accents")
    args = parser.parse_args()
    os.makedirs(args.out, exist_ok=True)

    d = Device(args.serial)
    d.wait_boot()
    d.set_dark_mode(False)
    d.set_font_scale(1.0)

    rows = []
    for lang in LANGS:
        try:
            row = run(d, lang, args.out)
        except Exception as exc:  # noqa: BLE001
            row = {"lang": lang, "ok": False, "error": f"{type(exc).__name__}: {exc}"}
        rows.append(row)
        print(json.dumps(row, ensure_ascii=False), flush=True)

    with open(os.path.join(args.out, "accents.json"), "w") as handle:
        json.dump(rows, handle, indent=2, ensure_ascii=False)
    failed = [r for r in rows if not r.get("ok")]
    print(f"== accents: {len(rows) - len(failed)}/{len(rows)} languages ==", flush=True)
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
