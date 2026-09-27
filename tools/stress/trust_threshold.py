#!/usr/bin/env python3
"""#358: the trust rule's coverage threshold, measured from a structure-battery log.

    tools/stress/trust_threshold.py <out-dir>/structure/battery-check.log

Each `result=ok` line the battery keeps (tools/stress/structure-battery.sh, one per document,
keyed by the path hash) carries, per tagged page that had a `pdftotext -layout` reference, four
index-aligned comma lists from `megapdf_structure_check check`: tree_cov (the tree's character
coverage x1000), tau_treeref (Kendall tau of the tree's marked-content order against poppler),
tau_heurref (the heuristic path's order against the same reference) and tree_cov_page. This
script bins those pages by coverage and prints, per bin, how many pages, the median tau of each
order against poppler, and the share of pages where the tree agrees with poppler better than
the heuristics do. The trust threshold (core/megapdf_structure.cpp's kTaggedMinCoverage) is the
coverage below which that share drops under one half -- below it the tree is more often the
worse reading of the page than the better one. Poppler is another heuristic, not truth (design
#142 §7 measure 3), which is why the question asked of it is only "which of the two orders does
an independent reader agree with more", never "is the tree right".

Numbers only: the log has no document names or text, and neither does this output.
"""
import re
import statistics
import sys

BINS = [(0, 500), (500, 700), (700, 800), (800, 850), (850, 900), (900, 950), (950, 990), (990, 1001)]


def field_list(line, key):
    m = re.search(r"(?:^| )%s=([^ ]*)" % re.escape(key), line)
    if not m or not m.group(1):
        return []
    return [int(v) for v in m.group(1).strip().split(",") if v not in ("", "-")]


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    rows = []  # (coverage x1000, tau_tree, tau_heur)
    docs = pages_tagged = pages_tree = 0
    with open(sys.argv[1], encoding="utf-8", errors="replace") as f:
        for line in f:
            if " result=ok " not in line and not line.split(" ", 1)[-1].startswith("result=ok"):
                continue
            docs += 1
            m = re.search(r" tagged=(\d+)", line)
            if m:
                pages_tagged += int(m.group(1))
            m = re.search(r" tree=(\d+)", line)
            if m:
                pages_tree += int(m.group(1))
            cov = field_list(line, "tree_cov")
            tt = field_list(line, "tau_treeref")
            th = field_list(line, "tau_heurref")
            if not (len(cov) == len(tt) == len(th)):
                continue
            rows.extend(zip(cov, tt, th))
    print("documents ok: %d   tagged pages: %d   pages read via tree: %d   pages measured (tagged + reference): %d"
          % (docs, pages_tagged, pages_tree, len(rows)))
    print("%-12s %7s %12s %12s %14s" % ("coverage", "pages", "tau tree", "tau heur", "tree >= heur"))
    for lo, hi in BINS:
        sel = [r for r in rows if lo <= r[0] < hi]
        if not sel:
            print("%-12s %7d" % ("%.1f-%.1f%%" % (lo / 10, min(hi, 1000) / 10), 0))
            continue
        med_tree = statistics.median(r[1] for r in sel) / 1000.0
        med_heur = statistics.median(r[2] for r in sel) / 1000.0
        better = sum(1 for r in sel if r[1] >= r[2]) / len(sel)
        print("%-12s %7d %12.3f %12.3f %13.1f%%" % ("%.1f-%.1f%%" % (lo / 10, min(hi, 1000) / 10), len(sel), med_tree,
                                                  med_heur, better * 100))
    # The cumulative view the threshold is read from: for each candidate threshold, the pages
    # AT OR ABOVE it (the ones the rule would trust) and how often the tree beats the heuristics
    # among them, versus the pages below it (the ones it would reject).
    print()
    print("%-10s %9s %14s %9s %14s" % ("threshold", "trusted", "tree >= heur", "rejected", "tree >= heur"))
    for t in (500, 700, 800, 850, 900, 950, 990):
        above = [r for r in rows if r[0] >= t]
        below = [r for r in rows if r[0] < t]
        ba = (sum(1 for r in above if r[1] >= r[2]) / len(above) * 100) if above else float("nan")
        bb = (sum(1 for r in below if r[1] >= r[2]) / len(below) * 100) if below else float("nan")
        print("%-10s %9d %13.1f%% %9d %13.1f%%" % ("%.0f%%" % (t / 10), len(above), ba, len(below), bb))
    return 0


if __name__ == "__main__":
    sys.exit(main())
