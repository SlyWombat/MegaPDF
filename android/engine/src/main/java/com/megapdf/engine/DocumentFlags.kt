package com.megapdf.engine

/**
 * Facts about a document as a whole, from `megapdf_document_flags()` (#456/#457) — read once
 * right after open, the same way [PdfSecurity] and the page count are. Grown by new fields as
 * the core grows new bits, never by a new parameter or a second call (megapdf_core.h's own
 * "frozen struct" note on `megapdf_document_flags`).
 */
data class DocumentFlags(
    /**
     * The document is dynamic XFA: its AcroForm needs Acrobat's XFA/JavaScript engine to
     * render the real form, which PDFium — and so MegaPDF — does not implement, so the only
     * page content there is is Adobe's own "please wait, install Adobe Reader" placeholder
     * (#456: 40 of 57 real IRCC forms — visitor visa, study/work permit, citizenship — carry
     * this shape). The document still opens, reports a plausible page count and draws a page,
     * so nothing *looks* wrong; only filling it is unavailable (#458) — view, save, share,
     * export and every page tool keep working.
     *
     * Never set for a hybrid-XFA document (an `/XFA` entry present, but the static content
     * *is* the complete, real form — CRA's, Service Canada's and most of IRCC's own fillable
     * forms), an ordinary AcroForm document, or one with no form at all.
     */
    val isDynamicXfa: Boolean,

    /**
     * The document carries an existing digital signature (#476/#481): PDFium's
     * FPDF_GetSignatureCount() is greater than zero. Not a verdict on the signature's
     * cryptographic validity — only that saving here rewrites the whole file and so always
     * invalidates it (measured 33/33 on real GPO documents in #476), which is why the app
     * warns before overwriting the signed original rather than on open.
     */
    val isSigned: Boolean = false,

    /**
     * At least one of the document's signatures is a certification (`/DocMDP`) signature,
     * which can forbid modification outright rather than merely being invalidated by one.
     * Always accompanied by [isSigned]. Every one of #476's 33 real corpus documents set
     * this — the common case on real documents, not the edge case.
     */
    val isCertificationSigned: Boolean = false,
) {
    companion object {
        val NONE = DocumentFlags(isDynamicXfa = false)

        fun of(bits: Int): DocumentFlags = DocumentFlags(
            isDynamicXfa = (bits and PdfiumNative.DOC_DYNAMIC_XFA) != 0,
            isSigned = (bits and PdfiumNative.DOC_SIGNED) != 0,
            isCertificationSigned = (bits and PdfiumNative.DOC_SIGNED_CERTIFICATION) != 0,
        )
    }
}
