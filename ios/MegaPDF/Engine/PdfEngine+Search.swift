import CPdfium
import Foundation

// Text search (#26) — literal case-insensitive substring search, implemented
// once in the shared engine core (#105) and decoded here from its packed stream:
// per match, a rect count and then (left, bottom, right, top) per rect, in
// crop space. Desktop and Android decode the same stream.

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
        return try withCorePage(document, index: pageIndex) { page in
            // The core wants NUL-terminated UTF-16.
            let wide = Array(term.utf16) + [0]
            let packed: [Double] = wide.withUnsafeBufferPointer { t in
                let total = megapdf_search_page(page, t.baseAddress, nil, 0)
                guard total > 0 else { return [] }
                var out = [Double](repeating: 0, count: total)
                let filled = out.withUnsafeMutableBufferPointer {
                    megapdf_search_page(page, t.baseAddress, $0.baseAddress, total)
                }
                return Array(out.prefix(min(filled, total)))
            }

            var result: [PdfSearchMatch] = []
            var i = 0
            while i < packed.count {
                let rectCount = Int(packed[i]); i += 1
                var rects: [PdfRect] = []
                var n = 0
                while n < rectCount, i + 4 <= packed.count {
                    rects.append(PdfRect(left: packed[i], bottom: packed[i + 1],
                                         right: packed[i + 2], top: packed[i + 3]))
                    i += 4; n += 1
                }
                if !rects.isEmpty { result.append(PdfSearchMatch(rects: rects)) }
            }
            return result
        }
    }
}
