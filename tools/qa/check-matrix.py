#!/usr/bin/env python3
"""Check tests/matrix/coverage.toml against the tree, and regenerate docs/qa/test-matrix.md.

#330 asked for "a test matrix of features x actions x lifecycle states x platforms, and
automated tests for every applicable cell". The honest shape of that is not a grid of
combinations — feature x action x state x platform is tens of thousands of cells and any
file claiming to fill them would be mostly invention. What the bugs behind #330 actually
had in common is that **nobody knew what was not covered**, so this is a map of
(feature x platform) pairings where every cell has to say two things and prove both:

  presence — does the feature exist on that platform, and is it *reachable* from the UI?
  coverage — does a test in CI exercise it, and at which level?

Both halves are checked against the tree, every push, because the three holes this week
were not failing tests. Android had no whiteout tool while an issue discussed its chrome
on four platforms. Linux had an About window and a notices window with nothing leading to
them (#571). A corpus refusal path read as zero for two runs because no operation reached
it. A document is no use against that class of defect: it describes intent, and intent is
what went stale. So:

  * `presence = "present"` must name an `entry` — a file:needle that exists *and* lives in
    that platform's UI entry-point layer (meta.platform.<p>.entry_roots). A feature whose
    only mention is in an engine file cannot claim to be present: that is the #571 shape.
  * `coverage` must name `tests` that resolve to real files containing the real symbol.
  * and in reverse, every test artefact the tree contains must be cited by some cell, or
    exempted with a reason. Adding a test file without placing it on the map fails CI.
    That ratchet is what stops the map going quietly stale.

Nothing here runs the app or the tests; it reads the tree. It is a map of what the other
jobs cover, not a substitute for them, and (TESTING.md, docs/RELEASING.md §2.3) not a
substitute for the real-machine pass either.

    tools/qa/check-matrix.py            # validate and rewrite docs/qa/test-matrix.md
    tools/qa/check-matrix.py --check    # validate, and fail if the doc has drifted
    tools/qa/check-matrix.py --gaps     # print the gap list only
"""

from __future__ import annotations

import argparse
import fnmatch
import re
import sys
import tomllib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MATRIX = ROOT / "tests" / "matrix" / "coverage.toml"
DOC = ROOT / "docs" / "qa" / "test-matrix.md"

# Coverage levels, weakest first. `engine` is a level of its own on purpose: "the shared
# core proves the operation, nothing proves this platform's own path to it" is a different
# statement from "this platform's UI is tested", and collapsing the two is how a matrix
# starts flattering itself.
LEVELS = ["n/a", "none", "engine", "ui"]
LEVEL_LABEL = {
    "ui": "UI/VM",
    "engine": "engine only",
    "none": "nothing",
    "n/a": "n/a",
}
PRESENCE = ["present", "absent"]
ABSENCE = ["deliberate", "gap"]

CELL_KEYS = {
    "presence", "entry", "coverage", "tests", "manual", "absence", "note", "issue",
}

MANUAL_DOCS = ("TESTING.md", "docs/RELEASING.md", "docs/qa/")


class Problems:
    def __init__(self) -> None:
        self.items: list[str] = []

    def add(self, where: str, what: str) -> None:
        self.items.append(f"{where}: {what}")

    def __bool__(self) -> bool:
        return bool(self.items)


_text_cache: dict[Path, str] = {}


def file_text(rel: str) -> str | None:
    path = ROOT / rel
    if path in _text_cache:
        return _text_cache[path]
    if not path.is_file():
        return None
    text = path.read_text(encoding="utf-8", errors="replace")
    _text_cache[path] = text
    return text


def split_ref(ref: str) -> tuple[str, str | None]:
    """`path::needle` -> (path, needle). A bare path is allowed for whole-file refs."""
    if "::" in ref:
        path, needle = ref.split("::", 1)
        return path.strip(), needle
    return ref.strip(), None


def resolve(ref: str, problems: Problems, where: str) -> str | None:
    """Check a ref points at something that is really there. Returns the path part."""
    path, needle = split_ref(ref)
    text = file_text(path)
    if text is None:
        problems.add(where, f"{path} does not exist")
        return None
    if needle is not None and needle not in text:
        problems.add(where, f"{path} does not contain {needle!r}")
    return path


def under_roots(path: str, roots: list[str]) -> bool:
    return any(fnmatch.fnmatch(path, pattern) for pattern in roots)


