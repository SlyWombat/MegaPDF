#!/usr/bin/env python3
"""Walks the Android screen inventory and captures every state it can reach.

    python3 capture.py --serial emulator-5554 --device small \
        --lang fr-CA --theme light --font-scale 1.0 --out /work/out/shots

One scene per inventory item. A scene that cannot be reached records its error
and the run continues, so one broken state never costs the other forty.

See docs/qa/android-screen-inventory.md for what each id refers to.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time
import traceback

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from adbui import Device, NotFound  # noqa: E402
from strings import Strings  # noqa: E402

FIXTURES = "/work/out/fixtures"


class Run:
    def __init__(self, device: Device, strings: Strings, out: str, prefix: str):
        self.d = device
        self.s = strings
        self.out = out
        self.prefix = prefix
        self.results: list[dict] = []

    def shot(self, name: str) -> str:
        self.d.dismiss_ime_promo()
        self.d.demo_status_bar()
        path = os.path.join(self.out, f"{self.prefix}__{name}.png")
        self.d.screencap(path)
        return path

    def scene(self, name: str, body) -> None:
        try:
            body()
            self.results.append({"scene": name, "ok": True})
        except Exception as exc:  # noqa: BLE001 — a scene failing must not stop the run
            self.results.append({
                "scene": name, "ok": False,
                "error": f"{type(exc).__name__}: {exc}",
                "trace": traceback.format_exc(limit=3),
            })
            print(f"  ! {name}: {type(exc).__name__}: {exc}", flush=True)
            try:
                self.shot(f"{name}--FAILED")
            except Exception:
                pass
            self.recover()

    def recover(self) -> None:
        """Back out of whatever is on screen — including a system picker, which
        force-stopping our own app would leave in the foreground."""
        for _ in range(3):
            try:
                self.d.back(settle=0.4)
            except Exception:
                pass
        try:
            self.d.press("KEYCODE_HOME", settle=0.5)
            self.d.stop_app()
        except Exception:
            pass

    # --- helpers -----------------------------------------------------------

    def open_file(self, display_name: str, settle: float = 4.0) -> None:
        """Home -> the system picker -> that file."""
        self.pick(display_name)
        time.sleep(settle)

    def pick(self, display_name: str) -> None:
        """Everything up to and including the tap on the file, and no waiting after
        it — a busy state only exists in the moment right after."""
        self.d.tap(text=self.s["open_pdf"])
        time.sleep(2.5)
        if not self.d.exists(text=display_name):
            # Only the newest handful show under "Recent files"; the rest are found
            # the way a person would find them.
            self.d.tap(desc="Search", settle=1.5)
            self.d.type_text(display_name.replace(".pdf", ""))
            self.d.press("KEYCODE_ENTER", settle=2.5)
        node = self.d.wait_for(text=display_name)
        left, top, right, bottom = self.d._bounds(node)
        self.d.tap_xy((left + right) // 2, (top + bottom) // 2, settle=0.0)

    def reset_app(self, wait: float = 3.0) -> None:
        """`pm clear` for a genuine first run — and then the per-app locale again,
        because clearing an app's data also drops its LocaleManager override, which
        silently gave the French cells four English screens."""
        self.d.clear_app()
        self.d.set_app_locale(self.s.lang)
        time.sleep(1.5)
        self.d.launch(wait=wait)

    def fresh(self, screenshot_state: str | None = None, wait: float = 4.0) -> None:
        # Also stop the system picker: left in the foreground it shadows the app,
        # and `am start` does not always bring ours back in front of it. One Home
        # capture came out as the picker's search history that way.
        self.d.shell("am force-stop com.google.android.documentsui")
        self.d.stop_app()
        self.d.launch(screenshot_state, wait=wait)

    def tap_page(self, x: float, y: float, page: int = 1, settle: float = 3.0) -> None:
        """A point in PDF points on `page`, tapped where it is on *this* screen."""
        node = self.d.wait_for(desc=self.s.format("page_n", page))
        left, top, right, bottom = self.d._bounds(node)
        self.d.tap_xy(left + (x / FORM_W) * (right - left),
                      top + ((FORM_H - y) / FORM_H) * (bottom - top), settle=settle)

    def field(self, index: int) -> None:
        """Focus the index-th text field, scrolling the dialog if it is out of reach.

        At the largest text size a dialog body scrolls (#183), so its second field
        can start below the fold — which is where the Set a password capture lost
        it on the small phone."""
        try:
            node = self.d.find(klass="android.widget.EditText", index=index)
        except NotFound:
            self.d.scroll_down(0.25, ms=200)
            node = self.d.wait_for(klass="android.widget.EditText", index=index)
        left, top, right, bottom = self.d._bounds(node)
        _, height = self.d.screen_size()
        if bottom > height * 0.62:
            self.d.scroll_down(0.2, ms=200)
            node = self.d.find(klass="android.widget.EditText", index=index)
            left, top, right, bottom = self.d._bounds(node)
        self.d.tap_xy((left + right) // 2, (top + bottom) // 2, settle=0.9)


# --------------------------------------------------------------------------
# Scenes
# --------------------------------------------------------------------------

def demo_scenes(r: Run) -> None:
    """Everything reachable through the `--es screenshot <state>` launch extra."""
    s, d = r.s, r.d

    def home():
        r.fresh("home")
        r.shot("A2-home-recents")
    r.scene("A2-home-recents", home)

    def about():
        r.fresh("home")
        d.tap(desc=s["about_megapdf"])
        r.shot("A4-about")
    r.scene("A4-about", about)

    def notices():
        r.fresh("home")
        d.tap(desc=s["about_megapdf"])
        d.tap(text=s["third_party_notices"], settle=2.5)
        r.shot("A5-third-party-notices")
    r.scene("A5-third-party-notices", notices)

    def viewer():
        r.fresh("viewer", wait=6)
        r.shot("D1-viewer-clean")
    r.scene("D1-viewer-clean", viewer)

    def menu():
        r.fresh("viewer", wait=6)
        d.tap(desc=s["more_options"])
        r.shot("D8-overflow-menu")
    r.scene("D8-overflow-menu", menu)

    def tooltip():
        r.fresh("viewer", wait=6)
        node = d.wait_for(desc=s["add_text"])
        left, top, right, bottom = d._bounds(node)
        x, y = (left + right) // 2, (top + bottom) // 2
        import subprocess
        held = subprocess.Popen(
            ["adb", "-s", d.serial, "shell", f"input swipe {x} {y} {x} {y} 4000"])
        time.sleep(1.8)
        r.shot("D9-tool-tooltip")
        held.wait()
    r.scene("D9-tool-tooltip", tooltip)

    def zoom():
        r.fresh("viewer", wait=6)
        d.double_tap_fraction(0.5, 0.45)
        r.shot("D4-viewer-zoomed")
    r.scene("D4-viewer-zoomed", zoom)

    def search_hits():
        r.fresh("search", wait=7)
        r.shot("E3-search-hits")
    r.scene("E3-search-hits", search_hits)

    def sign_sheet():
        r.fresh("sign", wait=7)
        r.shot("F2-signatures-sheet")
    r.scene("F2-signatures-sheet", sign_sheet)

    def sign_card_menu():
        r.fresh("sign", wait=7)
        d.tap(desc=s.format("signature_options", s["screenshot_signature_name"]))
        r.shot("F3-signature-card-menu")
    r.scene("F3-signature-card-menu", sign_card_menu)

    def sign_rename():
        r.fresh("sign", wait=7)
        d.tap(desc=s.format("signature_options", s["screenshot_signature_name"]))
        d.tap(text=s["signature_rename"])
        r.shot("F4-signature-rename")
    r.scene("F4-signature-rename", sign_rename)

    def sign_delete():
        r.fresh("sign", wait=7)
        d.tap(desc=s.format("signature_options", s["screenshot_signature_name"]))
        d.tap(text=s["delete"])
        r.shot("F5-signature-delete")
    r.scene("F5-signature-delete", sign_delete)

    def draw():
        r.fresh("draw", wait=7)
        r.shot("F6-draw-signature")
    r.scene("F6-draw-signature", draw)

    def type_signature():
        r.fresh("sign", wait=7)
        d.tap(text=s["type_signature"])
        time.sleep(1.0)
        d.type_text(TYPEABLE_NAME[r.s.lang])
        time.sleep(1.5)
        r.shot("F7-type-signature")
    r.scene("F7-type-signature", type_signature)

    def add_text():
        r.fresh("text", wait=7)
        r.shot("G2-add-text-dialog")
    r.scene("G2-add-text-dialog", add_text)

    def edit_text():
        r.fresh("text-edit", wait=7)
        r.shot("G4-edit-body-text-dialog")
    r.scene("G4-edit-body-text-dialog", edit_text)


# What the app's own screenshot flow types, from `screenshot_text` (#146 §3).
DEMO_NAME = {
    "en": "Jane Whitfield",
    "fr-CA": "Hélène Bélanger",
    "fr-FR": "Céline Lefèvre",
}

# What *this script* can type: adb's `input text` is ASCII-only (see adbui).
# The accented names are covered by the screenshot states, which take them from
# the string catalogue, and end to end by accents.py.
TYPEABLE_NAME = {
    "en": "Jane Whitfield",
    "fr-CA": "Helene Belanger",
    "fr-FR": "Celine Lefevre",
}

FORM = "MegaPDF-Test-Form.pdf"

# The review form is US Letter, and tools/gen_review_form.py puts its first drawn
# checkbox here, in PDF points. Given in points rather than as a share of the
# screen because the bars above and below the page are a different share of it on
# each device: a fraction that hit the box at 320 dpi missed it at 480.
FORM_W, FORM_H = 612.0, 792.0
TICK = (78.5, 570.5)


def real_file_scenes(r: Run) -> None:
    """The states that need a real document, opened through the system picker."""
    s, d = r.s, r.d

    def first_run():
        r.reset_app()
        r.shot("A1-home-first-run")
    r.scene("A1-home-first-run", first_run)

    def picker():
        r.reset_app()
        d.tap(text=s["open_pdf"], settle=3.0)
        r.shot("A6-system-document-picker")
        d.back()
    r.scene("A6-system-document-picker", picker)

    def viewer_real():
        r.reset_app()
        r.open_file(FORM)
        r.shot("D1r-viewer-real-file")
    r.scene("D1r-viewer-real-file", viewer_real)

    def signatures_empty():
        r.reset_app()
        r.open_file(FORM)
        d.tap(desc=s["sign"], settle=2.0)
        r.shot("F1-signatures-empty")
    r.scene("F1-signatures-empty", signatures_empty)

    def search_empty():
        r.fresh()
        r.open_file(FORM)
        d.tap(desc=s["search"], settle=2.0)
        r.shot("E1-search-empty")
    r.scene("E1-search-empty", search_empty)

    def search_no_results():
        r.fresh()
        r.open_file(FORM)
        d.tap(desc=s["search"], settle=2.0)
        d.type_text("zzqqx")
        time.sleep(3.0)
        r.shot("E4-search-no-results")
    r.scene("E4-search-no-results", search_no_results)

    def tick_and_dirty():
        r.fresh()
        r.open_file(FORM)
        # "Include delivery and pickup" — the first drawn square on the page.
        r.tap_page(*TICK)
        r.shot("D2-viewer-dirty-ticked")
    r.scene("D2-viewer-dirty-ticked", tick_and_dirty)

    def unsaved_prompt():
        r.fresh()
        r.open_file(FORM)
        r.tap_page(*TICK)
        d.tap(desc=s["close_document"], settle=1.5)
        r.shot("I1-unsaved-changes")
    r.scene("I1-unsaved-changes", unsaved_prompt)

    def add_text_armed():
        r.fresh()
        r.open_file(FORM)
        d.tap(desc=s["add_text"], settle=1.0)
        r.shot("D13e-tap-to-place-text-toast")
    r.scene("D13e-tap-to-place-text-toast", add_text_armed)

    def text_box_selected():
        r.fresh()
        r.open_file(FORM)
        d.tap(desc=s["add_text"], settle=1.0)
        d.tap_fraction(0.35, 0.56, settle=1.5)
        d.type_text(TYPEABLE_NAME[r.s.lang])
        time.sleep(0.8)
        r.shot("G2r-add-text-dialog-typed")
        d.tap(text=s["add"], settle=3.0)
        d.tap_fraction(0.35, 0.56, settle=2.0)
        r.shot("G5-text-box-selected")
    r.scene("G5-text-box-selected", text_box_selected)

    def save_as_picker():
        r.fresh()
        r.open_file(FORM)
        d.tap(desc=s["more_options"])
        d.tap(text=s["save_a_copy"], settle=3.0)
        r.shot("I4-save-a-copy-picker")
        d.back()
    r.scene("I4-save-a-copy-picker", save_as_picker)

    def open_error():
        r.fresh()
        r.open_file("corrupt.pdf", settle=3.0)
        r.shot("A3b-home-open-error")
    r.scene("A3b-home-open-error", open_error)

    def password_prompt():
        r.fresh()
        r.open_file("aes-256.pdf", settle=3.0)
        r.shot("C1-password-required")
        d.type_text("wrong-one")
        d.tap(text=s["open"], settle=3.0)
        r.shot("C2-password-rejected")
        d.tap(text=s["cancel"], settle=1.5)
    r.scene("C1-password-required", password_prompt)

    def restricted():
        r.fresh()
        r.open_file("owner-only.pdf", settle=1.5)
        r.shot("D12f-restricted-notice")
        time.sleep(6.0)
        r.shot("D10-restricted-tools-disabled")
        d.tap(desc=s["more_options"])
        r.shot("D8r-overflow-menu-restricted")
        d.tap(text=s["security_unlock_menu"], settle=1.5)
        r.shot("H1-unlock-document")
        d.type_text("nope")
        d.tap(text=s["security_unlock"], settle=3.0)
        r.shot("H1b-unlock-rejected")
        d.tap(text=s["cancel"], settle=1.5)
    r.scene("H1-unlock-document", restricted)

    def restricted_password_command():
        r.fresh()
        r.open_file("owner-only.pdf", settle=5.0)
        d.tap(desc=s["more_options"])
        d.tap(text=s["security_password_menu"], settle=1.5)
        r.shot("H2-password-command-restricted")
        d.tap(text=s["cancel"], settle=1.0)
    r.scene("H2-password-command-restricted", restricted_password_command)

    def set_password():
        r.fresh()
        r.open_file(FORM)
        d.tap(desc=s["more_options"])
        d.tap(text=s["security_password_menu"], settle=1.5)
        r.shot("H3-set-a-password")
        d.tap(text=s["security_set"], settle=1.5)
        r.shot("H5-password-empty")
        r.field(0)
        d.type_text("one")
        r.field(1)
        d.type_text("two")
        d.tap(text=s["security_set"], settle=1.5)
        r.shot("H6-password-mismatch")
        d.tap(text=s["cancel"], settle=1.0)
    r.scene("H3-set-a-password", set_password)


def busy_scenes(r: Run) -> None:
    """The indicators, which only show on work slow enough to need them (#145)."""
    s, d = r.s, r.d

    def opening():
        r.fresh()
        r.pick("deep-10000.pdf")
        time.sleep(0.9)
        r.shot("D11a-busy-opening")
        time.sleep(20)
    r.scene("D11a-busy-opening", opening)

    def searching():
        r.fresh()
        r.open_file("deep-10000.pdf", settle=25.0)
        d.tap(desc=s["search"], settle=2.0)
        d.type_text("lighthouse", settle=0.0)
        time.sleep(1.2)
        r.shot("D11d-busy-searching")
        time.sleep(3)
        r.shot("E3r-search-hits-real")
    r.scene("D11d-busy-searching", searching)

    def place_signature():
        r.fresh("sign", wait=7)
        d.tap(desc=s.format("signature_card_a11y", s["screenshot_signature_name"]),
              settle=1.0)
        r.shot("D13d-tap-to-place-signature-toast")
        d.tap_fraction(0.45, 0.62, settle=3.0)
        r.shot("F10a-signature-placed")
        d.tap_fraction(0.45, 0.62, settle=2.0)
        r.shot("F10-signature-selected")
    r.scene("F10-signature-selected", place_signature)


def wide_page_scenes(r: Run) -> None:
    """Page geometry the layout has to survive (inventory D7)."""
    for name, fixture in (
        ("D7a-wide-poster", "wide-poster.pdf"),
        ("D7b-tall-receipt", "tall-receipt.pdf"),
        ("D7c-user-unit-banner", "wide-userunit.pdf"),
        ("D7d-mixed-sizes", "mixed-sizes.pdf"),
    ):
        def body(name=name, fixture=fixture):
            r.fresh()
            r.open_file(fixture, settle=6.0)
            r.shot(name)
        r.scene(name, body)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--serial", default="emulator-5554")
    parser.add_argument("--device", required=True)
    parser.add_argument("--lang", required=True, choices=["en", "fr-CA", "fr-FR"])
    parser.add_argument("--theme", default="light", choices=["light", "dark"])
    parser.add_argument("--font-scale", type=float, default=1.0)
    parser.add_argument("--out", default="/work/out/shots")
    parser.add_argument("--groups", default="demo,real,busy,wide")
    args = parser.parse_args()

    d = Device(args.serial)
    d.wait_boot()
    d.set_dark_mode(args.theme == "dark")
    d.set_font_scale(args.font_scale)
    d.set_app_locale(args.lang)
    time.sleep(2)
    d.demo_status_bar()

    scale_tag = "t1" if abs(args.font_scale - 1.0) < 0.01 else f"t{args.font_scale:g}"
    prefix = f"{args.device}__{args.lang}__{args.theme}__{scale_tag}"
    out = os.path.join(args.out, prefix)
    os.makedirs(out, exist_ok=True)

    run = Run(d, Strings(args.lang), out, prefix)
    groups = set(args.groups.split(","))
    print(f"== {prefix} ==", flush=True)
    started = time.time()
    if "demo" in groups:
        demo_scenes(run)
    if "real" in groups:
        real_file_scenes(run)
    if "busy" in groups:
        busy_scenes(run)
    if "wide" in groups:
        wide_page_scenes(run)
    d.stop_app()

    failed = [x for x in run.results if not x["ok"]]
    summary = {
        "cell": prefix, "device": args.device, "lang": args.lang,
        "theme": args.theme, "font_scale": args.font_scale,
        "seconds": round(time.time() - started, 1),
        "scenes": len(run.results), "failed": len(failed),
        "results": run.results,
    }
    with open(os.path.join(out, "_summary.json"), "w") as handle:
        json.dump(summary, handle, indent=2)
    print(f"== {prefix}: {len(run.results) - len(failed)}/{len(run.results)} scenes, "
          f"{summary['seconds']}s ==", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
