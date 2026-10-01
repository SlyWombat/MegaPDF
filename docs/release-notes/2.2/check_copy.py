#!/usr/bin/env python3
"""Derives the France French, recounts every block and checks every limit (#330).

Same job as `docs/release-notes/2.1.1/check_copy.py`, with one step removed. The
Canadian French in this folder is written by hand; the France French is not.
`tools/gen_strings.py fr-fr` derives the app's catalogues, and this script derives
the store blocks the same way — `to_france` (the FR_CA_TO_FR_FR table) plus
France's punctuation, a non-breaking space before `?`, `!` and `;` as well as `:`
— so there is one French to review and one table to keep.

    python3 docs/release-notes/2.2/check_copy.py            # write
    python3 docs/release-notes/2.2/check_copy.py --check    # fail on drift

Per file it (1) applies Quebec's spacing rule to the Canadian block and says so if
that changed anything a reader should look at, (2) replaces the France block with
the derivation, (3) rewrites the `[limit] (count)` beside every block from the text
inside it, counted as `len()` counts it, which is how the four store fields count,
and (4) fails on any block over its limit.

**No sync step.** 2.1.1's script also copied the four store files into
`docs/release-notes/2.1/`, because `tools/msstore_submit.py` and
`tools/asc_publish.py` derive the folder they read from the first two parts of the
version (`2.1.1.0` → `2.1`) and 2.1.1's record folder was not that. 2.2.0 derives
`2.2`, which is this folder, so the record and the folder the tools read are the
same directory and there is nothing to copy.

`linux.md` is not in the list below. The Linux channel's field is an AppStream
`<release>` element in `tools/linux/flatpak/ca.electricrv.MegaPDF.metainfo.xml`,
which has no character limit, is not fenced the way the four store files are, and
is English only in that file today — see this folder's README.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
sys.path.insert(0, str(ROOT / "tools"))
sys.path.insert(0, str(ROOT / "docs/release-notes/2.0"))
from fix_french_spacing import space_before  # noqa: E402
from gen_strings import to_france  # noqa: E402

STORES = ["microsoft-store.md", "app-store.md", "mac-app-store.md", "google-play.md"]
LONG_FORM = "release-notes-2.2.md"

HEADING = re.compile(r"^## (.*?)\s*$", re.M)
# "**Label** [limit] (count)", a blank line, then the fenced block.
COUNTED = re.compile(r"(\*\*[^*\n]+\*\*\s*\[(\d+)\]\s*)\((\d+)\)(\s*\n\n```\n)(.*?)(\n```)", re.S)


def sections(text: str) -> list[tuple[str, int, int]]:
    """(title, body start, body end) for every `## ` section."""
    found = list(HEADING.finditer(text))
    return [(m.group(1), m.end(), found[i + 1].start() if i + 1 < len(found) else len(text))
            for i, m in enumerate(found)]


def section(text: str, word: str) -> tuple[int, int]:
    for title, start, end in sections(text):
        if word in title:
            return start, end
    raise SystemExit(f"no `## … {word}` section")


def first_block(body: str) -> re.Match:
    m = COUNTED.search(body)
    if not m:
        raise SystemExit("no counted block in section")
    return m


def derive_store(text: str, name: str, problems: list[str]) -> str:
    """The France block from the Canadian one, and every count refreshed."""
    ca_start, ca_end = section(text, "Français (Canada)")
    m = first_block(text[ca_start:ca_end])
    canada = m.group(5)
    spaced = space_before(canada, ":")
    if spaced != canada:
        print(f"{name}: Canadian spacing corrected")
        text = text[:ca_start + m.start(5)] + spaced + text[ca_start + m.end(5):]
        canada = spaced
    france = space_before(to_france(canada), ":?!;")
    fr_start, fr_end = section(text, "Français (France)")
    fm = first_block(text[fr_start:fr_end])
    text = text[:fr_start + fm.start(5)] + france + text[fr_start + fm.end(5):]

    def recount(match: re.Match) -> str:
        limit, body = int(match.group(2)), match.group(5)
        if len(body) > limit:
            problems.append(f"{name}: a block is {len(body)} > {limit}")
        return f"{match.group(1)}({len(body)}){match.group(4)}{body}{match.group(6)}"

    return COUNTED.sub(recount, text)


def derive_long_form(text: str) -> str:
    """The France section, regenerated from the Canadian one below its note."""
    ca_start, ca_end = section(text, "Français (Canada)")
    fr_start, fr_end = section(text, "Français (France)")
    body = text[fr_start:fr_end]
    note = re.match(r"\s*(?:>.*\n)+\n", body)
    if not note:
        raise SystemExit(f"{LONG_FORM}: the France section must open with a `>` note")
    canada = space_before(text[ca_start:ca_end].strip("\n"), ":")
    france = space_before(to_france(canada), ":?!;")
    return (text[:ca_start] + "\n" + canada + "\n\n" + text[ca_end:fr_start]
            + body[:note.end()] + france + "\n" + text[fr_end:])


def report(text: str, name: str) -> None:
    for title, start, end in sections(text):
        for m in COUNTED.finditer(text[start:end]):
            print(f"  {name:20} {title:40} {len(m.group(5)):>5} of {m.group(2)}")


def main(argv: list[str]) -> int:
    check = "--check" in argv
    problems: list[str] = []
    changed: list[str] = []

    def emit(path: Path, new: str) -> None:
        old = path.read_text(encoding="utf-8") if path.exists() else None
        if old == new:
            return
        changed.append(str(path.relative_to(ROOT)))
        if not check:
            path.write_text(new, encoding="utf-8", newline="\n")

    for name in STORES:
        path = HERE / name
        text = derive_store(path.read_text(encoding="utf-8"), name, problems)
        emit(path, text)
        report(text, name)

    lf = HERE / LONG_FORM
    if lf.exists():
        emit(lf, derive_long_form(lf.read_text(encoding="utf-8")))

    if problems:
        print("over a store limit:\n  " + "\n  ".join(problems), file=sys.stderr)
        return 1
    if changed:
        print(("would change" if check else "wrote") + ": " + ", ".join(changed))
        return 1 if check else 0
    print("nothing to change")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
