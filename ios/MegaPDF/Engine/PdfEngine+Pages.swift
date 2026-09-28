import CPdfium
import Foundation

// Page extract (#469): the C ABI call directly. No production UI wraps
// megapdf_pages_extract/megapdf_pages_import on iOS yet (that's the page-tools work
// tracked separately, #174/2.2); this exists so PagesFieldHierarchyTests can prove
// MEGAPDF_PDFIUM_PATCHES actually reaches megapdf_core.cpp on this platform, the way
// core/tests/core_tests.cpp already proves it for the desktop and Android builds.

extension PdfEngine {

    /// Writes `indices` (nil/empty means every page) as a new PDF at `url`, exactly as
    /// `megapdf_pages_extract` does (core/megapdf_core.h) -- including MEGAPDF_ERR_FIELDS
    /// for a page whose form fields sit in a /Parent hierarchy this build's PDFium cannot
    /// carry across a page copy. Returns the raw core status code (MEGAPDF_OK and friends,
    /// imported as plain `Int`) rather than throwing `PdfError`, since that refusal is
    /// exactly what the test needs to see.
    func extractPages(_ document: PdfDocument, indices: [Int]? = nil, to url: URL) -> Int {
        guard !document.isDestroyed else { return Int(MEGAPDF_ERR_ARGUMENT) }
        return url.withUnsafeFileSystemRepresentation { path in
            guard let path else { return Int(MEGAPDF_ERR_ARGUMENT) }
            guard let indices, !indices.isEmpty else {
                return Int(megapdf_pages_extract(document.core, nil, 0, path, nil))
            }
            let pages = indices.map { Int32($0) }
            return pages.withUnsafeBufferPointer {
                Int(megapdf_pages_extract(document.core, $0.baseAddress, $0.count, path, nil))
            }
        }
    }

    /// The fully qualified name of every form field on the page (`MEGAPDF_FIELD_NAME`,
    /// core/megapdf_core.h contract 3) -- the minimal read `PagesFieldHierarchyTests` needs
    /// to prove an extracted hierarchical field kept its name, not a general-purpose API.
    func fieldNames(_ document: PdfDocument, pageIndex: Int) throws -> [String] {
        try withCorePage(document, index: pageIndex) { page in
            guard let fields = megapdf_form_fields_load(page) else { return [] }
            defer { megapdf_form_fields_free(fields) }
            var names: [String] = []
            for i in 0..<megapdf_form_field_count(fields) {
                var buffer = [UInt16](repeating: 0, count: 256)
                let count = buffer.withUnsafeMutableBufferPointer {
                    megapdf_form_field_string(fields, i, MEGAPDF_FIELD_NAME, $0.baseAddress, $0.count)
                }
                names.append(String(decoding: buffer.prefix(count), as: UTF16.self))
            }
            return names
        }
    }
}
