#!/usr/bin/env python3
"""Summarise one or more MegaPDF.Stress runs as anonymised Markdown (#92).

    tools/stress/report.py <run-dir> [<run-dir> ...] [--md out.md] [--top N]

A run directory is what `MegaPDF.Stress run --out` wrote: results.jsonl, run.json,
run.log, files.txt. The report never prints a file name: documents are referred to
by their index in files.txt (private), and every error message has paths stripped.
Stdlib only.
"""
import argparse
import collections
import json
import os
import re
import statistics
import sys

PATH_RE = re.compile(r"([A-Za-z]:\\|/Users/|/home/|/private/|/var/|/tmp/)\S*")


def sanitize(msg):
    if not msg:
        return msg
    msg = PATH_RE.sub("<path>", msg)
    return msg.strip()


def pct(values, p):
    if not values:
        return None
    s = sorted(values)
    k = (len(s) - 1) * p / 100.0
    lo, hi = int(k), min(int(k) + 1, len(s) - 1)
    return s[lo] + (s[hi] - s[lo]) * (k - lo)


def fmt(v, digits=0):
    if v is None:
        return "—"
    if isinstance(v, float):
        return f"{v:,.{digits}f}"
    return f"{v:,}"


def pct_row(label, values, digits=0, unit=""):
    if not values:
        return f"| {label} | — | — | — | — | — |"
    return (f"| {label} | {fmt(pct(values, 50), digits)}{unit} | {fmt(pct(values, 90), digits)}{unit} | "
            f"{fmt(pct(values, 99), digits)}{unit} | {fmt(max(values), digits)}{unit} | {len(values):,} |")


PCT_HEADER = "| metric | p50 | p90 | p99 | max | n |\n|---|---:|---:|---:|---:|---:|"


class Run:
    def __init__(self, path):
        self.path = path
        self.name = os.path.basename(os.path.normpath(path))
        self.info = {}
        try:
            with open(os.path.join(path, "run.json"), encoding="utf-8") as f:
                self.info = json.load(f)
        except OSError:
            pass
        self.results = []
        with open(os.path.join(path, "results.jsonl"), encoding="utf-8") as f:
            for line in f:
                try:
                    self.results.append(json.loads(line))
                except ValueError:
                    pass
        self.log_tail = ""
        try:
            with open(os.path.join(path, "run.log"), encoding="utf-8") as f:
                lines = f.read().splitlines()
            fin = [l for l in lines if " finished:" in l]
            prog = [l for l in lines if " progress " in l]
            self.log_tail = (fin or prog or [""])[-1]
        except OSError:
            pass
        self.by_index = {r["i"]: r for r in self.results}
        # Windows and macOS enumerate the same tree in different orders (path
        # separators sort differently), so cross-run matching goes by path.
        self.by_key = {r["path"].replace("\\", "/").lower(): r for r in self.results}

    def ok(self):
        return [r for r in self.results if r.get("pages") is not None and r.get("outcome") in ("ok", "partial")]


def section_overview(run, out):
    info = run.info
    out.append(f"## Run `{run.name}`\n")
    out.append(f"- Machine: {info.get('machine', '?')} — {info.get('os', '?')} {info.get('arch', '')}, "
               f"{info.get('cpus', '?')} logical CPUs, {info.get('workers', '?')} workers, render scale {info.get('scale', '?')}")
    out.append(f"- PDFium pin: {info.get('pdfium') or '?'}; phases: {info.get('phases', '?')}; terms: {info.get('terms', '?')}")
    out.append(f"- Started: {info.get('started', '?')}")
    if run.log_tail:
        out.append(f"- Last log line: `{run.log_tail}`")
    out.append("")

    counts = collections.Counter(r.get("outcome") for r in run.results)
    out.append("| outcome | files |\n|---|---:|")
    for k, v in counts.most_common():
        out.append(f"| {k} | {v:,} |")
    out.append(f"| **total** | **{len(run.results):,}** |")
    out.append("")

    ok = run.ok()
    pages = [r["pages"] for r in ok]
    total_bytes = sum(r.get("bytes", 0) for r in run.results)
    out.append(f"Corpus: {len(run.results):,} files, {total_bytes / 1e9:.2f} GB; {len(ok):,} opened with "
               f"{sum(pages):,} pages. Page counts: p50 {fmt(pct(pages, 50))}, p90 {fmt(pct(pages, 90))}, "
               f"p99 {fmt(pct(pages, 99))}, max {fmt(max(pages) if pages else None)}. "
               f"Largest file {max((r.get('bytes', 0) for r in run.results), default=0) / 1e6:.0f} MB.")
    scans = [r for r in ok if (r.get("scroll") or {}).get("text_chars") == 0]
    out.append(f"Documents with no extractable text at all (scans/pictures): {len(scans):,} "
               f"({100.0 * len(scans) / max(1, len(ok)):.0f}%).")
    out.append("")


