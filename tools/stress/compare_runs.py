#!/usr/bin/env python3
"""Compares two corpus edit-battery runs (#127): counts only, never a file name or text.

    python3 tools/stress/compare_runs.py <before-out-dir> <after-out-dir>

Each directory is a `MegaPDF.Stress run --out` folder holding results.jsonl. Edits are
matched by (file, kind, page position, line position), so a run with extra edit kinds
(delete_word) still compares on everything the two runs share.

Prints, for each run and between them:
- document outcomes (ok, encrypted, format, crash, hang)
- edit results per kind (in_place, substituted, layout, no_font, deleted, skipped, ...)
- read-back failures per kind
- hidden copies (#136): edits where a whole second copy of the line survived, from
  line_overlap = runs overlapping the line "before->after"; a clean edit leaves the
  neighbours plus the new run (a delete, the neighbours alone)
- render_outside_px distribution: pixels changed outside the edited line's rows
- result transitions between the runs, most common first
"""
import collections
import json
import sys


def load(out_dir):
    rows = {}
    with open(f"{out_dir}/results.jsonl", encoding="utf-8") as f:
        for line in f:
            try:
                r = json.loads(line)
            except json.JSONDecodeError:
                continue
            rows[r["path"]] = r  # a resumed run appends; the last row for a file wins
    return rows


def edits(row):
    return (row.get("edits") or {}).get("items") or []


def hidden_copy(e):
    """True when a whole second copy of the line is still where the line was."""
    lo, runs = e.get("line_overlap"), e.get("runs")
    if not lo or not runs:
        return False
    before, after = map(int, lo.split("->"))
    expected = before - runs + (0 if e["kind"] == "delete" else 1)
    return after - expected >= runs


def render_bucket(px, total):
    if px is None or not total:
        return None
    if px == 0:
        return "0"
    share = px / total
    return "<=0.05%" if share <= 0.0005 else "<=0.5%" if share <= 0.005 else "<=5%" if share <= 0.05 else ">5%"


def summarise(name, rows):
    print(f"== {name}: {len(rows)} documents")
    print("  outcomes:", dict(collections.Counter(r["outcome"] for r in rows.values()).most_common()))
    results = collections.defaultdict(collections.Counter)
    read_back = collections.Counter()
    copies = collections.Counter()
    render = collections.Counter()
    for r in rows.values():
        for e in edits(r):
            results[e["kind"]][e["result"]] += 1
            if e.get("read_back") is False:
                read_back[e["kind"]] += 1
            if hidden_copy(e):
                copies[e["kind"]] += 1
            bucket = render_bucket(e.get("render_outside_px"), e.get("render_px"))
            if bucket:
                render[bucket] += 1
    for kind in sorted(results):
        print(f"  {kind:12}", dict(results[kind].most_common()))
    print("  read-back failures:", dict(read_back) or "none")
    print("  hidden copies (#136):", dict(copies) or "none", f"({sum(copies.values())} edits)")
    order = ["0", "<=0.05%", "<=0.5%", "<=5%", ">5%"]
    print("  render outside the line:", {b: render[b] for b in order if render[b]} or "not recorded")


def compare(before, after):
    shared = set(before) & set(after)
    print(f"== transitions: {len(shared)} documents in both runs")
    outcome = collections.Counter((before[p]["outcome"], after[p]["outcome"]) for p in shared
                                  if before[p]["outcome"] != after[p]["outcome"])
    print("  document outcome changes:", dict(outcome.most_common()) or "none")
    changes = collections.Counter()
    compared = 0
    for p in shared:
        was = {(e["kind"], e["page"], e["line"]): e for e in edits(before[p])}
        for e in edits(after[p]):
            key = (e["kind"], e["page"], e["line"])
            if key not in was:
                continue
            compared += 1
            old = was[key]
            if old["result"] != e["result"]:
                changes[(e["kind"], old["result"], e["result"])] += 1
            if old.get("read_back") is not False and e.get("read_back") is False:
                changes[(e["kind"], "read back", "not read back")] += 1
            if old.get("read_back") is False and e.get("read_back") is True:
                changes[(e["kind"], "not read back", "read back")] += 1
            if hidden_copy(old) and not hidden_copy(e):
                changes[(e["kind"], "hidden copy", "gone")] += 1
            if not hidden_copy(old) and hidden_copy(e):
                changes[(e["kind"], "no hidden copy", "hidden copy")] += 1
    print(f"  {compared} edits compared, {sum(changes.values())} changes")
    for (kind, was, now), n in changes.most_common(40):
        print(f"    {n:6}  {kind:12} {was} -> {now}")


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    before, after = load(sys.argv[1]), load(sys.argv[2])
    summarise(sys.argv[1].rstrip("/\\").split("/")[-1].split("\\")[-1], before)
    summarise(sys.argv[2].rstrip("/\\").split("/")[-1].split("\\")[-1], after)
    compare(before, after)


if __name__ == "__main__":
    main()