# ---------------------------------------------------------------------------
# The reverse direction: what the tree contains that the map has to account for.
# ---------------------------------------------------------------------------

def avalonia_self_test_sections() -> list[str]:
    """The named Check* blocks of the Avalonia --self-test (src/MegaPDF.Avalonia/Program.cs).

    The harness has no check ids — each of its ~650 assertions is a human sentence — so its
    Check* methods are the only stable handles it offers, and they are what the map cites.
    """
    text = file_text("src/MegaPDF.Avalonia/Program.cs") or ""
    pattern = re.compile(
        r"^\s*(?:private|internal|public)\s+static\s+[\w<>,\[\]\?\s]+?\s(Check\w+)\s*\(",
        re.MULTILINE,
    )
    return sorted(set(pattern.findall(text)))


def windows_selftest_states() -> list[str]:
    """The --screenshot-state checks the Windows UI self-test job actually runs in CI.

    Read from the workflow, not from Screenshot.cs: the harness has more states than CI
    drives, and what the map is accounting for is the ones that run (ci.yml, job
    `windows-ui-selftest`, non-blocking as of 2026-09-29, #462).
    """
    text = file_text(".github/workflows/ci.yml") or ""
    return sorted(set(re.findall(r'^\s*Run-Check\s+"([a-z-]+)"', text, re.MULTILINE)))


SUITE_GLOBS = [
    ("core tests (.NET)", "tests/MegaPDF.Core.Tests/*Tests.cs"),
    ("core tests (native)", "core/tests/*.c*"),
    ("Android app UI tests", "android/app/src/androidTest/java/com/megapdf/android/*.kt"),
    ("Android app unit tests", "android/app/src/test/java/com/megapdf/android/*.kt"),
    ("Android engine device tests", "android/engine/src/androidTest/java/com/megapdf/engine/*.kt"),
    ("Android engine unit tests", "android/engine/src/test/java/com/megapdf/engine/*.kt"),
    ("iOS unit tests", "ios/MegaPDFTests/*.swift"),
    ("iOS UI tests", "ios/MegaPDFUITests/*.swift"),
]


def suite_files() -> list[tuple[str, str]]:
    found: list[tuple[str, str]] = []
    for kind, glob in SUITE_GLOBS:
        for path in sorted(ROOT.glob(glob)):
            found.append((kind, path.relative_to(ROOT).as_posix()))
    return found


# ---------------------------------------------------------------------------
# Load and validate
# ---------------------------------------------------------------------------

def load() -> dict:
    with MATRIX.open("rb") as handle:
        return tomllib.load(handle)