def section_open(run, out, top):
    ok = run.ok()
    out.append("### Open\n")
    out.append("`open_ms` is parse time from a local copy; `read_ms` is the read from the corpus location; "
               "`sizepass` is the open-time geometry pass over every page (the app does this before showing anything); "
               "`first3` is the time until the first three pages are rendered with their interaction regions, "
               "measured from the start of scrolling.\n")
    out.append(PCT_HEADER)
    out.append(pct_row("read ms", [r["read_ms"] for r in run.results if r.get("read_ms") is not None]))
    mbps = [r["bytes"] / 1e6 / (r["read_ms"] / 1000.0) for r in run.results if r.get("read_ms") and r["read_ms"] > 5 and r.get("bytes", 0) > 1e6]
    out.append(pct_row("read MB/s (files > 1 MB)", mbps))
    out.append(pct_row("open ms", [r["open_ms"] for r in ok]))
    out.append(pct_row("sizepass ms", [r["sizepass_ms"] for r in ok]))
    out.append(pct_row("sizepass µs/page", [1000.0 * r["sizepass_ms"] / r["pages"] for r in ok if r["pages"]]))
    out.append(pct_row("first3 ms", [r["scroll"]["first3_ms"] for r in ok if r.get("scroll")]))
    out.append(pct_row("open+sizepass+first3 ms", [r["open_ms"] + r["sizepass_ms"] + r["scroll"]["first3_ms"] for r in ok if r.get("scroll")]))
    out.append("")
    slow = sorted(ok, key=lambda r: -(r["open_ms"] + r["sizepass_ms"] + (r.get("scroll") or {}).get("first3_ms", 0)))[:top]
    out.append(f"Slowest {top} to first paint (open + sizepass + first 3 pages):\n")
    out.append("| doc | pages | MB | open ms | sizepass ms | first3 ms | total ms |\n|---|---:|---:|---:|---:|---:|---:|")
    for r in slow:
        f3 = (r.get("scroll") or {}).get("first3_ms", 0)
        out.append(f"| #{r['i']} | {r['pages']:,} | {r['bytes'] / 1e6:.1f} | {r['open_ms']:.0f} | {r['sizepass_ms']:.0f} | {f3:.0f} | "
                   f"{r['open_ms'] + r['sizepass_ms'] + f3:.0f} |")
    out.append("")


