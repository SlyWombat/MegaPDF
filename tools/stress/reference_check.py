#!/usr/bin/env python3
"""Cross-check a stress run against independent readers (#92).

    tools/stress/reference_check.py <run-dir> <corpus-root> [--out report.json]

Uses pypdf (page counts, encryption, text) and Ghostscript (rasterising) as
references for the things the run can only flag, not prove:

  * files PDFium refused that pypdf opens, and page counts that disagree;
  * "password" files that actually open with an empty user password;
  * documents with lots of text but zero hits for `the`: does pypdf's text contain it?
  * pages that rendered entirely white while carrying text: does Ghostscript put ink there?

Prints indices and numbers only, never a file name or any document text.
"""
import argparse
import io
import json
import os
import re
import subprocess
import sys
import tempfile
import warnings

warnings.filterwarnings("ignore")
import pypdf  # noqa: E402

try:
    from pypdf.errors import PdfReadError
except Exception:  # pragma: no cover
    PdfReadError = Exception


def load_run(run_dir):
    files = open(os.path.join(run_dir, "files.txt"), encoding="utf-8").read().splitlines()
    files = [f.rstrip("\r") for f in files]
    results = {}
    for line in open(os.path.join(run_dir, "results.jsonl"), encoding="utf-8"):
        try:
            r = json.loads(line)
        except ValueError:
            continue
        results[r["i"]] = r
    return files, results


def full_path(root, rel):
    return os.path.join(root, rel.replace("\\", "/"))


def pypdf_probe(path):
    """(pages, encrypted, opened_with_empty_password, error)"""
    try:
        with open(path, "rb") as f:
            data = f.read()
        rd = pypdf.PdfReader(io.BytesIO(data))
        enc = rd.is_encrypted
        empty_ok = None
        if enc:
            try:
                empty_ok = bool(rd.decrypt(""))
            except Exception:
                empty_ok = False
        try:
            n = len(rd.pages)
        except Exception as e:
            return None, enc, empty_ok, f"pages: {type(e).__name__}"
        return n, enc, empty_ok, None
    except Exception as e:
        return None, None, None, type(e).__name__


def pypdf_text(path, pages=None):
    rd = pypdf.PdfReader(path)
    if rd.is_encrypted:
        rd.decrypt("")
    out = {}
    idx = range(len(rd.pages)) if pages is None else pages
    for p in idx:
        try:
            out[p] = rd.pages[p].extract_text() or ""
        except Exception as e:
            out[p] = f"\0error {type(e).__name__}"
    return out


