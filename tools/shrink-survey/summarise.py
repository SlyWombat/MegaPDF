#!/usr/bin/env python3
"""Summarise a shrink-survey run (#6): the tables the five measurement gates read.

    summarise.py <survey.jsonl> [--md] [--g5-samples N]

Counts and percentages only. A document id never appears in the output, so it is safe to paste into
TESTING.md and an issue. The one exception is --g5-samples, which prints (doc id, page, object) rows
for gate G5's by-hand check; that output stays on the machine and is not pasted.

Run it once per corpus: the private and public corpora are different populations and their numbers
are recorded separately (TESTING.md, "Which corpus gates what").

How an image is bucketed:
  kind  bilevel      1 bit per pixel, or CCITT or JBIG2 (decided from the filter, #696)
        grey         rendered, every pixel within 2 of grey, not bilevel
        colour       rendered, not grey, not bilevel
        unrendered   today's loop never renders it: small, or a render failure
  transparent        rendered and some pixel off the outermost 1-px ring has alpha < 255
Outcome columns are today's loop (the baseline): replaced / kept small / kept no-saving (the JPEG
would save under 10%) / failed. Images inside form XObjects are never reached by today's loop; they
are counted in their own column.
"""
import json
import statistics
import sys
from collections import defaultdict

MB = 1_000_000


def pct(part, whole):
    return "n/a" if whole == 0 else f"{100.0 * part / whole:.1f}%"


def median(xs):
    return statistics.median(xs) if xs else float("nan")


def p90(xs):
    if not xs:
        return float("nan")
    s = sorted(xs)
    k = max(0, min(len(s) - 1, int(round(0.9 * (len(s) - 1)))))
    return s[k]


def is_bilevel(im):
    return im["bpp"] == 1 or im["codec"] in ("CCITT", "JBIG2")


def kind_of(im):
    if is_bilevel(im):
        return "bilevel"
    if "is_grey" in im:
        return "grey" if im["is_grey"] else "colour"
    return "unrendered"


def is_transparent(im):
    return im.get("alpha_inner", 0) > 0


def table(rows, header, md, numeric_from=1):
    NUMERIC_FROM = numeric_from
    widths = [max(len(str(r[i])) for r in [header] + rows) for i in range(len(header))]
    out = []
    if md:
        out.append("| " + " | ".join(str(h) for h in header) + " |")
        out.append("|" + "|".join("---:" if i >= NUMERIC_FROM else "---" for i in range(len(header))) + "|")
        for r in rows:
            out.append("| " + " | ".join(str(c) for c in r) + " |")
    else:
        out.append("  ".join(str(h).ljust(widths[i]) for i, h in enumerate(header)))
        for r in rows:
            out.append("  ".join(str(c).ljust(widths[i]) for i, c in enumerate(r)))
    return "\n".join(out)


