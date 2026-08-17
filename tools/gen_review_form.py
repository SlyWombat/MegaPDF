#!/usr/bin/env python3
"""Generate the blank test form handed to human testers and to App Review.

`gen_test_fixtures.gen_demo()` produces the *filled* agreement used for store
screenshots (its marks and signature are already stamped in), so it is useless
as something to fill in. This is the same document, blank, plus two real
AcroForm checkbox widgets so a tester exercises both checkbox paths the iOS
app supports:

  * three stroked 13x13pt squares  -> the drawn-checkbox heuristic
    (stroked-not-filled, 6-24pt, <=25% squareness; SDD 6.2)
  * two /Btn checkbox widgets      -> PDFium's form machinery (FORM_OnLButton*)

"insurance" appears four times so the search arrows have matches to step
through, and the signature rule leaves clear space for a placed signature.

Deliberately no text form fields and nothing to type into: iOS has no
text-editing path (that is the Windows app), and a tester filming a review
video must not be pointed at a feature that will not respond.

Usage: python3 tools/gen_review_form.py [outfile]
       (default: docs/review/MegaPDF-Test-Form.pdf)
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gen_test_fixtures import build, stream  # noqa: E402


def gen_review_form():
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]

    helv = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    bold = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>")

    def text(font, size, x, y, s):
        return b"BT /%s %d Tf %d %d Td (%s) Tj ET" % (font, size, x, y, s)

    def square(x, y, size=13):
        return b"1 w 0.13 0.13 0.13 RG %d %d %d %d re S" % (x, y, size, size)

    body = [
        text(b"F2", 22, 72, 716, b"Equipment Rental Agreement"),
        text(b"F1", 10, 72, 696, b"Sunrise Tool Rental - customer copy"),

        text(b"F1", 11, 72, 664, b"This agreement is made between Sunrise Tool Rental and the customer named"),
        text(b"F1", 11, 72, 648, b"below. It covers the rental equipment, the delivery options, and the insurance"),
        text(b"F1", 11, 72, 632, b"terms set out in sections 1 to 4. Insurance is optional and may be declined."),

        text(b"F2", 13, 72, 596, b"Options"),
        square(72, 564), text(b"F1", 12, 94, 567, b"Include delivery and pickup"),
        square(72, 538), text(b"F1", 12, 94, 541, b"Damage insurance accepted"),
        square(72, 512), text(b"F1", 12, 94, 515, b"Extended weekend rate"),

        text(b"F2", 13, 72, 470, b"Confirmations"),
        text(b"F1", 12, 94, 441, b"I have read the insurance terms"),
        text(b"F1", 12, 94, 415, b"I agree to the rental period shown above"),

        text(b"F2", 13, 72, 372, b"Customer signature"),
        b"0.6 w 0.4 0.4 0.4 RG 72 300 m 320 300 l S",
        text(b"F1", 9, 72, 288, b"Sign above the line"),
    ]
    content = add(stream(b"", b"\n".join(body) + b"\n"))

    # Widget appearances: an empty box and a box with an X, 15x15.
    def widget_ap(checked):
        ops = b"0.13 0.13 0.13 RG 1 w 0.5 0.5 14 14 re S"
        if checked:
            ops += b" 1.6 w 3 3 m 12 12 l S 3 12 m 12 3 l S"
        return stream(b"/Type /XObject /Subtype /Form /BBox [0 0 15 15]", ops + b"\n")

    yes1, off1 = add(widget_ap(True)), add(widget_ap(False))
    yes2, off2 = add(widget_ap(True)), add(widget_ap(False))

    pages_num = len(objs) + 4
    w1, w2 = len(objs) + 2, len(objs) + 3
    page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
               b"/Resources << /Font << /F1 %d 0 R /F2 %d 0 R >> >> /Contents %d 0 R "
               b"/Annots [%d 0 R %d 0 R] >>"
               % (pages_num, helv, bold, content, w1, w2))
    a = add(b"<< /Type /Annot /Subtype /Widget /FT /Btn /T (read_terms) /V /Off /AS /Off "
            b"/Rect [72 438 87 453] /F 4 /P %d 0 R "
            b"/AP << /N << /Yes %d 0 R /Off %d 0 R >> >> >>" % (page, yes1, off1))
    b = add(b"<< /Type /Annot /Subtype /Widget /FT /Btn /T (agree_period) /V /Off /AS /Off "
            b"/Rect [72 412 87 427] /F 4 /P %d 0 R "
            b"/AP << /N << /Yes %d 0 R /Off %d 0 R >> >> >>" % (page, yes2, off2))
    assert (a, b) == (w1, w2)
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    assert pages == pages_num
    add(b"<< /Type /Catalog /Pages %d 0 R /AcroForm << /Fields [%d 0 R %d 0 R] >> >>"
        % (pages, w1, w2))
    return build(objs)


if __name__ == "__main__":
    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        repo, "docs", "review", "MegaPDF-Test-Form.pdf")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "wb") as f:
        f.write(gen_review_form())
    print(out)
