#!/usr/bin/env python3
"""Generate #457's dynamic/hybrid XFA fixtures (core tests, MEGAPDF_DOC_DYNAMIC_XFA).

Usage: python3 tools/gen_xfa_fixtures.py <outdir>
Writes:
  dynamic-xfa.pdf - AcroForm carries /XFA and /NeedsRendering true (PDFium's
                    FPDF_GetFormType() answers FORMTYPE_XFA_FULL); the one page's own
                    content stream is exactly one of Adobe's two stable "please wait,
                    install Adobe Reader" placeholder templates (#456's measurement
                    quotes both verbatim from real IRCC forms) -- nothing else. This is
                    the shape #456 found on 40 of 57 real IRCC forms (visitor visa,
                    study/work permit, citizenship): PDFium opens it, reports a
                    plausible page count, and draws a page, but there is no form here to
                    fill. megapdf_document_flags() must answer MEGAPDF_DOC_DYNAMIC_XFA.

  hybrid-xfa.pdf  - AcroForm also carries /XFA, but without /NeedsRendering (PDFium
                    answers FORMTYPE_XFA_FOREGROUND) and the page's own content stream
                    is real, substantial text -- the shape of CRA's and Service Canada's
                    fillable forms (and most of IRCC's), which extract correctly today
                    and must keep doing so untouched. megapdf_document_flags() must NOT
                    set MEGAPDF_DOC_DYNAMIC_XFA for this document.

An ordinary AcroForm document (no /XFA at all) is already covered by forms.pdf
(tools/gen_test_fixtures.py) -- not duplicated here.

Deterministic output; core_tests.cpp asserts against these by name.
"""
import os
import sys


def build(objects):
    out = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
    offsets = [0]
    for i, body in enumerate(objects, start=1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % i + body + b"\nendobj\n"
    xref_pos = len(out)
    out += b"xref\n0 %d\n" % (len(objects) + 1)
    out += b"0000000000 65535 f \n"
    for off in offsets[1:]:
        out += b"%010d 00000 n \n" % off
    catalog = next(i for i, b in enumerate(objects, start=1) if b.startswith(b"<< /Type /Catalog"))
    out += (b"trailer\n<< /Size %d /Root %d 0 R >>\nstartxref\n%d\n%%%%EOF\n"
            % (len(objects) + 1, catalog, xref_pos))
    return bytes(out)


def stream(dict_extra, content):
    return b"<< %s /Length %d >>\nstream\n%s\nendstream" % (dict_extra, len(content), content)


def pdf_string(text):
    """A PDF literal string: backslash and parens escaped. ASCII only, which is all these
    fixtures need (the real placeholder and the real hybrid content are both ASCII)."""
    escaped = text.replace("\\", r"\\").replace("(", r"\(").replace(")", r"\)")
    return escaped.encode("ascii")


# Adobe's own stable placeholder text, quoted verbatim in #456 from real IRCC forms
# (IMM 5257, IMM 0008, CIT 0002, ...): PDFium's non-XFA build never renders anything else
# for a dynamic-XFA document, because it does not run Acrobat's XFA engine.
DYNAMIC_PLACEHOLDER = (
    "The document you are trying to load requires Adobe Reader 8 or higher. "
    "You may not have the Adobe Reader installed or your viewing environment "
    "may not be properly configured to use Adobe Reader."
)


def _xfa_document(needs_rendering, page_text):
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]

    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    content_ops = b"BT /F1 12 Tf 72 700 Td (%s) Tj ET\n" % pdf_string(page_text)
    content = add(stream(b"", content_ops))
    # A dummy XFA packet: PDFium's non-XFA build never parses it (this repo builds plain
    # PDFium, not the XFA variant -- core/megapdf_core.cpp:225's "no JavaScript, no XFA").
    # FPDF_GetFormType() only reads the AcroForm dictionary's /XFA and /NeedsRendering keys,
    # so the packet's own bytes never matter to the test.
    xfa_packet = add(stream(b"", b"<xdp:xdp xmlns:xdp='http://ns.adobe.com/xdp/'></xdp:xdp>"))
    pages_num = len(objs) + 2
    page = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] "
               b"/Resources << /Font << /F1 %d 0 R >> >> /Contents %d 0 R >>"
               % (pages_num, font, content))
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    assert pages == pages_num
    acroform = add(b"<< /Fields [] /XFA [(template) %d 0 R] >>" % xfa_packet)
    # /NeedsRendering is the Catalog's own key (PDF 2.0 §12.7.8.2 / the XFA spec), not the
    # AcroForm dictionary's -- confirmed against a real dynamic-XFA IRCC form (#456's corpus,
    # IRCC_IMM0008): its Catalog carries `/NeedsRendering true`, its AcroForm does not. A real
    # hybrid form (CRA_T4) has no /NeedsRendering key anywhere.
    needs_rendering_entry = b" /NeedsRendering true" if needs_rendering else b""
    add(b"<< /Type /Catalog /Pages %d 0 R /AcroForm %d 0 R%s >>" % (pages, acroform, needs_rendering_entry))
    return build(objs)


def gen_dynamic_xfa():
    return _xfa_document(needs_rendering=True, page_text=DYNAMIC_PLACEHOLDER)


def gen_hybrid_xfa():
    return _xfa_document(
        needs_rendering=False,
        page_text="Statement of Remuneration Paid - real, static form content, not a placeholder.",
    )


def main():
    outdir = sys.argv[1]
    os.makedirs(outdir, exist_ok=True)
    for name, data in (
        ("dynamic-xfa.pdf", gen_dynamic_xfa()),
        ("hybrid-xfa.pdf", gen_hybrid_xfa()),
    ):
        path = os.path.join(outdir, name)
        with open(path, "wb") as f:
            f.write(data)
        print("wrote %s (%d bytes)" % (path, len(data)))


if __name__ == "__main__":
    main()
