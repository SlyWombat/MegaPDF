import Foundation

// Editing text that was already in the document (#113) — the phone counterpart of
// the desktop's LineEditOperation and DeleteLineOperation. The policy is the shared
// core's: which font draws the new text (#116), and an undo that puts the original
// runs back byte-identical (#117).

/// Retyping a visual line. The new text goes into the line's first run; the other
/// runs of the line are detached but kept, so undo restores the original
/// fragmentation, fonts and layout exactly.
final class BodyTextEditOperation: PdfEditOperation {
    let pageIndex: Int
    private let line: PdfTextLine
    private let newText: String
    private var firstOriginal: PdfDetachedObject?
    private var detached: [(run: PdfTextRun, object: PdfDetachedObject)] = []

    /// How the last apply landed: `.substituted` means the UI owes the user a notice.
    private(set) var lastOutcome: PdfTextEditOutcome?

    init(pageIndex: Int, line: PdfTextLine, newText: String) {
        self.pageIndex = pageIndex
        self.line = line
        self.newText = newText
    }

    var name: String { "edit text" }

    func apply(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        guard let first = line.runs.first else { throw PdfError.editFailed }
        // The edited first run is a new object at the same index, so the indices of
        // the runs detached below do not move because of it.
        let result = try await engine.setText(document, pageIndex: pageIndex,
                                              objectIndex: first.objectIndex, text: newText)
        firstOriginal = result.original
        lastOutcome = result.outcome
        // Highest object index first, so the earlier indices stay valid.
        detached = []
        for run in line.runs.dropFirst().sorted(by: { $0.objectIndex > $1.objectIndex }) {
            let object = try await engine.detachObject(document, pageIndex: pageIndex,
                                                       objectIndex: run.objectIndex)
            detached.append((run, object))
        }
    }

    func revert(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        guard let first = line.runs.first, let original = firstOriginal else { throw PdfError.editFailed }
        // Lowest index first, so every run lands exactly where it came from.
        for entry in detached.sorted(by: { $0.run.objectIndex < $1.run.objectIndex }) {
            try await engine.restoreObject(document, pageIndex: pageIndex, entry.object,
                                           objectIndex: entry.run.objectIndex)
        }
        detached = []
        try await engine.restoreOriginal(document, pageIndex: pageIndex, original,
                                         objectIndex: first.objectIndex)
        firstOriginal = nil
    }
}

/// Clearing a line in the editor removes it; undo brings every run back exactly.
final class BodyTextDeleteOperation: PdfEditOperation {
    let pageIndex: Int
    private let line: PdfTextLine
    private var detached: [(run: PdfTextRun, object: PdfDetachedObject)] = []

    init(pageIndex: Int, line: PdfTextLine) {
        self.pageIndex = pageIndex
        self.line = line
    }

    var name: String { "delete text" }

    func apply(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        detached = []
        for run in line.runs.sorted(by: { $0.objectIndex > $1.objectIndex }) {
            let object = try await engine.detachObject(document, pageIndex: pageIndex,
                                                       objectIndex: run.objectIndex)
            detached.append((run, object))
        }
    }

    func revert(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        for entry in detached.sorted(by: { $0.run.objectIndex < $1.run.objectIndex }) {
            try await engine.restoreObject(document, pageIndex: pageIndex, entry.object,
                                           objectIndex: entry.run.objectIndex)
        }
        detached = []
    }
}
