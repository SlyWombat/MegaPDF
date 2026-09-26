#!/usr/bin/env python3
"""#384 investigation: slices a #354 structure-battery run's measure 3 (tau_ref, agreement
with `pdftotext -layout`) by a "title-page-like" page-shape proxy, to measure whether #384's
hand-found example (a stylized title/cover page with badly out-of-order text) generalizes
across the corpus or is a one-off. Numbers only, never a file name or document text — the
same discipline as every other tool in this directory (structure_compare.py, report.py,
compare_runs.py).

    python3 tools/stress/structure_titlepage_slice.py <out-dir> [--sparse-pctl P] [--tau-bad T]

<out-dir> is what tools/stress/structure-battery.sh --reference wrote: battery-check.log,
keyed by the 12-character path hash, whose "ok" lines carry (since this investigation added
them to structure_check.cpp's `check` output) tau_ref alongside three index-aligned lists:
tau_ref_page (the page index each tau came from), tau_ref_tokens (that page's heuristic
block-token count) and tau_ref_area (that page's area in points^2). A run predating that
change has no such fields and this script reports zero title-page-like pages found, honestly,
rather than guessing.

Two proxies for "title-page-like", both defensible from #384's own description (a short,
sparsely-laid-out front page) and both computed from data the battery already gathers:

  A. PAGE-1 PROXY: page index 0 of a document with 2+ pages — the direct reading of #384's
     "single [title] page" sitting in front of a longer document with a Table of Contents.
     Compared against pages 2+ of the SAME multi-page documents ("ordinary" pages), not
     against single-page documents (a one-page invoice or letter is not the same population
     as a cover page in front of a longer body, so mixing them into "ordinary" would blur the
     comparison). Single-page documents' own page 0 is reported separately, for context only.

  B. SPARSE-PAGE PROXY: pages whose heuristic token count is below the P-th percentile
     (default 25th) of the token-count distribution over ALL measured pages in this run —
     "very little text ... relative to its size" per the issue. Token count alone (not
     token/area density) is used by default because the corpus is overwhelmingly one page
     size (US Letter, ~484704 pt^2 — confirmed by this run's own tau_ref_area values, printed
     below), so normalising by area would not change the ranking much; --by-density switches
     to token-count-per-area if the run's own area spread says otherwise.

Threshold for "scores badly": tau < 0.9 by default — the same median gate #354 already uses
for measure 2 (order agreement on tagged pages), reused here for consistency rather than
inventing a new number.
"""
import argparse
import os
import re
import sys

FIELD_RE = re.compile(r"(\w+)=([^\s]*)")


def load_rows(out_dir):
    path = os.path.join(out_dir, "battery-check.log")
    if not os.path.exists(path):
        sys.exit(f"no battery-check.log in {out_dir} (this script needs a `check` run, not --census)")
    rows = []
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
            rows.append(fields)
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
            out.append(None)
    return out


def median(values):
    if not values:
        return None
    s = sorted(values)
    n = len(s)
    mid = n // 2
    return s[mid] if n % 2 else (s[mid - 1] + s[mid]) / 2.0


def percentile(values, p):
    if not values:
        return None
    s = sorted(values)
    k = int((len(s) - 1) * p / 100.0)
    return s[k]


class PageRow:
    __slots__ = ("doc_id", "doc_pages", "page_idx", "tau", "tokens", "area")

    def __init__(self, doc_id, doc_pages, page_idx, tau, tokens, area):
        self.doc_id = doc_id
        self.doc_pages = doc_pages
        self.page_idx = page_idx
        self.tau = tau
        self.tokens = tokens
        self.area = area


def collect_page_rows(rows):
    out = []
    docs_ok = 0
    docs_with_tau_ref = 0
    for r in rows:
        if r.get("result") != "ok":
            continue
        docs_ok += 1
        doc_pages = as_int(r, "pages")
        taus = as_int_list(r, "tau_ref")
        pages_idx = as_int_list(r, "tau_ref_page")
        tokens = as_int_list(r, "tau_ref_tokens")
        areas = as_int_list(r, "tau_ref_area")
        if not taus:
            continue
        if not (len(taus) == len(pages_idx) == len(tokens) == len(areas)):
            # A battery run predating this investigation's fields, or a partial line — skip
            # rather than mismatch index-aligned lists silently.
            continue
        docs_with_tau_ref += 1
        for tau, pidx, tok, area in zip(taus, pages_idx, tokens, areas):
            out.append(PageRow(r["_id"], doc_pages, pidx, tau / 1000.0, tok, area))
    return out, docs_ok, docs_with_tau_ref


