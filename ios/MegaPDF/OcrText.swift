import Foundation

// Recognised text for the pages of a scan (#676), held in memory for as long as the document is
// open and answering Find with it. Nothing here is written to the document or to disk: this is a
// transient cache, not a text layer (that is #175).
//
// The types are plain values and the matcher is a pure function, so how a hit is found and where
// its rectangle falls are unit-tested without Vision. Vision itself is in `PdfEngine+Ocr.swift`.

/// One recognised word: where it stands in its line's text (character offsets, so a
/// grapheme is one unit whatever it is made of) and where it sits on the page in crop space.
struct OcrWord: Equatable {
    let range: Range<Int>
    let rect: PdfRect
}

/// One recognised line of text, with the words that make it up.
struct OcrLine: Equatable {
    let text: String
    let words: [OcrWord]
}

/// Everything the recogniser read on one page, in the order it read it.
struct OcrPage: Equatable {
    let lines: [OcrLine]

    /// Case-insensitive literal matches of `term`, in reading order -- the same contract as
    /// `PdfEngine.search`, so the caller merges the two without caring which produced a hit.
    ///
    /// A match is within one recognised line (the engine's own search can wrap across lines;
    /// this does not, because recognised lines are not guaranteed to be one paragraph's
    /// consecutive lines). Its rectangle is the union of the **words** it touches: a highlight
    /// that is a word too wide is honest about what the recogniser told us, where one that
    /// interpolates inside a word would claim a precision nobody measured.
    func matches(of term: String) -> [PdfSearchMatch] {
        guard !term.isEmpty else { return [] }
        var found: [PdfSearchMatch] = []
        for line in lines {
            var from = line.text.startIndex
            while from < line.text.endIndex,
                  let hit = line.text.range(of: term, options: .caseInsensitive,
                                            range: from..<line.text.endIndex) {
                let lo = line.text.distance(from: line.text.startIndex, to: hit.lowerBound)
                let hi = line.text.distance(from: line.text.startIndex, to: hit.upperBound)
                let touched = line.words.filter { $0.range.lowerBound < hi && $0.range.upperBound > lo }
                if let first = touched.first {
                    let rect = touched.dropFirst().reduce(first.rect) { $0.union($1.rect) }
                    found.append(PdfSearchMatch(rects: [rect]))
                }
                // Past the match, not past one character: "aa" in "aaaa" is two hits, as in the engine.
                from = hit.upperBound > hit.lowerBound ? hit.upperBound
                    : line.text.index(after: hit.lowerBound)
            }
        }
        return found
    }
}

extension PdfRect {
    func union(_ other: PdfRect) -> PdfRect {
        PdfRect(left: min(left, other.left), bottom: min(bottom, other.bottom),
                right: max(right, other.right), top: max(top, other.top))
    }
}

/// The recognised pages of one open document, by page index.
///
/// **A missing entry means "not read yet", never "read and empty"**: a page that was read and
/// had no words is stored as an empty `OcrPage`, so a scan is not redone for every search.
struct OcrCache: Equatable {
    private(set) var pages: [Int: OcrPage] = [:]
    /// Pages found to carry a text layer, so a text document is not asked again on every keystroke.
    private(set) var textPages: Set<Int> = []

    var isEmpty: Bool { pages.isEmpty && textPages.isEmpty }

    func hasText(_ page: Int) -> Bool { textPages.contains(page) }
    mutating func noteText(_ page: Int) { textPages.insert(page) }

    subscript(page: Int) -> OcrPage? {
        get { pages[page] }
        set { pages[page] = newValue }
    }

    /// Carried across a page operation, exactly like the rendered images (#174): recognised text
    /// that stayed on the wrong page after a move would be a silent wrong answer.
    func shifted(by shifts: [PageShift]) -> OcrCache {
        var out = self
        out.pages = pages.shifted(by: shifts)
        out.textPages = textPages.shifted(by: shifts)
        return out
    }

    mutating func remove(page: Int) {
        pages.removeValue(forKey: page)
        textPages.remove(page)
    }

    mutating func removeAll() {
        pages.removeAll()
        textPages.removeAll()
    }
}
