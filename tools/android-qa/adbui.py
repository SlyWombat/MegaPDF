#!/usr/bin/env python3
"""A small adb + uiautomator driver for the Android QA pass (#146 §1b).

The app has no test hooks beyond the `--es screenshot <state>` launch extra, so
the pass drives the real UI: dump the view hierarchy, find a node by its text or
its content description, tap the middle of it. That is slower than an
instrumentation test and it is the point — it exercises what a person's finger
would, including the system pickers the app hands off to.

Run inside the container built by `tools/android-qa/Dockerfile`, against an AVD
booted by `tools/android-qa/boot.sh`.
"""
from __future__ import annotations

import os
import re
import subprocess
import time
import xml.etree.ElementTree as ET

from touch import Touch

PACKAGE = "ca.electricrv.megapdf"
ACTIVITY = f"{PACKAGE}/com.megapdf.android.MainActivity"


class AdbError(RuntimeError):
    pass


class NotFound(RuntimeError):
    pass


class Device:
    def __init__(self, serial: str = "emulator-5554"):
        self.serial = serial
        # Real kernel touch events, for the gestures `input` cannot make.
        self.touch = Touch(self)

    # --- plumbing ----------------------------------------------------------

    def adb(self, *args: str, binary: bool = False, timeout: int = 300):
        proc = subprocess.run(
            ["adb", "-s", self.serial, *args],
            capture_output=True, timeout=timeout)
        if proc.returncode != 0:
            raise AdbError(f"adb {' '.join(args)}: {proc.stderr.decode(errors='replace')}")
        return proc.stdout if binary else proc.stdout.decode(errors="replace")

    def shell(self, command: str, **kw) -> str:
        return self.adb("shell", command, **kw)

    # --- device state ------------------------------------------------------

    def wait_boot(self, seconds: int = 300) -> None:
        deadline = time.time() + seconds
        while time.time() < deadline:
            try:
                if self.shell("getprop sys.boot_completed").strip() == "1":
                    return
            except AdbError:
                pass
            time.sleep(2)
        raise AdbError("device never finished booting")

    def demo_status_bar(self) -> None:
        """A fixed clock, a full battery, no notification icons (#49)."""
        for command in (
            "am broadcast -a com.android.systemui.demo -e command enter",
            "am broadcast -a com.android.systemui.demo -e command clock -e hhmm 0941",
            "am broadcast -a com.android.systemui.demo -e command battery "
            "-e level 100 -e plugged false",
            # `fully true` or SystemUI draws the "no internet" exclamation over the
            # Wi-Fi icon, the emulator having no validated connection (#146).
            "am broadcast -a com.android.systemui.demo -e command network "
            "-e wifi show -e level 4 -e fully true",
            "am broadcast -a com.android.systemui.demo -e command network -e mobile hide",
            "am broadcast -a com.android.systemui.demo -e command notifications -e visible false",
        ):
            try:
                self.shell(command)
            except AdbError:
                pass

    def set_app_locale(self, tag: str | None) -> None:
        """Android 13's per-app language (#91). None or 'en' clears the override."""
        if not tag or tag == "en":
            self.shell(f"cmd locale set-app-locales {PACKAGE} --user 0 --locales")
        else:
            self.shell(f"cmd locale set-app-locales {PACKAGE} --user 0 --locales {tag}")

    def set_dark_mode(self, dark: bool) -> None:
        self.shell(f"cmd uimode night {'yes' if dark else 'no'}")

    def set_font_scale(self, scale: float) -> None:
        self.shell(f"settings put system font_scale {scale}")

    def screen_size(self) -> tuple[int, int]:
        text = self.shell("wm size")
        match = re.search(r"(\d+)x(\d+)\s*$", text.strip().splitlines()[-1])
        return int(match.group(1)), int(match.group(2))

    # --- the app -----------------------------------------------------------

    def install(self, apk: str) -> None:
        self.adb("install", "-r", "-g", apk, timeout=600)

    def stop_app(self) -> None:
        self.shell(f"am force-stop {PACKAGE}")

    def clear_app(self) -> None:
        self.shell(f"pm clear {PACKAGE}")

    def launch(self, screenshot_state: str | None = None, wait: float = 3.0) -> None:
        extra = f" --es screenshot {screenshot_state}" if screenshot_state else ""
        self.shell(f"am start -n {ACTIVITY}{extra}")
        time.sleep(wait)

    def pid(self) -> int | None:
        out = self.shell(f"pidof {PACKAGE}").strip()
        return int(out.split()[0]) if out else None

    def peak_rss_kb(self) -> int | None:
        """VmHWM — the high-water mark of the app process's resident set."""
        pid = self.pid()
        if pid is None:
            return None
        for line in self.shell(f"cat /proc/{pid}/status").splitlines():
            if line.startswith("VmHWM:"):
                return int(line.split()[1])
        return None

    def total_pss_kb(self) -> int | None:
        out = self.shell(f"dumpsys meminfo {PACKAGE}")
        match = re.search(r"TOTAL PSS:\s*(\d+)", out) or re.search(r"TOTAL\s+(\d+)", out)
        return int(match.group(1)) if match else None

    # --- the view hierarchy ------------------------------------------------

    def dump(self, retries: int = 4) -> ET.Element:
        for attempt in range(retries):
            try:
                self.shell("uiautomator dump /sdcard/win.xml")
                raw = self.adb("exec-out", "cat /sdcard/win.xml", binary=True)
                text = raw.decode("utf-8", errors="replace")
                start = text.find("<?xml")
                if start >= 0:
                    return ET.fromstring(text[start:])
            except (AdbError, ET.ParseError):
                pass
            time.sleep(1.0 + attempt)
        raise AdbError("could not dump the view hierarchy")

    @staticmethod
    def _bounds(node: ET.Element) -> tuple[int, int, int, int]:
        m = re.match(r"\[(\d+),(\d+)]\[(\d+),(\d+)]", node.get("bounds", ""))
        return tuple(int(g) for g in m.groups())  # type: ignore[return-value]

    def nodes(self, root: ET.Element | None = None) -> list[ET.Element]:
        return list((root if root is not None else self.dump()).iter("node"))

    def find(self, *, text: str | None = None, desc: str | None = None,
             contains: str | None = None, rid: str | None = None,
             klass: str | None = None, root: ET.Element | None = None,
             index: int = 0) -> ET.Element:
        """The index-th node matching every condition given."""
        found = []
        for node in self.nodes(root):
            if text is not None and node.get("text") != text:
                continue
            if desc is not None and node.get("content-desc") != desc:
                continue
            if contains is not None and contains not in (
                    (node.get("text") or "") + "\x00" + (node.get("content-desc") or "")):
                continue
            if rid is not None and node.get("resource-id") != rid:
                continue
            if klass is not None and node.get("class") != klass:
                continue
            found.append(node)
        if len(found) <= index:
            raise NotFound(
                f"no node #{index} matching "
                f"text={text!r} desc={desc!r} contains={contains!r} rid={rid!r}")
        return found[index]

    def exists(self, **kw) -> bool:
        try:
            self.find(**kw)
            return True
        except NotFound:
            return False

    def wait_for(self, timeout: float = 15.0, poll: float = 0.7, **kw) -> ET.Element:
        deadline = time.time() + timeout
        last = None
        while time.time() < deadline:
            try:
                return self.find(**kw)
            except NotFound as exc:
                last = exc
            time.sleep(poll)
        raise last or NotFound(str(kw))

    # The keyboard's stylus onboarding sheet, which the emulator's touchscreen is
    # enough to trigger and which covers whatever is under it. boot.sh turns the
    # feature off; this is the belt to that pair of braces.
    IME_PROMOS = ("Try out your stylus", "Essayez votre stylet",
                  "Essaie ton stylet")

    def dismiss_ime_promo(self) -> bool:
        """True if a keyboard promo was on screen and has been dismissed."""
        try:
            root = self.dump()
        except AdbError:
            return False
        if not any((n.get("text") or "") in self.IME_PROMOS for n in root.iter("node")):
            return False
        for _ in range(3):
            self.press("KEYCODE_BACK", settle=0.8)
            try:
                root = self.dump()
            except AdbError:
                return True
            if not any((n.get("text") or "") in self.IME_PROMOS for n in root.iter("node")):
                return True
        return True

    # --- input -------------------------------------------------------------

    def tap_xy(self, x: int, y: int, settle: float = 0.8) -> None:
        self.shell(f"input tap {int(x)} {int(y)}")
        time.sleep(settle)

    def tap(self, settle: float = 0.8, **kw) -> None:
        node = self.wait_for(**kw)
        left, top, right, bottom = self._bounds(node)
        self.tap_xy((left + right) // 2, (top + bottom) // 2, settle=settle)

    def long_press(self, settle: float = 1.2, **kw) -> None:
        node = self.wait_for(**kw)
        left, top, right, bottom = self._bounds(node)
        x, y = (left + right) // 2, (top + bottom) // 2
        self.shell(f"input swipe {x} {y} {x} {y} 900")
        time.sleep(settle)

    def tap_fraction(self, fx: float, fy: float, settle: float = 0.8) -> None:
        """A point on the screen as a fraction of its size — for the page itself."""
        w, h = self.screen_size()
        self.tap_xy(int(w * fx), int(h * fy), settle=settle)

    # `input text` goes through KeyCharacterMap, which has no mapping for
    # characters outside ASCII: "input text Hélène" throws
    # NullPointerException: Attempt to get length of null array and leaves
    # "H elene" in the field. There is no `cmd clipboard` on this image either,
    # so there is no way to type an accent from adb. Anything accented is
    # therefore driven through the app's own string resources (the
    # `--es screenshot` states) rather than typed, and `accents.py` proves the
    # accented text reaches the saved PDF.
    ASCII_ONLY = True

    def type_text(self, value: str, settle: float = 0.6) -> None:
        """Types `value`. Raises rather than quietly mangling a non-ASCII string."""
        if not value.isascii():
            raise AdbError(
                f"adb cannot type {value!r}: input text is ASCII-only. "
                "Drive accented text through the app's own resources instead.")
        escaped = value.replace("\\", "\\\\").replace('"', '\\"')
        self.shell(f'input text "{escaped.replace(" ", "%s")}"')
        time.sleep(settle)

    def press(self, key: str, settle: float = 0.8) -> None:
        self.shell(f"input keyevent {key}")
        time.sleep(settle)

    def back(self, settle: float = 0.9) -> None:
        self.press("KEYCODE_BACK", settle)

    def swipe(self, x1: int, y1: int, x2: int, y2: int, ms: int = 300,
              settle: float = 0.8) -> None:
        self.shell(f"input swipe {x1} {y1} {x2} {y2} {ms}")
        time.sleep(settle)

    def scroll_down(self, fraction: float = 0.6, ms: int = 300) -> None:
        w, h = self.screen_size()
        self.swipe(w // 2, int(h * 0.75), w // 2, int(h * (0.75 - fraction)), ms)

    # Zoom needs real touch events. Two `input tap` calls are two JVM launches,
    # which never land inside the 300 ms double-tap window, and `input` has one
    # pointer, so it cannot pinch at all. See touch.py.

    def pinch_out(self, fy: float = 0.45, settle: float = 1.5) -> None:
        w, h = self.screen_size()
        self.touch.pinch(w * 0.5, h * fy, from_gap=w * 0.17, to_gap=w * 0.8,
                         steps=20, settle=settle)

    def pinch_in(self, fy: float = 0.45, settle: float = 1.5) -> None:
        w, h = self.screen_size()
        self.touch.pinch(w * 0.5, h * fy, from_gap=w * 0.8, to_gap=w * 0.17,
                         steps=20, settle=settle)

    def double_tap_fraction(self, fx: float, fy: float, settle: float = 1.5) -> None:
        w, h = self.screen_size()
        self.touch.double_tap(w * fx, h * fy, settle=settle)

    # --- capture -----------------------------------------------------------

    def screencap(self, path: str) -> str:
        os.makedirs(os.path.dirname(path), exist_ok=True)
        data = self.adb("exec-out", "screencap -p", binary=True)
        with open(path, "wb") as handle:
            handle.write(data)
        return path

    # --- files -------------------------------------------------------------

    def push_document(self, local: str, name: str | None = None) -> str:
        """Puts a file in Downloads and makes the Files picker see it."""
        name = name or os.path.basename(local)
        remote = f"/sdcard/Download/{name}"
        self.adb("push", local, remote, timeout=1800)
        self.shell(
            "content call --uri content://media/external/file "
            f"--method scan_file --arg {remote}")
        return remote
