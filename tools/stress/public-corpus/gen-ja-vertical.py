#!/usr/bin/env python3
"""Generate vertical-writing (tb-rl) Japanese PDFs for the #471 part 2 bonus sample.

NOT part of manifest.tsv, and this is deliberate rather than an oversight: every other
row in this corpus is fetched by URL and checked against a pinned sha256, because the
byte content of a git-pinned commit or a static government form is stable. Wikipedia's
per-article PDF export is not a static file -- it is rendered on demand by the wiki's own
service, and this script's output is rendered a second time, locally, by LibreOffice.
Neither step is guaranteed byte-stable across a LibreOffice version, a font package
version, or (per NONLATIN_WIKI_TITLES's own comment) even two requests to the same wiki
export URL minutes apart. A sha256-pinned manifest row promises bytes that do not change;
this generator cannot make that promise, so it stays a recipe instead of a row.

What it produces IS reproducible in substance: the same pinned article titles (below),
the same source text (ja.wikipedia.org, CC BY-SA 4.0 / GFDL, same licence and the same
attribution obligations as the wiki-ja manifest rows -- see README.md), laid out with a
real tb-rl (top-to-bottom, right-to-left column) paragraph direction so the resulting PDF
carries genuine downward glyph-advance geometry -- the same shape #444's fix
(core/megapdf_structure.cpp's local-displacement check) was written to recognise, not a
rotated or hand-positioned imitation of it.

One caveat stated plainly: LibreOffice export here embeds each page's Han/Kana glyphs as
a simple TrueType subset, not a composite Type0/Identity-V CMap font the way a CJK
typesetting system with true vertical font metrics (or #444's original veraPDF CMap
fixtures) would. The page geometry is genuinely vertical; the font-level "WMode 1"
composite-CMap shape is not reproduced. Whether that distinction matters to a specific
finding is exactly the kind of thing to say plainly when filing it, not paper over.

Requires: libreoffice-writer (soffice on PATH) and a CJK font (fonts-noto-cjk here).
Network: fetches plain-text extracts from ja.wikipedia.org.

    tools/stress/public-corpus/gen-ja-vertical.py <out-dir> [--count N]
"""
import argparse
import html
import json
import os
import subprocess
import sys
import urllib.parse
import urllib.request

UA = "MegaPDF-corpus-builder/1.0 (+https://github.com/SlyWombat/MegaPDF)"

# Pinned 2026-09-28 on kdocker3 (MediaWiki list=random, ns=0) -- the exact titles this
# script's own first run used to produce the #471 part 2 vertical-Japanese finding. A
# title Wikipedia has since deleted or renamed is skipped (reported on stderr), same as
# NONLATIN_WIKI_TITLES's own stale-link handling.
TITLES = [
    "森下篤史", "SATURDAY ON FLEEK", "フィリップ・ド・ヴィリエ", "ホーリーブル", "FC-75",
    "ステファニー・ベッケルト", "佐倉知佳", "君だけを (西郷輝彦の曲)", "オトメノカサ",
    "リボンナポリン", "デニニオ・ムリンヘン", "月雲よる", "自動車運搬船", "ヨーハン・ヴァイヤー",
    "メアリ・エイケンヘッド", "石原喜久太郎", "新田 (津山市)", "周淮郡",
]

FODT_TEMPLATE = """<?xml version="1.0" encoding="UTF-8"?>
<office:document xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
  xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0"
  xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"
  xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0"
  xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0"
  office:version="1.2" office:mimetype="application/vnd.oasis.opendocument.text">
 <office:automatic-styles>
  <style:page-layout style:name="PMvert">
   <style:page-layout-properties fo:page-width="21.0cm" fo:page-height="29.7cm"
     fo:margin="1.5cm" style:writing-mode="tb-rl"/>
  </style:page-layout>
  <style:style style:name="PVert" style:family="paragraph">
   <style:paragraph-properties style:writing-mode="tb-rl" fo:line-height="150%"/>
   <style:text-properties style:font-name="Noto Sans CJK JP" fo:font-size="14pt"/>
  </style:style>
 </office:automatic-styles>
 <office:master-styles>
  <style:master-page style:name="Standard" style:page-layout-name="PMvert"/>
 </office:master-styles>
 <office:body>
  <office:text>
{paragraphs}
  </office:text>
 </office:body>
</office:document>
"""


def get(url, timeout=30):
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read()


def fetch_extract(title):
    url = ("https://ja.wikipedia.org/w/api.php?action=query&prop=extracts"
           "&explaintext=1&exsectionformat=plain&format=json&titles="
           + urllib.parse.quote(title))
    data = json.loads(get(url))
    for _, page in data["query"]["pages"].items():
        return page.get("extract", "")
    return ""


def build_fodt(text, out_path):
    paras = [p.strip() for p in text.split("\n") if p.strip()]
    body = "\n".join(
        f'   <text:p text:style-name="PVert">{html.escape(p)}</text:p>' for p in paras)
    with open(out_path, "w", encoding="utf-8") as fh:
        fh.write(FODT_TEMPLATE.format(paragraphs=body))


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                  formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("out_dir")
    ap.add_argument("--count", type=int, default=len(TITLES))
    args = ap.parse_args()

    os.makedirs(args.out_dir, exist_ok=True)
    built = 0
    for i, title in enumerate(TITLES[:args.count]):
        text = fetch_extract(title)
        if len(text) < 200:
            print(f"SKIP {title}: extract too short ({len(text)} chars)", file=sys.stderr)
            continue
        fodt = os.path.join(args.out_dir, f"doc-{i:02d}.fodt")
        build_fodt(text, fodt)
        subprocess.run(["soffice", "--headless", "--convert-to", "pdf",
                         "--outdir", args.out_dir, fodt], check=True,
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        os.remove(fodt)
        built += 1
        print(f"OK {title}: {len(text)} chars -> doc-{i:02d}.pdf", file=sys.stderr)
    print(f"built {built} of {args.count} requested", file=sys.stderr)


if __name__ == "__main__":
    main()