def section_scroll(run, out, top):
    ok = [r for r in run.ok() if r.get("scroll")]
    out.append("### Scroll to the end\n")
    out.append("Every page rendered at the viewport scale, followed by the interaction-region pass the app runs "
               "on the same task (stamps, text boxes, form fields, text lines, whiteouts, drawn checkboxes). "
               "Both are per page; a page that takes long here is a page that stays blank while the user waits.\n")
    render, regions, both = [], [], []
    worst = []
    for r in ok:
        s = r["scroll"]
        for p, (a, b) in enumerate(zip(s["render_ms"], s["regions_ms"])):
            render.append(a)
            regions.append(b)
            both.append(a + b)
            worst.append((a + b, a, b, r["i"], p))
    out.append(PCT_HEADER)
    out.append(pct_row("render ms/page", render))
    out.append(pct_row("regions ms/page", regions))
    out.append(pct_row("render+regions ms/page", both))
    out.append(pct_row("doc total ms", [r["scroll"]["total_ms"] for r in ok]))
    out.append(pct_row("doc ms/page", [r["scroll"]["total_ms"] / r["pages"] for r in ok if r["pages"]]))
    out.append("")
    n = max(1, len(both))
    out.append("| pages slower than | count | share |\n|---|---:|---:|")
    for t in (100, 250, 500, 1000, 2000, 5000):
        c = sum(1 for v in both if v > t)
        out.append(f"| {t} ms | {c:,} | {100.0 * c / n:.2f}% |")
    out.append("")
    worst.sort(reverse=True)
    out.append(f"Worst {top} pages:\n")
    out.append("| doc | page | render ms | regions ms | doc pages | doc MB | largest page (pt) |\n|---|---:|---:|---:|---:|---:|---|")
    for tot, a, b, i, p in worst[:top]:
        r = run.by_index[i]
        lp = r.get("largest_pt") or []
        out.append(f"| #{i} | {p + 1} | {a:,} | {b:,} | {r['pages']:,} | {r['bytes'] / 1e6:.1f} | {'×'.join(str(x) for x in lp)} |")
    out.append("")
    slow_docs = sorted(ok, key=lambda r: -r["scroll"]["total_ms"])[:top]
    out.append(f"Worst {top} documents by total scroll time:\n")
    out.append("| doc | pages | MB | total s | ms/page | max page ms | text chars |\n|---|---:|---:|---:|---:|---:|---:|")
    for r in slow_docs:
        s = r["scroll"]
        mx = max((a + b for a, b in zip(s["render_ms"], s["regions_ms"])), default=0)
        out.append(f"| #{r['i']} | {r['pages']:,} | {r['bytes'] / 1e6:.1f} | {s['total_ms'] / 1000:.1f} | "
                   f"{s['total_ms'] / max(1, r['pages']):.0f} | {mx:,} | {s.get('text_chars', 0):,} |")
    out.append("")
    blank = sum(len(r["scroll"].get("blank_pages", [])) for r in ok)
    bwt = [(r["i"], r["scroll"]["blank_with_text"]) for r in ok if r["scroll"].get("blank_with_text")]
    out.append(f"Blank renders: {blank:,} pages came back entirely white; {sum(len(b) for _, b in bwt):,} of those "
               f"have extractable text on them (a candidate rendering defect) across {len(bwt):,} documents"
               + (": " + ", ".join(f"#{i} p{','.join(str(p + 1) for p in b[:5])}" for i, b in bwt[:top]) if bwt else "") + ".")
    fields = sum(r["scroll"].get("fields", 0) for r in ok)
    squares = sum(r["scroll"].get("squares", 0) for r in ok)
    lines = sum(r["scroll"].get("lines", 0) for r in ok)
    out.append(f"Interaction regions found: {lines:,} text lines, {fields:,} form fields, {squares:,} drawn checkbox squares, "
               f"{sum(r['scroll'].get('stamps', 0) for r in ok):,} MegaPDF stamps.")
    out.append("")


