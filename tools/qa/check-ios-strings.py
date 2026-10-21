#!/usr/bin/env python3
"""Every literal SwiftUI shows a person has a key in Localizable.xcstrings, with its fr-CA (#644).

On iOS a user-visible string is a bare Swift literal: `Text("Save")` looks the literal up in
`ios/MegaPDF/Localizable.xcstrings` at run time and, when there is no key, shows the English
unchanged. Nothing fails. #576's "Save as a copy, keeping the invalid signature" shipped that
way, and `StringCatalogueTests.IosCatalogueIsComplete` could not see it: it builds its English
side from the catalogue's own keys, so a key that is absent is absent from both sides.

This script reads the Swift instead. It lives in `qa-matrix.yml` and not in the .NET test for one
reason: `ci.yml` has `paths-ignore: ios/**`, and a Swift-only pull request is exactly where a
missing key is introduced.

    tools/qa/check-ios-strings.py              # check; ::error:: lines and exit 1 on any problem
    tools/qa/check-ios-strings.py --list       # every (file:line, position, normalised text)
    tools/qa/check-ios-strings.py --self-test  # the rule fixtures; exit 1 on any mismatch

Positions, not "every literal": a literal is a candidate only where SwiftUI treats it as a
`LocalizedStringKey` (see POSITIONS below), so SF Symbol names, identifiers and log text are
never candidates. Prefer `Text(verbatim:)` at the call site for text that is deliberately not a
key; `ios/strings-not-in-catalogue.toml` is for the case where that is not possible.

What it cannot see: the placeholder match is type-blind (`"Page %lld"` satisfies a literal that
interpolates a `String`, whose runtime key is `"Page %@"`). Only the compiler's own
`.stringsdata` knows that; see the #644 issue for the one-off comparison.

Standard library only; Python 3.11+ (tomllib).
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import tomllib
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SWIFT_DIR = ROOT / "ios" / "MegaPDF"
CATALOGUE = SWIFT_DIR / "Localizable.xcstrings"
ALLOW = ROOT / "ios" / "strings-not-in-catalogue.toml"

PLACEHOLDER = "\x01"
_APPLE_PLACEHOLDER = re.compile(r"%(?:\d+\$)?(?:lld|ld|d|@|lf|f)")

# A single-line Swift string literal. `\(...)` interpolations are allowed, with one level of
# parentheses inside them, so `\(String(format: "%.4f", zoom))` parses.
LITERAL = r'"((?:[^"\\\n]|\\\((?:[^()]|\([^()]*\))*\)|\\.)*)"'
_LITERAL_RE = re.compile(LITERAL)
# Comments and literals in one scan, so that a `//` inside a string and a `"` inside a comment
# are each consumed by whichever starts first.
_MASK_RE = re.compile(r'"""[\s\S]*?"""|' + LITERAL + r'|//[^\n]*|/\*[\s\S]*?\*/')

# The condition of a ternary: no quote, parenthesis, `?` or `:`; it may span lines.
_COND = r'[^"()?:]+'
# The first argument of a localised position: one literal, a multi-line literal (an error), or
# `<condition> ? "A" : "B"`, which yields both.
ARG = (
    r'\s*(?:'
    r'(?P<ml>""")'
    r'|(?P<t1>' + LITERAL + r')'
    r'|' + _COND + r'\?\s*(?P<a>' + LITERAL + r')\s*:\s*(?P<b>' + LITERAL + r')'
    r')'
)

# SwiftUI initialisers whose first unlabelled argument is a LocalizedStringKey.
INITIALISERS = (
    "Text", "Button", "Label", "Toggle", "Picker", "Section", "Menu", "TextField",
    "SecureField", "Link", "NavigationLink", "Stepper", "LabeledContent", "ProgressView",
    "ContentUnavailableView",
)
# Modifiers whose first argument is a LocalizedStringKey.
MODIFIERS = (
    "navigationTitle", "alert", "confirmationDialog", "accessibilityLabel", "accessibilityHint",
    "accessibilityValue", "help",
)

