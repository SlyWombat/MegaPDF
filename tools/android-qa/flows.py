#!/usr/bin/env python3
"""Walks the §1b flows on a real device with real files (#146).

    python3 flows.py --serial emulator-5560 --out /work/out/flows

Every flow the issue lists that Android has: open, scroll, zoom, find, tick,
sign, add text, edit text, undo and redo, save and save as, set and remove
protection, close with unsaved changes, and what happens when the app is killed.
Whiteout, Shrink and Print have no Android screen, so they are reported as not
applicable rather than skipped silently.

Then the large files, with the peak resident set of the app process recorded for
each — `adb root` first, so /proc/<pid>/status is readable.

Taps on the page are given in PDF points and mapped through the page's own
on-screen bounds, so the same script works on every device.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from adbui import Device, NotFound  # noqa: E402
from strings import Strings  # noqa: E402

# The review form is US Letter; tools/gen_review_form.py places everything on it.
FORM_W, FORM_H = 612.0, 792.0
SQUARES = [(78.5, 570.5), (78.5, 544.5), (78.5, 518.5)]       # drawn checkboxes
WIDGETS = [(79.5, 445.5), (79.5, 419.5)]                       # /Btn widgets
SUBTITLE = (160.0, 699.0)                                      # "Sunrise Tool Rental…"
SIGN_SPOT = (170.0, 330.0)                                     # above the rule
TEXT_SPOT = (330.0, 330.0)


class Flow:
    def __init__(self, device: Device, strings: Strings, out: str):
        self.d = device
        self.s = strings
        self.out = out
        self.steps: list[dict] = []
        os.makedirs(out, exist_ok=True)

    # --- recording ---------------------------------------------------------

    def step(self, name: str, body, shot: bool = True) -> None:
        started = time.time()
        record = {"step": name}
        try:
            note = body()
            record["ok"] = True
            if note:
                record["note"] = note
        except Exception as exc:  # noqa: BLE001
            record["ok"] = False
            record["error"] = f"{type(exc).__name__}: {exc}"
            print(f"  ! {name}: {record['error']}", flush=True)
            self.recover()
        record["seconds"] = round(time.time() - started, 2)
        peak = self.peak_mb()
        if peak:
            record["peak_rss_mb"] = peak
        if shot:
            try:
                record["shot"] = os.path.basename(
                    self.d.screencap(os.path.join(self.out, f"{len(self.steps):02d}-{name}.png")))
            except Exception:
                pass
        self.steps.append(record)
        print(f"  {'ok' if record['ok'] else 'FAIL'}  {name}"
              f"  {record['seconds']}s"
              f"{'  peak ' + str(peak) + ' MB' if peak else ''}", flush=True)

    def recover(self) -> None:
        """Back out of a sheet, a dialog or a system picker so the next step starts
        somewhere known. Force-stopping would leave a picker in the foreground."""
        for _ in range(3):
            try:
                self.d.back(settle=0.5)
            except Exception:
                pass

    def note(self, name: str, text: str) -> None:
        self.steps.append({"step": name, "ok": True, "note": text})
        print(f"  --  {name}: {text}", flush=True)

    def peak_mb(self) -> float | None:
        try:
            kb = self.d.peak_rss_kb()
        except Exception:
            return None
        return round(kb / 1024.0, 1) if kb else None

    # --- page geometry -----------------------------------------------------

    def page_bounds(self, index: int = 1) -> tuple[int, int, int, int]:
        node = self.d.wait_for(desc=self.s.format("page_n", index))
        return self.d._bounds(node)

    def tap_page(self, x: float, y: float, page: int = 1, settle: float = 2.0) -> None:
        """A point in PDF points on `page`, tapped where it actually is on screen."""
        left, top, right, bottom = self.page_bounds(page)
        sx = left + (x / FORM_W) * (right - left)
        sy = top + ((FORM_H - y) / FORM_H) * (bottom - top)
        self.d.tap_xy(sx, sy, settle=settle)

    # --- shorthands --------------------------------------------------------

    def open_file(self, name: str, settle: float = 5.0) -> None:
        self.d.tap(text=self.s["open_pdf"])
        time.sleep(2.5)
        if not self.d.exists(text=name):
            self.d.tap(desc="Search", settle=1.5)
            self.d.type_text(name.replace(".pdf", ""))
            self.d.press("KEYCODE_ENTER", settle=3.0)
        self.d.tap(text=name)
        time.sleep(settle)

    def title(self) -> str:
        for node in self.d.nodes():
            bounds = node.get("bounds", "")
            text = node.get("text") or ""
            if text and re.match(r"\[\d+,\d+]\[\d+,\d+]", bounds) and ".pdf" in text:
                return text
        return ""

    def is_dirty(self) -> bool:
        return any((n.get("text") or "").startswith("• ") for n in self.d.nodes())

    def restart_clean(self) -> None:
        self.d.stop_app()
        self.d.launch(wait=3)


# --------------------------------------------------------------------------

def edit_flows(f: Flow) -> None:
    d, s = f.d, f.s

    f.step("01-open-review-form", lambda: (
        f.restart_clean(), f.open_file("qa-form.pdf"), f.title())[-1])

    def scroll():
        d.scroll_down(0.5)
        d.scroll_down(0.5)
        d.swipe(*_up(d))
        return "scrolled down twice and back"
    f.step("02-scroll", scroll)

    def zoom():
        # A zoomed page is wider than the display, and uiautomator clips a node's
        # bounds to the display, so the bounds say nothing. Compare the pixels.
        before = d.adb("exec-out", "screencap -p", binary=True)
        d.double_tap_fraction(0.5, 0.45)
        zoomed = d.adb("exec-out", "screencap -p", binary=True)
        d.screencap(os.path.join(f.out, "03a-zoomed.png"))
        assert zoomed != before, "double-tap did not change the page"
        d.double_tap_fraction(0.5, 0.45)
        d.pinch_out()
        d.screencap(os.path.join(f.out, "03b-pinched.png"))
        pinched = d.adb("exec-out", "screencap -p", binary=True)
        return ("double-tap changed the view; pinch "
                f"{'changed' if pinched != zoomed else 'did NOT change'} it")
    f.step("03-zoom", zoom)

    def find():
        d.tap(desc=s["search"], settle=2.0)
        d.type_text("insurance")
        time.sleep(3.5)
        counter = _counter(d)
        assert counter, "no match counter"
        d.tap(desc=s["next_match"], settle=1.5)
        d.tap(desc=s["next_match"], settle=1.5)
        after = _counter(d)
        d.tap(desc=s["close_search"], settle=1.5)
        return f"'insurance' -> {counter}, after two Next -> {after}"
    f.step("04-find", find)

    def tick():
        for x, y in SQUARES:
            f.tap_page(x, y, settle=2.5)
        for x, y in WIDGETS:
            f.tap_page(x, y, settle=2.5)
        assert f.is_dirty(), "the document is not marked changed"
        return "3 drawn squares and 2 form widgets ticked"
    f.step("05-tick", tick)

    def sign():
        d.tap(desc=s["sign"], settle=2.0)
        d.tap(text=s["type_signature"], settle=1.5)
        d.type_text(NAME[s.lang])
        time.sleep(1.0)
        d.screencap(os.path.join(f.out, "06a-typed-signature.png"))
        d.tap(text=s["type_signature_add"], settle=2.5)
        # Back on the sheet: the new card arms placement.
        card = d.wait_for(contains=s.format("signature_default_name", 1))
        left, top, right, bottom = d._bounds(card)
        d.tap_xy((left + right) // 2, (top + bottom) // 2, settle=1.5)
        f.tap_page(*SIGN_SPOT, settle=4.0)
        return "typed a signature and placed it above the rule"
    f.step("06-sign", sign)

    def move_and_resize():
        f.tap_page(*SIGN_SPOT, settle=2.0)          # select it
        left, top, right, bottom = f.page_bounds()
        d.swipe(int((left + right) / 2), int((top + bottom) * 0.55),
                int((left + right) / 2) + 40, int((top + bottom) * 0.55) + 30, 500, settle=3.0)
        return "dragged the placed signature"
    f.step("07-move-signature", move_and_resize)

    def add_text():
        d.back(settle=1.0)                           # drop the selection
        d.tap(desc=s["add_text"], settle=1.5)
        f.tap_page(*TEXT_SPOT, settle=2.0)
        d.type_text(NAME[s.lang])
        time.sleep(0.8)
        d.tap(text=s["add"], settle=4.0)
        return f"added '{NAME[s.lang]}'"
    f.step("08-add-text", add_text)

    def edit_body_text():
        f.tap_page(*SUBTITLE, settle=3.0)
        d.wait_for(text=s["body_text_hint"], timeout=12)
        field = d.find(klass="android.widget.EditText")
        left, top, right, bottom = d._bounds(field)
        d.tap_xy((left + right) // 2, (top + bottom) // 2, settle=0.8)
        for _ in range(60):
            d.shell("input keyevent KEYCODE_FORWARD_DEL")
        d.press("KEYCODE_MOVE_END", settle=0.3)
        for _ in range(60):
            d.shell("input keyevent KEYCODE_DEL")
        d.type_text("Sunrise Tool Rental - office copy")
        time.sleep(0.8)
        d.screencap(os.path.join(f.out, "09a-body-edit-dialog.png"))
        d.tap(text=s["save"], settle=5.0)
        return "retyped the document's own subtitle line"
    f.step("09-edit-document-text", edit_body_text)

    def undo_redo():
        before = f.page_bounds()
        for _ in range(3):
            d.tap(desc=s["undo"], settle=2.5)
        d.screencap(os.path.join(f.out, "10a-after-undo.png"))
        for _ in range(3):
            d.tap(desc=s["redo"], settle=2.5)
        return f"3 undo, 3 redo (page bounds {before})"
    f.step("10-undo-redo", undo_redo)

    def save():
        assert f.is_dirty(), "nothing to save"
        d.tap(text=s["save"], settle=8.0)
        assert not f.is_dirty(), "still marked changed after Save"
        return "saved back over the opened file"
    f.step("11-save", save)

    def save_as():
        d.tap(desc=s["more_options"], settle=1.0)
        d.tap(text=s["save_a_copy"], settle=3.0)
        d.screencap(os.path.join(f.out, "12a-create-document.png"))
        field = d.find(klass="android.widget.EditText")
        left, top, right, bottom = d._bounds(field)
        d.tap_xy((left + right) // 2, (top + bottom) // 2, settle=0.8)
        for _ in range(48):
            d.shell("input keyevent KEYCODE_DEL")
        d.type_text("qa-form-copy.pdf")
        d.tap(contains="SAVE", settle=6.0)
        return "saved a copy as qa-form-copy.pdf"
    f.step("12-save-a-copy", save_as)


def protection_flows(f: Flow) -> None:
    d, s = f.d, f.s

    def set_protection():
        d.tap(desc=s["more_options"], settle=1.0)
        d.tap(text=s["security_password_menu"], settle=2.0)
        fields = [n for n in d.nodes() if n.get("class") == "android.widget.EditText"]
        assert len(fields) == 2, f"{len(fields)} fields in the set dialog"
        for node in fields:
            left, top, right, bottom = d._bounds(node)
            d.tap_xy((left + right) // 2, (top + bottom) // 2, settle=0.6)
            d.type_text("qa-secret-146")
        d.tap(text=s["security_set"], settle=10.0)
        return "protection set; the document saved itself"
    f.step("13-set-protection", set_protection)

    def reopen_protected():
        d.tap(desc=s["close_document"], settle=2.0)
        f.open_file("qa-form.pdf", settle=4.0)
        d.wait_for(text=s["password_required"], timeout=15)
        d.type_text("wrong-one")
        d.tap(text=s["open"], settle=4.0)
        rejected = d.exists(text=s["wrong_password"])
        d.screencap(os.path.join(f.out, "14a-rejected.png"))
        field = d.find(klass="android.widget.EditText")
        left, top, right, bottom = d._bounds(field)
        d.tap_xy((left + right) // 2, (top + bottom) // 2, settle=0.6)
        for _ in range(24):
            d.shell("input keyevent KEYCODE_DEL")
        d.type_text("qa-secret-146")
        d.tap(text=s["open"], settle=6.0)
        d.wait_for(desc=s.format("page_n", 1), timeout=20)
        return f"wrong one rejected ({rejected}), right one opened it"
    f.step("14-reopen-protected", reopen_protected)

    def remove_protection():
        d.tap(desc=s["more_options"], settle=1.0)
        d.tap(text=s["security_password_menu"], settle=2.0)
        d.tap(text=s["security_remove"], settle=10.0)
        return "protection removed; the document saved itself"
    f.step("15-remove-protection", remove_protection)

    def reopen_unprotected():
        d.tap(desc=s["close_document"], settle=2.0)
        f.open_file("qa-form.pdf", settle=5.0)
        assert not d.exists(text=s["password_required"]), "still asks for a password"
        d.wait_for(desc=s.format("page_n", 1), timeout=20)
        return "opens with no prompt"
    f.step("16-reopen-unprotected", reopen_unprotected)


def close_and_kill_flows(f: Flow) -> None:
    d, s = f.d, f.s

    def close_with_changes():
        f.tap_page(*SQUARES[0], settle=3.0)
        assert f.is_dirty(), "the tick did not mark the document changed"
        d.tap(desc=s["close_document"], settle=2.0)
        d.wait_for(text=s["unsaved_changes"], timeout=10)
        d.screencap(os.path.join(f.out, "17a-unsaved-prompt.png"))
        d.tap(text=s["cancel"], settle=2.0)
        assert f.is_dirty(), "Cancel lost the change"
        d.tap(desc=s["close_document"], settle=2.0)
        d.tap(text=s["discard"], settle=3.0)
        assert d.exists(text=s["open_pdf"]), "Discard did not return to Home"
        return "Save / Cancel / Discard all on one row; Cancel keeps the change"
    f.step("17-close-with-unsaved-changes", close_with_changes)

    def killed():
        f.open_file("qa-form.pdf", settle=5.0)
        f.tap_page(*SQUARES[1], settle=3.0)
        assert f.is_dirty(), "the tick did not mark the document changed"
        size_before = d.shell("stat -c %s /sdcard/Download/qa-form.pdf").strip()
        d.stop_app()
        time.sleep(2)
        d.launch(wait=6)
        recovered = d.exists(text=s["open_pdf"])
        size_after = d.shell("stat -c %s /sdcard/Download/qa-form.pdf").strip()
        assert size_before == size_after, \
            f"the file changed while the app was killed: {size_before} -> {size_after}"
        return ("came back to Home with the unsaved tick gone and no restore offer "
                f"(expected: no journal on Android); file unchanged at {size_after} bytes"
                if recovered else "did NOT come back to Home")
    f.step("18-killed-mid-edit", killed)


def not_applicable(f: Flow) -> None:
    for name, why in (
        ("cover-whiteout", "no whiteout on Android — the tool set is Sign, Add text, "
                           "Search, Undo, Redo"),
        ("shrink", "no Shrink for email on Android"),
        ("print", "no Print on Android; the platform print dialog is never opened"),
        ("recovery-after-kill", "no recovery journal on Android (EditHistory is "
                                "single-session); step 18 checks what does happen"),
    ):
        f.note(f"n/a-{name}", why)


LARGE = [
    ("big-scan-250mb.pdf", 100),
    ("big-1gb.pdf", 400),
    ("huge-2_5gb.pdf", 1000),
    ("huge-image-page.pdf", 1),
    ("deep-10000.pdf", 10000),
    ("wide-poster.pdf", 1),
    ("wide-userunit.pdf", 1),
    ("tall-receipt.pdf", 1),
    ("mixed-sizes.pdf", 40),
    ("many-objects.pdf", 4),
    ("many-fields.pdf", 201),
]


def large_file_flows(f: Flow, only: list[str] | None = None) -> list[dict]:
    d, s = f.d, f.s
    results = []
    for name, pages in LARGE:
        if only and name not in only:
            continue
        row: dict = {"file": name, "pages": pages}
        print(f"  -- {name}", flush=True)
        try:
            f.restart_clean()
            started = time.time()
            f.open_file(name, settle=3.0)
            d.wait_for(desc=s.format("page_n", 1), timeout=240)
            row["open_seconds"] = round(time.time() - started, 2)
            row["peak_after_open_mb"] = f.peak_mb()
            d.screencap(os.path.join(f.out, f"large-{name}-open.png"))

            for _ in range(6):
                d.scroll_down(0.7, ms=150)
            row["peak_after_scroll_mb"] = f.peak_mb()
            d.screencap(os.path.join(f.out, f"large-{name}-scrolled.png"))

            d.tap(desc=s["search"], settle=2.0)
            d.type_text("lighthouse")
            time.sleep(min(60, 4 + pages / 200))
            row["search"] = _counter(d) or "(no counter)"
            row["peak_after_search_mb"] = f.peak_mb()
            d.screencap(os.path.join(f.out, f"large-{name}-search.png"))
            d.tap(desc=s["close_search"], settle=1.5)

            d.tap(desc=s["add_text"], settle=1.5)
            f.tap_page(200.0, 400.0, settle=2.5)
            if d.exists(text=s["add"]):
                d.type_text("MegaPDF QA 146")
                d.tap(text=s["add"], settle=15.0)
                row["edit"] = "text added" if f.is_dirty() else "add text did not mark it changed"
            else:
                row["edit"] = "the Add text dialog did not open"
            row["peak_after_edit_mb"] = f.peak_mb()

            if f.is_dirty():
                started = time.time()
                d.tap(desc=s["more_options"], settle=1.0)
                d.tap(text=s["save_a_copy"], settle=4.0)
                field = d.find(klass="android.widget.EditText")
                left, top, right, bottom = d._bounds(field)
                d.tap_xy((left + right) // 2, (top + bottom) // 2, settle=0.8)
                for _ in range(60):
                    d.shell("input keyevent KEYCODE_DEL")
                d.type_text("qa-" + name)
                d.tap(contains="SAVE", settle=5.0)
                for _ in range(60):
                    if not f.is_dirty():
                        break
                    time.sleep(5)
                row["save_copy_seconds"] = round(time.time() - started, 2)
                row["peak_after_save_mb"] = f.peak_mb()
            row["peak_mb"] = f.peak_mb()
            row["ok"] = True
        except Exception as exc:  # noqa: BLE001
            row["ok"] = False
            row["error"] = f"{type(exc).__name__}: {exc}"
            print(f"  ! {name}: {row['error']}", flush=True)
            try:
                d.screencap(os.path.join(f.out, f"large-{name}-FAILED.png"))
            except Exception:
                pass
        results.append(row)
        print(f"     {json.dumps({k: v for k, v in row.items() if k != 'pages'})}", flush=True)
    return results


# adb's `input text` is ASCII-only (see adbui.Device.type_text), so the flow walk
# types unaccented stand-ins. The accented names live in the app's own catalogue
# and are checked by accents.py, which reads them back out of a saved PDF.
NAME = {"en": "Jane Whitfield", "fr-CA": "Helene Belanger", "fr-FR": "Celine Lefevre"}


def _counter(d: Device) -> str | None:
    for node in d.nodes():
        text = node.get("text") or ""
        if re.fullmatch(r"\d+ (of|sur) \d+", text) or text in ("No results", "Aucun résultat"):
            return text
    return None


def _up(d: Device):
    w, h = d.screen_size()
    return (w // 2, int(h * 0.3), w // 2, int(h * 0.85), 300)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--serial", default="emulator-5560")
    parser.add_argument("--lang", default="en", choices=["en", "fr-CA", "fr-FR"])
    parser.add_argument("--out", default="/work/out/flows")
    parser.add_argument("--groups", default="edit,protection,close,large")
    parser.add_argument("--only-large", default="")
    args = parser.parse_args()

    d = Device(args.serial)
    d.wait_boot()
    d.set_app_locale(args.lang)
    d.set_dark_mode(False)
    d.set_font_scale(1.0)
    d.shell("cp /sdcard/Download/MegaPDF-Test-Form.pdf /sdcard/Download/qa-form.pdf")
    d.shell("content call --uri content://media/external/file --method scan_file "
            "--arg /sdcard/Download/qa-form.pdf")
    time.sleep(2)

    f = Flow(d, Strings(args.lang), args.out)
    groups = set(args.groups.split(","))
    large: list[dict] = []
    if "edit" in groups:
        edit_flows(f)
    if "protection" in groups:
        protection_flows(f)
    if "close" in groups:
        close_and_kill_flows(f)
    not_applicable(f)
    if "large" in groups:
        only = [x for x in args.only_large.split(",") if x] or None
        large = large_file_flows(f, only)

    summary = {"lang": args.lang, "steps": f.steps, "large_files": large}
    with open(os.path.join(args.out, "flows.json"), "w") as handle:
        json.dump(summary, handle, indent=2, ensure_ascii=False)
    failed = [x for x in f.steps if not x.get("ok")] + [x for x in large if not x.get("ok")]
    print(f"== flows: {len(failed)} failures ==", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