def summarize_group(name, taus, tau_bad):
    n = len(taus)
    if n == 0:
        print(f"  {name}: n=0")
        return
    m = median(taus)
    bad = sum(1 for t in taus if t < tau_bad)
    print(f"  {name}: n={n:,}  median tau={m:.3f}  tau<{tau_bad}: {bad:,} ({100.0 * bad / n:.1f}%)")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("out_dir")
    ap.add_argument("--sparse-pctl", type=float, default=25.0,
                     help="percentile of the token-count distribution below which a page counts "
                          "as 'sparse' (default: 25)")
    ap.add_argument("--tau-bad", type=float, default=0.9,
                     help="tau threshold below which a page 'scores badly' (default: 0.9, "
                          "matching #354's own measure-2 median gate)")
    ap.add_argument("--by-density", action="store_true",
                     help="use token-count-per-area instead of raw token count for the sparse proxy")
    args = ap.parse_args()

    rows = load_rows(args.out_dir)
    page_rows, docs_ok, docs_with_tau_ref = collect_page_rows(rows)

    print(f"== {args.out_dir}")
    print(f"documents opened ok:            {docs_ok:,}")
    print(f"documents with measured pages (tau_ref, this investigation's fields present): {docs_with_tau_ref:,}")
    if not page_rows:
        print("no page-level tau_ref/tau_ref_page/tau_ref_tokens/tau_ref_area data found.")
        print("(either the run predates this investigation's structure_check.cpp change, or no "
              "document had a usable --reference file.)")
        return

    total_pages = len(page_rows)
    areas = [p.area for p in page_rows if p.area]
    area_median = median(areas)
    area_min, area_max = (min(areas), max(areas)) if areas else (None, None)
    print(f"total pages measured (tau_ref_n across the run): {total_pages:,}")
    print(f"page area (pt^2): median={area_median:.0f}  min={area_min}  max={area_max} "
          f"(~constant means raw token count is a fine sparse-page proxy without area-normalising)")

    # ---- Proxy A: page 1 of a multi-page document, vs pages 2+ of the SAME population. ----
    multi = [p for p in page_rows if p.doc_pages >= 2]
    single = [p for p in page_rows if p.doc_pages == 1]
    group_a = [p.tau for p in multi if p.page_idx == 0]
    group_rest = [p.tau for p in multi if p.page_idx > 0]
    docs_multi = len({p.doc_id for p in multi})
    docs_single = len({p.doc_id for p in single})

    print()
    print("=== Proxy A: page 1 of a multi-page document ('title-page-like') vs pages 2+ ===")
    print(f"multi-page documents with a measured page 1: {len({p.doc_id for p in multi if p.page_idx == 0}):,} "
          f"(of {docs_multi:,} multi-page documents with any measured page)")
    print(f"single-page documents (reported separately, not pooled into either group): {docs_single:,}")
    print(f"denominator: page-1 pages are {100.0 * len(group_a) / total_pages:.1f}% of all {total_pages:,} "
          f"measured pages; pages 2+ are {100.0 * len(group_rest) / total_pages:.1f}%")
    summarize_group("page 1 of multi-page docs (proxy A, title-page-like)", group_a, args.tau_bad)
    summarize_group("pages 2+ of multi-page docs (ordinary)", group_rest, args.tau_bad)
    single_taus = [p.tau for p in single]
    summarize_group("single-page documents (context only, not 'ordinary' baseline)", single_taus, args.tau_bad)

    # ---- Proxy B: sparse pages (low token count) vs the rest, corpus-wide. ----
    if args.by_density:
        metric_name = "tokens/area (x1e6)"
        metrics = [1e6 * p.tokens / p.area if p.area else 0.0 for p in page_rows]
    else:
        metric_name = "token count"
        metrics = [float(p.tokens) for p in page_rows]
    threshold = percentile(metrics, args.sparse_pctl)
    print()
    print(f"=== Proxy B: sparse pages (bottom {args.sparse_pctl:.0f}th percentile of {metric_name}, "
          f"threshold={threshold:.1f}) vs the rest, corpus-wide ===")
    sparse_taus = [p.tau for p, m in zip(page_rows, metrics) if m <= threshold]
    dense_taus = [p.tau for p, m in zip(page_rows, metrics) if m > threshold]
    print(f"denominator: sparse pages are {100.0 * len(sparse_taus) / total_pages:.1f}% of all "
          f"{total_pages:,} measured pages ({len(sparse_taus):,} pages)")
    summarize_group("sparse pages (proxy B)", sparse_taus, args.tau_bad)
    summarize_group("non-sparse pages", dense_taus, args.tau_bad)

    # ---- Cross-tab: how much do the two proxies overlap on page 1s? ----
    page1_sparse = sum(1 for p, m in zip(page_rows, metrics) if p.page_idx == 0 and p.doc_pages >= 2 and m <= threshold)
    page1_total = len(group_a)
    if page1_total:
        print()
        print(f"overlap: {page1_sparse:,} of {page1_total:,} page-1-of-multipage pages ("
              f"{100.0 * page1_sparse / page1_total:.1f}%) also fall in the sparse-page bucket")

    # ---- Corpus-wide baseline, for reference. ----
    all_taus = [p.tau for p in page_rows]
    print()
    summarize_group("ALL measured pages (corpus-wide baseline)", all_taus, args.tau_bad)


if __name__ == "__main__":
    main()
