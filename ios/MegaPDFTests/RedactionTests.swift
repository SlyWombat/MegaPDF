import XCTest
@testable import MegaPDF

/// Redaction on iOS (#173), against the shared fixtures.
///
/// The engine is the same C++ core every platform links, and the core's own tests
/// prove what it removes. What had no test at all was the Swift binding in
/// `PdfEngine+Redaction.swift` — the marshalling between `megapdf_redaction_*` and
/// the types the view model uses. A mistake there would not show up in a core test
/// and would not show up until somebody redacted something on a phone.
///
/// The property each of these checks is the one the feature exists for: a mark is
/// not content, and applying really removes.
final class RedactionTests: XCTestCase {

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: name, withExtension: "pdf") else {
            throw XCTSkip("fixture \(name).pdf missing from test bundle")
        }
        return try Data(contentsOf: url)
    }

    /// The line on the demo agreement that names the company, and a heading below it
    /// that must survive untouched.
    private let secret = "Sunrise"
    private let neighbour = "Options"

    private func page0Text(_ engine: PdfEngine, _ doc: PdfDocument) async throws -> String {
        try await engine.textLines(doc, pageIndex: 0).map(\.text).joined(separator: "\n")
    }

    func testMarkingChangesNothingAndIsNotWrittenToTheFile() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("demo"))
        defer { Task { await engine.close(doc) } }

        let lines = try await engine.textLines(doc, pageIndex: 0)
        let line = try XCTUnwrap(lines.first { $0.text.contains(secret) },
                                 "the demo agreement no longer names \(secret)")

        let made = try await engine.markTextForRedaction(doc, pageIndex: 0, rect: line.rect)
        XCTAssertFalse(made.isEmpty, "a drag across text should mark the text")
        var count = await engine.redactionMarkCount(doc)
        XCTAssertEqual(count, made.count)
        // And the ids it reports are the page's own, in the order it made them: that is
        // what lets Undo name what the gesture made (#329).
        let onPage = try await engine.redactionMarks(doc, pageIndex: 0)
        XCTAssertEqual(made, onPage.map(\.markId))

        // Nothing has been removed: the words are still there.
        let after = try await page0Text(engine, doc)
        XCTAssertTrue(after.contains(secret), "marking must not remove anything")

        // And a document saved with marks on it carries none — the failure this
        // feature exists to stop is a file that looks redacted and is not.
        let bytes = try await engine.save(doc)
        let reopened = try await engine.open(bytes)
        count = await engine.redactionMarkCount(reopened)
        XCTAssertEqual(count, 0, "marks must never be written to the file")
        await engine.close(reopened)
    }

    func testApplyingRemovesTheTextAndLeavesTheRest() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("demo"))

        let lines = try await engine.textLines(doc, pageIndex: 0)
        let line = try XCTUnwrap(lines.first { $0.text.contains(secret) })
        _ = try await engine.markTextForRedaction(doc, pageIndex: 0, rect: line.rect)

        let report = await engine.applyRedactions(doc)
        XCTAssertTrue(report.applied, "refusals: \(report.refusals)")
        XCTAssertGreaterThan(report.counts.characters, 0, "the summary should count what went")
        let leftOver = await engine.redactionMarkCount(doc)
        XCTAssertEqual(leftOver, 0, "applying drops the marks")

        // Read the saved bytes back through a second open, not through the document
        // that wrote them.
        let saved = try await engine.save(doc)
        await engine.close(doc)
        let after = try await engine.open(saved)
        let text = try await page0Text(engine, after)
        XCTAssertFalse(text.contains(secret), "the marked words are still in the text layer")
        XCTAssertTrue(text.contains(neighbour), "an unmarked neighbour was taken with them")
        await engine.close(after)

        // And not in the bytes either, however they are spelled.
        for (label, pattern) in [
            ("UTF-8", Data(secret.utf8)),
            ("UTF-16LE", Data(secret.unicodeScalars.flatMap { [UInt8($0.value & 0xFF), UInt8($0.value >> 8)] })),
            ("UTF-16BE", Data(secret.unicodeScalars.flatMap { [UInt8($0.value >> 8), UInt8($0.value & 0xFF)] })),
            ("PDF hex", Data(secret.utf8.map { $0 }.flatMap { Array(String(format: "%02x", $0).utf8) })),
        ] {
            XCTAssertNil(saved.range(of: pattern), "\(secret) is in the saved bytes as \(label)")
        }
    }

    func testAMarkCanBeTakenOffAgain() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("demo"))
        defer { Task { await engine.close(doc) } }

        let lines = try await engine.textLines(doc, pageIndex: 0)
        let line = try XCTUnwrap(lines.first { $0.text.contains(secret) })
        _ = try await engine.markTextForRedaction(doc, pageIndex: 0, rect: line.rect)

        let marks = try await engine.redactionMarks(doc, pageIndex: 0)
        XCTAssertFalse(marks.isEmpty)
        for mark in marks {
            try await engine.removeRedactionMark(doc, pageIndex: 0, markId: mark.markId)
        }
        let remaining = await engine.redactionMarkCount(doc)
        XCTAssertEqual(remaining, 0)

        // Removing one that has gone is success, so an undo cannot fail.
        try await engine.removeRedactionMark(doc, pageIndex: 0, markId: marks[0].markId)
    }

    func testMarkingAnAreaWithNoTextInItMarksTheRectangle() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("demo"))
        defer { Task { await engine.close(doc) } }

        // Well below the last line of the agreement: empty paper.
        let empty = PdfRect(left: 60, bottom: 80, right: 200, top: 120)
        let grown = try await engine.markTextForRedaction(doc, pageIndex: 0, rect: empty)
        XCTAssertTrue(grown.isEmpty, "no text there, so nothing to grow to glyphs")
        let markId = try await engine.markForRedaction(doc, pageIndex: 0, rect: empty)
        XCTAssertGreaterThanOrEqual(markId, 0)
        let marked = await engine.redactionMarkCount(doc)
        XCTAssertEqual(marked, 1)
    }

    /// The summary and the refusal are looked up by their `%@` literals, and in French they
    /// have to find French. Written with `{0}` in the catalogue they matched nothing and read
    /// in English (#282). Read out of the built app's own .lproj folders.    func testTheSummaryAndTheRefusalAreTranslated() throws {
        let app = Bundle(for: ViewerModel.self)
        let keys = ["1 area redacted: %@", "%@ areas redacted: %@", "%@ characters", "%@ images",
                    "%@ form fields", "%@ annotations",
                    "MegaPDF couldn't remove everything you marked on page %@, so it removed nothing and left the file as it was."]
        for lang in ["fr-CA", "fr"] {
            guard let path = app.path(forResource: lang, ofType: "lproj"), let bundle = Bundle(path: path) else {
                XCTFail("no \(lang).lproj in the app"); continue
            }
            for key in keys {
                let value = bundle.localizedString(forKey: key, value: "\u{1}missing", table: nil)
                XCTAssertNotEqual(value, "\u{1}missing", "\(lang): no entry for \(key)")
                // "images" and "annotations" are the same word in French.
                if !["%@ images", "%@ annotations"].contains(key) {
                    XCTAssertNotEqual(value, key, "\(lang): \(key) reads in English")
                }
            }
            let two = bundle.localizedString(forKey: "%@ areas redacted: %@", value: nil, table: nil)
            XCTAssertEqual(String(format: two, "2", "13 caractères"), "2 zones caviardées\u{00A0}: 13 caractères")
        }
    }

    // MARK: - marks that can be taken back (#329)
    //
    // A mark is the only thing a person can put on a page that is not an edit. These cover
    // the layer that had nothing: what the app does with a mark once the engine has made it.
    // Nothing here reads the view model's map to decide whether a mark is gone — that map is
    // what lied in 2.0.1 — so every check asks the core.

    /// The state rule the whole issue turns on: a mark does not change the file.
    func testAMarkOperationDoesNotChangeTheDocument() {
        let rect = PdfRect(left: 10, bottom: 10, right: 100, top: 40)
        let other = PdfRect(left: 5, bottom: 5, right: 60, top: 20)
        XCTAssertFalse(RedactMarkOperation(pageIndex: 0, rects: [rect], ids: [1], adding: true)
            .changesDocument, "marking must not make the document unsaved")
        XCTAssertFalse(RedactMarkOperation(pageIndex: 0, rects: [rect], ids: [1], adding: false)
            .changesDocument)
        XCTAssertFalse(MoveRedactionMarkOperation(pageIndex: 0, markId: 1, from: rect, to: other)
            .changesDocument)
        XCTAssertFalse(ClearRedactionMarksOperation(pageIndex: 0, marksByPage: [0: [rect]])
            .changesDocument)

        // Everything else does, which is why the default is the safe answer.
        XCTAssertTrue(MarkOperation(pageIndex: 0, square: rect, id: "mark:x", adding: true)
            .changesDocument)
        XCTAssertTrue(FieldToggleOperation(pageIndex: 0, x: 5, y: 5).changesDocument)
        XCTAssertTrue(TextBoxOperation(pageIndex: 0, id: "text:x", text: "hi", fontSize: 12,
                                       x: 1, y: 1, adding: true).changesDocument)
        XCTAssertTrue(MoveStampOperation(pageIndex: 0, id: "sig:x", pixels: [], pixelWidth: 1,
                                         pixelHeight: 1, from: rect, to: other).changesDocument)
    }

    /// One gesture is one undo step, however many marks the core made of it, and redo puts
    /// back the rectangles the person saw rather than re-running the selection.
    @MainActor
    func testUndoTakesBackTheWholeGestureAndRedoPutsItBack() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("demo"))
        defer { Task { await engine.close(doc) } }

        let lines = try await engine.textLines(doc, pageIndex: 0)
        let line = try XCTUnwrap(lines.first { $0.text.contains(secret) })
        // The engine MAKES the marks as it answers "did that selection cross text?" — so
        // the model records the operation rather than performing it, which is what these
        // lines do too (#329).
        let made = try await engine.markTextForRedaction(doc, pageIndex: 0, rect: line.rect)
        XCTAssertFalse(made.isEmpty)
        let rects = try await engine.redactionMarks(doc, pageIndex: 0)
            .filter { made.contains($0.markId) }
            .map(\.rect)
        XCTAssertEqual(rects.count, made.count, "every id the engine reported is on the page")

        let history = EditHistory()
        history.record(RedactMarkOperation(pageIndex: 0, rects: rects, ids: made, adding: true))
        XCTAssertTrue(history.canUndo, "marking must be undoable")
        XCTAssertFalse(history.canRedo)

        _ = try await history.undo(engine, doc)
        var count = await engine.redactionMarkCount(doc)
        XCTAssertEqual(count, 0, "one press of Undo takes back the whole drag")
        XCTAssertFalse(history.canUndo)
        XCTAssertTrue(history.canRedo)

        _ = try await history.redo(engine, doc)
        let back = try await engine.redactionMarks(doc, pageIndex: 0)
        XCTAssertEqual(back.map(\.rect), rects, "redo puts every rectangle back where it was")
        XCTAssertTrue(Set(back.map(\.markId)).isDisjoint(with: Set(made)),
                      "the core never reuses an id, so redo takes fresh ones")
        count = await engine.redactionMarkCount(doc)
        XCTAssertEqual(count, rects.count)
    }

    /// Removing a mark is undoable, and the undo is a mark again at the same rectangle.
    @MainActor
    func testARemovedMarkComesBackWhereItWas() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("demo"))
        defer { Task { await engine.close(doc) } }

        // Paper with no text on it, so the mark is the rectangle it was drawn as.
        let rect = PdfRect(left: 60, bottom: 80, right: 240, top: 130)
        let id = try await engine.markForRedaction(doc, pageIndex: 0, rect: rect)
        let history = EditHistory()

        try await history.perform(
            RedactMarkOperation(pageIndex: 0, rects: [rect], ids: [id], adding: false),
            engine, doc)
        var count = await engine.redactionMarkCount(doc)
        XCTAssertEqual(count, 0, "the mark is gone from the core")

        _ = try await history.undo(engine, doc)
        let back = try await engine.redactionMarks(doc, pageIndex: 0)
        XCTAssertEqual(back.map(\.rect), [rect], "undo puts the mark back, exactly")

        // And a second undo of a removal that has already happened cannot fail: the core
        // treats a missing id as success, so the history never gets stuck.
        _ = try await history.redo(engine, doc)
        count = await engine.redactionMarkCount(doc)
        XCTAssertEqual(count, 0)
    }

    /// A move keeps the mark's id, so undo is exact rather than a re-place.
    @MainActor
    func testMovingAMarkKeepsItsIdAndUndoPutsItBack() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("demo"))
        defer { Task { await engine.close(doc) } }

        let from = PdfRect(left: 60, bottom: 80, right: 240, top: 130)
        let to = PdfRect(left: 300, bottom: 200, right: 500, top: 260)
        let id = try await engine.markForRedaction(doc, pageIndex: 0, rect: from)
        let history = EditHistory()

        try await history.perform(
            MoveRedactionMarkOperation(pageIndex: 0, markId: id, from: from, to: to), engine, doc)
        var marks = try await engine.redactionMarks(doc, pageIndex: 0)
        XCTAssertEqual(marks.map(\.markId), [id], "a move keeps the mark's id")
        XCTAssertEqual(marks.map(\.rect), [to])

        _ = try await history.undo(engine, doc)
        marks = try await engine.redactionMarks(doc, pageIndex: 0)
        XCTAssertEqual(marks.map(\.rect), [from], "undo puts the mark back exactly")
        XCTAssertEqual(marks.map(\.markId), [id])
    }

    /// "Clear all marks" is one action across the whole document, and one press of Undo
    /// brings every one of them back.
    @MainActor
    func testClearAllMarksIsOneStepAcrossPages() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))   // two pages
        defer { Task { await engine.close(doc) } }

        let page0 = PdfRect(left: 20, bottom: 20, right: 120, top: 60)
        let page1 = PdfRect(left: 30, bottom: 30, right: 130, top: 70)
        _ = try await engine.markForRedaction(doc, pageIndex: 0, rect: page0)
        _ = try await engine.markForRedaction(doc, pageIndex: 0, rect: page1)
        _ = try await engine.markForRedaction(doc, pageIndex: 1, rect: page1)
        let count = await engine.redactionMarkCount(doc)
        XCTAssertEqual(count, 3)

        let history = EditHistory()
        try await history.perform(
            ClearRedactionMarksOperation(pageIndex: 0, marksByPage: [0: [page0, page1], 1: [page1]]),
            engine, doc)
        let cleared = await engine.redactionMarkCount(doc)
        XCTAssertEqual(cleared, 0, "every mark on every page went")

        _ = try await history.undo(engine, doc)
        let onPage0 = try await engine.redactionMarks(doc, pageIndex: 0).map(\.rect)
        let onPage1 = try await engine.redactionMarks(doc, pageIndex: 1).map(\.rect)
        XCTAssertEqual(onPage0, [page0, page1], "one press of Undo brings both back")
        XCTAssertEqual(onPage1, [page1], "on the page each was on")
    }
}