def validate(data: dict, problems: Problems) -> None:
    meta = data.get("meta", {})
    platforms = meta.get("order", [])
    if not platforms:
        problems.add("meta", "meta.order must list the platform ids, in display order")
        return
    for pid in platforms:
        if pid not in meta.get("platform", {}):
            problems.add("meta", f"meta.platform.{pid} is missing")
    for pid, spec in meta.get("platform", {}).items():
        if pid not in platforms:
            problems.add("meta", f"meta.platform.{pid} is not in meta.order")
        if not spec.get("title"):
            problems.add(f"meta.platform.{pid}", "needs a title")
        if not spec.get("entry_roots"):
            problems.add(f"meta.platform.{pid}", "needs entry_roots")

    features = data.get("feature", [])
    if not features:
        problems.add("feature", "no features")
        return

    seen_ids: set[str] = set()
    cited_paths: set[str] = set()
    cited_needles: set[tuple[str, str]] = set()

    for feature in features:
        fid = feature.get("id", "<no id>")
        where = f"feature {fid}"
        if not feature.get("id"):
            problems.add(where, "needs an id")
        if fid in seen_ids:
            problems.add(where, "duplicate id")
        seen_ids.add(fid)
        for field in ("title", "area"):
            if not feature.get(field):
                problems.add(where, f"needs a {field}")

        cells = {k: v for k, v in feature.items() if isinstance(v, dict)}
        for pid in platforms:
            if pid not in cells:
                problems.add(where, f"no cell for {pid}")
        for pid in cells:
            if pid not in platforms:
                problems.add(where, f"cell {pid} is not a platform")

        for pid in platforms:
            cell = cells.get(pid)
            if cell is None:
                continue
            cwhere = f"{where}/{pid}"
            unknown = set(cell) - CELL_KEYS
            if unknown:
                problems.add(cwhere, f"unknown keys: {', '.join(sorted(unknown))}")

            presence = cell.get("presence")
            coverage = cell.get("coverage")
            if presence not in PRESENCE:
                problems.add(cwhere, f"presence must be one of {PRESENCE}")
            if coverage not in LEVELS:
                problems.add(cwhere, f"coverage must be one of {LEVELS}")

            # --- presence ---------------------------------------------------
            if presence == "present":
                entry = cell.get("entry")
                if not entry:
                    problems.add(cwhere, "present, so it must name an `entry` that a user can reach")
                else:
                    path = resolve(entry, problems, f"{cwhere} entry")
                    roots = meta["platform"].get(pid, {}).get("entry_roots", [])
                    if path and not under_roots(path, roots):
                        problems.add(
                            cwhere,
                            f"entry {path} is not in this platform's UI entry layer "
                            f"(meta.platform.{pid}.entry_roots) — a feature whose only "
                            "mention is below the UI is not reachable, which is the #571 shape",
                        )
                    if path:
                        cited_paths.add(path)
                if cell.get("absence"):
                    problems.add(cwhere, "`absence` belongs on an absent cell only")
                if coverage == "n/a":
                    problems.add(cwhere, "coverage n/a is for absent features; use `none`")
            elif presence == "absent":
                if cell.get("entry"):
                    problems.add(cwhere, "absent, so it cannot have an `entry`")
                if coverage != "n/a":
                    problems.add(cwhere, "absent, so coverage must be n/a")
                if cell.get("absence") not in ABSENCE:
                    problems.add(cwhere, f"absent, so `absence` must be one of {ABSENCE}")
                if not cell.get("note"):
                    problems.add(cwhere, "absent, so it must say why in `note`")

            # --- coverage ---------------------------------------------------
            tests = cell.get("tests", [])
            if not isinstance(tests, list):
                problems.add(cwhere, "`tests` must be a list")
                tests = []
            if coverage in ("ui", "engine") and not tests:
                problems.add(cwhere, f"coverage {coverage} must name at least one test")
            if coverage in ("none", "n/a") and tests:
                problems.add(cwhere, f"coverage {coverage} cannot name tests")
            for ref in tests:
                path = resolve(ref, problems, f"{cwhere} tests")
                if path:
                    cited_paths.add(path)
                    _, needle = split_ref(ref)
                    if needle:
                        cited_needles.add((path, needle))

            if coverage in ("none", "engine") and not cell.get("note"):
                problems.add(cwhere, f"coverage {coverage} is a hole — say what is missing in `note`")

            manual = cell.get("manual")
            if manual:
                path = resolve(manual, problems, f"{cwhere} manual")
                if path and not path.startswith(MANUAL_DOCS):
                    problems.add(
                        cwhere,
                        f"manual must point into {', '.join(MANUAL_DOCS)} — the place a human is "
                        f"actually told to do it — not {path}",
                    )

            issue = cell.get("issue")
            if issue is not None and not isinstance(issue, int):
                problems.add(cwhere, "`issue` must be the issue number, as an integer")

    # --- the ratchet: everything in the tree has to be on the map -----------
    exempt = {e.get("ref"): e.get("why") for e in data.get("exempt", [])}
    for ref, why in exempt.items():
        if not ref:
            problems.add("exempt", "an exemption needs a `ref`")
        elif not why:
            problems.add(f"exempt {ref}", "an exemption needs a `why`")

    unmapped: list[str] = []
    for kind, rel in suite_files():
        if rel in cited_paths or rel in exempt:
            continue
        unmapped.append(f"{kind}: {rel}")

    program = "src/MegaPDF.Avalonia/Program.cs"
    for section in avalonia_self_test_sections():
        ref = f"{program}::{section}"
        if (program, section) not in cited_needles and ref not in exempt:
            unmapped.append(f"Avalonia --self-test section: {ref}")

    workflow = ".github/workflows/ci.yml"
    for state in windows_selftest_states():
        needle = f'Run-Check "{state}"'
        ref = f"{workflow}::{needle}"
        if (workflow, needle) not in cited_needles and ref not in exempt:
            unmapped.append(f"Windows UI self-test state: {ref}")

    for item in unmapped:
        problems.add(
            "unmapped",
            f"{item} — no cell cites it. Put it on the map (or add an `[[exempt]]` with a why).",
        )

    for ref in exempt:
        if not ref:
            continue
        path, needle = split_ref(ref)
        text = file_text(path)
        if text is None:
            problems.add(f"exempt {ref}", f"{path} does not exist — stale exemption")
        elif needle and needle not in text:
            problems.add(f"exempt {ref}", f"{path} no longer contains {needle!r} — stale exemption")


