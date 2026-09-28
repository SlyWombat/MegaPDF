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

    /// The document carries an existing digital signature (#476, #481 phase 1). Not a
    /// verdict on the signature's cryptographic validity — only that PDFium's
    /// `FPDF_GetSignatureCount()` is greater than zero.
    ///
    /// It exists because `PdfEngine.save(_:to:)` cannot preserve a signature: it re-serialises
    /// the whole file, so any `/ByteRange` a signature recorded no longer covers the saved
    /// file. Measured in #476 against 33 genuinely signed GPO documents: every one goes from
    /// "Signature is Valid" to "Digest Mismatch" after a save, whether or not anything was
    /// actually edited. Nothing refuses the save — `ViewerModel`/`ViewerView` use this bit to
    /// warn before it happens, at the point of Save (and of changing the document's
    /// password, which writes over the file the same way), not as a banner on open. Save a
    /// copy is unaffected — the signed original is untouched — and notes once, quietly, that
    /// the signature does not carry to the copy.
    static let signed = PdfDocumentFlags(rawValue: 1 << 1)

    /// At least one of the document's signatures is a certification signature carrying a
    /// `/DocMDP` transform, rather than an ordinary approval signature. Always accompanied by
    /// `.signed`; a separate bit because the wording differs, not because either implies the
    /// document is otherwise safe to overwrite.
    ///
    /// A certification signature can *forbid* modification outright (DocMDP permission 1,
    /// "no changes"), rather than merely being invalidated the way an ordinary signature is.
    /// Measured across #476's 33-document corpus: this is not the edge case it might sound
    /// like — 33 of 33 are certification signatures, every one permission 1. Wherever this
    /// bit is set, the wording says the document is certified closed to changes, not merely
    /// that a signature will stop verifying.
    static let signedCertification = PdfDocumentFlags(rawValue: 1 << 2)
}
