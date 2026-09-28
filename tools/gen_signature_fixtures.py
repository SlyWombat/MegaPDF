#!/usr/bin/env python3
"""Generate #481's digital-signature fixtures (core tests, MEGAPDF_DOC_SIGNED /
MEGAPDF_DOC_SIGNED_CERTIFICATION).

Usage: python3 tools/gen_signature_fixtures.py <outdir>
Writes:
  signed-approval.pdf   - one AcroForm field, /FT /Sig, whose /V is a /Sig dictionary with
                           no /Reference entry: an ordinary ("approval") signature. PDFium's
                           FPDF_GetSignatureCount() answers 1; FPDFSignatureObj_GetDocMDPPermission()
                           fails (0) because there is no /DocMDP transform.
                           megapdf_document_flags() must set MEGAPDF_DOC_SIGNED and must NOT
                           set MEGAPDF_DOC_SIGNED_CERTIFICATION.

  signed-certified.pdf  - the same shape, but the /Sig dictionary also carries a /Reference
                           entry whose /TransformMethod is /DocMDP with /TransformParams /P 1
                           ("no changes allowed"), and the Catalog's /Perms /DocMDP points at
                           it -- a certification signature, the shape every one of #476's 33
                           genuinely-signed GPO documents turned out to carry (measured for
                           #481: 33/33 certification, all permission 1).
                           megapdf_document_flags() must set both MEGAPDF_DOC_SIGNED and
                           MEGAPDF_DOC_SIGNED_CERTIFICATION.

An unsigned document is already covered by fixture.pdf and forms.pdf
(tools/gen_test_fixtures.py) -- not duplicated here.

The /Contents and /ByteRange values are placeholders -- PDFium's signature-table read
(FPDF_GetSignatureCount, FPDF_GetSignatureObject, FPDFSignatureObj_GetDocMDPPermission) is a
structural read of the /Sig dictionary, not a cryptographic verification, so these fixtures
need not carry a real digest or a valid /ByteRange the way a genuinely signed document does
(confirmed by hand against a real one of #476's corpus documents before writing this).

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


def _signature_document(certification):
    objs = []
    add = lambda b: (objs.append(b), len(objs))[1]

    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    content = add(stream(b"", b"BT /F1 12 Tf 72 700 Td (signed) Tj ET\n"))

    # Not a real digest or byte range: FPDFSignatureObj_GetDocMDPPermission and
    # FPDF_GetSignatureCount only read the /Sig dictionary's own structure (see the module
    # docstring) -- verified against one of #476's real corpus documents before relying on it.
    placeholder_contents = b"00" * 128

    sig_extra = b""
    if certification:
        # Permission 1 ("no changes allowed"): the shape all 33 of #476's real corpus
        # documents carry.
        sig_extra = (b" /Reference [ << /Type /SigRef /TransformMethod /DocMDP "
                     b"/DigestMethod /MD5 /TransformParams << /Type /TransformParams "
                     b"/P 1 /V /1.2 >> >> ]")
    sig = add(b"<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached "
              b"/ByteRange [0 9 9 9] /Contents <%s> /M (D:20250101000000+00'00')%s >>"
              % (placeholder_contents, sig_extra))

    field = add(b"<< /FT /Sig /Type /Annot /Subtype /Widget /Rect [0 0 0 0] /F 132 "
                b"/T (Signature1) /V %d 0 R >>" % sig)
    page = add(b"<< /Type /Page /Parent PAGES_REF /MediaBox [0 0 612 792] "
               b"/Resources << /Font << /F1 %d 0 R >> >> /Contents %d 0 R /Annots [%d 0 R] >>"
               % (font, content, field))
    pages = add(b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    objs[page - 1] = objs[page - 1].replace(b"PAGES_REF", b"%d 0 R" % pages)   # now that Pages exists

    acroform = add(b"<< /Fields [%d 0 R] /SigFlags 3 >>" % field)
    catalog_extra = b" /Perms << /DocMDP %d 0 R >>" % sig if certification else b""
    add(b"<< /Type /Catalog /Pages %d 0 R /AcroForm %d 0 R%s >>" % (pages, acroform, catalog_extra))
    return build(objs)


def gen_signed_approval():
    return _signature_document(certification=False)


def gen_signed_certified():
    return _signature_document(certification=True)


def main():
    outdir = sys.argv[1]
    os.makedirs(outdir, exist_ok=True)
    for name, data in (
        ("signed-approval.pdf", gen_signed_approval()),
        ("signed-certified.pdf", gen_signed_certified()),
    ):
        path = os.path.join(outdir, name)
        with open(path, "wb") as f:
            f.write(data)
        print("wrote %s (%d bytes)" % (path, len(data)))


if __name__ == "__main__":
    main()
