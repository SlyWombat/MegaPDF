#!/usr/bin/env python3
"""The one list of which iOS UI tests each CI lane runs (#598): ios/ui-test-lanes.txt.

ios-ci.yml asks this script for the xcodebuild arguments of each lane, and
tools/qa/check-matrix.py asks it what each lane runs, so CI and the coverage map read the
same file and cannot disagree.

Python 3.9 compatible on purpose: the self-hosted Mac's /usr/bin/python3 is 3.9.6, which has
no `tomllib`, no `match` and no `X | Y` at runtime. Standard library only.

    ios_ui_lanes.py xcodebuild-args iphone|ipad   # one line of -only-testing/-skip-testing words
    ios_ui_lanes.py list iphone|ipad              # the resolved units, sorted, one per line
    ios_ui_lanes.py check                         # problems as ::error:: lines; exit 1 if any
"""

from __future__ import annotations

import re
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Set, Tuple

ROOT = Path(__file__).resolve().parents[2]
MANIFEST = "ios/ui-test-lanes.txt"
UI_TEST_DIR = "ios/MegaPDFUITests"
TARGET = "MegaPDFUITests"
LANES = ("iphone", "ipad")
ACTIONS = ("run", "skip")

_CLASS = re.compile(r"\bclass\s+(\w+)\s*:\s*XCTestCase\b")
_TEST = re.compile(r"\bfunc\s+(test\w*)\s*\(")


@dataclass(frozen=True)
class Entry:
    lane: str    # "iphone" | "ipad"
    action: str  # "run" | "skip"
    unit: str    # "Suite" or "Suite/testMethod"
    why: str
    line: int


def suites(root: Path = ROOT) -> Tuple[Dict[str, List[str]], List[str]]:
    """Suite name -> its test methods, from ios/MegaPDFUITests/*.swift, and any problems.

    A file's suite is its one `class X: XCTestCase`; its tests are `func testX(`. A file with
    no such class (DocumentOpening.swift, PageInput.swift) is a helper and is skipped; a file
    with two is a problem, because "suite = file stem = class" is what the map relies on.
    """
    found: Dict[str, List[str]] = {}
    problems: List[str] = []
    for path in sorted((root / UI_TEST_DIR).glob("*.swift")):
        text = path.read_text(encoding="utf-8", errors="replace")
        classes = _CLASS.findall(text)
        if not classes:
            continue
        rel = path.relative_to(root).as_posix()
        if len(classes) > 1:
            problems.append(f"{rel}: has {len(classes)} XCTestCase classes; keep one per file")
            continue
        if classes[0] != path.stem:
            problems.append(f"{rel}: the class is {classes[0]}, not {path.stem}; keep suite = file = class")
        found[classes[0]] = _TEST.findall(text)
    return found, problems


def load(root: Path = ROOT) -> Tuple[List[Entry], List[str]]:
    """Parse the manifest. Returns (entries, problems)."""
    path = root / MANIFEST
    if not path.is_file():
        return [], [f"{MANIFEST}: does not exist"]
    known, problems = suites(root)
    entries: List[Entry] = []
    seen: Set[Tuple[str, str]] = set()
    for number, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        text = raw.strip()
        if not text or text.startswith("#"):
            continue
        where = f"{MANIFEST}:{number}"
        head, sep, why = text.partition(" -- ")
        words = head.split()
        if not sep or not why.strip():
            problems.append(f"{where}: needs ` -- <why>`")
            continue
        if len(words) != 3:
            problems.append(f"{where}: expected `<lane> <run|skip> <Suite>[/<test>] -- <why>`")
            continue
        lane, action, unit = words
        if lane not in LANES:
            problems.append(f"{where}: unknown lane {lane!r}; use one of {', '.join(LANES)}")
            continue
        if action not in ACTIONS:
            problems.append(f"{where}: unknown action {action!r}; use run or skip")
            continue
        if lane == "iphone" and action == "run":
            problems.append(f"{where}: the iphone lane runs every suite; it takes `skip` lines only")
            continue
        suite, _, test = unit.partition("/")
        if suite not in known:
            problems.append(f"{where}: {suite} is not an XCTestCase suite in {UI_TEST_DIR}/")
            continue
        if test and test not in known[suite]:
            problems.append(f"{where}: {suite} has no test named {test}")
            continue
        if (lane, unit) in seen:
            problems.append(f"{where}: duplicate {lane} {unit}")
            continue
        seen.add((lane, unit))
        entries.append(Entry(lane, action, unit, why.strip(), number))
    for entry in entries:
        if entry.lane == "ipad" and entry.action == "skip":
            suite = entry.unit.split("/")[0]
            if not any(e.lane == "ipad" and e.action == "run" and e.unit == suite for e in entries):
                problems.append(f"{MANIFEST}:{entry.line}: ipad skip {entry.unit} needs an `ipad run {suite}`")
    return entries, problems


def units(root: Path = ROOT, lane: str = "iphone") -> Set[str]:
    """What the lane runs: `Suite` when every test of the suite runs on it, `Suite/test` for
    each test when only some do. Empty if the manifest has problems worth reporting; callers
    that must fail loudly call load() first."""
    entries, _ = load(root)
    known, _ = suites(root)
    mine = [e for e in entries if e.lane == lane]
    skipped_suites = {e.unit for e in mine if e.action == "skip" and "/" not in e.unit}
    skipped_tests = {e.unit for e in mine if e.action == "skip" and "/" in e.unit}
    if lane == "iphone":
        chosen = set(known)
    else:
        chosen = {e.unit for e in mine if e.action == "run"}
    out: Set[str] = set()
    for suite in chosen - skipped_suites:
        gone = {t for t in known.get(suite, []) if f"{suite}/{t}" in skipped_tests}
        if gone:
            out.update(f"{suite}/{t}" for t in known[suite] if t not in gone)
        else:
            out.add(suite)
    return out


def xcodebuild_args(root: Path, lane: str) -> str:
    """The -only-testing/-skip-testing words for a lane, on one line."""
    entries, _ = load(root)
    mine = [e for e in entries if e.lane == lane]
    words: List[str] = []
    if lane == "iphone":
        # Pass the target even though the scheme tests only it, so the lane cannot widen if
        # the scheme does.
        words.append(f"-only-testing:{TARGET}")
    else:
        words += [f"-only-testing:{TARGET}/{e.unit}" for e in mine if e.action == "run"]
    words += [f"-skip-testing:{TARGET}/{e.unit}" for e in mine if e.action == "skip"]
    return " ".join(words)


def main(argv: List[str]) -> int:
    root = ROOT
    if len(argv) == 2 and argv[1] == "check":
        _, problems = load(root)
        for p in problems:
            print(f"::error::{p}")
        return 1 if problems else 0
    if len(argv) == 3 and argv[1] in ("xcodebuild-args", "list") and argv[2] in LANES:
        _, problems = load(root)
        if problems:
            for p in problems:
                print(p, file=sys.stderr)
            return 1
        if argv[1] == "list":
            print("\n".join(sorted(units(root, argv[2]))))
        else:
            print(xcodebuild_args(root, argv[2]))
        return 0
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
