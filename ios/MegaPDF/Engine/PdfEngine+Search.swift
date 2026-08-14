import CPdfium
import Foundation

// Text search (#26) — literal case-insensitive substring search via PDFium's
// FPDFText API, matching the desktop and Android implementations (same flags,
// same per-page rect semantics).

/// One search hit on a page: the bounding rects (PDF points, bottom-left
/// origin) covering the matched glyphs — usually one, more when the match
/// wraps across lines.
struct PdfSearchMatch: Equatable {
    let rects: [PdfRect]
}

extension PdfEngine {

    /// Case-insensitive literal matches of `term` on the page, in text order.
    /// An empty term matches nothing.
    func search(_ document: PdfDocument, pageIndex: Int, term: String)
        throws -> [PdfSearchMatch] {
        guard !term.isEmpty else { return [] }
        return try withPage(document, index: pageIndex) { page in
            guard let text = FPDFText_LoadPage(page) else { return [] }
            defer { FPDFText_ClosePage(text) }

            // FPDF_WIDESTRING is null-terminated UTF-16.
            let wide = Array(term.utf16) + [0]
            let handle = wide.withUnsafeBufferPointer {
                FPDFText_FindStart(text, $0.baseAddress, 0, 0)
            }
            guard let handle else { return [] }
            defer { FPDFText_FindClose(handle) }

            var result: [PdfSearchMatch] = []
            while FPDFText_FindNext(handle) != 0 {
                let start = FPDFText_GetSchResultIndex(handle)
                let count = FPDFText_GetSchCount(handle)
                var rects: [PdfRect] = []
                // CountRects returns -1 on failure; clamp so the range is valid.
                for i in 0..<max(FPDFText_CountRects(text, start, count), 0) {
                    var l = 0.0, t = 0.0, r = 0.0, b = 0.0
                    guard FPDFText_GetRect(text, i, &l, &t, &r, &b) != 0 else { continue }
                    rects.append(PdfRect(left: l, bottom: b, right: r, top: t))
                }
                if !rects.isEmpty { result.append(PdfSearchMatch(rects: rects)) }
            }
            return result
        }
    }
}
