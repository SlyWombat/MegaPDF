#!/usr/bin/env python3
"""tools/leakcheck-style checks on a file an *app* saved, from outside that app.

    tools/leakcheck/outside.py <saved.pdf> --gone "customer copy" [--kept "Sunrise Tool Rental"]
                         [--area L,B,R,T:page] [--json out.json]

tools/leakcheck/leakcheck.h is the reference; this runs the same searches with a
different toolchain (poppler and qpdf, never PDFium) so nothing here can agree with
the engine that did the removing just because it is the same engine.

  1. text extraction, per page                     pdftotext -layout
  2. every stream decompressed                     qpdf --qdf --decode-level=all
  3. the raw file as ascii / utf-16le / utf-16be / PDF hex digits, either case
  4. /Info, XMP, the outline and every annotation  qpdf --json
  5. the pixels inside a marked area               pdftoppm, one flat colour

Exit 0 when nothing carrying the canary survives.
"""
from __future__ import annotations
import argparse, json, os, re, subprocess, sys, tempfile


def forms(canary: str) -> list[tuple[str, bytes]]:
    raw = canary.encode("ascii", "replace")
    out = [("ascii", raw),
           ("utf-16le", b"".join(bytes([c, 0]) for c in raw)),
           ("utf-16be", b"".join(bytes([0, c]) for c in raw))]
    hexup = "".join(f"{c:02X}" for c in raw).encode()
    out.append(("hex-upper", hexup))
    out.append(("hex-lower", hexup.lower()))
    return out


def run(cmd: list[str]) -> tuple[int, bytes]:
    p = subprocess.run(cmd, capture_output=True)
    return p.returncode, p.stdout + p.stderr


def check(path: str, gone: list[str], kept: list[str], areas: list[str]) -> dict:
    findings: list[dict] = []
    result = {"file": os.path.abspath(path), "size": os.path.getsize(path),
              "canaries": gone, "must_keep": kept}

    # --- 1. text extraction, per page (poppler, not PDFium) -------------------
    rc, text = run(["pdftotext", "-layout", path, "-"])
    text_s = text.decode("utf-8", "replace")
    result["pdftotext_ok"] = rc == 0
    for c in gone:
        if c.lower() in text_s.lower():
            findings.append({"where": "pdftotext", "canary": c,
                             "detail": "the canary extracts as text"})
    result["kept_found"] = {k: (k.lower() in text_s.lower()) for k in kept}

    # --- 2. every stream decompressed ----------------------------------------
    with tempfile.NamedTemporaryFile(suffix=".qdf.pdf", delete=False) as t:
        qdf = t.name
    rc, err = run(["qpdf", "--qdf", "--decode-level=all", "--object-streams=disable",
                   path, qdf])
    result["qpdf_qdf_rc"] = rc
    if os.path.exists(qdf) and os.path.getsize(qdf):
        blob = open(qdf, "rb").read()
        for c in gone:
            for name, needle in forms(c):
                if needle in blob:
                    findings.append({"where": f"qpdf-qdf {name}", "canary": c,
                                     "detail": "the canary is in a decompressed stream"})
    os.unlink(qdf) if os.path.exists(qdf) else None

    # --- 3. the raw file, four shapes ----------------------------------------
    raw = open(path, "rb").read()
    for c in gone:
        for name, needle in forms(c):
            if needle in raw:
                findings.append({"where": f"raw {name}", "canary": c,
                                 "detail": "the canary is in the file's bytes"})

    # --- 4. /Info, XMP, outline, annotations ---------------------------------
    rc, js = run(["qpdf", "--json", "--json-stream-data=none", path])
    result["qpdf_json_rc"] = rc
    if rc == 0:
        blob = js
        for c in gone:
            if c.encode() in blob:
                findings.append({"where": "qpdf-json (objects/metadata/annots)", "canary": c,
                                 "detail": "the canary is in an object string, /Info, an "
                                           "outline entry or an annotation"})
    rc, meta = run(["pdfinfo", "-meta", path])
    for c in gone:
        if c.encode() in meta:
            findings.append({"where": "XMP/pdfinfo", "canary": c,
                             "detail": "the canary is in the metadata"})

    # --- 5. the pixels inside each marked area -------------------------------
    result["areas"] = []
    for spec in areas:
        try:
            box, page = spec.split(":")
            l, b, r, t = (float(x) for x in box.split(","))
            page = int(page)
        except ValueError:
            result["areas"].append({"spec": spec, "error": "unparsable"})
            continue
        with tempfile.TemporaryDirectory() as d:
            root = os.path.join(d, "p")
            rc, _ = run(["pdftoppm", "-r", "150", "-f", str(page), "-l", str(page),
                         "-png", path, root])
            pngs = sorted(f for f in os.listdir(d) if f.endswith(".png"))
            if rc != 0 or not pngs:
                result["areas"].append({"spec": spec, "error": "render failed"})
                continue
            from PIL import Image
            im = Image.open(os.path.join(d, pngs[0])).convert("RGB")
            scale = 150.0 / 72.0
            # PDF points, origin bottom-left -> pixels, origin top-left.
            px = (int(l * scale), int(im.height - t * scale),
                  int(r * scale), int(im.height - b * scale))
            px = (max(0, px[0]), max(0, px[1]), min(im.width, px[2]), min(im.height, px[3]))
            crop = im.crop(px)
            cols = crop.getcolors(maxcolors=1 << 20) or []
            cols.sort(reverse=True)
            total = sum(n for n, _ in cols)
            top_n, top_c = cols[0] if cols else (0, None)
            flat = total and top_n / total > 0.995
            result["areas"].append({"spec": spec, "pixels": total, "distinct": len(cols),
                                    "dominant": top_c, "share": round(top_n / total, 5) if total else 0,
                                    "flat": bool(flat)})
            if not flat:
                findings.append({"where": f"pixels {spec}", "canary": "(area)",
                                 "detail": f"the marked area is not one flat colour: "
                                           f"{len(cols)} distinct, top share "
                                           f"{top_n / total:.3f}"})

    result["findings"] = findings
    result["leaked"] = bool(findings)
    missing = [k for k, v in result["kept_found"].items() if not v]
    result["unmarked_text_lost"] = missing
    result["pass"] = not findings and not missing
    return result


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("pdf")
    ap.add_argument("--gone", action="append", default=[])
    ap.add_argument("--kept", action="append", default=[])
    ap.add_argument("--area", action="append", default=[])
    ap.add_argument("--json")
    a = ap.parse_args()
    r = check(a.pdf, a.gone, a.kept, a.area)
    print(json.dumps(r, indent=2))
    if a.json:
        with open(a.json, "w") as h:
            json.dump(r, h, indent=2)
    print("\nLEAKCHECK: " + ("PASS" if r["pass"] else "FAIL"), file=sys.stderr)
    return 0 if r["pass"] else 1


if __name__ == "__main__":
    sys.exit(main())
