#!/usr/bin/env python3
"""Compares two #354 structure-battery runs: counts and per-measure deltas only, never a
file name or document text.

    python3 tools/stress/structure_compare.py <before-out-dir> <after-out-dir>

Each directory is what tools/stress/structure-battery.sh wrote: battery-check.log (or
battery-census.log for a --census run) and summary.txt. Documents are matched by the
12-character path hash that keys every line, exactly as redaction-battery.sh's log does —
the same hash on two runs over the same corpus means the same document, with no file name
ever read or printed.
"""
import collections
import os
import re
import sys

FIELD_RE = re.compile(r"(\w+)=([^\s]*)")


def load(out_dir):
    for name in ("battery-check.log", "battery-census.log"):
        path = os.path.join(out_dir, name)
        if os.path.exists(path):
            break
    else:
        sys.exit(f"no battery-*.log found in {out_dir}")
    rows = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line:
                continue
            parts = line.split(" ", 1)
            if len(parts) != 2:
                continue
            doc_id, rest = parts
            fields = dict(FIELD_RE.findall(rest))
            fields["_id"] = doc_id
            rows[doc_id] = fields
    return rows


def as_int(fields, key, default=0):
    v = fields.get(key)
    if v is None or v == "":
        return default
    try:
        return int(float(v))
    except ValueError:
        return default


def as_int_list(fields, key):
    v = fields.get(key)
    if not v:
        return []
    out = []
    for part in v.split(","):
        try:
            out.append(int(part))
        except ValueError:
            pass
    return out


def median(values):
    if not values:
        return None
    s = sorted(values)
    n = len(s)
    mid = n // 2
    return s[mid] if n % 2 else (s[mid - 1] + s[mid]) / 2.0


def summarise(name, rows):
    print(f"== {name}: {len(rows)} documents")
    outcomes = collections.Counter(r.get("result", "?") for r in rows.values())
    print("  outcomes:", dict(outcomes.most_common()))

    ok = [r for r in rows.values() if r.get("result") == "ok"]
    if not ok:
        return
    pages = sum(as_int(r, "pages") for r in ok)
    tagged = sum(as_int(r, "tagged") for r in ok)
    textless = sum(as_int(r, "textless") for r in ok)
    multicol = sum(as_int(r, "multicol") for r in ok)
    manycut = sum(as_int(r, "manycut") for r in ok)
    print(f"  pages: {pages:,}  tagged: {tagged:,}  textless: {textless:,}  "
          f"multi-column: {multicol:,}  many-cut: {manycut:,}")

    fid_m = sum(as_int(r, "fid_match") for r in ok)
    fid_a = sum(as_int(r, "fid_a") for r in ok)
    fid_b = sum(as_int(r, "fid_b") for r in ok)
    fid_low09 = sum(as_int(r, "fid_low09") for r in ok)
    if fid_a + fid_b > 0:
        f1 = 2.0 * fid_m / (fid_a + fid_b)
        print(f"  measure 1 (token fidelity): aggregate F1 = {f1:.6f}, pages < 0.9: {fid_low09:,}")

    tau_tree = [v for r in ok for v in as_int_list(r, "tau_tree")]
    tau_ref = [v for r in ok for v in as_int_list(r, "tau_ref")]
    if tau_tree:
        m = median(tau_tree)
        print(f"  measure 2 (order agreement, tagged pages): n={len(tau_tree):,} median tau={m / 1000.0:.3f}")
    if tau_ref:
        m = median(tau_ref)
        print(f"  measure 3 (poppler agreement, informational): n={len(tau_ref):,} median tau={m / 1000.0:.3f}")

    rss = [as_int(r, "rss_kb") for r in ok if r.get("rss_kb")]
    if rss:
        print(f"  peak RSS: max {max(rss):,} KB")


def compare(before, after):
    shared = set(before) & set(after)
    print(f"== transitions: {len(shared)} documents in both runs")
    outcome_changes = collections.Counter()
    fid_regressions = 0
    fid_improvements = 0
    multicol_changes = collections.Counter()
    for doc_id in shared:
        b, a = before[doc_id], after[doc_id]
        if b.get("result") != a.get("result"):
            outcome_changes[(b.get("result"), a.get("result"))] += 1
            continue
        if b.get("result") != "ok":
            continue
        fb = as_int(b, "fid_match"), as_int(b, "fid_a"), as_int(b, "fid_b")
        fa = as_int(a, "fid_match"), as_int(a, "fid_a"), as_int(a, "fid_b")
        f1_before = 2.0 * fb[0] / (fb[1] + fb[2]) if fb[1] + fb[2] else None
        f1_after = 2.0 * fa[0] / (fa[1] + fa[2]) if fa[1] + fa[2] else None
        if f1_before is not None and f1_after is not None:
            if f1_after < f1_before - 0.01:
                fid_regressions += 1
            elif f1_after > f1_before + 0.01:
                fid_improvements += 1
        mb, ma = as_int(b, "multicol"), as_int(a, "multicol")
        if mb != ma:
            multicol_changes[(mb, ma)] += 1
    print("  document outcome changes:", dict(outcome_changes.most_common()) or "none")
    print(f"  token-fidelity F1 regressions (page-level >1pt drop): {fid_regressions:,}; "
          f"improvements: {fid_improvements:,}")
    print("  multi-column page-count changes per document:", dict(multicol_changes.most_common(20)) or "none")


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    before_dir, after_dir = sys.argv[1], sys.argv[2]
    before, after = load(before_dir), load(after_dir)
    summarise(os.path.basename(os.path.normpath(before_dir)), before)
    summarise(os.path.basename(os.path.normpath(after_dir)), after)
    compare(before, after)


if __name__ == "__main__":
    main()