# ---------------------------------------------------------------------------
# Gaps and the generated document
# ---------------------------------------------------------------------------

def gaps(data: dict) -> list[dict]:
    """Every cell the map admits is a hole, worst first.

    Four kinds, in the order they are worth fixing:
      1 missing   — the feature is absent on a platform where it should exist (#571 shape)
      2 blind     — present, nothing automated at all
      3 engine    — present, only the shared core is tested; this platform's path is not
      4 excluded  — present and tested, but the test does not run in CI (noted per cell)
    """
    order = {"missing": 1, "blind": 2, "engine": 3}
    out = []
    for feature in data["feature"]:
        for pid in data["meta"]["order"]:
            cell = feature.get(pid, {})
            kind = None
            if cell.get("presence") == "absent" and cell.get("absence") == "gap":
                kind = "missing"
            elif cell.get("presence") == "present" and cell.get("coverage") == "none":
                kind = "blind"
            elif cell.get("coverage") == "engine":
                kind = "engine"
            if kind:
                out.append({
                    "kind": kind,
                    "feature": feature["id"],
                    "title": feature["title"],
                    "area": feature["area"],
                    "platform": pid,
                    "note": cell.get("note", ""),
                    "issue": cell.get("issue"),
                    "manual": cell.get("manual"),
                })
    out.sort(key=lambda g: (order[g["kind"]], g["area"], g["feature"], g["platform"]))
    return out


def cell_code(cell: dict) -> str:
    if cell.get("presence") == "absent":
        return "GAP" if cell.get("absence") == "gap" else "n/a"
    code = {"ui": "UI", "engine": "eng", "none": "—"}[cell["coverage"]]
    if cell.get("manual"):
        code += "+m"
    return code


