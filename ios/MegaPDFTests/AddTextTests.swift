import XCTest
@testable import MegaPDF

/// Added text (#34): the on-disk representation must match the desktop's
/// (a marked page text object), survive a save/reopen round trip, and address
/// itself by id rather than by page-object index.
final class AddTextTests: XCTestCase {

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: name, withExtension: "pdf") else {
            throw XCTSkip("fixture \(name).pdf missing from test bundle")
        }
        return try Data(contentsOf: url)
    }

    func testAddedTextIsReadableAndSurvivesSave() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))

        let id = try await engine.addTextBox(doc, pageIndex: 0, text: "Jane Smith",
                                             fontSize: 12, x: 100, y: 300)
        let boxes = try await engine.textBoxes(doc, pageIndex: 0)
        XCTAssertEqual(boxes.count, 1)
        XCTAssertEqual(boxes.first?.id, id)
        XCTAssertEqual(boxes.first?.text, "Jane Smith")
        XCTAssertEqual(boxes.first?.fontSize ?? 0, 12, accuracy: 0.5)
        XCTAssertEqual(boxes.first?.rect.left ?? 0, 100, accuracy: 2.0)

        let saved = try await engine.save(doc)
        await engine.close(doc)

        let reopened = try await engine.open(saved)
        defer { Task { await engine.close(reopened) } }
        let after = try await engine.textBoxes(reopened, pageIndex: 0)
        XCTAssertEqual(after.count, 1, "the text box must survive save and reopen")
        XCTAssertEqual(after.first?.text, "Jane Smith")
        XCTAssertEqual(after.first?.id, id, "its id is the handle undo relies on")
    }

    func testTextIsFoundBySearchLikeAnyOtherText() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        defer { Task { await engine.close(doc) } }

        try await engine.addTextBox(doc, pageIndex: 0, text: "Marmalade",
                                    fontSize: 12, x: 100, y: 300)
        let matches = try await engine.search(doc, pageIndex: 0, term: "marmalade")
        XCTAssertEqual(matches.count, 1, "added text is real page text, not an overlay")
    }

    func testRemoveAndMoveAddressTheBoxById() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        defer { Task { await engine.close(doc) } }

        let first = try await engine.addTextBox(doc, pageIndex: 0, text: "First",
                                                fontSize: 12, x: 100, y: 300)
        let second = try await engine.addTextBox(doc, pageIndex: 0, text: "Second",
                                                 fontSize: 12, x: 100, y: 200)

        try await engine.moveTextBox(doc, pageIndex: 0, id: second, x: 250, y: 400)
        var boxes = try await engine.textBoxes(doc, pageIndex: 0)
        let moved = try XCTUnwrap(boxes.first { $0.id == second })
        XCTAssertEqual(moved.rect.left, 250, accuracy: 2.0)
        XCTAssertEqual(moved.rect.bottom, 400, accuracy: 2.0)

        // Removing the *first* box shifts the second one's object index; the id
        // must still find it.
        try await engine.removeTextBox(doc, pageIndex: 0, id: first)
        boxes = try await engine.textBoxes(doc, pageIndex: 0)
        XCTAssertEqual(boxes.count, 1)
        XCTAssertEqual(boxes.first?.id, second)
        XCTAssertEqual(boxes.first?.text, "Second")

        // Removing something already gone is a no-op, not a failure — undo may
        // race a re-render.
        try await engine.removeTextBox(doc, pageIndex: 0, id: first)
    }

    func testTextGoesWhereAskedOnACroppedPage() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("cropped"))
        defer { Task { await engine.close(doc) } }

        // Crop space: the visible page is 612 x 600 (#30).
        try await engine.addTextBox(doc, pageIndex: 0, text: "Signed",
                                    fontSize: 12, x: 80, y: 120)
        let placed = try await engine.textBoxes(doc, pageIndex: 0)
        let box = try XCTUnwrap(placed.first)
        XCTAssertEqual(box.rect.left, 80, accuracy: 2.0)
        XCTAssertEqual(box.rect.bottom, 120, accuracy: 4.0)
        XCTAssertTrue(box.rect.top <= 600, "must land inside the visible page, was \(box.rect)")
    }
}
