import Foundation

/// Facts about a document as a whole, from `megapdf_document_flags()` (#456, #457) — read
/// once right after open, the same way `PdfSecurity` and the page count are (contract 9's
/// structure needs an explicit load over a range; this does not). Grown by new bits as the
/// core grows new facts, never by a new parameter or a second call — the "frozen struct"
/// note on `megapdf_document_flags` in megapdf_core.h.
struct PdfDocumentFlags: OptionSet, Equatable {
    let rawValue: UInt32

    /// The document is dynamic XFA: its AcroForm needs Acrobat's XFA/JavaScript engine to
    /// render the real form, which PDFium — and so MegaPDF — does not implement, so the
    /// only page content there is is Adobe's own "please wait... install Adobe Reader"
    /// placeholder (#456: 40 of 57 real IRCC forms — visitor visa, study/work permit,
    /// citizenship — carry this shape). The document still opens, reports a plausible page
    /// count and draws a page, so nothing *looks* wrong; only filling it is unavailable
    /// (#458) — view, print, save, share, export and every page tool keep working exactly
    /// as before, because none of them depend on the field values XFA would have supplied.
    ///
    /// Never set for a hybrid-XFA document (an `/XFA` entry present, but the static content
    /// *is* the complete, real form — CRA's, Service Canada's and most of IRCC's own
    /// fillable forms), an ordinary AcroForm document, or one with no form at all.
    static let dynamicXFA = PdfDocumentFlags(rawValue: 1 << 0)
}