def section_search(run, out, top):
    ok = [r for r in run.ok() if r.get("search")]
    out.append("### Search\n")
    out.append("Whole-document sweep, page by page, exactly as the find bar does it. `ms/page` is what the "
               "user waits per page before the count appears; `first hit` is when the first highlight could show.\n")
    terms = []
    for r in ok:
        for t in r["search"]:
            if t not in terms:
                terms.append(t)
    for t in terms:
        rs = [(r, r["search"][t]) for r in ok if t in r["search"]]
        out.append(f"**Term `{t}`** — {len(rs):,} documents searched, {sum(s['hits'] for _, s in rs):,} hits in "
                   f"{sum(1 for _, s in rs if s['hits']):,} documents.\n")
        out.append(PCT_HEADER)
        out.append(pct_row("doc total ms", [s["ms"] for _, s in rs]))
        out.append(pct_row("ms/page", [s["ms"] / r["pages"] for r, s in rs if r["pages"]], 1))
        out.append(pct_row("slowest page ms", [s["max_page_ms"] for _, s in rs]))
        out.append(pct_row("first hit ms", [s["first_hit_ms"] for _, s in rs if s.get("first_hit_ms") is not None]))
        out.append("")
        slow = sorted(rs, key=lambda x: -x[1]["ms"])[:top]
        out.append(f"Slowest {top} documents for `{t}`:\n")
        out.append("| doc | pages | total s | ms/page | slowest page | its ms | hits |\n|---|---:|---:|---:|---:|---:|---:|")
        for r, s in slow:
            out.append(f"| #{r['i']} | {r['pages']:,} | {s['ms'] / 1000:.1f} | {s['ms'] / max(1, r['pages']):.0f} | "
                       f"{s['max_page'] + 1} | {s['max_page_ms']:.0f} | {s['hits']:,} |")
        out.append("")
    if "the" in terms:
        sus = [r for r in ok if "the" in r["search"] and r["search"]["the"]["hits"] == 0
               and (r.get("scroll") or {}).get("text_chars", 0) > 5000]
        out.append(f"Documents with more than 5,000 extractable characters but zero hits for `the`: {len(sus):,}"
                   + (" (" + ", ".join(f"#{r['i']}" for r in sus[:top]) + ")" if sus else "")
                   + ". Some are legitimately non-English or all-numeric; the rest are search-extraction mismatches worth a look.")
        out.append("")


def section_zoom(run, out, top):
    ok = [r for r in run.ok() if r.get("zoom")]
    out.append("### Zoom extremes\n")
    out.append("The first page and the largest page rendered at 300% and 50% zoom at the viewport scale.\n")
    big = [z for r in ok for z in r["zoom"] if z["zoom"] >= 2.9]
    small = [z for r in ok for z in r["zoom"] if z["zoom"] <= 0.6]
    out.append(PCT_HEADER)
    out.append(pct_row("300% render ms", [z["ms"] for z in big if z.get("ms") is not None]))
    out.append(pct_row("300% megapixels", [z["px"][0] * z["px"][1] / 1e6 for z in big if z.get("px")], 1))
    out.append(pct_row("50% render ms", [z["ms"] for z in small if z.get("ms") is not None]))
    out.append("")
    fails = [(r["i"], z) for r in ok for z in r["zoom"] if z.get("error")]
    out.append(f"Zoom renders that failed: {len(fails):,}.")
    if fails:
        out.append("\n| doc | page | zoom | px | error |\n|---|---:|---:|---|---|")
        for i, z in fails[:top]:
            out.append(f"| #{i} | {z['page'] + 1} | {z['zoom']:.1f} | {'×'.join(str(x) for x in z.get('px', []))} | {sanitize(z['error'])} |")
    worst = sorted(((z["ms"], r["i"], z) for r in ok for z in r["zoom"] if z.get("ms") is not None and z["zoom"] >= 2.9), reverse=True)[:top]
    out.append(f"\nSlowest {top} 300% renders:\n")
    out.append("| doc | page | px | ms |\n|---|---:|---|---:|")
    for ms, i, z in worst:
        out.append(f"| #{i} | {z['page'] + 1} | {'×'.join(str(x) for x in z['px'])} | {ms:,.0f} |")
    out.append("")


def section_save(run, out, top):
    ok = [r for r in run.ok() if r.get("save")]
    out.append("### Save and reopen\n")
    good = [r for r in ok if not r["save"].get("error")]
    out.append(PCT_HEADER)
    out.append(pct_row("save ms", [r["save"]["ms"] for r in good]))
    out.append(pct_row("reopen ms", [r["save"]["reopen_ms"] for r in good if r["save"].get("reopen_ms") is not None]))
    out.append(pct_row("saved/original size", [r["save"]["bytes"] / r["bytes"] for r in good if r.get("bytes")], 2))
    out.append("")
    errs = collections.Counter(sanitize(r["save"]["error"]) for r in ok if r["save"].get("error"))
    mismatch = [r for r in good if r["save"].get("reopen_pages") is not None and r["save"]["reopen_pages"] != r["pages"]]
    out.append(f"Save failures: {sum(errs.values()):,}; reopened with a different page count: {len(mismatch):,}"
               + (" (" + ", ".join(f"#{r['i']}" for r in mismatch[:top]) + ")" if mismatch else "") + ".")
    for msg, c in errs.most_common(top):
        ids = [r["i"] for r in ok if sanitize(r["save"].get("error")) == msg][:5]
        out.append(f"- {c:,} × `{msg}` e.g. {', '.join(f'#{i}' for i in ids)}")
    grow = sorted(good, key=lambda r: -(r["save"]["bytes"] - r["bytes"]))[:5]
    out.append("\nLargest growth on save (bytes appended by the incremental update):\n")
    out.append("| doc | original MB | saved MB | ratio |\n|---|---:|---:|---:|")
    for r in grow:
        out.append(f"| #{r['i']} | {r['bytes'] / 1e6:.2f} | {r['save']['bytes'] / 1e6:.2f} | {r['save']['bytes'] / max(1, r['bytes']):.2f} |")
    out.append("")


