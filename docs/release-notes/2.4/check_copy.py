#!/usr/bin/env python3
"""Derives the France and Spain blocks, recounts every block, checks every limit and
the code points of the punctuation (#724).

Same job as `docs/release-notes/2.3/check_copy.py`, with Spanish added. The Canadian
French and the Latin American Spanish in this folder are written by hand; the France
French and the Spain Spanish are not.

* France: `tools/gen_strings.py fr-fr` derives the app's catalogues, and this script
  derives the store blocks the same way, `to_france` (the FR_CA_TO_FR_FR table) plus
  France's punctuation, a non-breaking space before `?`, `!` and `;` as well as `:`.
* Spain: `tools/gen_strings.py es-es` derives the app's catalogues, and this script
  derives the store blocks the same way, `to_spain` (the ES_419_TO_ES_ES table) and
  **nothing else**. Spanish has no space before `: ; ? !`, of any kind, so the
  France punctuation pass (`space_before`) never runs on it.

    python3 docs/release-notes/2.4/check_copy.py            # write
    python3 docs/release-notes/2.4/check_copy.py --check    # fail on drift

Per file it (1) applies Quebec's spacing rule to the Canadian block and says so if that
changed anything a reader should look at, (2) replaces the France and Spain blocks with
their derivations, (3) rewrites the `[limit] (count)` beside every block from the text
inside it, counted as `len()` counts it, which is how the four store fields count, and
(4) fails on any block over its limit.

It then checks by code point, because U+00A0 and a plain space look the same:

* no Spanish block contains U+00A0 or U+202F anywhere, nothing but a plain character
  precedes `: ; ? !`, `¿ ¡` balance `? !`, and there is no « or »;
* every `: ? ! ;` in the France block is preceded by exactly one U+00A0, and in the
  Canadian block `:` is, while `? ! ;` have no space before them;
* the English blocks contain no U+00A0;
* each heading carries the locale code the tool that reads it wants (see this folder's
  README): `tools/asc_publish.py` takes the last backticked word, `tools/msstore_submit.py`
  takes `xx-XX` codes, and `tools/gen_listing_copy.py` finds a section by the text of the
  heading before the dash;
* the `<p>` lines of `linux.md` are the same sentences as the store blocks.

**No sync step.** 2.4.0 derives the folder `2.4`, which is this one, so the record and
the folder the tools read are the same directory (see 2.3's script).

`linux.md` is otherwise not rewritten: the Linux channel's field is an AppStream
`<release>` element in `tools/linux/flatpak/ca.electricrv.MegaPDF.metainfo.xml`, which
has no character limit.
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
from gen_strings import to_france, to_spain  # noqa: E402

# file -> (heading word -> the locale codes that heading must carry)
STORES = {
    "microsoft-store.md": {"English": {"en-US", "en-CA"}, "Français (Canada)": {"fr-CA"},
                           "Français (France)": {"fr-FR"},
                           "Español (Latinoamérica)": {"es-MX"}, "Español (España)": {"es-ES"}},
    "app-store.md": {"English (Canada)": {"en-CA"}, "Français (Canada)": {"fr-CA"},
                     "Français (France)": {"fr"},
                     "Español (Latinoamérica)": {"es-MX"}, "Español (España)": {"es-ES"}},
    "mac-app-store.md": {"English (Canada)": {"en-CA"}, "Français (Canada)": {"fr-CA"},
                         "Français (France)": {"fr"},
                         "Español (Latinoamérica)": {"es-MX"}, "Español (España)": {"es-ES"}},
    "google-play.md": {"English (Canada)": {"en-CA"}, "Français (Canada)": {"fr-CA"},
                       "Français (France)": {"fr-FR"},
                       "Español (Latinoamérica)": {"es-419"}, "Español (España)": {"es-ES"}},
}
LINUX = "linux.md"

HEADING = re.compile(r"^## (.*?)\s*$", re.M)
# "**Label** [limit] (count)", a blank line, then the fenced block.
COUNTED = re.compile(r"(\*\*[^*\n]+\*\*\s*\[(\d+)\]\s*)\((\d+)\)(\s*\n\n```\n)(.*?)(\n```)", re.S)

NBSP, NNBSP = "\u00a0", "\u202f"
PUNCT = ":;?!"
SPANISH_SPACE_BEFORE = re.compile(r"[\s\u00a0\u202f]+[:;?!]")


def sections(text: str) -> list[tuple[str, int, int]]:
    """(title, body start, body end) for every `## ` section."""
    found = list(HEADING.finditer(text))
    return [(m.group(1), m.end(), found[i + 1].start() if i + 1 < len(found) else len(text))
            for i, m in enumerate(found)]


def section(text: str, word: str) -> tuple[int, int]:
    for title, start, end in sections(text):
        if title.startswith(word):
            return start, end
    raise SystemExit(f"no `## {word}` section")


def first_block(body: str) -> re.Match:
    m = COUNTED.search(body)
    if not m:
        raise SystemExit("no counted block in section")
    return m


def block_of(text: str, word: str) -> str:
    start, end = section(text, word)
    return first_block(text[start:end]).group(5)


def replace_block(text: str, word: str, new: str) -> str:
    start, end = section(text, word)
    m = first_block(text[start:end])
    return text[:start + m.start(5)] + new + text[start + m.end(5):]


def derive_store(text: str, name: str, problems: list[str]) -> str:
    """The France and Spain blocks from the hand-written ones, every count refreshed."""
    canada = block_of(text, "Français (Canada)")
    spaced = space_before(canada, ":")
    if spaced != canada:
        print(f"{name}: Canadian spacing corrected")
        text = replace_block(text, "Français (Canada)", spaced)
        canada = spaced
    text = replace_block(text, "Français (France)", space_before(to_france(canada), ":?!;"))
    # Spain: the table, and no punctuation pass of any kind.
    text = replace_block(text, "Español (España)", to_spain(block_of(text, "Español (Latinoamérica)")))

    def recount(match: re.Match) -> str:
        limit, body = int(match.group(2)), match.group(5)
        if len(body) > limit:
            problems.append(f"{name}: a block is {len(body)} > {limit}")
        return f"{match.group(1)}({len(body)}){match.group(4)}{body}{match.group(6)}"

    return COUNTED.sub(recount, text)


def check_code_points(text: str, name: str, codes: dict[str, set[str]], problems: list[str]) -> None:
    for word, want in codes.items():
        start, end = section(text, word)
        title = next(t for t, s, _ in sections(text) if s == start)
        have = set(re.findall(r"`([A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,3})?)`", title))
        if have != want:
            problems.append(f"{name}: `## {title}` carries {sorted(have)}, the tools want {sorted(want)}")
        body = first_block(text[start:end]).group(5)
        where = f"{name}: {word}"
        if word.startswith("Español"):
            for ch, label in ((NBSP, "U+00A0"), (NNBSP, "U+202F"), ("«", "«"), ("»", "»")):
                if ch in body:
                    problems.append(f"{where} contains {label}")
            if SPANISH_SPACE_BEFORE.search(body):
                problems.append(f"{where} has a space before one of : ; ? !")
            if body.count("¿") != body.count("?") or body.count("¡") != body.count("!"):
                problems.append(f"{where}: ¿ ¡ do not balance ? !")
        elif word == "Français (France)":
            for i, ch in enumerate(body):
                if ch in PUNCT and (i == 0 or body[i - 1] != NBSP or (i > 1 and body[i - 2] in " " + NBSP)):
                    problems.append(f"{where}: `{ch}` at {i} is not preceded by exactly one U+00A0")
            if re.search(r" [:;?!]", body):
                problems.append(f"{where}: a plain space before : ; ? !")
        elif word == "Français (Canada)":
            for i, ch in enumerate(body):
                if ch == ":" and (i == 0 or body[i - 1] != NBSP):
                    problems.append(f"{where}: `:` at {i} is not preceded by U+00A0")
                if ch in ";?!" and i and body[i - 1] in " " + NBSP + NNBSP:
                    problems.append(f"{where}: `{ch}` at {i} has a space before it (Quebec has none)")
        else:
            if NBSP in body or NNBSP in body:
                problems.append(f"{where} contains U+00A0 or U+202F")


def check_linux(store: str, name: str, problems: list[str]) -> None:
    text = (HERE / name).read_text(encoding="utf-8")
    want = {"": block_of(store, "English"), "fr-CA": block_of(store, "Français (Canada)"),
            "fr": block_of(store, "Français (France)"),
            "es": block_of(store, "Español (Latinoamérica)"),
            "es-ES": block_of(store, "Español (España)")}
    found = {m.group(1) or "": m.group(2) for m in
             re.finditer(r'<p(?: xml:lang="([^"]+)")?>(.*?)</p>', text)}
    if found != want:
        for k in sorted(set(found) | set(want)):
            if found.get(k) != want.get(k):
                problems.append(f"{name}: <p xml:lang={k or '(none)'!r}> differs from the store block")
    for ch, label in ((NBSP, "U+00A0"), (NNBSP, "U+202F")):
        for k, v in found.items():
            if ch in v and k.startswith("es"):
                problems.append(f"{name}: Spanish <p> contains {label}")


def report(text: str, name: str) -> None:
    for title, start, end in sections(text):
        for m in COUNTED.finditer(text[start:end]):
            print(f"  {name:20} {title:48} {len(m.group(5)):>5} of {m.group(2)}")


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

    texts: dict[str, str] = {}
    for name, codes in STORES.items():
        path = HERE / name
        text = derive_store(path.read_text(encoding="utf-8"), name, problems)
        emit(path, text)
        texts[name] = text
        check_code_points(text, name, codes, problems)
        report(text, name)

    # One sentence per language, the same on every channel.
    ref = texts["app-store.md"]
    for name, text in texts.items():
        for word in ("English", "Français (Canada)", "Español (Latinoamérica)"):
            if block_of(text, word) != block_of(ref, "English (Canada)" if word == "English" else word):
                problems.append(f"{name}: the {word} block differs from app-store.md's")
    check_linux(ref, LINUX, problems)

    if problems:
        print("problems:\n  " + "\n  ".join(problems), file=sys.stderr)
        return 1
    if changed:
        print(("would change" if check else "wrote") + ": " + ", ".join(changed))
        return 1 if check else 0
    print("nothing to change")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