def gs_ink(path, page, dpi=40):
    """Fraction of non-white pixels when Ghostscript rasterises the page; None if gs fails."""
    with tempfile.TemporaryDirectory() as td:
        out = os.path.join(td, "p.pgm")
        cmd = ["gs", "-q", "-dNOPAUSE", "-dBATCH", "-dSAFER", "-sDEVICE=pgmraw", f"-r{dpi}",
               f"-dFirstPage={page + 1}", f"-dLastPage={page + 1}", f"-sOutputFile={out}", path]
        try:
            subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=120, check=False)
            with open(out, "rb") as f:
                data = f.read()
        except Exception:
            return None
        # P5\n<w> <h>\n255\n<bytes>
        m = re.match(rb"P5\s+(\d+)\s+(\d+)\s+(\d+)\s", data)
        if not m:
            return None
        pixels = data[m.end():]
        if not pixels:
            return None
        ink = sum(1 for b in pixels if b < 240)
        return ink / len(pixels)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("run")
    ap.add_argument("root")
    ap.add_argument("--out")
    ap.add_argument("--skip-counts", action="store_true")
    args = ap.parse_args()
    files, results = load_run(args.run)
    report = {"page_count_mismatch": [], "pdfium_refused_pypdf_opened": [], "password_but_empty_ok": [],
              "pypdf_failed_pdfium_ok": 0, "both_failed": [], "search_check": [], "blank_check": []}

    if not args.skip_counts:
        print(f"[1/3] page counts and encryption for {len(files)} files", flush=True)
        for i, rel in enumerate(files):
            r = results.get(i)
            if r is None:
                continue
            n, enc, empty_ok, err = pypdf_probe(full_path(args.root, rel))
            if r.get("pages") is None:
                if n is not None and n > 0 and r["outcome"] != "password":
                    report["pdfium_refused_pypdf_opened"].append({"i": i, "outcome": r["outcome"], "pypdf_pages": n, "encrypted": enc})
                elif r["outcome"] == "password" and empty_ok:
                    report["password_but_empty_ok"].append({"i": i, "pypdf_pages": n})
                elif n is None:
                    report["both_failed"].append({"i": i, "outcome": r["outcome"], "pypdf": err, "bytes": r.get("bytes")})
            else:
                if n is None:
                    report["pypdf_failed_pdfium_ok"] += 1
                elif n != r["pages"]:
                    report["page_count_mismatch"].append({"i": i, "pdfium": r["pages"], "pypdf": n})
            if i % 500 == 0:
                print(f"  {i}...", flush=True)

    print("[2/3] search consistency", flush=True)
    sus = [r for r in results.values() if r.get("search") and "the" in r["search"] and r["search"]["the"]["hits"] == 0
           and (r.get("scroll") or {}).get("text_chars", 0) > 5000]
    for r in sus:
        i = r["i"]
        try:
            texts = pypdf_text(full_path(args.root, files[i]))
        except Exception as e:
            report["search_check"].append({"i": i, "error": type(e).__name__})
            continue
        joined = "\n".join(t for t in texts.values() if not t.startswith("\0"))
        low = joined.lower()
        the_sub = low.count("the")
        the_word = len(re.findall(r"\bthe\b", low))
        # crude language hint: share of French stop words vs English
        fr = sum(low.count(w) for w in (" le ", " la ", " les ", " des ", " et ", " une ", " pour "))
        en = sum(low.count(w) for w in (" and ", " of ", " to ", " in ", " for ", " with "))
        letters = sum(c.isalpha() for c in joined)
        report["search_check"].append({"i": i, "pdfium_chars": r["scroll"]["text_chars"], "pypdf_chars": len(joined),
                                       "letters": letters, "the_substring": the_sub, "the_word": the_word,
                                       "fr_hint": fr, "en_hint": en, "pages": r["pages"]})
        flag = "SUSPECT" if the_sub >= 3 else "ok"
        print(f"  #{i}: pdfium chars {r['scroll']['text_chars']}, pypdf chars {len(joined)}, letters {letters}, "
              f"'the' substrings {the_sub} (words {the_word}), fr {fr} en {en} -> {flag}", flush=True)

    print("[3/3] blank pages with text", flush=True)
    for r in results.values():
        pages = (r.get("scroll") or {}).get("blank_with_text") or []
        if not pages:
            continue
        i = r["i"]
        path = full_path(args.root, files[i])
        try:
            texts = pypdf_text(path, pages)
        except Exception as e:
            texts = {p: f"\0error {type(e).__name__}" for p in pages}
        for p in pages:
            t = texts.get(p, "")
            ink = gs_ink(path, p)
            entry = {"i": i, "page": p + 1, "pypdf_chars": len(t), "pypdf_nonspace": len(t.strip()),
                     "gs_ink_fraction": ink}
            report["blank_check"].append(entry)
            verdict = "GS-HAS-INK" if (ink or 0) > 0.001 else ("gs-blank-too" if ink is not None else "gs-failed")
            print(f"  #{i} p{p + 1}: pypdf text {len(t)} chars ({len(t.strip())} non-space), gs ink {ink if ink is None else round(ink, 4)} -> {verdict}", flush=True)

    print("\nSummary:")
    print(f"  page-count mismatches: {len(report['page_count_mismatch'])} {report['page_count_mismatch'][:10]}")
    print(f"  PDFium refused, pypdf opened: {len(report['pdfium_refused_pypdf_opened'])} {report['pdfium_refused_pypdf_opened'][:10]}")
    print(f"  password files that open with an empty password: {len(report['password_but_empty_ok'])} {report['password_but_empty_ok']}")
    print(f"  pypdf failed where PDFium opened: {report['pypdf_failed_pdfium_ok']}")
    print(f"  both failed: {len(report['both_failed'])}")
    print(f"  search suspects (pypdf finds 'the' 3+ times): {sum(1 for s in report['search_check'] if s.get('the_substring', 0) >= 3)} of {len(report['search_check'])}")
    print(f"  blank pages where Ghostscript draws ink: {sum(1 for b in report['blank_check'] if (b['gs_ink_fraction'] or 0) > 0.001)} of {len(report['blank_check'])}")
    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            json.dump(report, f, indent=1)
        print(f"  wrote {args.out}")


if __name__ == "__main__":
    main()

// trigger structural scan
