import Foundation

// The page tools' half of the undo history (#174, core contract 10): rotate, delete, move,
// insert a blank page and import pages from another file, each one **a single undo step** and
// each one its own inverse or paired with one.
//
// They are `PdfEditOperation`s like every other edit, so Undo and Redo reach them through the
// same `EditHistory` and the same buttons — a page change is not a second kind of history.
// What they add is the two answers a page change owes the view model, which no other edit
// does:
//
//   * `shifts(reverted:)` — how the document was renumbered, which is what the app applies to
//     its own index-keyed state. Read *after* apply or revert: an import does not know how
//     many pages arrived until it has asked for them.
//   * `changedPages` — which pages need drawing again, in the numbering that holds afterwards.
//     A rotation renumbers nothing and changes everything about how its pages look, so the two
//     are genuinely separate questions.
//
// Nothing here can answer `MEGAPDF_ERR_LAYOUT`. The #118 layout guard judges an edit that
// makes PDFium **rewrite a page's content stream**; a rotation sets `/Rotate` and delete,
// move, insert and import move whole page objects. Contract 10 lists no `MEGAPDF_ERR_LAYOUT`
// among its statuses, so the "Change this page?" question (#139) is not on this path and no
// dialog is invented for a state that cannot happen. `PageToolsTests` asserts that rather than
// leaving it as a claim.

/// A reversible change to which pages a document has, their order, or the way up one is shown.
protocol PageStructureOperation: PdfEditOperation {
    /// How the pages were renumbered by the last apply (`reverted: false`) or revert
    /// (`reverted: true`), in the order it happened.
    func shifts(reverted: Bool) -> [PageShift]

    /// Pages whose rendered image is now wrong, numbered as the document is numbered now.
    var changedPages: [Int] { get }

    /// The deleted pages this operation is still holding for an undo, if any. Handed to the
    /// engine to free when the operation leaves the history for good (`EditHistory.onDropped`).
    var heldPages: [RemovedPage] { get }
}

extension PageStructureOperation {
    var changedPages: [Int] { [] }
    var heldPages: [RemovedPage] { [] }
}

/// Turns pages by quarter turns clockwise. Sets each page's `/Rotate` and rewrites no content,
/// so a rotation can lose nothing. Its own inverse with the turns negated, which is exactly
/// what contract 10 promises.
///
/// One operation for the whole selection, so "turn these six pages" is one press of Undo
/// rather than six.
final class RotatePagesOperation: PageStructureOperation {
    let pages: [Int]
    let quarterTurns: Int

    init(pages: [Int], quarterTurns: Int) {
        self.pages = Array(Set(pages)).sorted()
        self.quarterTurns = quarterTurns
    }

    var pageIndex: Int { pages.first ?? 0 }
    var name: String { quarterTurns > 0 ? "rotate right" : "rotate left" }

    /// Nothing is renumbered — which is why the app must not assume "no shifts" means
    /// "nothing to redraw". That is `changedPages`.
    func shifts(reverted: Bool) -> [PageShift] { [] }

    var changedPages: [Int] { pages }

    func apply(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await turn(engine, document, quarterTurns)
    }

    func revert(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await turn(engine, document, -quarterTurns)
    }

    private func turn(_ engine: PdfEngine, _ document: PdfDocument, _ turns: Int) async throws {
        for page in pages {
            try await engine.rotatePage(document, pageIndex: page, quarterTurns: turns)
        }
    }
}

/// Takes pages off the document, keeping each one alive so the undo puts back exactly what was
/// deleted — the page itself, not a description of it, which is what `megapdf_page_restore`
/// exists for.
///
/// Highest index first, so the indices still to delete do not move under the delete; and back
/// lowest first, so each page lands at the index it came from. A selection need not be
/// contiguous, which is why the shifts are a list and not one shift.
final class DeletePagesOperation: PageStructureOperation {
    /// Ascending: the indices the pages had, and the indices they go back to.
    let pages: [Int]

    private var removed: [RemovedPage] = []

    init(pages: [Int]) {
        self.pages = Array(Set(pages)).sorted()
    }

    var pageIndex: Int { pages.first ?? 0 }
    var name: String { pages.count == 1 ? "delete a page" : "delete pages" }

    func shifts(reverted: Bool) -> [PageShift] {
        reverted
            ? pages.map { .inserted(at: $0, count: 1) }            // ascending, as Revert restores
            : pages.sorted(by: >).map { .removed(at: $0) }          // descending, as Apply deletes
    }

    var heldPages: [RemovedPage] { removed }