def section_images(run, out, top):
    ok = [r for r in run.ok() if r.get("images")]
    out.append("### Shrink-for-email candidates\n")
    out.append("`GetImages` over the document, then the decode (`RenderImageAt`) of every image the shrinker's "
               "rules would re-encode. This is the expensive half of Shrink; the JPEG encode is not exercised here.\n")
    withimg = [r for r in ok if r["images"]["count"]]
    elig = [r for r in ok if r["images"]["eligible"]]
    out.append(f"{len(withimg):,} documents carry {sum(r['images']['count'] for r in ok):,} images "
               f"({sum(r['images']['stored_bytes'] for r in ok) / 1e9:.2f} GB stored); "
               f"{len(elig):,} documents have {sum(r['images']['eligible'] for r in ok):,} shrink-eligible images; "
               f"{sum(r['images']['decode_errors'] for r in ok):,} decodes threw.\n")
    out.append(PCT_HEADER)
    out.append(pct_row("GetImages ms", [r["images"]["list_ms"] for r in ok]))
    out.append(pct_row("decode ms/image", [r["images"]["decode_ms"] / r["images"]["eligible"] for r in elig]))
    out.append(pct_row("slowest decode ms", [r["images"]["decode_max_ms"] for r in elig]))
    out.append(pct_row("doc decode total ms", [r["images"]["decode_ms"] for r in elig]))
    out.append(pct_row("largest image megapixels", [r["images"]["max_px"] / 1e6 for r in withimg], 1))
    out.append("")
    worst = sorted(elig, key=lambda r: -r["images"]["decode_ms"])[:top]
    out.append(f"Slowest {top} documents to decode for shrink:\n")
    out.append("| doc | pages | images | eligible | decode s | slowest image ms | largest image MP |\n|---|---:|---:|---:|---:|---:|---:|")
    for r in worst:
        im = r["images"]
        out.append(f"| #{r['i']} | {r['pages']:,} | {im['count']:,} | {im['eligible']:,} | {im['decode_ms'] / 1000:.1f} | "
                   f"{im['decode_max_ms']:.0f} | {im['max_px'] / 1e6:.1f} |")
    out.append("")


def section_memory(run, out):
    out.append("### Memory\n")
    out.append("Working set of each worker process after the document is closed and the GC has run, first file "
               "versus last file it processed; a steady climb across hundreds of files is a leak.\n")
    by_pid = collections.defaultdict(list)
    for r in run.results:
        if r.get("mem") and r.get("worker"):
            by_pid[r["worker"]].append(r)
    out.append("| worker pid | files | first ws MB | last ws MB | max ws-after-gc MB | max peak MB | max gc heap MB |\n|---|---:|---:|---:|---:|---:|---:|")
    for pid, rs in sorted(by_pid.items(), key=lambda kv: -len(kv[1])):
        ws = [r["mem"]["ws_after_gc"] for r in rs]
        peak = [r["mem"].get("ws_peak") or 0 for r in rs]
        heap = [r["mem"].get("gc_heap") or 0 for r in rs]
        out.append(f"| {pid} | {len(rs):,} | {ws[0] / 1e6:.0f} | {ws[-1] / 1e6:.0f} | {max(ws) / 1e6:.0f} | {max(peak) / 1e6:.0f} | {max(heap) / 1e6:.0f} |")
    out.append("")


