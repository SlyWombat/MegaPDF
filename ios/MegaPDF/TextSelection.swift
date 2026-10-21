import Foundation

/// A selection of a page's text (#710): two carets into one page's snapshot, kept as an anchor
/// and a focus so a caller never has to order them.
///
/// **One page.** iOS shows the pages in a lazy scroll view of independent page views, and a
/// long-press recogniser belongs to the page it started on; carrying a drag across pages would
/// mean a coordinator over all of them. A selection stays on the page it began on, and the
/// contract's cross-page stitching (join with one `\n`) is not needed here.
///
/// Pure value logic, so what a handle drag does is tested without a view or a document.
struct PageTextSelection: Equatable {
    /// Which end of the highlight a handle is.
    enum Handle { case start, end }

    let pageIndex: Int
    private(set) var anchor: Int
    private(set) var focus: Int

    init(pageIndex: Int, anchor: Int, focus: Int) {
        self.pageIndex = pageIndex
        self.anchor = anchor
        self.focus = focus
    }

    init(pageIndex: Int, word: Range<Int>) {
        self.init(pageIndex: pageIndex, anchor: word.lowerBound, focus: word.upperBound)
    }

    /// The lower caret, whichever was pressed first.
    var start: Int { min(anchor, focus) }
    var end: Int { max(anchor, focus) }
    var isEmpty: Bool { anchor == focus }

    /// Moves `handle` to `caret`. The handle being dragged becomes the focus and the other one
    /// the anchor, so dragging the start handle past the end flips the selection instead of
    /// collapsing it. A caret outside 0 ... `length` is clamped.
    mutating func drag(_ handle: Handle, to caret: Int, length: Int) {
        let clamped = max(0, min(caret, length))
        switch handle {
        case .start: anchor = end; focus = clamped
        case .end: anchor = start; focus = clamped
        }
    }
}

/// Where a selection's two handles go and where the edit menu points, from the selection's
/// line rectangles. Crop space in, crop space out.
enum SelectionGeometry {
    /// The start handle sits on the top-left of the first line, the end handle on the
    /// bottom-right of the last: the pair every iOS text selection has.
    static func handlePoints(for rects: [PdfRect]) -> (start: PdfPoint, end: PdfPoint)? {
        guard let first = rects.first, let last = rects.last else { return nil }
        return (PdfPoint(x: first.left, y: first.top), PdfPoint(x: last.right, y: last.bottom))
    }

    /// The box around every line, for anchoring the edit menu.
    static func bounds(of rects: [PdfRect]) -> PdfRect? {
        guard var union = rects.first else { return nil }
        for r in rects.dropFirst() {
            union = PdfRect(left: min(union.left, r.left), bottom: min(union.bottom, r.bottom),
                            right: max(union.right, r.right), top: max(union.top, r.top))
        }
        return union
    }
}

struct PdfPoint: Equatable {
    var x: Double
    var y: Double
}
