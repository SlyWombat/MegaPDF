import XCTest
@testable import MegaPDF

/// Undo/redo (#34). The interesting cases are the ones where indices move
/// underneath an operation — that is why operations address targets by id.
@MainActor
final class UndoTests: XCTestCase {

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: name, withExtension: "pdf") else {
            throw XCTSkip("fixture \(name).pdf missing from test bundle")
        }
        return try Data(contentsOf: url)
    }

    func testMarkUndoRedoRoundTrip() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        defer { Task { await engine.close(doc) } }
        let history = EditHistory()

        // fixture.pdf page 1 carries one drawn square at (72,600)-(84,612).
        let squares = try await engine.detectCheckboxSquares(doc, pageIndex: 0)
        let square = try XCTUnwrap(squares.first)
        let op = MarkOperation(pageIndex: 0, square: square, id: "mark:undo-1", adding: true)

        try await history.perform(op, engine, doc)
        var marks = try await engine.stamps(doc, pageIndex: 0).filter { $0.id.hasPrefix("mark:") }
        XCTAssertEqual(marks.count, 1)
        XCTAssertTrue(history.canUndo)

        let undonePage = try await history.undo(engine, doc)
        XCTAssertEqual(undonePage, 0)
        marks = try await engine.stamps(doc, pageIndex: 0).filter { $0.id.hasPrefix("mark:") }
        XCTAssertTrue(marks.isEmpty, "undo must remove the mark")
        XCTAssertFalse(history.canUndo)
        XCTAssertTrue(history.canRedo)

        let redonePage = try await history.redo(engine, doc)
        XCTAssertEqual(redonePage, 0)
        marks = try await engine.stamps(doc, pageIndex: 0).filter { $0.id.hasPrefix("mark:") }
        XCTAssertEqual(marks.count, 1, "redo must put it back")
        XCTAssertEqual(marks.first?.id, "mark:undo-1", "under the same id")
    }

    /// The regression this design exists for: undoing an edit made *before*
    /// other edits shifted the annotation indices.
    func testUndoStillFindsItsTargetAfterLaterEdits() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        defer { Task { await engine.close(doc) } }
        let history = EditHistory()

        let squares = try await engine.detectCheckboxSquares(doc, pageIndex: 0)
        let square = try XCTUnwrap(squares.first)
        try await history.perform(
            MarkOperation(pageIndex: 0, square: square, id: "mark:first", adding: true),
            engine, doc)

        // Two more marks stacked on the same square, then take the middle one
        // away — every index after it moves.
        try await history.perform(
            MarkOperation(pageIndex: 0, square: square, id: "mark:second", adding: true),
            engine, doc)
        try await engine.removeAnnot(doc, pageIndex: 0, id: "mark:first")

        // Undo of "add second" must still remove exactly that annot.
        _ = try await history.undo(engine, doc)
        let ids = try await engine.stamps(doc, pageIndex: 0).map(\.id)
        XCTAssertFalse(ids.contains("mark:second"), "undo removed the wrong annotation")
    }

    func testFormToggleIsItsOwnInverse() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("forms"))
        defer { Task { await engine.close(doc) } }
        let history = EditHistory()

        let initialFields = try await engine.formFields(doc, pageIndex: 0)
        let field = try XCTUnwrap(initialFields.first)
        XCTAssertFalse(field.isChecked)

        try await history.perform(
            FieldToggleOperation(pageIndex: 0, x: field.rect.centerX, y: field.rect.centerY),
            engine, doc)
        var fields = try await engine.formFields(doc, pageIndex: 0)
        XCTAssertTrue(fields[0].isChecked)

        _ = try await history.undo(engine, doc)
        fields = try await engine.formFields(doc, pageIndex: 0)
        XCTAssertFalse(fields[0].isChecked, "undo must clear the box again")
    }

    func testAddedTextCanBeTakenBack() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        defer { Task { await engine.close(doc) } }
        let history = EditHistory()

        try await history.perform(
            TextBoxOperation(pageIndex: 0, id: "text:undo-1", text: "Oops",
                             fontSize: 12, x: 100, y: 300, adding: true),
            engine, doc)
        let added = try await engine.textBoxes(doc, pageIndex: 0)
        XCTAssertEqual(added.count, 1)

        _ = try await history.undo(engine, doc)
        let afterUndo = try await engine.textBoxes(doc, pageIndex: 0)
        XCTAssertTrue(afterUndo.isEmpty, "undo must take the text off the page")

        _ = try await history.redo(engine, doc)
        let boxes = try await engine.textBoxes(doc, pageIndex: 0)
        XCTAssertEqual(boxes.first?.text, "Oops")
        XCTAssertEqual(boxes.first?.id, "text:undo-1")
    }

    /// `square(fromMark:)` reconstructs the detected square from the drawn mark,
    /// which is what lets "clear a mark" be undone.
    func testSquareReconstructionRoundTrips() {
        let square = PdfRect(left: 72, bottom: 600, right: 84, top: 612)
        let w = square.right - square.left, h = square.top - square.bottom
        let mark = PdfRect(left: square.left + 0.10 * w, bottom: square.bottom + 0.10 * h,
                           right: square.right - 0.10 * w, top: square.top - 0.10 * h)
        let back = MarkOperation.square(fromMark: mark)
        XCTAssertEqual(back.left, square.left, accuracy: 0.001)
        XCTAssertEqual(back.bottom, square.bottom, accuracy: 0.001)
        XCTAssertEqual(back.right, square.right, accuracy: 0.001)
        XCTAssertEqual(back.top, square.top, accuracy: 0.001)
    }
}