def section_errors(run, out, top):
    out.append("### Failures\n")
    groups = collections.defaultdict(list)
    for r in run.results:
        oc = r.get("outcome")
        if oc in ("ok",):
            continue
        if oc in ("password", "format", "file") or (oc or "").startswith("load-"):
            groups[(oc, "open", "")].append(r["i"])
            continue
        if oc in ("crash", "hang", "error", "aborted"):
            groups[(oc, r.get("last_phase") or "?", sanitize(r.get("error") or ""))].append(r["i"])
        for phase, msg in (r.get("errors") or {}).items():
            groups[("partial", phase.split(":")[0], sanitize(msg))].append(r["i"])
    if not groups:
        out.append("None.\n")
        return
    out.append("| outcome | phase | message | files | examples |\n|---|---|---|---:|---|")
    for (oc, phase, msg), ids in sorted(groups.items(), key=lambda kv: -len(kv[1])):
        out.append(f"| {oc} | {phase} | {msg[:160]} | {len(ids):,} | {', '.join(f'#{i}' for i in ids[:6])} |")
    out.append("")
    ch = [r for r in run.results if r.get("outcome") in ("crash", "hang")]
    if ch:
        out.append("Crashes and hangs in detail:\n")
        out.append("| doc | outcome | last phase | last page | detail | MB |\n|---|---|---|---:|---|---:|")
        for r in ch:
            out.append(f"| #{r['i']} | {r['outcome']} | {r.get('last_phase')} | {(r.get('last_page') or 0) + 1} | "
                       f"{sanitize(r.get('error'))} | {r.get('bytes', 0) / 1e6:.1f} |")
        out.append("")