_INITIALISER_RE = re.compile(r'(?<![\w.])(?P<n>' + "|".join(INITIALISERS) + r')\s*\(' + ARG)
_MODIFIER_RE = re.compile(r'\.(?P<n>' + "|".join(MODIFIERS) + r')\s*\(' + ARG)
_STRING_LOCALIZED_RE = re.compile(r'(?<![\w.])String\s*\(\s*localized\s*:' + ARG)

# Discovered from the code rather than listed (rule 4).
_FUNC_RE = re.compile(
    r'func\s+(\w+)\s*(?:<[^>]*>)?\(\s*_\s+\w+\s*:\s*LocalizedStringKey')
_LABEL_RE = re.compile(r'\b(\w+)\s*:\s*LocalizedStringKey')
_VAR_BODY_RE = re.compile(r'\bvar\s+\w+\s*:\s*LocalizedStringKey\??\s*\{')
_FUNC_BODY_RE = re.compile(r'\bfunc\s+\w+\s*(?:<[^>]*>)?\([^{}]*\)\s*->\s*LocalizedStringKey\??\s*\{')


@dataclass(frozen=True)
class Site:
    file: str
    line: int
    position: str
    text: str  # normalised: each interpolation is PLACEHOLDER
    error: str = ""


def normalise_literal(body: str) -> str:
    """Unescape `\\" \\\\ \\n \\t \\u{..}` and turn each `\\(...)` into PLACEHOLDER."""
    out: list[str] = []
    i = 0
    while i < len(body):
        c = body[i]
        if c != "\\":
            out.append(c)
            i += 1
            continue
        n = body[i + 1] if i + 1 < len(body) else ""
        if n == "(":
            depth, j = 1, i + 2
            while j < len(body) and depth:
                depth += (body[j] == "(") - (body[j] == ")")
                j += 1
            out.append(PLACEHOLDER)
            i = j
        elif n == "u" and body[i + 2:i + 3] == "{":
            j = body.index("}", i)
            out.append(chr(int(body[i + 3:j], 16)))
            i = j + 1
        else:
            out.append({"n": "\n", "t": "\t"}.get(n, n))
            i += 2
    # A literal that is itself a format string (`String(format: String(localized: "%@ items"), …)`)
    # carries the Apple placeholders in its text, and the key is spelled the same way.
    return _APPLE_PLACEHOLDER.sub(PLACEHOLDER, "".join(out))


def normalise_key(key: str) -> str:
    return _APPLE_PLACEHOLDER.sub(PLACEHOLDER, key)


def display(text: str) -> str:
    return text.replace(PLACEHOLDER, "%@")


def mask_comments(src: str) -> str:
    """Blank out comments, keeping offsets and newlines so line numbers survive."""
    def blank(m: re.Match) -> str:
        s = m.group(0)
        if s.startswith("//") or s.startswith("/*"):
            return re.sub(r"[^\n]", " ", s)
        return s
    return _MASK_RE.sub(blank, src)


def _literal_after(src: str, pos: int) -> re.Match | None:
    """The literal beginning at `pos` (after whitespace), or None."""
    m = re.compile(r"\s*").match(src, pos)
    p = m.end()
    return _LITERAL_RE.match(src, p) if src.startswith('"', p) else None


def _followed_by_plus(src: str, end: int) -> bool:
    return re.compile(r"\s*\+").match(src, end) is not None


def _sites_from_arg(file: str, src: str, m: re.Match, position: str) -> list[Site]:
    def line(offset: int) -> int:
        return src.count("\n", 0, offset) + 1

    out: list[Site] = []
    if m.group("ml"):
        out.append(Site(file, line(m.start("ml")), position, "",
                        "multi-line literal in a localised position; write it as a "
                        "single-line literal"))
        return out
    groups = ("t1",) if m.group("t1") is not None else ("a", "b")
    for g in groups:
        s, e = m.start(g), m.end(g)
        if _followed_by_plus(src, e):
            continue  # `"x" + y` is a String, not a key
        body = src[s + 1:e - 1]
        if body == "":
            continue
        out.append(Site(file, line(s), position, normalise_literal(body)))
    return out


