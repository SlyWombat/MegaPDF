#!/usr/bin/env python3
"""#453/#444: a four-page PDF whose pages carry /Rotate 0, 90, 180 and 270, each drawing the
same four words, so contract 9's word building is exercised at every page rotation.

    tools/gen_rotated_page_fixture.py tests/MegaPDF.Core.Tests/Fixtures/structure/rotated-pages.pdf

Built on tools/gen_structure_fixtures.py's FontKit/Doc, so this fixture embeds the same
DejaVu Sans subset every other contract 9 fixture does. That is load-bearing, not tidiness:
a golden that compares block bounds byte-for-byte can only do so if the glyph metrics are the
same on ubuntu, macos and windows, and PDFium substitutes a different system face per platform
for a non-embedded base-14 font (see core_tests.cpp's kSameFontsAsExpectation comment). The
first draft of this fixture used unembedded Helvetica and would have gone red on the macOS and
Windows legs for exactly that reason.

fontTools is not on the CI runners, so — like its parent script — this one is not part of
core-tests.yml's fixture-generation step and its *output* is committed. Re-running it rewrites
the fixture (fontTools stamps each subset), which moves the golden; only do that when the
fixture is meant to change.

Why this fixture exists
-----------------------
#444 was diagnosed and fixed through a *vertical composite font* (Identity-V), and
`vertical-cid.pdf` locks that. But the fix's real-world trigger is this one: an ordinary
embedded TrueType font on a **rotated page**. #363's matrix inversion leaves a rotated page's
characters advancing along their local +y rather than +x, which is the exact condition the #444
fix taught BuildWords to recognise — so a plain rotated page hits the same code path a WMode-1
CJK column does, and rotated pages are common in real documents where Identity-V text is not.

That gap is what #453 ran into. Two qpdf page-rotation fixtures (`uo-4.pdf`,
`overlay-copy-annotations.pdf`) were reported as over-counting tokens 1.9-2.7x against PDFium;
the cause was the pre-#444 BuildWords shattering every rotated-page word into one-character
tokens. Before that fix this fixture's 90-degree page reads `A lp h aB r a v oC h a r lieD e
lt a` and measures 44 tokens against PDFium's 16 (F1 0.267); after it, 16/16. Nothing in the
repository covered that, so the fix could have regressed at its most common trigger with the
whole suite green.

Known-wrong output this fixture also pins
-----------------------------------------
The golden is a change detector, not an endorsement. Two defects remain visible in it, both
PRE-DATING the #444 fix and both filed as #472 -- do not "correct" the golden by hand:

  * /Rotate 180 and 270 emit the page's words in REVERSE order;
  * /Rotate 90 and 270 lose the inter-word gaps.

poppler's `pdftotext -layout` reads all four pages correctly, so the engine is the side that is
wrong. When #472 is fixed this golden SHOULD change, and that diff is the proof.
"""
import importlib.util
import os
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "gen_structure_fixtures", os.path.join(_HERE, "gen_structure_fixtures.py"))
_gsf = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_gsf)

TEXT = "Alpha Bravo Charlie Delta"
ROTATIONS = (0, 90, 180, 270)


def gen_rotated_pages(regular, bold):
    doc = _gsf.Doc(regular, bold)
    for rotate in ROTATIONS:
        # Identical content on every page: the only variable is /Rotate, so any difference in
        # the blocks is the rotation handling and nothing else.
        doc.add_page(_gsf.text_ops(b"F1", 18, 72, 700, TEXT),
                     page_extra=b" /Rotate %d" % rotate)
    return doc.finish()


def main():
    if len(sys.argv) != 2:
        sys.exit("usage: gen_rotated_page_fixture.py <out.pdf>")
    regular = _gsf.FontKit(_gsf.REGULAR_PATH)
    bold = _gsf.FontKit(_gsf.BOLD_PATH)
    data = gen_rotated_pages(regular, bold)
    with open(sys.argv[1], "wb") as fh:
        fh.write(data)
    print("wrote %s (%d bytes, %d pages at /Rotate %s)"
          % (sys.argv[1], len(data), len(ROTATIONS),
             "/".join(str(r) for r in ROTATIONS)))


if __name__ == "__main__":
    main()
