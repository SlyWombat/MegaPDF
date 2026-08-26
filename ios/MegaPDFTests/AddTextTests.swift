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

    /// The anchor contract the app's edit and remove operations rest on (#36):
    /// `addTextBox` places the text **baseline** on the point given, while
    /// `textBoxes()` reports **bounds** and `moveTextBox` anchors bounds. So a box
    /// moved to its own reported lower-left must not shift — if it does, undoing a
    /// removal or a text correction would put the box back a descender too high.
    ///
    /// The text carries a descender ("g", "y") on purpose: without one the two
    /// conventions coincide and the test proves nothing.
    func testMovingABoxToItsOwnRectIsANoOp() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        defer { Task { await engine.close(doc) } }

        let id = try await engine.addTextBox(doc, pageIndex: 0, text: "paging gravy",
                                             fontSize: 12, x: 100, y: 300)
        var boxes = try await engine.textBoxes(doc, pageIndex: 0)
        let before = try XCTUnwrap(boxes.first { $0.id == id }).rect
        XCTAssertLessThan(before.bottom, 300,
                          "the fixture text must have a descender below the baseline")

        try await engine.moveTextBox(doc, pageIndex: 0, id: id,
                                     x: before.left, y: before.bottom)
        boxes = try await engine.textBoxes(doc, pageIndex: 0)
        let after = try XCTUnwrap(boxes.first { $0.id == id }).rect
        XCTAssertEqual(after.left, before.left, accuracy: 0.01)
        XCTAssertEqual(after.bottom, before.bottom, accuracy: 0.01)

        // And it must stay a no-op when repeated — the app normalizes through
        // move on every re-add.
        try await engine.moveTextBox(doc, pageIndex: 0, id: id, x: after.left, y: after.bottom)
        boxes = try await engine.textBoxes(doc, pageIndex: 0)
        let third = try XCTUnwrap(boxes.first { $0.id == id }).rect
        XCTAssertEqual(third.left, before.left, accuracy: 0.01)
        XCTAssertEqual(third.bottom, before.bottom, accuracy: 0.01)
    }

    /// Correcting a typo (#36) is remove + re-add under the same id, anchored to
    /// the old bounds lower-left. The width changes with the new text; the corner
    /// the user placed does not, and reverting restores the original exactly.
    func testCorrectingTheTextKeepsTheBoxWhereItWas() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        defer { Task { await engine.close(doc) } }

        let id = try await engine.addTextBox(doc, pageIndex: 0, text: "Jhon Smithy",
                                             fontSize: 12, x: 100, y: 300)
        let placed = try await engine.textBoxes(doc, pageIndex: 0)
        let original = try XCTUnwrap(placed.first { $0.id == id }).rect

        func replace(with text: String) async throws -> PdfTextBox {
            try await engine.removeTextBox(doc, pageIndex: 0, id: id)
            try await engine.addTextBox(doc, pageIndex: 0, text: text, fontSize: 12,
                                        x: original.left, y: original.bottom, id: id)
            try await engine.moveTextBox(doc, pageIndex: 0, id: id,
                                         x: original.left, y: original.bottom)
            let updated = try await engine.textBoxes(doc, pageIndex: 0)
            return try XCTUnwrap(updated.first { $0.id == id })
        }

        let fixed = try await replace(with: "John Smithy")
        XCTAssertEqual(fixed.text, "John Smithy")
        XCTAssertEqual(fixed.rect.left, original.left, accuracy: 0.01)
        XCTAssertEqual(fixed.rect.bottom, original.bottom, accuracy: 0.01)

        // Undo: the same operation run with the old text.
        let reverted = try await replace(with: "Jhon Smithy")
        XCTAssertEqual(reverted.text, "Jhon Smithy")
        XCTAssertEqual(reverted.rect.left, original.left, accuracy: 0.01)
        XCTAssertEqual(reverted.rect.bottom, original.bottom, accuracy: 0.01)
        XCTAssertEqual(reverted.rect.right, original.right, accuracy: 0.5,
                       "reverting must restore the original width")
    }

    /// #43: the face is carried on the mark, not inferred from the font resource —
    /// pdfium is free to normalise a standard font's reported name, so the only
    /// thing that can be a cross-platform contract is what we wrote down.
    func testTheChosenFaceAndSizeSurviveSaveAndReopen() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))

        try await engine.addTextBox(doc, pageIndex: 0, text: "Eighteen point Times",
                                    fontSize: 18, x: 100, y: 300,
                                    id: "text:serif", fontName: "Times-Roman")
        try await engine.addTextBox(doc, pageIndex: 0, text: "Twelve point Courier",
                                    fontSize: 12, x: 100, y: 250,
                                    id: "text:mono", fontName: "Courier")
        var boxes = try await engine.textBoxes(doc, pageIndex: 0)
        var serif = try XCTUnwrap(boxes.first { $0.id == "text:serif" })
        XCTAssertEqual(serif.fontName, "Times-Roman")
        XCTAssertEqual(serif.fontSize, 18, accuracy: 0.5)
        XCTAssertEqual(try XCTUnwrap(boxes.first { $0.id == "text:mono" }).fontName, "Courier")

        let saved = try await engine.save(doc)
        await engine.close(doc)

        let reopened = try await engine.open(saved)
        defer { Task { await engine.close(reopened) } }
        boxes = try await engine.textBoxes(reopened, pageIndex: 0)
        XCTAssertEqual(boxes.count, 2)
        serif = try XCTUnwrap(boxes.first { $0.id == "text:serif" })
        XCTAssertEqual(serif.fontName, "Times-Roman")
        XCTAssertEqual(serif.fontSize, 18, accuracy: 0.5)
        XCTAssertEqual(try XCTUnwrap(boxes.first { $0.id == "text:mono" }).fontName, "Courier")
    }

    /// Deliberately strict: the app passes one of three constants, so anything else
    /// is a bug and should fail loudly rather than silently render in the wrong face.
    func testAFaceOutsideTheThreeIsRejected() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        defer { Task { await engine.close(doc) } }

        do {
            try await engine.addTextBox(doc, pageIndex: 0, text: "Nope", fontSize: 12,
                                        x: 100, y: 300, fontName: "Comic Sans MS")
            XCTFail("an unsupported face must be rejected")
        } catch {
            // expected
        }
        let boxes = try await engine.textBoxes(doc, pageIndex: 0)
        XCTAssertTrue(boxes.isEmpty, "a rejected face must not leave a box behind")
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

    /// Interop: a box written by another platform must read back here. The
    /// fixture carries the marked-content section the engines write, not one
    /// this platform produced.
    func testReadsATextBoxWrittenElsewhere() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("textbox"))
        defer { Task { await engine.close(doc) } }

        let boxes = try await engine.textBoxes(doc, pageIndex: 0)
        XCTAssertEqual(boxes.count, 4, "the ordinary body text must not read as a box")
        XCTAssertEqual(boxes.first?.text, "Fixture text box")
        XCTAssertEqual(boxes.first?.id, "text:fixture-1")
        XCTAssertEqual(boxes.first?.fontName, PdfEngine.defaultFont,
                       "a box with no recorded face is Helvetica")

        // The box that chose its face and size (#43).
        let times = try XCTUnwrap(boxes.first { $0.id == "text:fixture-times" })
        XCTAssertEqual(times.text, "Eighteen point Times")
        XCTAssertEqual(times.fontName, "Times-Roman")
        XCTAssertEqual(times.fontSize, 18, accuracy: 0.5)

        // The two marked-but-unidentified boxes are what MegaPDF for Windows
        // wrote before it stamped ids. They must be told apart: one shared id
        // would let a remove delete an arbitrary one.
        let legacy = boxes.filter { $0.id.hasPrefix(PdfEngine.untaggedPrefix) }
        XCTAssertEqual(legacy.count, 2)
        XCTAssertEqual(legacy.map(\.fontName),
                       [PdfEngine.defaultFont, PdfEngine.defaultFont],
                       "untagged boxes are Helvetica too")
        XCTAssertEqual(Set(legacy.map(\.id)).count, 2, "untagged boxes must not share an id")
        XCTAssertEqual(legacy.map(\.text), ["Legacy box one", "Legacy box two"])
    }

    /// Removing one untagged box must leave the other alone — the failure this
    /// guards against is a shared handle resolving to whichever came first.
    func testRemovingOneUntaggedBoxLeavesTheOther() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("textbox"))
        defer { Task { await engine.close(doc) } }

        let before = try await engine.textBoxes(doc, pageIndex: 0)
        let target = try XCTUnwrap(before.first { $0.text == "Legacy box one" })
        try await engine.removeTextBox(doc, pageIndex: 0, id: target.id)

        let after = try await engine.textBoxes(doc, pageIndex: 0)
        XCTAssertEqual(after.count, 3, "only the targeted box goes")
        XCTAssertTrue(after.contains { $0.text == "Legacy box two" },
                      "the other untagged box must survive")
        XCTAssertFalse(after.contains { $0.text == "Legacy box one" })
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
