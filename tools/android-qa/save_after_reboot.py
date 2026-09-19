#!/usr/bin/env python3
"""Save back to a document reopened from Recents after the device restarts.

`ACTION_OPEN_DOCUMENT` grants read *and* write, but a grant the app does not
persist lasts only until the device restarts. So the question this answers is
not "does Save work" (it does, the same session) but "does Save still work on a
document reopened from Recents after a reboot" — the case the listing's "Save
writes back to the original file" makes a promise about.

    save_after_reboot.py [--serial emulator-5554] [--out /work/out/save-reboot]

Run inside the tools/android-qa container, against an AVD booted by boot.sh with
the debug APK installed. Prints one verdict line and exits 0 when the save after
the reboot wrote the file in place, 1 when it did not.
"""
from __future__ import annotations

import argparse
import hashlib
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from adbui import Device  # noqa: E402
from flows import FORM, SQUARES, Flow  # noqa: E402
from strings import Strings  # noqa: E402

REMOTE = f"/sdcard/Download/{FORM}"


def md5_on_device(d: Device) -> str:
    return d.shell(f"md5sum {REMOTE}").split()[0]


def grants(d: Device) -> str:
    """The app's persisted URI grants, as the system holds them."""
    out = d.shell("dumpsys activity permissions")
    lines = [l.strip() for l in out.splitlines() if "megapdf" in l.lower() or "UriPermission" in l]
    return "\n".join(lines[:12])


def reboot(d: Device) -> None:
    d.adb("reboot", timeout=120)
    time.sleep(10)
    d.adb("wait-for-device", timeout=600)
    d.wait_boot(600)
    for cmd in ("svc power stayon true", "wm dismiss-keyguard",
                "input keyevent KEYCODE_WAKEUP", "input keyevent KEYCODE_MENU"):
        d.shell(cmd)
        time.sleep(1)


def edit_and_save(f: Flow, step: str) -> tuple[bool, str]:
    before = md5_on_device(f.d)
    x, y = SQUARES[0] if step == "first" else SQUARES[1]
    f.tap_page(x, y, settle=2.5)
    assert f.is_dirty(), f"{step}: ticking a box did not mark the document changed"
    f.d.tap(text=f.s["save"], settle=8.0)
    after = md5_on_device(f.d)
    return (after != before and not f.is_dirty()), f"md5 {before[:8]} -> {after[:8]}, dirty={f.is_dirty()}"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--serial", default="emulator-5554")
    ap.add_argument("--out", default="/work/out/save-reboot")
    ap.add_argument("--fixture", default="/work/repo/docs/review/MegaPDF-Test-Form.pdf")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)

    d = Device(args.serial)
    d.wait_boot()
    d.set_app_locale("en")
    d.push_document(args.fixture, FORM)
    time.sleep(2)
    f = Flow(d, Strings("en"), args.out)

    f.restart_clean()
    f.open_file(FORM)
    ok1, note1 = edit_and_save(f, "first")
    print(f"same session: save {'wrote the file' if ok1 else 'DID NOT write'} ({note1})", flush=True)
    print("persisted grants before reboot:\n" + grants(d), flush=True)
    d.screencap(os.path.join(args.out, "1-saved-same-session.png"))

    reboot(d)
    print("rebooted; persisted grants after reboot:\n" + grants(d), flush=True)
    d.launch(wait=4)
    d.dismiss_ime_promo()
    d.screencap(os.path.join(args.out, "2-home-after-reboot.png"))
    d.tap(contains=FORM, settle=6.0)
    d.screencap(os.path.join(args.out, "3-reopened-from-recents.png"))
    ok2, note2 = edit_and_save(f, "second")
    d.screencap(os.path.join(args.out, "4-after-save.png"))
    verdict = "PASS" if ok2 else "FAIL"
    print(f"after reboot, reopened from Recents: save {'wrote the file in place' if ok2 else 'DID NOT write'} "
          f"({note2}) -> {verdict}", flush=True)
    subprocess.run(["adb", "-s", args.serial, "pull", REMOTE, os.path.join(args.out, FORM)],
                   capture_output=True)
    return 0 if ok2 else 1


if __name__ == "__main__":
    sys.exit(main())
