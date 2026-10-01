import Foundation

// Placing, moving and removing a whiteout (#3) — the phone counterpart of the desktop's
// `WhiteoutOperation` / `MoveWhiteoutOperation` (#535).
//
// A whiteout breaks the history's first rule — "operations address their target by id,
// never by index" (EditHistory.swift) — because a whiteout *has* no id. It is page
// content: a white filled path carrying the MegaPDFWhiteout mark, with nothing in the
// format to hang an id off. So these two operations carry the one thing an index-addressed
// operation must: `currentObjectIndex`, which is where the whiteout is **now**, updated by
// every apply and revert. The view model re-anchors its selection off it afterwards
// (`ViewerModel.reselectWhiteout`), the same job `commitStampRect` already does for a
// moved signature.
//
// Nothing here needs a new engine primitive. Adding is `megapdf_add_whiteout`, newly bound
// in PdfEngine+Whiteout.swift; removing and restoring are the generic
// `detachObject`/`restoreObject` pair the body-text editor already uses, so an undo puts
// the very bytes back rather than a lookalike.

/// Placing a whiteout, or taking one off — one type, because they are each other's inverse.
///
/// `objectIndex` is -1 for a placement, which does not know where the object will land until
/// the core has appended it; a removal is built with the index the selection was anchored on.
final class WhiteoutOperation: PdfEditOperation {
    let pageIndex: Int
    private let rect: PdfRect
    private let adding: Bool

    /// Where the whiteout is now. -1 while it is off the page.
    private(set) var currentObjectIndex: Int

    /// The object itself while it is off the page, so it goes back byte-identical.
    private var detached: PdfDetachedObject?

    init(pageIndex: Int, rect: PdfRect, objectIndex: Int = -1, adding: Bool) {
        self.pageIndex = pageIndex
        self.rect = rect
        self.adding = adding
        self.currentObjectIndex = objectIndex
    }

    /// The tool's own word (SDD §2.2), and the same one every other platform uses for it.
    var name: String { adding ? "whiteout" : "remove whiteout" }

    func apply(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        if adding { try await add(engine, document) } else { try await remove(engine, document) }
    }

    func revert(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        if adding { try await remove(engine, document) } else { try await add(engine, document) }
    }

    private func add(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        if let detached {
            // It has been on this page before: put the same object back where it was.
            try await engine.restoreObject(document, pageIndex: pageIndex, detached,
                                           objectIndex: currentObjectIndex)
            self.detached = nil
            return
        }
        currentObjectIndex = try await engine.addWhiteout(document, pageIndex: pageIndex,
                                                         bounds: rect)
    }

    private func remove(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        guard currentObjectIndex >= 0 else { throw PdfError.editFailed }
        detached = try await engine.detachObject(document, pageIndex: pageIndex,
                                                 objectIndex: currentObjectIndex)
    }
}

/// Moving or resizing a whiteout.
///
/// There is no "move this page object" primitive, and a whiteout is not an annotation whose
/// rect could be set — so this is the detach-then-append the Avalonia and Android legs both
/// settled on (#535, #565): take the rectangle that is there off the page, put a fresh one
/// at the new bounds. It is byte-for-byte the redraw-it-by-hand the old remove-only chrome
/// forced, in one gesture and one undo step instead of two.
///
/// The detached object is discarded rather than kept for the undo, which would be wrong
/// here: the undo appends the rectangle at its *old* bounds, and a whiteout is fully
/// described by its bounds — there is no appearance, no text and no id to preserve. Keeping
/// a handle would only mean two ways to put the same rectangle back.
final class MoveWhiteoutOperation: PdfEditOperation {
    let pageIndex: Int
    private let from: PdfRect
    private let to: PdfRect
    private(set) var currentObjectIndex: Int

    init(pageIndex: Int, objectIndex: Int, from: PdfRect, to: PdfRect) {
        self.pageIndex = pageIndex
        self.currentObjectIndex = objectIndex
        self.from = from
        self.to = to
    }

    var name: String { "move whiteout" }

    func apply(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await replace(engine, document, with: to)
    }

    func revert(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await replace(engine, document, with: from)
    }

    private func replace(_ engine: PdfEngine, _ document: PdfDocument,
                         with rect: PdfRect) async throws {
        guard currentObjectIndex >= 0 else { throw PdfError.editFailed }
        let detached = try await engine.detachObject(document, pageIndex: pageIndex,
                                                     objectIndex: currentObjectIndex)
        await engine.discardDetached(detached)
        currentObjectIndex = try await engine.addWhiteout(document, pageIndex: pageIndex,
                                                         bounds: rect)
    }
}
