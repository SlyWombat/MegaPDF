#!/usr/bin/env python3
"""Walks the §1b flows on a real device with real files (#146).

    python3 flows.py --serial emulator-5560 --out /work/out/flows

Every flow the issue lists that Android has: open, scroll, zoom, find, tick,
sign, add text, **redact**, edit text, undo and redo, save and save as, set and
remove protection, close with unsaved changes, and what happens when the app is
killed. Whiteout, Shrink and Print have no Android screen, so they are reported
as not applicable rather than skipped silently.

The redaction group reads the saved file back with pdftotext and qpdf rather than
asking PDFium whether PDFium removed something — the same rule tools/leakcheck
follows (#173).

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

# A copy of the review form, so the destructive steps (Save over it, set and
# remove protection) do not consume the fixture itself.
FORM = "qa-form.pdf"

# The redaction group works on its own copy and writes its own output, so what it
# checks for in the saved file cannot have been changed by an earlier flow (#173).
REDACT_FORM = "qa-redact.pdf"
REDACTED_OUT = "qa-redacted.pdf"

# Test data, and the only two secrets this rig has. Both are throwaway strings
# for one emulator run; nothing real is ever typed into a QA device.
SECRET = "qa-secret-146"
WRONG = "not-the-right-one"


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
            try:
                # Before recovering, not after: Back on the viewer is Close, so a
                # shot taken afterwards shows Home whatever actually went wrong.
                record["failure_shot"] = os.path.basename(self.d.screencap(
                    os.path.join(self.out, f"{len(self.steps):02d}-{name}--FAILED.png")))
            except Exception:
                pass
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
        """Dismiss whatever is over the viewer, without closing the document.

        Back on the viewer itself *is* Close, so this presses it only while
        something is on top — a dialog, the signature sheet or a system picker —
        and stops as soon as the bare viewer or Home is showing."""
        self.d.dismiss_ime_promo()
        for _ in range(4):
            try:
                if self.at_home() or self.at_bare_viewer():
                    return
                self.d.back(settle=0.8)
            except Exception:
                return

    def at_home(self) -> bool:
        return self.d.exists(text=self.s["open_pdf"])

    def at_bare_viewer(self, name: str | None = None) -> bool:
        """The viewer with nothing over it: a page, the toolbar, and no text field.

        With `name`, also that it is *that* document — Save a copy leaves the copy
        open, so "a document is open" is not the same as "the right one is"."""
        nodes = self.d.nodes()
        if name is not None and not any(
                (n.get("text") or "").lstrip("\u2022 ").startswith(name) for n in nodes):
            return False
        has_page = any((n.get("content-desc") or "").startswith(
            self.s.format("page_n", 1).split()[0]) for n in nodes)
        overlay = any(n.get("class") == "android.widget.EditText" for n in nodes) \
            or any((n.get("text") or "") == self.s["cancel"] for n in nodes)
        return has_page and not overlay

    def ensure_viewer(self, name: str) -> None:
        """Whatever the last step left behind, be in the viewer on `name` again."""
        self.recover()
        if self.at_bare_viewer(name):
            return
        self.restart_clean()
        self.open_file(name)

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

    def type_into(self, index: int, value: str) -> None:
        """Type into the index-th text field, aiming at where it is *now*.

        The soft keyboard moves a dialog up as it opens, so a second field tapped
        at coordinates read before the keyboard appeared lands on the first one —
        which is how both halves of one secret once went into the same box."""
        node = self.d.find(klass="android.widget.EditText", index=index)
        left, top, right, bottom = self.d._bounds(node)
        self.d.tap_xy((left + right) // 2, (top + bottom) // 2, settle=0.9)
        self.d.type_text(value)

    def clear_field(self, index: int = 0, presses: int = 64) -> None:
        node = self.d.find(klass="android.widget.EditText", index=index)
        left, top, right, bottom = self.d._bounds(node)
        self.d.tap_xy((left + right) // 2, (top + bottom) // 2, settle=0.9)
        self.d.press("KEYCODE_MOVE_END", settle=0.3)
        self.d.shell("; ".join(["input keyevent KEYCODE_DEL"] * presses))
        time.sleep(0.5)

    def tap_page(self, x: float, y: float, page: int = 1, settle: float = 2.0) -> None:
        """A point in PDF points on `page`, tapped where it actually is on screen."""
        left, top, right, bottom = self.page_bounds(page)
        sx = left + (x / FORM_W) * (right - left)
        sy = top + ((FORM_H - y) / FORM_H) * (bottom - top)
        self.d.tap_xy(sx, sy, settle=settle)

    # --- shorthands --------------------------------------------------------

    def open_file(self, name: str, settle: float = 5.0) -> None:
        self.pick(name)
        time.sleep(settle)

    def pick(self, name: str) -> None:
        """Home to the tap on the file, and no waiting after it. Split out so a
        caller can time the *app's* open rather than the picker's."""
        self.d.tap(text=self.s["open_pdf"])
        time.sleep(2.5)
        if not self.d.exists(text=name):
            self.d.tap(desc="Search", settle=1.5)
            self.d.type_text(name.replace(".pdf", ""))
            self.d.press("KEYCODE_ENTER", settle=3.0)
        node = self.d.wait_for(text=name)
        left, top, right, bottom = self.d._bounds(node)
        self.d.tap_xy((left + right) // 2, (top + bottom) // 2, settle=0.0)

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
        f.restart_clean(), f.open_file(FORM), f.title())[-1])

    def scroll():
        d.scroll_down(0.5)
        d.scroll_down(0.5)
        d.swipe(*_up(d))
        return "scrolled down twice and back"
    f.step("02-scroll", lambda body=scroll: (f.ensure_viewer(FORM), body())[1])

    def zoom():
        # A zoomed page is wider than the display, and uiautomator clips a node's
        # bounds to the display, so the bounds say nothing. Compare the pixels.
        #
        # Whatever happens, leave the page at 1x. A double tap is timing-sensitive
        # on a loaded emulator (detectTapGestures defers a tap ~300 ms to tell one
        # from the other), and when the second tap was missed the page stayed at 2x
        # — where tap_page maps points through bounds the window manager has
        # clipped to the display, so the *next* flow tapped the wrong place and
        # reported that ticking a checkbox no longer marks the document. One
        # flaky gesture presenting as a defect in another flow is worse than the
        # flake, so the restart is unconditional (#146).
        before = d.adb("exec-out", "screencap -p", binary=True)
        try:
            d.double_tap_fraction(0.5, 0.45)
            zoomed = d.adb("exec-out", "screencap -p", binary=True)
            d.screencap(os.path.join(f.out, "03a-zoomed.png"))
            assert zoomed != before, "double-tap did not zoom the page"
            d.double_tap_fraction(0.5, 0.45)
            back = d.adb("exec-out", "screencap -p", binary=True)
            assert back == before, "a second double-tap did not come back to 1x"
            # Pinch, in both directions and twice over (#336). Asserting only that
            # something changed let "pinch works once" and "pinch-in does nothing"
            # both pass: this flow used to pinch in and check nothing, and the page
            # was left at 1x by the double-tap above rather than by the gesture.
            d.pinch_out()
            d.screencap(os.path.join(f.out, "03b-pinched.png"))
            pinched = d.adb("exec-out", "screencap -p", binary=True)
            assert pinched != before, "pinch did not zoom the page"
            d.pinch_in()
            unpinched = d.adb("exec-out", "screencap -p", binary=True)
            # Byte-equal to the 1x screen, the same assertion the double-tap gets:
            # a pinch that scrolled the list, or that left the page at some other
            # width, does not come back to this.
            assert unpinched == before, "pinch-in did not come back to 1x"
            # And again, because the second one arrives with the render window and
            # the horizontal scroll both already live (#336).
            d.pinch_out()
            again = d.adb("exec-out", "screencap -p", binary=True)
            assert again != before, "a second pinch did not zoom the page"
            d.pinch_in()
            back_again = d.adb("exec-out", "screencap -p", binary=True)
            assert back_again == before, "the second pinch-in did not come back to 1x"
            return ("double-tap 1x -> 2x -> 1x, and pinch out/in twice; "
                    "both at real touch events")
        finally:
            f.restart_clean()
            f.open_file(FORM)
    f.step("03-zoom", lambda body=zoom: (f.ensure_viewer(FORM), body())[1])

    def find():
        d.tap(desc=s["search"], settle=2.5)
        d.type_text("insurance")
        time.sleep(6.0)
        d.screencap(os.path.join(f.out, "04a-search-hits.png"))
        counter = _counter(d)
        assert counter, "no match counter"
        d.tap(desc=s["next_match"], settle=1.5)
        d.tap(desc=s["next_match"], settle=1.5)
        after = _counter(d)
        d.tap(desc=s["close_search"], settle=1.5)
        return f"'insurance' -> {counter}, after two Next -> {after}"
    f.step("04-find", lambda body=find: (f.ensure_viewer(FORM), body())[1])

    def tick():
        for x, y in SQUARES:
            f.tap_page(x, y, settle=2.5)
        for x, y in WIDGETS:
            f.tap_page(x, y, settle=2.5)
        assert f.is_dirty(), "the document is not marked changed"
        return "3 drawn squares and 2 form widgets ticked"
    f.step("05-tick", lambda body=tick: (f.ensure_viewer(FORM), body())[1])

    def sign():
        d.tap(desc=s["sign"], settle=2.0)
        d.tap(text=s["type_signature"], settle=1.5)
        d.type_text(NAME[s.lang])
        time.sleep(1.0)
        d.screencap(os.path.join(f.out, "06a-typed-signature.png"))
        d.tap(text=s["type_signature_add"], settle=2.5)
        # Back on the sheet: the new card arms placement.
        # "<name>. Tap to place, long-press for options." — match the part that is
        # not the name, so it does not matter what the library called this one.
        card = d.wait_for(contains=s.format("signature_card_a11y", "").strip(". "))
        left, top, right, bottom = d._bounds(card)
        d.tap_xy((left + right) // 2, (top + bottom) // 2, settle=1.5)
        f.tap_page(*SIGN_SPOT, settle=4.0)
        return "typed a signature and placed it above the rule"
    f.step("06-sign", lambda body=sign: (f.ensure_viewer(FORM), body())[1])

    def move_and_resize():
        f.tap_page(*SIGN_SPOT, settle=2.0)          # select it
        left, top, right, bottom = f.page_bounds()
        d.swipe(int((left + right) / 2), int((top + bottom) * 0.55),
                int((left + right) / 2) + 40, int((top + bottom) * 0.55) + 30, 500, settle=3.0)
        return "dragged the placed signature"
    f.step("07-move-signature", lambda body=move_and_resize: (f.ensure_viewer(FORM), body())[1])

    def add_text():
        # Back on the viewer is Close, so drop the selection by tapping bare page.
        f.tap_page(480.0, 620.0, settle=1.5)
        d.tap(desc=s["add_text"], settle=1.5)
        f.tap_page(*TEXT_SPOT, settle=2.0)
        d.type_text(NAME[s.lang])
        time.sleep(0.8)
        d.tap(text=s["add"], settle=4.0)
        return f"added '{NAME[s.lang]}'"
    f.step("08-add-text", lambda body=add_text: (f.ensure_viewer(FORM), body())[1])

    def edit_body_text():
        f.tap_page(*SUBTITLE, settle=3.0)
        d.wait_for(text=s["body_text_hint"], timeout=12)
        f.clear_field()
        d.type_text("Sunrise Tool Rental - office copy")
        time.sleep(0.8)
        d.screencap(os.path.join(f.out, "09a-body-edit-dialog.png"))
        d.tap(text=s["save"], settle=5.0)
        return "retyped the document's own subtitle line"
    f.step("09-edit-document-text", lambda body=edit_body_text: (f.ensure_viewer(FORM), body())[1])

    def undo_redo():
        before = f.page_bounds()
        for _ in range(3):
            d.tap(desc=s["undo"], settle=2.5)
        d.screencap(os.path.join(f.out, "10a-after-undo.png"))
        for _ in range(3):
            d.tap(desc=s["redo"], settle=2.5)
        return f"3 undo, 3 redo (page bounds {before})"
    f.step("10-undo-redo", lambda body=undo_redo: (f.ensure_viewer(FORM), body())[1])

    def save():
        assert f.is_dirty(), "nothing to save"
        d.tap(text=s["save"], settle=8.0)
        assert not f.is_dirty(), "still marked changed after Save"
        return "saved back over the opened file"
    f.step("11-save", lambda body=save: (f.ensure_viewer(FORM), body())[1])

    def save_as():
        d.tap(desc=s["more_options"], settle=1.0)
        d.tap(text=s["save_a_copy"], settle=3.0)
        d.screencap(os.path.join(f.out, "12a-create-document.png"))
        f.clear_field()
        d.type_text("qa-form-copy.pdf")
        d.tap(contains="SAVE", settle=6.0)
        return "saved a copy as qa-form-copy.pdf"
    f.step("12-save-a-copy", lambda body=save_as: (f.ensure_viewer(FORM), body())[1])


def redaction_flows(f: Flow) -> list[dict]:
    """Redact, end to end, and then read the saved file with a different toolchain.

    The rig had no redaction coverage at all — the screen inventory predates the
    feature on Android and #173's real-window checks were done on Windows, the Mac
    and iOS. So this walks it: arm, mark, confirm, save a copy, and then check with
    qpdf and pdftotext that the marked words are gone and an unmarked one is not.

    R6-R9 are #329's half of it, and the half that had no coverage anywhere on
    Android: a mark can be taken back (Undo), taken off (the ✕ on the selected
    mark), cleared in one action (the ⋯ item), and it does not outlive the document
    it was made on. Every one of those reads the *page* rather than a count the app
    keeps in its head, because the count is what lied in 2.0.1.
    """
    d, s = f.d, f.s
    result: dict = {}

    # Its own copy: the edit group saves over qa-form.pdf, and this group needs to say
    # what was in the file before and after.
    d.shell(f"cp /sdcard/Download/MegaPDF-Test-Form.pdf /sdcard/Download/{REDACT_FORM}")
    d.shell("content call --uri content://media/external/file --method scan_file "
            f"--arg /sdcard/Download/{REDACT_FORM}")
    time.sleep(2)

    def arm():
        # Open it here rather than through ensure_viewer in the next step: arming and
        # then re-opening loses the mode, which is how this walk first read as "the
        # drag does not mark" when the drag was never made with the tool on.
        f.restart_clean()
        f.open_file(REDACT_FORM)
        # Two taps now, not one (#328): Redact is a named row in the ⋯ menu, and the
        # armed state has to be *in the tree* while the menu is open (#173).
        _arm_redact(d, s)
        return "armed from the ⋯ menu, and the row reports it"
    f.step("R1-arm-redact", arm)

    def mark():
        count = _drag_along_subtitle(f)
        assert count, "a drag along the line made no mark"
        result["mark_note"] = count
        return f"dragged along the subtitle; the page reports {count!r}"
    f.step("R2-mark-a-line", mark)

    def disarms():
        state = _menu_state(d, s, s["redact"])
        _close_menu(d, s)
        assert state == "off", f"Redact stayed armed after a mark landed ({state!r})"
        return "the tool disarms itself once the mark lands"
    f.step("R3-disarms-after-marking", disarms)

    def confirm_and_save():
        d.tap(text=s["save"], settle=3.0)
        d.wait_for(text=s["redact_confirm_title"], timeout=15)
        d.screencap(os.path.join(f.out, "R4a-redact-confirmation.png"))
        # The body and the count are one Text joined by a blank line, so neither is a
        # node of its own — match inside the node rather than against it.
        blob = "\n".join((n.get("text") or "") for n in d.nodes())
        body = s["redact_confirm_body"] in blob
        counted = s["redact_mark_count_one"] in blob
        assert body, "the confirmation gave no reason"
        assert counted, "the confirmation did not say how many areas are marked"
        d.tap(text=s["redact_save_copy"], settle=4.0)
        f.clear_field()
        d.type_text(REDACTED_OUT)
        d.tap(contains="SAVE", settle=8.0)
        for _ in range(24):
            if not f.is_dirty():
                break
            time.sleep(5)
        return "confirmation shown with its reason and the mark count; saved a copy"
    f.step("R4-confirm-and-save-a-copy", confirm_and_save)

    def read_it_back():
        out = os.path.join(f.out, REDACTED_OUT)
        d.adb("pull", f"/sdcard/Download/{REDACTED_OUT}", out)
        text = _pdftotext(out)
        # "customer copy" is only in the subtitle that was marked. "Sunrise Tool Rental"
        # is in the body as well, so it must survive — which is what makes this a test of
        # redaction rather than of deletion.
        gone = "customer copy" not in text
        kept = "Sunrise Tool Rental" in text and "Options" in text
        qpdf = _qpdf_check(out)
        result.update({"marked_text_gone": gone, "unmarked_text_kept": kept, "qpdf": qpdf})
        assert gone, "the marked words are still in the saved file's text"
        assert kept, "unmarked text went missing too"
        return f"marked words gone, unmarked text kept, qpdf: {qpdf}"
    f.step("R5-read-the-saved-file-back", read_it_back, shot=False)

    def undo_the_mark():
        # Marking used to go round the history entirely: the core's text selection is
        # what decides which marks a drag makes, and it makes them as it answers, so
        # nothing recorded the gesture and Undo could not take it back (#329). One
        # press has to take the whole drag.
        f.restart_clean()
        f.open_file(REDACT_FORM)
        _arm_redact(d, s)
        assert _drag_along_subtitle(f), "a drag along the line made no mark"
        d.tap(desc=s["undo"], settle=2.5)
        left = _marks_on_screen(d, s)
        assert left is None, f"Undo left the page saying {left!r}"
        # And with nothing marked and nothing changed there is nothing to save, so
        # Save — which a mark deliberately makes reachable — must not be offered.
        save = d.find(text=s["save"])
        assert save.get("enabled") != "true", \
            f"Save is offered with nothing marked and nothing changed ({save.get('enabled')!r})"
        return "one press of Undo took the whole drag back, and Save is not offered"
    f.step("R6-undo-takes-back-the-mark", undo_the_mark)

    def remove_one_mark():
        # The other half of #329: a mark comes off without going near Undo. Tapping
        # it selects it — the same chrome a signature gets — and the ✕ removes it.
        f.restart_clean()
        f.open_file(REDACT_FORM)
        _arm_redact(d, s)
        assert _drag_along_subtitle(f), "a drag along the line made no mark"
        d.tap(desc=s["redact_mark_name"], settle=1.5)
        d.tap(text="✕", settle=2.5)
        left = _marks_on_screen(d, s)
        assert left is None, f"the ✕ left the mark on the page: {left!r}"
        # Removal is an edit, so one press of Undo puts it back where it was.
        d.tap(desc=s["undo"], settle=2.5)
        back = _marks_on_screen(d, s)
        assert back, "Undo did not put the removed mark back"
        return f"tapped the mark, the ✕ removed it, Undo put it back ({back!r})"
    f.step("R7-remove-a-mark-with-the-button", remove_one_mark)

    def clear_all_marks():
        # "Clear all marks" is one ⋯ item and one undo step (#329) — and the item
        # only exists while there is something to clear.
        f.restart_clean()
        f.open_file(REDACT_FORM)
        _arm_redact(d, s)
        assert _drag_along_subtitle(f), "a drag along the line made no mark"
        # A second drag over blank paper: it marks the rectangle it was drawn as, so
        # "all" is more than one without needing a second line's coordinates.
        left, top, right, bottom = f.page_bounds()
        blank = top + ((FORM_H - 200.0) / FORM_H) * (bottom - top)
        d.swipe(int(left + 0.15 * (right - left)), int(blank),
                int(left + 0.55 * (right - left)), int(blank), 700, settle=3.0)
        two = s.format("redact_mark_count", 2)
        counted = _mark_count(d, s)
        assert counted == two, f"two drags should mark two areas, the page says {counted!r}"
        d.tap(desc=s["more_options"], settle=1.0)
        d.tap(text=s["redact_clear_marks"], settle=2.5)
        gone = _marks_on_screen(d, s)
        assert gone is None, f"clearing left marks on the page: {gone!r}"
        d.tap(desc=s["undo"], settle=2.5)
        back = _mark_count(d, s)
        assert back == two, f"one press of Undo should bring both back, page says {back!r}"
        # The menu offers clearing only while there is something to clear: it has to
        # be gone once the marks are, or it is a dead item.
        d.tap(desc=s["more_options"], settle=1.0)
        assert not d.exists(text=s["redact_clear_marks"]), \
            "the ⋯ menu still offers Clear all marks with no marks on the page"
        _close_menu(d, s)
        return "one item cleared both, one Undo brought both back, and the item went with them"
    f.step("R8-clear-all-marks-is-one-step", clear_all_marks)

    def marks_do_not_survive_closing():
        # The bug Dave hit (#329): the view model outlives the document, and the marks
        # were never cleared with it — so the last file's marks were drawn over the
        # next one at the same page indices, and Save asked about a document that had
        # never been marked. A *different* file is opened, because reopening the same
        # one hides exactly this.
        f.restart_clean()
        f.open_file(REDACT_FORM)
        _arm_redact(d, s)
        assert _drag_along_subtitle(f), "a drag along the line made no mark"
        d.tap(desc=s["close_document"], settle=3.0)
        # A mark is not a change to the document, so nothing should be asked about
        # saving it: "you have unsaved changes" is the wrong question here.
        asked = d.exists(text=s["unsaved_changes"])
        result["close_asked_to_save"] = asked
        assert not asked, "closing with only a mark on the page asked to save it"
        f.open_file(FORM)
        left = _marks_on_screen(d, s)
        result["marks_after_reopen"] = left
        assert left is None, f"the closed document's marks are still on the page: {left!r}"
        return "the mark did not survive closing, and the next document opened without it"
    f.step("R9-marks-do-not-survive-closing", marks_do_not_survive_closing)

    return [result]


def _menu_state(d: Device, s: Strings, label: str) -> str:
    """A ⋯-menu tool's on/off state, read while the menu is open (#328/#329).

    Redact is a menu row now rather than a toolbar button for two reasons, and both
    of them change how its state is read: the row is only in the tree between
    opening the menu and tapping a row, and a menu row is not the checkable node
    the toolbar's tools are. Compose publishes the armed state as the `selected`
    semantic, which is an attribute uiautomator dumps — a state description, which
    is what a screen reader actually reads, is not.

    Leaves the menu open: the caller closes it, and reading the state never changes
    it.
    """
    d.tap(desc=s["more_options"], settle=1.0)
    node = d.wait_for(text=label)
    return "on" if node.get("selected") == "true" else "off"


def _close_menu(d: Device, s: Strings) -> None:
    """Dismisses the ⋯ menu if it is open.

    Back is the popup's while it is showing, so it dismisses the menu and never
    reaches the viewer — where Back *is* Close. Guarded, because pressing it with
    nothing open would close the document.
    """
    if d.exists(text=s["redact"]) or d.exists(text=s["redact_clear_marks"]):
        d.back(settle=0.8)


def _arm_redact(d: Device, s: Strings) -> None:
    """Arms Redact the way a person now has to: ⋯ menu, then the row (#328)."""
    d.tap(desc=s["more_options"], settle=1.0)
    d.tap(text=s["redact"], settle=1.5)
    armed = _menu_state(d, s, s["redact"])
    assert armed == "on", f"Redact armed but the accessibility tree says {armed!r}"
    _close_menu(d, s)


def _drag_along_subtitle(f: Flow) -> str | None:
    """The drag every redaction flow makes, and what the page says it marked.

    Straight along the subtitle line, which is how text is redacted — and which
    marked nothing at all until the flat-drag fix (#173).
    """
    d, s = f.d, f.s
    left, top, right, bottom = f.page_bounds()
    y = top + ((FORM_H - SUBTITLE[1]) / FORM_H) * (bottom - top)
    d.swipe(int(left + 0.10 * (right - left)), int(y),
            int(left + 0.88 * (right - left)), int(y), 700, settle=3.0)
    return _mark_count(d, s)


def _marks_on_screen(d: Device, s: Strings) -> str | None:
    """Whatever on screen says a mark is there — its count, or a mark node itself.

    Used for the assertions that a mark is *gone*: the count is what a person
    reads, and the per-mark nodes are what a screen reader finds, so a stale map
    that drew either one fails.
    """
    count = _mark_count(d, s)
    if count:
        return count
    return s["redact_mark_name"] if d.exists(desc=s["redact_mark_name"]) else None


def _mark_count(d: Device, s: Strings) -> str | None:
    """Whatever on screen says how many areas are marked, label or overlay."""
    wanted = {s["redact_mark_count_one"], s.format("redact_mark_count", 1),
              s.format("redact_mark_count", 2)}
    for node in d.nodes():
        for key in ("content-desc", "text"):
            value = node.get(key) or ""
            if value in wanted:
                return value
    return None


def _pdftotext(path: str) -> str:
    import subprocess
    return subprocess.run(["pdftotext", path, "-"], capture_output=True, text=True).stdout


def _qpdf_check(path: str) -> str:
    import subprocess
    done = subprocess.run(["qpdf", "--check", path], capture_output=True, text=True)
    return "no errors" if done.returncode == 0 else done.stdout.strip().splitlines()[-1][:120]


def protection_flows(f: Flow) -> None:
    d, s = f.d, f.s

    def set_protection():
        d.tap(desc=s["more_options"], settle=1.0)
        d.tap(text=s["security_password_menu"], settle=2.0)
        f.type_into(0, SECRET)
        f.type_into(1, SECRET)
        d.screencap(os.path.join(f.out, "13a-set-dialog-filled.png"))
        d.tap(text=s["security_set"], settle=12.0)
        assert not d.exists(text=s["security_password_mismatch"]), \
            "the two halves did not go into two fields"
        d.wait_for(desc=s.format("page_n", 1), timeout=30)
        return "protection set; the document saved itself"
    f.step("13-set-protection", lambda body=set_protection: (f.ensure_viewer(FORM), body())[1])

    def reopen_protected():
        d.tap(desc=s["close_document"], settle=2.0)
        f.open_file(FORM, settle=4.0)
        d.wait_for(text=s["password_required"], timeout=15)
        d.type_text(WRONG)
        d.tap(text=s["open"], settle=4.0)
        rejected = d.exists(text=s["wrong_password"])
        d.screencap(os.path.join(f.out, "14a-rejected.png"))
        f.clear_field()
        d.type_text(SECRET)
        d.tap(text=s["open"], settle=6.0)
        d.wait_for(desc=s.format("page_n", 1), timeout=20)
        return f"wrong one rejected ({rejected}), right one opened it"
    f.step("14-reopen-protected", lambda: (f.ensure_viewer(FORM), reopen_protected())[1])

    def remove_protection():
        d.tap(desc=s["more_options"], settle=1.0)
        d.tap(text=s["security_password_menu"], settle=2.0)
        d.tap(text=s["security_remove"], settle=10.0)
        return "protection removed; the document saved itself"
    f.step("15-remove-protection", lambda body=remove_protection: (f.ensure_viewer(FORM), body())[1])

    def reopen_unprotected():
        d.tap(desc=s["close_document"], settle=2.0)
        f.open_file(FORM, settle=5.0)
        assert not d.exists(text=s["password_required"]), "still asks for a password"
        d.wait_for(desc=s.format("page_n", 1), timeout=20)
        return "opens with no prompt"
    f.step("16-reopen-unprotected", lambda: (f.ensure_viewer(FORM), reopen_unprotected())[1])


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
    f.step("17-close-with-unsaved-changes", lambda: (f.ensure_viewer(FORM), close_with_changes())[1])

    def killed():
        f.open_file(FORM, settle=5.0)
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
            # From the tap on the file to the first page drawn: the app's open, not
            # the picker's. Timing open_file() instead measured the picker search.
            f.pick(name)
            started = time.time()
            d.wait_for(desc=s.format("page_n", 1), timeout=240, poll=0.2)
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
                f.clear_field()
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
    parser.add_argument("--groups", default="edit,redact,protection,close,large")
    parser.add_argument("--only-large", default="")
    args = parser.parse_args()

    d = Device(args.serial)
    d.wait_boot()
    d.set_app_locale(args.lang)
    d.set_dark_mode(False)
    d.set_font_scale(1.0)
    d.shell("rm -f /sdcard/Download/qa-form*.pdf")
    d.shell("cp /sdcard/Download/MegaPDF-Test-Form.pdf /sdcard/Download/qa-form.pdf")
    d.shell("content call --uri content://media/external/file --method scan_file "
            "--arg /sdcard/Download/qa-form.pdf")
    time.sleep(2)

    f = Flow(d, Strings(args.lang), args.out)
    groups = set(args.groups.split(","))
    large: list[dict] = []
    if "edit" in groups:
        edit_flows(f)
    redaction: list[dict] = []
    if "redact" in groups:
        redaction = redaction_flows(f)
    if "protection" in groups:
        protection_flows(f)
    if "close" in groups:
        close_and_kill_flows(f)
    not_applicable(f)
    if "large" in groups:
        only = [x for x in args.only_large.split(",") if x] or None
        large = large_file_flows(f, only)

    summary = {"lang": args.lang, "steps": f.steps,
               "redaction": redaction, "large_files": large}
    with open(os.path.join(args.out, "flows.json"), "w") as handle:
        json.dump(summary, handle, indent=2, ensure_ascii=False)
    failed = [x for x in f.steps if not x.get("ok")] + [x for x in large if not x.get("ok")]
    print(f"== flows: {len(failed)} failures ==", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