def _brace_body(src: str, open_brace: int) -> tuple[int, int]:
    """(start, end) of the brace-matched body whose `{` is at `open_brace`. Braces inside
    string literals are not counted."""
    depth = 0
    i = open_brace
    while i < len(src):
        if src[i] == '"':
            m = _LITERAL_RE.match(src, i)
            if m:
                i = m.end()
                continue
        if src[i] == "{":
            depth += 1
        elif src[i] == "}":
            depth -= 1
            if depth == 0:
                return open_brace + 1, i
        i += 1
    return open_brace + 1, len(src)


def scan(sources: dict[str, str]) -> list[Site]:
    """Every literal in a localised position, over `{path: swift source}`."""
    masked = {f: mask_comments(s) for f, s in sources.items()}

    funcs: set[str] = set()
    labels: set[str] = set()
    for src in masked.values():
        funcs.update(_FUNC_RE.findall(src))
        labels.update(_LABEL_RE.findall(src))

    wrapper_re = (re.compile(r'(?<!\w)(?P<n>' + "|".join(map(re.escape, sorted(funcs))) + r')\s*\(' + ARG)
                  if funcs else None)
    label_re = (re.compile(r'\b(?P<n>' + "|".join(map(re.escape, sorted(labels))) + r')\s*:' + ARG)
                if labels else None)
    default_re = (re.compile(r'\b(?P<n>' + "|".join(map(re.escape, sorted(labels)))
                             + r')\s*:\s*LocalizedStringKey\??\s*=' + ARG)
                  if labels else None)

    found: dict[tuple[str, int, str], Site] = {}

    def add(sites: list[Site]) -> None:
        for s in sites:
            found.setdefault((s.file, s.line, s.text or s.error), s)

    for file, src in masked.items():
        for rx, label in ((_INITIALISER_RE, "{n}("), (_MODIFIER_RE, ".{n}("),
                          (_STRING_LOCALIZED_RE, "String(localized:")):
            for m in rx.finditer(src):
                pos = label.format(n=m.groupdict().get("n", ""))
                add(_sites_from_arg(file, src, m, pos))
        if wrapper_re:
            for m in wrapper_re.finditer(src):
                add(_sites_from_arg(file, src, m, f"{m.group('n')}(_:)"))
        if label_re:
            for m in label_re.finditer(src):
                add(_sites_from_arg(file, src, m, f"{m.group('n')}:"))
        if default_re:
            for m in default_re.finditer(src):
                add(_sites_from_arg(file, src, m, f"{m.group('n')}: LocalizedStringKey ="))
        # Rule 4c: every literal inside the body of a member declared to return a
        # LocalizedStringKey.
        for rx in (_VAR_BODY_RE, _FUNC_BODY_RE):
            for m in rx.finditer(src):
                start, end = _brace_body(src, m.end() - 1)
                for lm in _LITERAL_RE.finditer(src, start, end):
                    if _followed_by_plus(src, lm.end()) or lm.group(1) == "":
                        continue
                    line = src.count("\n", 0, lm.start()) + 1
                    add([Site(file, line, "LocalizedStringKey member",
                              normalise_literal(lm.group(1)))])
    return sorted(found.values(), key=lambda s: (s.file, s.line, s.text))


# --- the catalogue and the check ----------------------------------------------------------


@dataclass
class Key:
    name: str
    translated: bool  # has a translated fr-CA, or needs none


