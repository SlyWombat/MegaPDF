import Foundation

/// What an open of a document may do: the standard security handler's permission bits
/// (ISO 32000-2, Table 22), as the shared core reports them (#131).
struct PdfPermissions: OptionSet, Equatable {
    let rawValue: UInt32

    static let print = PdfPermissions(rawValue: 1 << 2)
    static let modify = PdfPermissions(rawValue: 1 << 3)
    static let copy = PdfPermissions(rawValue: 1 << 4)
    static let annotate = PdfPermissions(rawValue: 1 << 5)
    static let fillForms = PdfPermissions(rawValue: 1 << 8)
    static let accessibility = PdfPermissions(rawValue: 1 << 9)
    static let assemble = PdfPermissions(rawValue: 1 << 10)
    static let printHighQuality = PdfPermissions(rawValue: 1 << 11)
    static let all: PdfPermissions = [.print, .modify, .copy, .annotate, .fillForms, .accessibility,
                                      .assemble, .printHighQuality]
}

/// A document's security as this open sees it (#131). `hasFullAccess` is true for an
/// unprotected document, an open with the owner password, or a document that restricts
/// nothing: the opens that may change or remove its security.
struct PdfSecurity: Equatable {
    let isEncrypted: Bool
    let revision: Int
    let permissions: PdfPermissions
    let hasFullAccess: Bool

    static let unprotected = PdfSecurity(isEncrypted: false, revision: -1, permissions: .all, hasFullAccess: true)

    func allows(_ permission: PdfPermissions) -> Bool { permissions.contains(permission) }
}
