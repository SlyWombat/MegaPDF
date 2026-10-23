import CoreGraphics
import CPdfium
import Foundation

// Text selection (#710) — the Swift face of core contract 11 (`megapdf_selectable_*`).
//
// The core reads a page once into an immutable snapshot of its characters in reading order;
// a selection is two carets (0 ... length) into it. Everything here is crop space, like every
// other contract: points, bottom-left origin, the render's own frame.
//
// Threading, from the contract: only the *load* takes the core's lock and reads PDFium, so
// `PdfEngine.loadSelectable` runs on the engine actor, off the main thread. Every query after
// it reads the snapshot only, takes no lock and costs microseconds, so the view calls them on
// the main thread on every pointer move.

/// One page's selectable text: a snapshot that outlives the page and the document.
final class PdfSelectableText: @unchecked Sendable {
    private let handle: OpaquePointer?

    fileprivate init(handle: OpaquePointer?) { self.handle = handle }

    deinit { megapdf_selectable_free(handle) }

    /// How many characters the page has to select; carets run 0 ... `length`. A scan or a blank
    /// page is 0.
    var length: Int { Int(megapdf_selectable_length(handle)) }

    /// A tolerance of "no limit": a page with any text always answers.
    static let anywhere = MEGAPDF_SELECT_ANYWHERE
    /// What a press uses to find text under a finger: a few points of slop around the line.
    static let pressTolerance = 6.0

    func caret(atX x: Double, y: Double, tolerance: Double = PdfSelectableText.anywhere) -> Int? {
        let caret = Int(megapdf_selectable_caret_at(handle, x, y, tolerance))
        return caret >= 0 ? caret : nil
    }

    func character(atX x: Double, y: Double, tolerance: Double = PdfSelectableText.anywhere) -> Int? {
        let index = Int(megapdf_selectable_char_at(handle, x, y, tolerance))
        return index >= 0 ? index : nil
    }

    /// The carets either side of the word at the point, or nil when there is none in tolerance.
    func word(atX x: Double, y: Double, tolerance: Double = PdfSelectableText.pressTolerance) -> Range<Int>? {
        var start: Int32 = 0, end: Int32 = 0
        guard megapdf_selectable_word_at(handle, x, y, tolerance, &start, &end) > 0, end > start else { return nil }
        return Int(start)..<Int(end)
    }

    /// The carets either side of the visual line at the point, or nil.
    func line(atX x: Double, y: Double, tolerance: Double = PdfSelectableText.pressTolerance) -> Range<Int>? {
        var start: Int32 = 0, end: Int32 = 0
        guard megapdf_selectable_line_at(handle, x, y, tolerance, &start, &end) > 0, end > start else { return nil }
        return Int(start)..<Int(end)
    }

    /// The carets either side of the word that holds character `index` (0 ..< `length`), the
    /// same word and the same punctuation rule a long press selects. The core finds a word by
    /// a point, so this asks about the middle of the character's own box.
    func word(containingCharacter index: Int) -> Range<Int>? {
        guard index >= 0, index < length, let box = rects(from: index, to: index + 1).first else { return nil }
        return word(atX: (box.left + box.right) / 2, y: (box.bottom + box.top) / 2)
    }

    /// The caret one word on from `caret` (the end of the word that starts there or holds it),
    /// or one word back (the start of the word before it, or of the word it is inside). Nil at
    /// the first or last word of the page, where there is nowhere to go. This is what a
    /// VoiceOver user's swipe up and down on a handle moves it by (#710).
    func caret(byWordFrom caret: Int, forward: Bool) -> Int? {
        if forward {
            guard caret >= 0, caret < length, let word = word(containingCharacter: caret) else { return nil }
            return word.upperBound
        }
        guard caret > 0, caret <= length, let word = word(containingCharacter: caret - 1) else { return nil }
        return word.lowerBound
    }

    /// The text between two carets, in either order, with `\n` between lines: what the
    /// pasteboard gets (iOS's own line ending, so no conversion). Plain text only.
    func text(from a: Int, to b: Int) -> String {
        let needed = megapdf_selectable_text(handle, Int32(a), Int32(b), nil, 0)
        guard needed > 0 else { return "" }
        var units = [UInt16](repeating: 0, count: needed)
        let filled = units.withUnsafeMutableBufferPointer {
            megapdf_selectable_text(handle, Int32(a), Int32(b), $0.baseAddress, $0.count)
        }
        return String(decoding: units.prefix(min(filled, needed)), as: UTF16.self)
    }

    /// One rectangle per line the selection touches, in reading order, crop space.
    func rects(from a: Int, to b: Int) -> [PdfRect] {
        let needed = megapdf_selectable_rects(handle, Int32(a), Int32(b), nil, 0)
        guard needed > 0 else { return [] }
        var buffer = [megapdf_rect](repeating: megapdf_rect(), count: needed)
        let filled = buffer.withUnsafeMutableBufferPointer {
            megapdf_selectable_rects(handle, Int32(a), Int32(b), $0.baseAddress, $0.count)
        }
        return buffer.prefix(min(filled, needed)).map {
            PdfRect(left: $0.left, bottom: $0.bottom, right: $0.right, top: $0.top)
        }
    }
}

extension PdfEngine {

    /// Reads page `pageIndex`'s text into a selection snapshot. Throws when the page cannot be
    /// loaded (a closed document); a scan loads, with length 0. Reload after any change to the
    /// page's content or rotation.
    func loadSelectable(_ document: PdfDocument, pageIndex: Int) throws -> PdfSelectableText {
        try withCorePage(document, index: pageIndex) { page in
            guard let snapshot = megapdf_selectable_load(page) else { throw PdfError.pageLoad(index: pageIndex) }
            return PdfSelectableText(handle: snapshot)
        }
    }
}