def section_compare(runs, out):
    out.append("## Side by side\n")
    out.append("| metric | " + " | ".join(r.name for r in runs) + " |\n|---|" + "---:|" * len(runs))

    def row(label, fn, digits=0):
        cells = []
        for run in runs:
            try:
                v = fn(run)
            except Exception:
                v = None
            cells.append(fmt(v, digits))
        out.append(f"| {label} | " + " | ".join(cells) + " |")

    def pages_ms(run, key):
        return [v for r in run.ok() if r.get("scroll") for v in r["scroll"][key]]

    row("files ok", lambda r: sum(1 for x in r.results if x.get("outcome") == "ok"))
    row("files not opened", lambda r: sum(1 for x in r.results if x.get("pages") is None))
    row("crashes + hangs", lambda r: sum(1 for x in r.results if x.get("outcome") in ("crash", "hang")))
    row("open ms p50", lambda r: pct([x["open_ms"] for x in r.ok()], 50), 1)
    row("open ms p99", lambda r: pct([x["open_ms"] for x in r.ok()], 99))
    row("render ms/page p50", lambda r: pct(pages_ms(r, "render_ms"), 50))
    row("render ms/page p99", lambda r: pct(pages_ms(r, "render_ms"), 99))
    row("render ms/page max", lambda r: max(pages_ms(r, "render_ms")))
    row("regions ms/page p50", lambda r: pct(pages_ms(r, "regions_ms"), 50))
    row("regions ms/page p99", lambda r: pct(pages_ms(r, "regions_ms"), 99))
    row("pages > 500 ms", lambda r: sum(1 for a, b in zip(pages_ms(r, "render_ms"), pages_ms(r, "regions_ms")) if a + b > 500))
    for t in ("Seaman", "the"):
        row(f"search `{t}` ms/page p50", lambda r, t=t: pct([x["search"][t]["ms"] / x["pages"] for x in r.ok() if x.get("search") and t in x["search"] and x["pages"]], 50), 2)
        row(f"search `{t}` ms/page p99", lambda r, t=t: pct([x["search"][t]["ms"] / x["pages"] for x in r.ok() if x.get("search") and t in x["search"] and x["pages"]], 99), 1)
        row(f"search `{t}` hits", lambda r, t=t: sum(x["search"][t]["hits"] for x in r.ok() if x.get("search") and t in x["search"]))
    row("300% render ms p50", lambda r: pct([z["ms"] for x in r.ok() for z in (x.get("zoom") or []) if z["zoom"] >= 2.9 and z.get("ms") is not None], 50))
    row("300% render failures", lambda r: sum(1 for x in r.ok() for z in (x.get("zoom") or []) if z.get("error")))
    row("save ms p50", lambda r: pct([x["save"]["ms"] for x in r.ok() if x.get("save") and not x["save"].get("error")], 50), 1)
    row("save failures", lambda r: sum(1 for x in r.ok() if x.get("save") and x["save"].get("error")))
    row("edits tried", lambda r: sum(1 for x in r.ok() if x.get("edit") and x["edit"].get("skipped") != "no text"))
    row("edits substituted", lambda r: sum(1 for x in r.ok() if x.get("edit") and x["edit"].get("outcome") == "substituted"))
    row("edits declined (layout, #118)", lambda r: sum(1 for x in r.ok() if x.get("edit") and x["edit"].get("skipped") == "layout"))
    row("edit failures", lambda r: sum(1 for x in r.ok() if x.get("edit") and x["edit"].get("error")))
    row("blank pages with text", lambda r: sum(len(x["scroll"].get("blank_with_text", [])) for x in r.ok() if x.get("scroll")))
    row("max worker ws MB", lambda r: max(x["mem"]["ws_after_gc"] for x in r.results if x.get("mem")) / 1e6)
    out.append("")

    if len(runs) == 2:
        a, b = runs
        out.append(f"Documents whose outcome differs between `{a.name}` and `{b.name}`:\n")
        diffs = []
        for key, ra in a.by_key.items():
            rb = b.by_key.get(key)
            if rb is None:
                continue
            i = f"#{ra['i']}/#{rb['i']}"
            if ra.get("outcome") != rb.get("outcome"):
                diffs.append((i, ra.get("outcome"), rb.get("outcome")))
            elif ra.get("pages") is not None and rb.get("pages") is not None and ra["pages"] != rb["pages"]:
                diffs.append((i, f"{ra['pages']} pages", f"{rb['pages']} pages"))
            elif ra.get("search") and rb.get("search"):
                for t in ra["search"]:
                    if t in rb["search"] and ra["search"][t]["hits"] != rb["search"][t]["hits"]:
                        diffs.append((i, f"{t}: {ra['search'][t]['hits']} hits", f"{t}: {rb['search'][t]['hits']} hits"))
            if ra.get("scroll") and rb.get("scroll") and ra["scroll"].get("blank_with_text") != rb["scroll"].get("blank_with_text"):
                diffs.append((i, f"blank-with-text pages {ra['scroll'].get('blank_with_text')}", f"{rb['scroll'].get('blank_with_text')}"))
        out.append(f"(Document ids are shown as `{a.name}` index / `{b.name}` index; the two runs number files differently.)\n")
        if diffs:
            out.append("| doc | " + a.name + " | " + b.name + " |\n|---|---|---|")
            for i, x, y in diffs[:50]:
                out.append(f"| {i} | {x} | {y} |")
            if len(diffs) > 50:
                out.append(f"\n… and {len(diffs) - 50} more.")
        else:
            out.append("None.")
        out.append("")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("runs", nargs="+")
    ap.add_argument("--md")
    ap.add_argument("--top", type=int, default=10)
    args = ap.parse_args()
    runs = [Run(p) for p in args.runs]
    out = ["# MegaPDF corpus stress report\n"]
    if len(runs) > 1:
        section_compare(runs, out)
    for run in runs:
        section_overview(run, out)
        section_open(run, out, args.top)
        section_scroll(run, out, args.top)
        section_search(run, out, args.top)
        section_zoom(run, out, args.top)
        section_save(run, out, args.top)
        section_images(run, out, args.top)
        section_memory(run, out)
        section_errors(run, out, args.top)
    text = "\n".join(out)
    if args.md:
        with open(args.md, "w", encoding="utf-8") as f:
            f.write(text)
        print(f"wrote {args.md} ({len(text):,} chars)")
    else:
        sys.stdout.write(text)


if __name__ == "__main__":
    main()
