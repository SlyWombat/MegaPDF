import XCTest
@testable import MegaPDF

/// Multi-line added text on iPhone and iPad (#4).
///
/// The **size** half of #4 was already here: `textSizes` and the sheet's Size picker shipped
/// with #43, which is the same position Android was in (#565). What was missing was a note of
/// more than one line.
///
/// Two things are under test, and the second is the one that bit two other platforms:
///
///  1. a note of several lines becomes that many ordinary, separately-selectable text boxes,
///     taken back as one undo step;
///  2. **every form of line ending folds to one.** Windows' `TextBox` handed back a lone
///     `"\r"` and Android's field needed the same fold; in both cases the note silently stayed
///     one object with a control character inside it (#564, #565). What SwiftUI's own field
///     actually returns is measured in a running app by `MultilineTextUITests`; these checks
///     pin the fold itself, including the forms a paste from another app can carry.
@MainActor
final class MultilineTextTests: XCTestCase {

    // MARK: - helpers

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: name, withExtension: "pdf") else {
            throw XCTSkip("fixture \(name).pdf missing from test bundle")
        }
        return try Data(contentsOf: url)
    }

    private func waitUntil(_ what: String, timeout: TimeInterval = 15,
                           _ condition: @MainActor () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition() {
            if Date() > deadline {
                XCTFail("timed out waiting for: \(what)")
                throw CancellationError()
            }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
    }

    private func openModel() async throws -> (ViewerModel, URL) {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("multiline-\(UUID().uuidString).pdf")
        try fixture("demo").write(to: url)
        let model = ViewerModel()
        model.pageCheck = { _, _ in .keepsLook }
        model.openPicked(url: url)
        try await ModelOpening.waitForTheDocument(model)
        return (model, url)
    }

    private func boxes(_ model: ViewerModel) async throws -> [PdfTextBox] {
        let doc = try XCTUnwrap(model.document)
        return try await PdfEngine.shared.textBoxes(doc, pageIndex: 0)
    }

    /// Arms Add text and taps a clear spot low on page 1, which is where a printed name goes.
    private func placeAt(_ model: ViewerModel) async throws {
        model.startTextPlacement()
        model.onPageTapped(index: 0, xFraction: 0.2, yFraction: 0.85)
        try await waitUntil("the text sheet opens") { model.pendingText != nil }
    }

    private func settle(_ model: ViewerModel, _ what: String) async throws {
        try await waitUntil(what) { !model.busy.isBlocked }
    }

    // MARK: - the fold

    func testEveryLineEndingFoldsToTheSameLines() {
        let want = ["one", "two"]
        XCTAssertEqual(textBoxLines("one\ntwo"), want, "a plain newline")
        XCTAssertEqual(textBoxLines("one\r\ntwo"), want, "Windows' pair, which a paste can carry")
        XCTAssertEqual(textBoxLines("one\rtwo"), want,
                       "a lone carriage return — exactly what Windows' own TextBox handed back (#564)")
        XCTAssertEqual(textBoxLines("one\u{2028}two"), want,
                       "LINE SEPARATOR, which a UIKit text view can insert for a soft return")
        XCTAssertEqual(textBoxLines("one\u{2029}two"), want, "PARAGRAPH SEPARATOR, for the same reason")
    }

    func testBlankLinesAndSurroundingSpaceAreDropped() {
        XCTAssertEqual(textBoxLines("  one  \n\n  two \n\n"), ["one", "two"],
                       "an empty line would be an empty text box, which is an object nobody can see or select")
        XCTAssertEqual(textBoxLines("\n\n"), [], "a field of nothing but returns places nothing")
        XCTAssertEqual(textBoxLines("   "), [], "and neither does a field of spaces")
        XCTAssertEqual(textBoxLines("one"), ["one"], "one line is still one line")
    }

    // MARK: - the operation

    func testANoteOfTwoLinesBecomesTwoBoxesStackedByTheChosenSize() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("demo"))
        defer { Task { await engine.close(doc) } }

        let before = try await engine.textBoxes(doc, pageIndex: 0).count
        let style = TextBoxStyle(text: "", fontSize: 18, fontName: PdfEngine.defaultFont)
        // Two lines with no descender in either, so each box's reported bottom IS its
        // baseline and the gap between them is the leading rather than the leading minus
        // whatever a "g" hangs below the line.
        let note = AddTextBoxesOperation(pageIndex: 0, lines: ["MEGA", "2026"],
                                        ids: ["text:a", "text:b"], style: style, x: 100, y: 300)
        try await note.apply(engine, doc)

        let after = try await engine.textBoxes(doc, pageIndex: 0)
        XCTAssertEqual(after.count, before + 2, "a two-line note is two text boxes")
        let first = try XCTUnwrap(after.first { $0.id == "text:a" })
        let second = try XCTUnwrap(after.first { $0.id == "text:b" },
                                  "each line must be its own addressable box")
        XCTAssertEqual(first.fontSize, 18, accuracy: 0.01, "at the chosen size")
        XCTAssertEqual(second.fontSize, 18, accuracy: 0.01)
        XCTAssertLessThan(second.rect.bottom, first.rect.bottom,
                          "the second line goes below the first, not on top of it")
        // 1.2x the size is the stack Android uses, so a note reads the same on both phones.
        XCTAssertEqual(first.rect.bottom - second.rect.bottom,
                       AddTextBoxesOperation.leading(forSize: 18), accuracy: 0.5,
                       "the lines should be one leading apart")
    }

    func testUndoTakesTheWholeNoteBackAndRedoPutsItAllBack() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("demo"))
        defer { Task { await engine.close(doc) } }

        let before = try await engine.textBoxes(doc, pageIndex: 0).count
        let note = AddTextBoxesOperation(pageIndex: 0, lines: ["one", "two", "three"],
                                         ids: ["text:1", "text:2", "text:3"],
                                         style: TextBoxStyle(text: "", fontSize: 12,
                                                             fontName: PdfEngine.defaultFont),
                                         x: 80, y: 260)
        try await note.apply(engine, doc)
        var count = try await engine.textBoxes(doc, pageIndex: 0).count
        XCTAssertEqual(count, before + 3)

        try await note.revert(engine, doc)
        count = try await engine.textBoxes(doc, pageIndex: 0).count
        XCTAssertEqual(count, before, "one Undo takes the whole note back, not a line of it")

        try await note.apply(engine, doc)
        count = try await engine.textBoxes(doc, pageIndex: 0).count
        XCTAssertEqual(count, before + 3, "and one Redo puts all of it back")
    }

    // MARK: - through the view model

    func testCommittingATwoLineNotePlacesTwoBoxesAsOneUndoStep() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        let before = try await boxes(model).count
        try await placeAt(model)
        model.commitText("Mega Woman\n2026-10-01", fontSize: 18, fontName: PdfEngine.defaultFont)
        try await settle(model, "the note lands")

        var after = try await boxes(model)
        XCTAssertEqual(after.count, before + 2, "two lines typed should be two boxes placed")
        XCTAssertTrue(after.contains { $0.text == "Mega Woman" })
        XCTAssertTrue(after.contains { $0.text == "2026-10-01" })
        XCTAssertTrue(after.allSatisfy { !$0.text.contains("\n") && !$0.text.contains("\r") },
                      "no box may carry a control character: that is the Windows/Android trap (#564, #565)")

        model.undo()
        try await settle(model, "the undo lands")
        after = try await boxes(model)
        XCTAssertEqual(after.count, before, "undo removes the whole note, which is #4's acceptance")
    }

    /// The acceptance test's own words: "each line remains individually editable afterwards".
    func testEachLineOfANoteIsSelectableAndEditableOnItsOwn() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        try await placeAt(model)
        model.commitText("first line\nsecond line", fontSize: 14, fontName: PdfEngine.defaultFont)
        try await settle(model, "the note lands")

        let placed = try await boxes(model)
        let second = try XCTUnwrap(placed.first { $0.text == "second line" })
        guard case let .viewing(_, sizes) = model.state else { return XCTFail("no document") }
        let size = sizes[0]
        model.onPageTapped(index: 0,
                           xFraction: (second.rect.left + second.rect.right) / 2 / Double(size.width),
                           yFraction: 1 - (second.rect.bottom + second.rect.top) / 2 / Double(size.height))
        try await waitUntil("the second line selects on its own") {
            model.selectedTextBox?.text == "second line"
        }
        XCTAssertEqual(model.selectedTextBox?.fontSize, 14, "and it carries the size it was placed at")
        let selectedId = try XCTUnwrap(model.selectedTextBox?.id)

        model.editSelectedTextBox()
        XCTAssertEqual(model.pendingText?.editingId, selectedId,
                       "the pencil opens the editor on that line alone")
        XCTAssertEqual(model.draftText, "second line")
    }

    /// A one-line note must still take the single-box route: the multi-line operation is for
    /// several lines, and a note of one should be the same object it has always been.
    func testAOneLineNoteIsStillASingleTextBox() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        let before = try await boxes(model).count
        try await placeAt(model)
        model.commitText("just one", fontSize: 12, fontName: PdfEngine.defaultFont)
        try await settle(model, "the note lands")

        let after = try await boxes(model)
        XCTAssertEqual(after.count, before + 1)
        XCTAssertTrue(after.contains { $0.text == "just one" })
    }

    /// The fold reaches the real commit path, not only the helper: a lone carriage return —
    /// the exact value Windows' field produced — must still make two boxes here.
    func testACarriageReturnStillMakesTwoBoxes() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        let before = try await boxes(model).count
        try await placeAt(model)
        model.commitText("alpha\rbeta", fontSize: 12, fontName: PdfEngine.defaultFont)
        try await settle(model, "the note lands")

        let after = try await boxes(model)
        XCTAssertEqual(after.count, before + 2,
                       "a lone carriage return must split, not end up inside one box")
        XCTAssertTrue(after.contains { $0.text == "alpha" })
        XCTAssertTrue(after.contains { $0.text == "beta" })
    }

    /// Correcting a box already on the page stays one line, as on every other platform: the
    /// field there is single-line, and a pasted newline must not quietly turn one box into two
    /// under an id the history is still holding.
    func testCorrectingAnExistingBoxDoesNotGrowIntoMoreThanOne() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        try await placeAt(model)
        model.commitText("original", fontSize: 12, fontName: PdfEngine.defaultFont)
        try await settle(model, "the note lands")
        let placed = try await boxes(model)
        let before = placed.count
        let box = try XCTUnwrap(placed.first { $0.text == "original" })

        guard case let .viewing(_, sizes) = model.state else { return XCTFail("no document") }
        let size = sizes[0]
        model.onPageTapped(index: 0,
                           xFraction: (box.rect.left + box.rect.right) / 2 / Double(size.width),
                           yFraction: 1 - (box.rect.bottom + box.rect.top) / 2 / Double(size.height))
        try await waitUntil("the box selects") { model.selectedTextBox?.text == "original" }
        model.editSelectedTextBox()
        try await waitUntil("the editor opens on it") { model.pendingText?.editingId != nil }

        model.commitText("corrected\nsecond", fontSize: 12, fontName: PdfEngine.defaultFont)
        try await settle(model, "the correction lands")

        let after = try await boxes(model)
        XCTAssertEqual(after.count, before, "a correction replaces one box with one box")
        XCTAssertTrue(after.contains { $0.text == "corrected" },
                      "and it keeps the first line, rather than the whole string with a newline in it")
        XCTAssertFalse(after.contains { $0.text.contains("second") })
    }

    /// The size half, which #43 already shipped — asserted here so #4 has it on the record for
    /// this platform too, and so a future change to the picker's list cannot quietly leave the
    /// phones without a size choice.
    func testTheSizeChoiceIsAShortListAndANoteTakesTheChosenSize() async throws {
        XCTAssertEqual(textSizes, [8, 10, 12, 14, 18, 24], "#43's list: a choice, not a number box")
        XCTAssertTrue(textSizes.contains(defaultTextSize))

        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }
        try await placeAt(model)
        model.commitText("big\nsmall", fontSize: 24, fontName: PdfEngine.defaultFont)
        try await settle(model, "the note lands")

        let placed = try await boxes(model).filter { $0.text == "big" || $0.text == "small" }
        XCTAssertEqual(placed.count, 2)
        XCTAssertTrue(placed.allSatisfy { abs($0.fontSize - 24) < 0.01 },
                      "every line of the note takes the size that was chosen for it")
    }
}