def load_catalogue(path: Path = CATALOGUE) -> dict[str, list[Key]]:
    data = json.loads(path.read_text(encoding="utf-8"))
    by_norm: dict[str, list[Key]] = {}
    for name, entry in data["strings"].items():
        if entry.get("shouldTranslate") is False:
            ok = True
        else:
            loc = entry.get("localizations", {}).get("fr-CA", {})
            if "stringUnit" in loc:
                ok = loc["stringUnit"].get("state") == "translated"
            else:
                units = [u["stringUnit"] for v in loc.get("variations", {}).values()
                         for u in (v.values() if isinstance(v, dict) else [])
                         if isinstance(u, dict) and "stringUnit" in u]
                ok = bool(units) and all(u.get("state") == "translated" for u in units)
        by_norm.setdefault(normalise_key(name), []).append(Key(name, ok))
    return by_norm


def load_allow(path: Path = ALLOW) -> list[dict]:
    if not path.exists():
        return []
    return tomllib.loads(path.read_text(encoding="utf-8")).get("literal", [])


def check(sites: list[Site], catalogue: dict[str, list[Key]], allow: list[dict]) -> list[str]:
    """Problems, as `::error` lines."""
    problems: list[str] = []

    def err(file: str, line: int | None, msg: str) -> None:
        where = f"file={file}" + (f",line={line}" if line else "")
        problems.append(f"::error {where}::{msg}")

    allowed: dict[tuple[str, str], dict] = {}
    for entry in allow:
        text = normalise_key(entry.get("text", ""))
        file = entry.get("file", "")
        allowed[(file, text)] = entry
        if not str(entry.get("why", "")).strip():
            err("ios/strings-not-in-catalogue.toml", None,
                f'allow-list entry "{display(text)}" has no reason (why)')
        if normalise_key(entry.get("text", "")) in catalogue:
            err("ios/strings-not-in-catalogue.toml", None,
                f'allow-list entry "{display(text)}" now has a key in Localizable.xcstrings: '
                "remove it from the allow-list")
        if not any(s.file == file and s.text == text for s in sites):
            err("ios/strings-not-in-catalogue.toml", None,
                f'allow-list entry "{display(text)}" is not a localised literal in {file}: '
                "stale, remove it")

    for s in sites:
        if s.error:
            err(s.file, s.line, s.error)
            continue
        if (s.file, s.text) in allowed:
            continue
        keys = catalogue.get(s.text)
        if not keys:
            err(s.file, s.line,
                f'"{display(s.text)}" [{s.position}] has no key in Localizable.xcstrings: '
                "French users see English. Add the key with its fr-CA (then "
                "`python3 tools/gen_strings.py fr-fr`), or write Text(verbatim:) if it is "
                "not for a person")
        elif not any(k.translated for k in keys):
            err(s.file, s.line,
                f'"{display(s.text)}" [{s.position}] has a key but no translated fr-CA value')
    return problems


# --- self-test ----------------------------------------------------------------------------

P = PLACEHOLDER
FIXTURES: list[tuple[str, set[str]]] = [
    ('Text("Hello")', {"Hello"}),
    ('Button("Save") { }', {"Save"}),
    ('.accessibilityHint("Selects this mark.")', {"Selects this mark."}),
    ('String(localized: "Page \\(n)")', {f"Page {P}"}),
    ('Button(flag\n    ? "A" : "B", role: .destructive) { }', {"A", "B"}),
    ('func control(_ t: LocalizedStringKey) {}\ncontrol("Next")', {"Next"}),
    ('var removeLabel: LocalizedStringKey = "Remove"\nX(removeLabel: "Remove whiteout")',
     {"Remove", "Remove whiteout"}),
    ('var title: LocalizedStringKey { switch m { case .a: return "One" } }', {"One"}),
    ('Text(verbatim: "x")\nImage(systemName: "doc")\n'
     'Label("Copy", systemImage: "doc.on.doc")\n.accessibilityIdentifier("id")', {"Copy"}),
    ('Text("a \\(x)" + " b")', set()),
    ('.alert("", isPresented: $p)', set()),
    # Beyond the design's eleven: the traps the scanner is built around.
    ('// Text("in a comment")\nText("real") /* Button("also a comment") */', {"real"}),
    ('Text("see https://example.com/x")', {"see https://example.com/x"}),
    ('Text("zoom \\(String(format: "%.4f", zoom))")', {f"zoom {P}"}),
    ('myText("no")\nfoo.Text("no")\nText(model.title)', set()),
    ('.accessibilityLabel(Text(verbatim: "Pinch probe"))', set()),
    ('func f(_ a: Int) -> LocalizedStringKey { "Back" }', {"Back"}),
    ('func g(_ t: LocalizedStringKey) { let s = "not a key" }', set()),
]


