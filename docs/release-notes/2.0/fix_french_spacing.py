#!/usr/bin/env python3
"""Applies the French spacing rules to the store blocks, and refreshes the counts.

`docs/localisation-glossary.md`: Quebec puts U+00A0 before `:` only; France puts
one before `?`, `!` and `;` as well. Typing those by hand is how a normal space
sneaks in, so the rule is applied by a script and the character counts beside
each block — which the store fields are checked against — are recomputed from
the text that results.

    python3 docs/release-notes/2.0/fix_french_spacing.py [--check]

`--check` fails instead of writing, which is what CI would run.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
NBSP = " "

# The heading that starts each language's section, and the punctuation that takes
# a non-breaking space before it in that language. Everything *not* in a
# language's list takes no space at all there, which is the other half of the
# rule and the half that is easy to get wrong: Quebec writes "1.7; la 2.0" while
# France writes "1.7 ; la 2.0".
ALL_PUNCTUATION = ":?!;"
RULES = {
    "Français (Canada)": ":",
    "Français (France)": ":?!;",
}
HEADING = re.compile(r"^##\s+(.*?)\s*$", re.M)
# A "**Label** [limit] (count)" line, then a fenced block.
COUNTED = re.compile(r"(\*\*[^*]+\*\*\s*\[(\d+)\]\s*)\((\d+)\)(\s*\n\n```\n)(.*?)(\n```)", re.S)
PLAIN = re.compile(r"(\n```\n)(.*?)(\n```)", re.S)


def space_before(text: str, punctuation: str) -> str:
    """U+00A0 before each of `punctuation`, and no space before the others."""
    for mark in ALL_PUNCTUATION:
        wanted = NBSP if mark in punctuation else ""
        # A digit before a colon is a clock or a ratio ("9:41"), never a sentence,
        # so ":" alone refuses to follow one. The others may: "rendues à 1.7 ;".
        preceding = rf"[^\s{NBSP}\d]" if mark == ":" else rf"[^\s{NBSP}]"
        text = re.sub(rf"(?<={preceding})[ {NBSP}]?(?={re.escape(mark)}(?:\s|$))",
                      wanted, text)
    if punctuation:
        # French groups thousands with a space, and a plain one lets "22 000" break
        # across two lines, so both variants take U+00A0 there ("22 000").
        text = re.sub(r"(?<=\d) (?=\d{3}(?!\d))", NBSP, text)
    return text


def sections(body: str) -> list[tuple[str, int, int]]:
    """(heading, start, end) for every `## ` section."""
    marks = [(m.group(1), m.end()) for m in HEADING.finditer(body)]
    out = []
    for index, (name, start) in enumerate(marks):
        end = marks[index + 1][1] - len(f"## {marks[index + 1][0]}") - 1 \
            if index + 1 < len(marks) else len(body)
        out.append((name, start, end))
    return out


def process(path: Path, check: bool) -> bool:
    original = path.read_text(encoding="utf-8")
    body = original
    for name, start, end in reversed(sections(body)):
        # English sections take no spacing rule, but their counts are refreshed
        # too — a stale count is the same defect whichever language it is in.
        punctuation = next((p for key, p in RULES.items() if key in name), "")
        chunk = body[start:end]

        def counted(match: re.Match) -> str:
            text = space_before(match.group(5), punctuation)
            if len(text) > int(match.group(2)):
                print(f"  !! {path.name}: a block is {len(text)} characters, "
                      f"over its {match.group(2)} limit")
            return f"{match.group(1)}({len(text)}){match.group(4)}{text}{match.group(6)}"

        chunk, counted_n = COUNTED.subn(counted, chunk)
        # Blocks with no count line (the long forms) still take the spacing.
        seen = set()

        def plain(match: re.Match) -> str:
            if match.start() in seen:
                return match.group(0)
            return match.group(1) + space_before(match.group(2), punctuation) + match.group(3)

        chunk = PLAIN.sub(plain, chunk)
        body = body[:start] + chunk + body[end:]

    if body == original:
        return True
    if check:
        print(f"  {path.name}: French spacing or counts are stale")
        return False
    path.write_text(body, encoding="utf-8", newline="\n")
    print(f"  {path.name}: rewritten")
    return True


def main() -> int:
    check = "--check" in sys.argv[1:]
    ok = True
    for path in sorted(HERE.glob("*.md")):
        ok &= process(path, check)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
