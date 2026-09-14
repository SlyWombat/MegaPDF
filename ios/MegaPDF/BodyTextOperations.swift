import Foundation

// Editing text that was already in the document (#113) — the phone counterpart of
// the desktop's LineEditOperation and DeleteLineOperation. The policy is the shared
// core's: which font draws the new text (#116), and an undo that puts the original
// runs back byte-identical (#117).

/// Retyping a visual line. The new text goes into the line's first run; the other
/// runs of the line leave the page but are kept, and so are the hidden copies a
/// producer drew under any run for fake bold, an outline or a shadow (#136), so undo
/// restores the original fragmentation, fonts and layout exactly.
final class BodyTextEditOperation: PdfEditOperation {
    let pageIndex: Int
    private let line: PdfTextLine
    private let newText: String
    private var originals: PdfDetachedObject?

    /// How the last apply landed: `.substituted` means the UI owes the user a notice.
    private(set) var lastOutcome: PdfTextEditOutcome?

    init(pageIndex: Int, line: PdfTextLine, newText: String) {
        self.pageIndex = pageIndex
        self.line = line
        self.newText = newText
    }

    var name: String { "edit text" }

    func apply(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        guard !line.runs.isEmpty else { throw PdfError.editFailed }
        // One core call for the whole line, hidden copies included (#136): taking runs one
        // at a time moves the indices of those still to be taken.
        let result = try await engine.setLineText(document, pageIndex: pageIndex,
                                                  objectIndices: line.runs.map(\.objectIndex), text: newText)
        originals = result.original
        lastOutcome = result.outcome
    }

    func revert(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        guard let originals else { throw PdfError.editFailed }
        // Every object goes back at its own index, the edited run off first.
        try await engine.restoreDetached(document, pageIndex: pageIndex, originals)
        self.originals = nil
    }
}

/// Clearing a line in the editor removes it, hidden copies and all (#136); undo brings
/// every object back exactly.
final class BodyTextDeleteOperation: PdfEditOperation {
    let pageIndex: Int
    private let line: PdfTextLine
    private var detached: PdfDetachedObject?

    init(pageIndex: Int, line: PdfTextLine) {
        self.pageIndex = pageIndex
        self.line = line
    }

    var name: String { "delete text" }

    func apply(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        detached = try await engine.detachTextRuns(document, pageIndex: pageIndex,
                                                   objectIndices: line.runs.map(\.objectIndex))
    }

    func revert(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        guard let detached else { throw PdfError.editFailed }
        try await engine.restoreDetached(document, pageIndex: pageIndex, detached)
        self.detached = nil
    }
}