def render(data: dict) -> str:
    meta = data["meta"]
    platforms = meta["order"]
    titles = [meta["platform"][p]["title"] for p in platforms]
    features = data["feature"]
    g = gaps(data)

    counts = {code: 0 for code in ("UI", "eng", "—", "n/a", "GAP")}
    for feature in features:
        for pid in platforms:
            counts[cell_code(feature[pid]).replace("+m", "")] += 1
    total = len(features) * len(platforms)

    lines: list[str] = []
    w = lines.append

    w("# Test matrix: features × platforms (#330)")
    w("")
    w("**Generated — do not edit.** The source is `tests/matrix/coverage.toml`;")
    w("`tools/qa/check-matrix.py` validates it against the tree and rewrites this file, and")
    w("the `qa-matrix` workflow fails if either has drifted. Read that script's header for why")
    w("the matrix has this shape rather than the feature × action × state × platform grid #330")
    w("first asked for.")
    w("")
    w("Each cell says two things and both are checked against the code:")
    w("")
    w("| code | means |")
    w("|---|---|")
    w("| `UI` | a test in CI drives this platform's own UI or view-model path |")
    w("| `eng` | only the shared engine/core is tested — this platform's path to it is not |")
    w("| `—` | present and reachable, nothing automated |")
    w("| `n/a` | the feature is deliberately not on this platform, with a reason |")
    w("| `GAP` | the feature is **missing** where it should exist — the #571 shape |")
    w("| `+m` | a by-hand step in `TESTING.md` / `docs/RELEASING.md` covers it too |")
    w("")
    w(f"{total} pairings: "
      + ", ".join(f"{counts[c]} {c}" for c in ("UI", "eng", "—", "n/a", "GAP")) + ".")
    w("")
    w("This map is about *what is covered*, not whether the covering tests pass — that is the")
    w("other jobs' business — and it does not replace the by-hand pass in `docs/RELEASING.md`")
    w("§2.3, which is still the release gate.")
    w("")

    w("## The gaps, worst first")
    w("")
    w("This is the part worth reading. A map that only restated what passes would not be")
    w("worth checking in.")
    w("")
    kinds = [
        ("missing", "Missing feature — absent where it should exist",
         "Nothing is failing; nothing is looking. #571 (Linux had an About window with no "
         "route to it) and #3 (Android had no whiteout at all) were both this."),
        ("blind", "Present, nothing automated",
         "The feature is reachable and no test in CI touches it."),
        ("engine", "Engine only — this platform's own path is untested",
         "The shared core proves the operation. Nothing proves this platform reaches it "
         "correctly, which is where #401 and #412 lived."),
    ]
    for kind, heading, blurb in kinds:
        rows = [x for x in g if x["kind"] == kind]
        w(f"### {heading} ({len(rows)})")
        w("")
        w(blurb)
        w("")
        if not rows:
            w("None.")
            w("")
            continue
        w("| feature | platform | what is missing | issue |")
        w("|---|---|---|---|")
        for row in rows:
            issue = f"#{row['issue']}" if row["issue"] else "—"
            note = row["note"].replace("\n", " ").strip()
            w(f"| {row['title']} (`{row['feature']}`) | {row['platform']} | {note} | {issue} |")
        w("")

    w("## The matrix")
    w("")
    area = None
    for index, feature in enumerate(features):
        if feature["area"] != area:
            area = feature["area"]
            w(f"### {area}")
            w("")
            w("| feature | " + " | ".join(titles) + " |")
            w("|---" * (len(platforms) + 1) + "|")
        codes = " | ".join(cell_code(feature[p]) for p in platforms)
        w(f"| **{feature['title']}** | {codes} |")
        if index + 1 == len(features) or features[index + 1]["area"] != area:
            w("")

    w("## Cell by cell")
    w("")
    w("What each cell rests on. Every `entry` and every test reference below is resolved")
    w("against the tree by `tools/qa/check-matrix.py` — a path or symbol that stops existing")
    w("fails CI rather than rotting here.")
    w("")
    for feature in features:
        w(f"### {feature['title']} (`{feature['id']}`)")
        if feature.get("about"):
            w("")
            w(feature["about"])
        w("")
        for pid in platforms:
            cell = feature[pid]
            bits = [f"**{meta['platform'][pid]['title']}** — {cell_code(cell)}"]
            if cell.get("presence") == "absent":
                bits.append(f"absent ({cell['absence']})")
            else:
                bits.append(f"coverage: {LEVEL_LABEL[cell['coverage']]}")
                bits.append(f"reachable from `{cell['entry']}`")
            if cell.get("tests"):
                bits.append("tests: " + ", ".join(f"`{t}`" for t in cell["tests"]))
            if cell.get("manual"):
                bits.append(f"by hand: `{cell['manual']}`")
            if cell.get("note"):
                bits.append(cell["note"])
            if cell.get("issue"):
                bits.append(f"#{cell['issue']}")
            w("- " + ". ".join(bit.rstrip(". ") for bit in bits) + ".")
        w("")

    if data.get("exempt"):
        w("## Test files deliberately not on the map")
        w("")
        w("The ratchet in `tools/qa/check-matrix.py` requires every test file, every Avalonia")
        w("`--self-test` section and every Windows self-test state to be cited by some cell.")
        w("These are the exceptions, each with its reason.")
        w("")
        w("| artefact | why not |")
        w("|---|---|")
        for item in data["exempt"]:
            w(f"| `{item['ref']}` | {item['why']} |")
        w("")

    return "\n".join(lines).rstrip() + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true",
                        help="fail instead of rewriting when docs/qa/test-matrix.md has drifted")
    parser.add_argument("--gaps", action="store_true", help="print the gap list and stop")
    args = parser.parse_args()

    data = load()
    problems = Problems()
    validate(data, problems)
    if problems:
        print(f"{MATRIX.relative_to(ROOT)}: {len(problems.items)} problem(s)", file=sys.stderr)
        for item in problems.items:
            print(f"::error::coverage map: {item}", file=sys.stderr)
        return 1

    if args.gaps:
        for row in gaps(data):
            issue = f" (#{row['issue']})" if row["issue"] else ""
            print(f"{row['kind']:8} {row['platform']:8} {row['feature']:24} {row['note']}{issue}")
        return 0

    rendered = render(data)
    current = DOC.read_text(encoding="utf-8") if DOC.is_file() else None
    if args.check:
        if current != rendered:
            print("::error::docs/qa/test-matrix.md is stale — run tools/qa/check-matrix.py "
                  "and commit the result", file=sys.stderr)
            return 1
        print(f"coverage map is sound and {DOC.relative_to(ROOT)} is up to date")
        return 0

    if current != rendered:
        DOC.write_text(rendered, encoding="utf-8")
        print(f"wrote {DOC.relative_to(ROOT)}")
    else:
        print(f"{DOC.relative_to(ROOT)} unchanged")
    return 0


if __name__ == "__main__":
    sys.exit(main())