    func apply(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        // A redo must not leak the pages the first apply took: whatever is still held here is
        // a page nothing can restore any more, because this operation is about to hold new ones.
        for page in removed { await engine.discardRemovedPage(page) }
        removed.removeAll()
        // Descending, so the next index to delete has not moved.
        for page in pages.sorted(by: >) {
            removed.append(try await engine.deletePage(document, pageIndex: page))
        }
        // Kept ascending, to match the order `revert` puts them back in.
        removed.reverse()
    }

    func revert(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        guard removed.count == pages.count else {
            // The history is holding fewer pages than it deleted, which cannot happen from the
            // UI and must not be papered over: #429 and #441 were both a history quietly
            // naming something the engine no longer had.
            throw PageToolError(.spentPage)
        }
        // Ascending, so each page lands at the index it was taken from: putting the lowest back
        // first re-opens the gap the next one goes into.
        for (i, page) in pages.enumerated() {
            try await engine.restorePage(document, removed[i], at: page)
        }
        removed.removeAll()
    }
}

/// Moves one page so it stands at another index: the drag in the grid, and the Move Earlier /
/// Move Later / Move to… that do the same without one. Its own inverse with the two indices
/// swapped. The page dictionary is untouched, so its fields, annotations, marks and everything
/// else travel with it.
final class MovePageOperation: PageStructureOperation {
    let from: Int
    let to: Int

    init(from: Int, to: Int) {
        self.from = from
        self.to = to
    }

    var pageIndex: Int { from }
    var name: String { "move a page" }

    func shifts(reverted: Bool) -> [PageShift] {
        reverted ? [.moved(from: to, to: from)] : [.moved(from: from, to: to)]
    }

    func apply(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await engine.movePage(document, from: from, to: to)
    }

    func revert(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await engine.movePage(document, from: to, to: from)
    }
}

/// Adds an empty page. Undone by deleting it again, and the page is **not** kept for a further
/// undo: an empty page can be made again from the same two numbers, so holding it would be
/// holding a page nothing needs.
final class InsertBlankPageOperation: PageStructureOperation {
    let at: Int
    let widthPoints: Double
    let heightPoints: Double

    init(at: Int, widthPoints: Double, heightPoints: Double) {
        self.at = at
        self.widthPoints = widthPoints
        self.heightPoints = heightPoints
    }

    var pageIndex: Int { at }
    var name: String { "insert a blank page" }

    func shifts(reverted: Bool) -> [PageShift] {
        reverted ? [.removed(at: at)] : [.inserted(at: at, count: 1)]
    }

    func apply(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await engine.insertBlankPage(document, at: at,
                                        widthPoints: widthPoints, heightPoints: heightPoints)
    }

    func revert(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await engine.deletePageForGood(document, pageIndex: at)
    }
}

/// Combine: pages of another file inserted into this one. Undone by taking exactly the pages
/// that arrived back off — the inverse contract 10 states, "delete(at) n times" — and the count
/// is known only once the import has run, which is why the shifts are read after apply rather
/// than built in the initialiser.
///
/// `path` is a copy of the picked file inside the app's own container, kept for as long as the
/// document is open: the other document stays open inside this one, a redo imports from it
/// again, and a URL the file picker lent the app is not readable that long.
final class ImportPagesOperation: PageStructureOperation {
    let path: String
    let password: String?
    let pages: [Int]?
    let insertAt: Int

    private(set) var imported = 0

    init(path: String, password: String? = nil, pages: [Int]? = nil, insertAt: Int) {
        self.path = path
        self.password = password
        self.pages = pages
        self.insertAt = insertAt
    }

    var pageIndex: Int { insertAt }
    var name: String { "insert pages from a file" }

    func shifts(reverted: Bool) -> [PageShift] {
        guard imported > 0 else { return [] }
        // Reverting, the same index `imported` times: each removal closes up behind itself, so
        // the next page to go is at `insertAt` again.
        return reverted
            ? Array(repeating: PageShift.removed(at: insertAt), count: imported)
            : [.inserted(at: insertAt, count: imported)]
    }

    func apply(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        imported = try await engine.importPages(document, from: path, password: password,
                                                pages: pages, insertAt: insertAt)
    }

    /// Only for `PageShiftTests`, which checks the inverse an import reports without an engine
    /// to run one on. Named so it cannot be mistaken for something production code should call.
    func setImportedForTesting(_ count: Int) { imported = count }

    func revert(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        // Highest first, so the next index has not moved; discarded rather than held, because a
        // redo imports them again from the file they came from.
        for i in stride(from: imported - 1, through: 0, by: -1) {
            try await engine.deletePageForGood(document, pageIndex: insertAt + i)
        }
        // `imported` is deliberately left standing: the view model reads the shifts after a
        // revert too, and a redo re-runs `apply`, which sets it again from the import that
        // actually happened.
    }
}
