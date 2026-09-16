import XCTest
@testable import MegaPDF

/// A page drawn in units larger than a point (#150), against the shared `userunit.pdf`
/// (tools/gen_test_fixtures.py) — mirrors `UserUnitTest` on Android and `UserUnitTests` on
/// the desktops. MediaBox [0 0 306 396], CropBox [0 50 306 350], /UserUnit 2: the page
/// measures 612 x 600 pt. Ignoring /UserUnit shows it at half size and puts every tap,
/// mark and text box at half its distance from the corner.
final class UserUnitTests: XCTestCase {

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: name, withExtension: "pdf") else {
            throw XCTSkip("fixture \(name).pdf missing from test bundle")
        }
        return try Data(contentsOf: url)
    }

    func testPageMeasuresInPointsAndContentIsWhereItIsDrawn() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("userunit"))
        defer { Task { await engine.close(doc) } }

        let size = try await engine.pageSize(doc, index: 0)
        XCTAssertEqual(size.width, 612.0, accuracy: 1.0)
        XCTAssertEqual(size.height, 600.0, accuracy: 1.0)

        let square = try XCTUnwrap(try await engine.detectCheckboxSquares(doc, pageIndex: 0).first)
        XCTAssertEqual(square.left, 100, accuracy: 1.0)
        XCTAssertEqual(square.bottom, 400, accuracy: 1.0)

        let field = try XCTUnwrap(try await engine.formFields(doc, pageIndex: 0).first)
        XCTAssertEqual(field.rect.left, 100, accuracy: 0.1)
        XCTAssertEqual(field.rect.bottom, 300, accuracy: 0.1)
        XCTAssertEqual(field.rect.right, 300, accuracy: 0.1)
        XCTAssertEqual(field.rect.top, 320, accuracy: 0.1)

        let rect = try XCTUnwrap(try await engine.search(doc, pageIndex: 0, term: "megapdf").first?.rects.first)
        XCTAssertTrue(rect.bottom < 556.0 && rect.top > 544.0,
                      "the hit should straddle the 550 pt baseline, was \(rect)")
    }

    func testPlacementsLandWhereAsked() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("userunit"))
        defer { Task { await engine.close(doc) } }

        let id = try await engine.addTextBox(doc, pageIndex: 0, text: "Signed", fontSize: 12, x: 80, y: 120)
        var box = try XCTUnwrap(try await engine.textBoxes(doc, pageIndex: 0).first)
        XCTAssertEqual(box.fontSize, 12, accuracy: 0.01)
        XCTAssertEqual(box.rect.left, 80, accuracy: 2.0)
        XCTAssertEqual(box.rect.bottom, 120, accuracy: 4.0)
        XCTAssertTrue((8.0...16.0).contains(box.rect.top - box.rect.bottom),
                      "a 12 pt box is about 12 pt tall, was \(box.rect)")
        try await engine.moveTextBox(doc, pageIndex: 0, id: id, x: 200, y: 220)
        box = try XCTUnwrap(try await engine.textBoxes(doc, pageIndex: 0).first)
        XCTAssertEqual(box.rect.left, 200, accuracy: 0.1)
        XCTAssertEqual(box.rect.bottom, 220, accuracy: 0.1)

        let placed = PdfRect(left: 350, bottom: 100, right: 470, top: 160)
        try await engine.addImageStamp(doc, pageIndex: 0, pixels: [UInt32](repeating: 0xFF40_4040, count: 16),
                                       pixelWidth: 4, pixelHeight: 4, rect: placed, id: "sig:unit")
        let stamp = try XCTUnwrap(try await engine.stamps(doc, pageIndex: 0).first { $0.id == "sig:unit" })
        XCTAssertEqual(stamp.rect.left, placed.left, accuracy: 0.1)
        XCTAssertEqual(stamp.rect.bottom, placed.bottom, accuracy: 0.1)
        XCTAssertEqual(stamp.rect.right, placed.right, accuracy: 0.1)
        XCTAssertEqual(stamp.rect.top, placed.top, accuracy: 0.1)
    }
}
