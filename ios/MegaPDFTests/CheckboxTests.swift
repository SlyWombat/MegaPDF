import XCTest
@testable import MegaPDF

/// Checkbox surface parity (#22) against the shared SDD §6.2 fixtures —
/// mirrors Android's CheckboxTest.
final class CheckboxTests: XCTestCase {

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: name, withExtension: "pdf") else {
            throw XCTSkip("fixture \(name).pdf missing from test bundle")
        }
        return try Data(contentsOf: url)
    }

    func testFormCheckboxTogglesAndSurvivesSave() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("forms"))

        let before = try await engine.formFields(doc, pageIndex: 0)
        XCTAssertEqual(before.count, 1)
        XCTAssertFalse(before[0].isRadio)
        XCTAssertFalse(before[0].isChecked)
        XCTAssertEqual(before[0].rect.left, 100.0, accuracy: 0.01)
        XCTAssertEqual(before[0].rect.top, 615.0, accuracy: 0.01)

        try await engine.clickAt(doc, pageIndex: 0,
                                 x: before[0].rect.centerX, y: before[0].rect.centerY)
        var fields = try await engine.formFields(doc, pageIndex: 0)
        XCTAssertTrue(fields[0].isChecked, "click should check the box")

        try await engine.clickAt(doc, pageIndex: 0,
                                 x: before[0].rect.centerX, y: before[0].rect.centerY)
        fields = try await engine.formFields(doc, pageIndex: 0)
        XCTAssertFalse(fields[0].isChecked, "second click should uncheck")

        try await engine.clickAt(doc, pageIndex: 0,
                                 x: before[0].rect.centerX, y: before[0].rect.centerY)
        let saved = try await engine.save(doc)
        await engine.close(doc)

        let reopened = try await engine.open(saved)
        let after = try await engine.formFields(reopened, pageIndex: 0)
        await engine.close(reopened)
        XCTAssertTrue(after[0].isChecked, "checked state must survive save/reopen")
    }

    func testDrawnSquareDetectedMarkAddedAndRoundTrips() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))

        // Parity rects: 12x12pt square at (72,600); bounds may include stroke.
        let squares = try await engine.detectCheckboxSquares(doc, pageIndex: 0)
        XCTAssertEqual(squares.count, 1)
        XCTAssertEqual(squares[0].left, 72.0, accuracy: 1.0)
        XCTAssertEqual(squares[0].bottom, 600.0, accuracy: 1.0)
        XCTAssertEqual(squares[0].right, 84.0, accuracy: 1.0)
        XCTAssertEqual(squares[0].top, 612.0, accuracy: 1.0)

        try await engine.addCheckMark(doc, pageIndex: 0,
                                      square: squares[0], id: "mark:ios-test-1")
        var stamps = try await engine.stamps(doc, pageIndex: 0)
        XCTAssertEqual(stamps.map(\.id), ["mark:ios-test-1"])

        let saved = try await engine.save(doc)
        await engine.close(doc)

        // MegaPDF_Id contract: identifiable and removable after save/reopen.
        let reopened = try await engine.open(saved)
        stamps = try await engine.stamps(reopened, pageIndex: 0)
        XCTAssertEqual(stamps.map(\.id), ["mark:ios-test-1"])
        try await engine.removeAnnot(reopened, pageIndex: 0,
                                     annotIndex: stamps[0].annotIndex)
        let remaining = try await engine.stamps(reopened, pageIndex: 0)
        await engine.close(reopened)
        XCTAssertTrue(remaining.isEmpty)
    }

    func testPageTwoHasNothingInteractive() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        let squares = try await engine.detectCheckboxSquares(doc, pageIndex: 1)
        let fields = try await engine.formFields(doc, pageIndex: 1)
        await engine.close(doc)
        XCTAssertTrue(squares.isEmpty)
        XCTAssertTrue(fields.isEmpty)
    }
}
