#!/usr/bin/env python3
"""The preview-video gate: check a store app preview, and cut it into stills.

    python3 tools/preview-gate.py -o /tmp/preview ~/captures/video-2.0/ios/en/iphone-6_9-preview.mp4

A clip is the one capture nobody can read at a glance: thirty seconds of it
goes past faster than the eye holds, and everything the stills gate looks for
— a word clipped by the frame, a stale screen, the wrong language, a name
without its accents — can sit in one frame of nine hundred and never be seen.

So this does two things and neither of them is clever:

1. **The container**, against what the store takes and what the repo's own
   tooling is supposed to produce: the pixel size is a listing slot, the
   duration is inside App Store Connect's 15-30 s, the frame rate is the
   constant 30 the cutting scripts force, the codec is H.264, and there is an
   audio track (App Store Connect refuses a preview without one — the
   MOV_RESAVE_STEREO rejection the cutting scripts carry a silent stereo
   track for).
2. **The frames**: one still per second, written as a capture set that
   `tools/capture-gate/gate.py --store video` reads, so that a clip is
   reviewed by the same checks and on the same contact sheet as the stills.

The gate is run **one language at a time**, and that is deliberate. A clip is
time-compressed to fit 30 s from however long its own take ran, and the takes
are not the same length — the French name takes longer to type — so still 12
of the English clip and still 12 of the fr-CA clip are not the same moment and
have no business being compared. Run per language, the cross-language checks
stand down and say so, and the one left doing real work is `chrome`: inside a
clip the posed status bar is the same pixels in every frame, so a notification
or a banner arriving halfway through has twenty-odd siblings to disagree with.

Neither replaces the eye. A clip is cut into twenty-odd stills precisely so
that a person can look at each one, which is what #146 §3 asks for; this
makes that a page of thumbnails instead of a scrubber.

Nothing is uploaded, and nothing is written next to the clip.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                "capture-gate"))
import stores                                                    # noqa: E402

PROFILE = stores.profile("video")

# App Store Connect's app-preview rules, as docs/app-store-listing.md § App
# preview videos states them and as the cutting scripts implement them.
MIN_SECONDS, MAX_SECONDS = 15.0, 30.0
FPS = 30
CODEC = "h264"


def probe(clip: str) -> dict:
    out = subprocess.run(
        ["ffprobe", "-v", "error", "-print_format", "json",
         "-show_streams", "-show_format", clip],
        capture_output=True, text=True, check=True).stdout
    return json.loads(out)


def rate(value: str) -> float:
    """`30/1` → 30.0. ffprobe gives frame rates as a fraction."""
    num, _, den = value.partition("/")
    try:
        return float(num) / float(den or 1)
    except (ValueError, ZeroDivisionError):
        return 0.0


def identify(clip: str, device: str | None, lang: str | None):
    """The device and language of a clip, from its path when not given.

    The cutting scripts file iOS clips as `<lang>/<label>-preview.mp4` and Mac
    clips as `macos-<lang>-<theme>-recorded-preview.mp4`, so both are
    readable; anything else has to be named on the command line rather than
    guessed at.
    """
    name = os.path.basename(clip)
    if not device:
        match = re.match(r"(iphone-[0-9_]+|ipad-[0-9]+)-", name)
        device = match.group(1) if match else ("mac" if "macos" in name else None)
    if not lang:
        match = re.search(r"macos-(en|fr-CA|fr)-", name)
        lang = stores.canonical(match.group(1)) if match else None
        if not lang:
            for part in os.path.abspath(clip).split(os.sep):
                found = stores.language_of(part)
                if found:
                    lang = found
    return device, lang


def container_checks(info: dict, device: str | None) -> list[tuple[str, str, str]]:
    """(verdict, check, what was measured) for the clip as a whole."""
    video = next((s for s in info["streams"] if s["codec_type"] == "video"), None)
    audio = [s for s in info["streams"] if s["codec_type"] == "audio"]
    found = []
    if not video:
        return [("flag", "video", "no video stream")]

    size = (int(video["width"]), int(video["height"]))
    slots = PROFILE["slots"].get(device or "", None)
    if slots is None:
        found.append(("skip", "size", f"{size[0]}x{size[1]}; no slot named "
                                      f"{device!r} to measure it against"))
    elif size in [tuple(s) for s in slots]:
        found.append(("pass", "size", f"{size[0]}x{size[1]} is the {device} slot"))
    else:
        found.append(("flag", "size", f"{size[0]}x{size[1]} is not a {device} "
                                      f"slot ({slots})"))

    seconds = float(info["format"]["duration"])
    if MIN_SECONDS <= seconds <= MAX_SECONDS:
        found.append(("pass", "duration", f"{seconds:.2f} s, inside "
                                          f"{MIN_SECONDS:g}-{MAX_SECONDS:g} s"))
    else:
        found.append(("flag", "duration", f"{seconds:.2f} s is outside App Store "
                                          f"Connect's {MIN_SECONDS:g}-{MAX_SECONDS:g} s"))

    avg, real = rate(video["avg_frame_rate"]), rate(video["r_frame_rate"])
    if abs(avg - FPS) < 0.02 and abs(real - FPS) < 0.02:
        found.append(("pass", "fps", f"{avg:.2f} constant"))
    else:
        found.append(("flag", "fps", f"avg {avg:.2f}, base {real:.2f}; the cut "
                                     f"is supposed to be a constant {FPS}"))

    if video["codec_name"] == CODEC:
        found.append(("pass", "codec", f"{video['codec_name']} "
                                       f"{video.get('pix_fmt', '?')}"))
    else:
        found.append(("flag", "codec", f"{video['codec_name']}, not {CODEC}"))

    if audio:
        found.append(("pass", "audio", f"{len(audio)} track "
                                       f"({audio[0]['codec_name']}, "
                                       f"{audio[0].get('channels', '?')} ch) — "
                                       f"App Store Connect refuses a preview "
                                       f"with none"))
    else:
        found.append(("flag", "audio", "no audio track; App Store Connect "
                                       "rejects the upload (MOV_RESAVE_STEREO)"))
    return found


def cut(clip: str, out_dir: str, device: str, interval: float) -> int:
    """One still every `interval` seconds, named for the video profile."""
    os.makedirs(out_dir, exist_ok=True)
    for stale in os.listdir(out_dir):
        if stale.endswith(".png"):
            os.remove(os.path.join(out_dir, stale))
    pattern = os.path.join(out_dir, f"{device}-t%03d.png")
    subprocess.run(
        # -vsync 0 with fps= would renumber from the first kept frame; the
        # default (cfr) is what makes still N the Nth interval of the clip.
        ["ffmpeg", "-v", "error", "-y", "-i", clip,
         "-vf", f"fps=1/{interval}", "-fps_mode", "passthrough", pattern],
        check=True)
    return len([f for f in os.listdir(out_dir) if f.endswith(".png")])


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        description="Check a store app preview and cut it into stills.")
    parser.add_argument("clips", nargs="+", help="the -preview.mp4 files that "
                                                 "would go to the store")
    parser.add_argument("-o", "--out", required=True,
                        help="where the stills and the report go")
    parser.add_argument("--device", help="iphone-6_9, ipad-13 or mac; read "
                                         "from the file name when not given")
    parser.add_argument("--lang", help="en, fr-CA or fr-FR; read from the "
                                       "path when not given")
    parser.add_argument("--interval", type=float, default=1.0,
                        help="seconds between stills (default 1)")
    parser.add_argument("--strict", action="store_true",
                        help="exit non-zero if anything is flagged")
    args = parser.parse_args(argv)

    for tool in ("ffprobe", "ffmpeg"):
        if not shutil.which(tool):
            raise SystemExit(f"{tool} is not on PATH")

    report, worst = [], 0
    for clip in args.clips:
        device, lang = identify(clip, args.device, args.lang)
        info = probe(clip)
        found = container_checks(info, device)
        frames_dir = os.path.join(args.out, "frames", lang or "unknown")
        count = cut(clip, frames_dir, device or "clip", args.interval)
        print(f"\n{clip}\n  {device or '?'} / {lang or '?'} — "
              f"{count} stills every {args.interval:g} s → {frames_dir}")
        for verdict, check, note in found:
            mark = {"pass": "ok  ", "flag": "FLAG", "skip": "--  "}[verdict]
            print(f"  {mark} {check:9s} {note}")
            worst = max(worst, 1 if verdict == "flag" else 0)
        report.append({"clip": os.path.abspath(clip), "device": device,
                       "language": lang, "frames": count,
                       "frames_dir": os.path.abspath(frames_dir),
                       "checks": [{"verdict": v, "check": c, "note": n}
                                  for v, c, n in found]})

    os.makedirs(args.out, exist_ok=True)
    path = os.path.join(args.out, "preview-gate.json")
    with open(path, "w", encoding="utf-8") as handle:
        json.dump({"clips": report}, handle, indent=2, ensure_ascii=False)
    print(f"\n  {path}")
    frames_root = os.path.join(args.out, "frames")
    print("  now read the stills, one language at a time:")
    for lang in sorted({c["language"] or "unknown" for c in report}):
        print(f"    python3 tools/capture-gate/gate.py --store video "
              f"{os.path.join(frames_root, lang)} "
              f"-o {os.path.join(args.out, 'gate', lang)}")
    return worst if args.strict else 0


if __name__ == "__main__":
    sys.exit(main())