def self_test() -> int:
    bad = 0
    for i, (src, expected) in enumerate(FIXTURES, 1):
        got = {s.text for s in scan({"fixture.swift": src}) if not s.error}
        if got != expected:
            bad += 1
            print(f"::error::fixture {i} {src!r}: expected {sorted(expected)}, got {sorted(got)}",
                  file=sys.stderr)

    ml = scan({"fixture.swift": 'Text("""\nhi\n""")'})
    if not (len(ml) == 1 and ml[0].error):
        bad += 1
        print("::error::fixture multi-line: a multi-line literal in a position must be an error",
              file=sys.stderr)

    # The check itself: a missing key, an untranslated one, a stale and a reasonless allow-list entry.
    cat = {"Have": [Key("Have", True)], "Untranslated": [Key("Untranslated", False)],
           f"Page {P}": [Key("Page %lld", True)]}
    sites = [Site("a.swift", 1, "Text(", "Have"), Site("a.swift", 2, "Text(", f"Page {P}")]
    cases = [
        ("clean", sites, [], 0),
        ("missing key", sites + [Site("a.swift", 3, "Text(", "Nope")], [], 1),
        ("untranslated", sites + [Site("a.swift", 3, "Text(", "Untranslated")], [], 1),
        ("allowed", sites + [Site("a.swift", 3, "Text(", "Nope")],
         [{"text": "Nope", "file": "a.swift", "why": "a probe"}], 0),
        ("allow-list: text no longer in the file", sites,
         [{"text": "Gone", "file": "a.swift", "why": "x"}], 1),
        ("allow-list: text now has a key", sites,
         [{"text": "Have", "file": "a.swift", "why": "x"}], 1),
        ("allow-list: no reason", sites + [Site("a.swift", 3, "Text(", "Nope")],
         [{"text": "Nope", "file": "a.swift", "why": " "}], 1),
        ("allow-list: placeholder written as %@", sites + [Site("a.swift", 3, "Text(", f"Zoom {P}")],
         [{"text": "Zoom %@", "file": "a.swift", "why": "a probe"}], 0),
    ]
    for name, s, a, want in cases:
        got = len(check(s, cat, a))
        if got != want:
            bad += 1
            print(f"::error::check case '{name}': expected {want} problem(s), got {got}",
                  file=sys.stderr)
    if bad:
        return 1
    print(f"self-test: {len(FIXTURES) + 1} extraction fixtures and {len(cases)} check cases pass")
    return 0


def read_sources() -> dict[str, str]:
    return {p.relative_to(ROOT).as_posix(): p.read_text(encoding="utf-8")
            for p in sorted(SWIFT_DIR.rglob("*.swift"))}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--list", action="store_true", help="print every localised literal")
    ap.add_argument("--self-test", action="store_true", help="run the rule fixtures")
    args = ap.parse_args()

    if args.self_test:
        return self_test()

    sites = scan(read_sources())
    if args.list:
        for s in sites:
            print(f"{s.file}:{s.line}\t{s.position}\t{display(s.text) if not s.error else s.error}")
        return 0

    problems = check(sites, load_catalogue(), load_allow())
    for p in problems:
        print(p, file=sys.stderr)
    distinct = len({s.text for s in sites})
    if problems:
        print(f"{len(problems)} problem(s) in {len(sites)} localised literals", file=sys.stderr)
        return 1
    print(f"ok: {len(sites)} localised literals ({distinct} distinct) all have a translated key")
    return 0


if __name__ == "__main__":
    sys.exit(main())