def main():
    args = sys.argv[1:]
    md = "--md" in args
    g5 = 0
    if "--g5-samples" in args:
        g5 = int(args[args.index("--g5-samples") + 1])
    paths = [a for a in args if not a.startswith("--") and not a.isdigit()]
    if len(paths) != 1:
        print(__doc__)
        sys.exit(2)

    images, docs = [], []
    with open(paths[0]) as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            row = json.loads(line)
            (images if row["type"] == "image" else docs).append(row)

    h = "### " if md else "== "
    outcomes = defaultdict(int)
    for d in docs:
        outcomes[d["outcome"]] += 1
    ok = [d for d in docs if d["outcome"] == "ok"]
    print(f"{h}Documents")
    print(f"documents: {len(docs)}; " + ", ".join(f"{k} {v}" for k, v in sorted(outcomes.items(), key=lambda kv: -kv[1])))
    print(f"images: {len(images)} in {len(set(i['doc'] for i in images))} documents "
          f"({sum(1 for i in images if i['in_form'])} inside form XObjects)")
    print()

    # ---- 1. buckets
    print(f"{h}1. By bucket (codec x kind x transparent), today's rules")
    buckets = defaultdict(lambda: defaultdict(int))
    for im in images:
        key = (im["codec"], kind_of(im), "yes" if is_transparent(im) else "no")
        b = buckets[key]
        b["images"] += 1
        b["stored"] += im["stored_bytes"]
        base = im["base"]
        b[base] += 1
        if base == "replaced":
            b["saved"] += im["stored_bytes"] - im["rgb_q75"]
    rows = []
    for key in sorted(buckets, key=lambda k: -buckets[k]["stored"]):
        b = buckets[key]
        rows.append([key[0], key[1], key[2], b["images"], f"{b['stored'] / MB:.1f}", b["replaced"], b["small"],
                     b["no_saving"], b["failed"], b["form"], f"{b['saved'] / MB:.1f}"])
    print(table(rows, ["codec", "kind", "transp", "images", "stored MB", "replaced", "small", "no-saving", "failed",
                       "in form", "saved MB"], md, 3))
    print()

    # ---- 2. grey candidates (G1, G2)
    print(f"{h}2. Grey candidates (G1, G2): rendered, grey, not bilevel, no transparency")
    grey = [i for i in images if "is_grey" in i and i["is_grey"] and not is_bilevel(i) and not is_transparent(i) and i["rgb_q75"] > 0]
    r80 = [i["rgb_q80"] / i["rgb_q75"] for i in grey]
    g75 = [i["grey_q75"] / i["rgb_q75"] for i in grey]
    print(f"grey candidates: {len(grey)} images, {sum(i['stored_bytes'] for i in grey) / MB:.1f} MB stored")
    if grey:
        print(f"G1 rgb_q80/rgb_q75   median {median(r80):.3f}   p90 {p90(r80):.3f}   "
              f"bytes-weighted {sum(i['rgb_q80'] for i in grey) / sum(i['rgb_q75'] for i in grey):.3f}")
        print(f"G2 grey_q75/rgb_q75  median {median(g75):.3f}   p90 {p90(g75):.3f}   "
              f"bytes-weighted {sum(i['grey_q75'] for i in grey) / sum(i['rgb_q75'] for i in grey):.3f}")
        replaced75 = sum(1 for i in grey if i["rgb_q75"] < 0.9 * i["stored_bytes"])
        replaced80 = sum(1 for i in grey if i["rgb_q80"] < 0.9 * i["stored_bytes"])
        print(f"replaced at q75 (today): {replaced75}; would be replaced at q80: {replaced80} "
              f"(the 10% rule flips {replaced75 - replaced80} images to kept)")
        print(f"(context) rgb_q85/rgb_q75 median {median([i['rgb_q85'] / i['rgb_q75'] for i in grey]):.3f}; "
              f"grey_q80/rgb_q80 median {median([i['grey_q80'] / i['rgb_q80'] for i in grey if i['rgb_q80'] > 0]):.3f}")
    colour = [i for i in images if "is_grey" in i and not i["is_grey"] and not is_bilevel(i) and not is_transparent(i) and i["rgb_q75"] > 0]
    print(f"(for scale) colour candidates: {len(colour)} images, {sum(i['stored_bytes'] for i in colour) / MB:.1f} MB stored")
    print()

    # ---- 3. bilevel (G3)
    print(f"{h}3. Bilevel (G3): stored bytes by codec, as a share of all image bytes")
    total = sum(i["stored_bytes"] for i in images)
    bil = defaultdict(lambda: [0, 0])
    for i in images:
        if is_bilevel(i):
            bil[i["codec"]][0] += 1
            bil[i["codec"]][1] += i["stored_bytes"]
    rows = [[c, n, f"{b / MB:.1f}", pct(b, total)] for c, (n, b) in sorted(bil.items(), key=lambda kv: -kv[1][1])]
    rows.append(["all bilevel", sum(v[0] for v in bil.values()), f"{sum(v[1] for v in bil.values()) / MB:.1f}",
                 pct(sum(v[1] for v in bil.values()), total)])
    print(table(rows, ["codec", "images", "stored MB", "share of all image bytes"], md))
    flate = sum(v[1] for c, v in bil.items() if c not in ("CCITT", "JBIG2"))
    print(f"G3 bilevel Flate/none/other bytes: {pct(flate, total)} of all image bytes (all image bytes {total / MB:.1f} MB)")
    print()

    # ---- 4. form XObjects (G4)
    print(f"{h}4. Form XObjects (G4): images today's loop cannot reach")
    inform = [i for i in images if i["in_form"]]
    fb = sum(i["stored_bytes"] for i in inform)
    print(f"{len(inform)} images ({pct(len(inform), len(images))} of images), {fb / MB:.1f} MB stored "
          f"({pct(fb, total)} of image bytes)")
    print()

    # ---- 5. G5 and transparency
    print(f"{h}5. Transparency (G5): rendered images with a pixel below alpha 255 off the outermost ring")
    rendered = [i for i in images if "alpha_inner" in i]
    tr = [i for i in rendered if is_transparent(i)]
    ring_only = [i for i in rendered if i["alpha_any"] > 0 and not is_transparent(i)]
    print(f"{len(tr)} of {len(rendered)} rendered images trip R5 ({pct(len(tr), len(rendered))}); "
          f"{len(ring_only)} more have alpha only on the 1-px ring (what the ring exclusion absorbs)")
    for ring, key in ((2, "alpha_inner2"), (4, "alpha_inner4")):
        n = sum(1 for i in rendered if i.get(key, 0) > 0)
        print(f"  with the excluded border widened to {ring} px: {n} images trip R5")
    print(f"R5 images by codec: " + (", ".join(f"{c} {n}" for c, n in sorted(
        ((c, sum(1 for i in tr if i['codec'] == c)) for c in set(i['codec'] for i in tr)), key=lambda kv: -kv[1])) or "none"))
    print("G5's by-hand check (no /SMask, /Mask or /ImageMask in the dictionary) is not in this file; use --g5-samples.")
    print()

    # ---- what the bilevel rule would give up
    print(f"{h}5b. What leaving bilevel alone gives up, against today's rules")
    saved_all = sum(i["stored_bytes"] - i["rgb_q75"] for i in images if i["base"] == "replaced")
    bil_rep = [i for i in images if i["base"] == "replaced" and is_bilevel(i)]
    saved_bil = sum(i["stored_bytes"] - i["rgb_q75"] for i in bil_rep)
    print(f"today's rules save {saved_all / MB:.1f} MB over {sum(1 for i in images if i['base'] == 'replaced')} images; "
          f"{len(bil_rep)} of them are bilevel, {saved_bil / MB:.2f} MB ({pct(saved_bil, saved_all)})")
    per_doc = defaultdict(lambda: [0, 0])   # replaced images, replaced bilevel images
    for i in images:
        if i["base"] == "replaced":
            per_doc[i["doc"]][0] += 1
            per_doc[i["doc"]][1] += 1 if is_bilevel(i) else 0
    only_bil = sum(1 for n, b in per_doc.values() if n == b)
    print(f"documents where today's rules replace something: {len(per_doc)}; where every replaced image is bilevel: {only_bil}")
    tr_bil = sum(1 for i in rendered if is_transparent(i) and is_bilevel(i))
    print(f"of the {len(tr)} R5 images, {tr_bil} are also bilevel (1-bit stencil masks render with alpha; R2 comes first)")
    print()

    # ---- 6. documents
    print(f"{h}6. Documents")
    shr = [d for d in ok if d["shrunk_bytes"] >= 0 and d["file_bytes"] > 0]
    sav = [(d["file_bytes"] - d["shrunk_bytes"]) / d["file_bytes"] for d in shr]
    changed = [d for d in shr if d["real_replaced"] > 0]
    sav_changed = [(d["file_bytes"] - d["shrunk_bytes"]) / d["file_bytes"] for d in changed]
    print(f"documents with a shrink result: {len(shr)}; with at least one image replaced: {len(changed)} "
          f"({pct(len(changed), len(shr))})")
    print(f"saving vs the original file: median {100 * median(sav):.2f}% over all, "
          f"{100 * median(sav_changed):.1f}% over the {len(changed)} that changed (p90 {100 * p90(sav_changed):.1f}%)")
    big = [d for d in shr if d["shrunk_bytes"] >= d["file_bytes"]]
    big_changed = [d for d in changed if d["shrunk_bytes"] >= d["file_bytes"]]
    print(f"documents where shrunk >= original: {len(big)} ({len(big_changed)} of them with an image replaced)")
    resaved = [d for d in shr if d["resaved_bytes"] >= 0]
    rewrite_grow = [d for d in resaved if d["resaved_bytes"] >= d["file_bytes"]]
    print(f"documents where a plain rewrite (no shrink) is already >= the original (D7): {len(rewrite_grow)} of {len(resaved)}")
    worse = [d for d in changed if d["resaved_bytes"] >= 0 and d["shrunk_bytes"] >= d["resaved_bytes"]]
    print(f"changed documents whose shrunk copy is no smaller than their own plain rewrite: {len(worse)}")
    emu = [d for d in ok if d["counts"][1] != d["real_replaced"]]
    print(f"cross-check: documents where this tool's replaced count differs from megapdf_shrink_images's: {len(emu)} of {len(ok)}")
    print(f"bytes: originals {sum(d['file_bytes'] for d in shr) / MB:.0f} MB -> shrunk {sum(d['shrunk_bytes'] for d in shr) / MB:.0f} MB")
    msh = [d["ms_shrink"] for d in ok if d.get("ms_shrink", -1) >= 0]
    if msh:
        print(f"megapdf_shrink_images alone: median {median(msh):.0f} ms, p90 {p90(msh):.0f} ms, max {max(msh)} ms")
    ms = [d["ms"] for d in ok]
    print(f"time per document: median {median(ms):.0f} ms, p90 {p90(ms):.0f} ms, max {max(ms) if ms else 0} ms; "
          f"peak RSS max {max((d['rss_mb'] for d in ok), default=0)} MB")
    trunc = sum(1 for d in ok if d["form_walk_truncated"])
    print(f"documents whose form walk hit the depth or visit budget: {trunc}")

    if g5:
        print()
        print("# G5 samples (doc, page, obj, ord, codec): stays on the machine")
        for i in tr[:g5]:
            print(f"{i['doc']}\t{i['page']}\t{i['obj']}\t{i['ord']}\t{i['codec']}")


if __name__ == "__main__":
    main()
