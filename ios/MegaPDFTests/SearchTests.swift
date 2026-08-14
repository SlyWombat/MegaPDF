import XCTest
@testable import MegaPDF

/// Text-search parity (#26) against the shared fixtures — mirrors the other
/// platforms' search tests (case-insensitive literal matching, per-page rects).
final class SearchTests: XCTestCase {

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: name, withExtension: "pdf") else {
            throw XCTSkip("fixture \(name).pdf missing from test bundle")
        }
        return try Data(contentsOf: url)
    }

    func testCaseInsensitiveMatchWithRects() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        defer { Task { await engine.close(doc) } }

        // Lower-case term must hit "MegaPDF engine fixture - page 1", the
        // 24pt heading drawn at (72,720).
        let matches = try await engine.search(doc, pageIndex: 0, term: "megapdf")
        XCTAssertEqual(matches.count, 1)
        let rect = try XCTUnwrap(matches.first?.rects.first)
        XCTAssertEqual(rect.left, 72.0, accuracy: 3.0)
        XCTAssertEqual(rect.bottom, 718.0, accuracy: 8.0)
        XCTAssertEqual(rect.top, 737.0, accuracy: 8.0)
    }

    func testMatchesAreSeparatePerPage() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        defer { Task { await engine.close(doc) } }

        // "page" appears once on each page ("- page 1" and "Page 2").
        let first = try await engine.search(doc, pageIndex: 0, term: "PAGE")
        XCTAssertEqual(first.count, 1)
        let second = try await engine.search(doc, pageIndex: 1, term: "page")
        XCTAssertEqual(second.count, 1)
    }

    func testNoMatchesAndEmptyTerm() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        defer { Task { await engine.close(doc) } }

        let none = try await engine.search(doc, pageIndex: 0, term: "zebra")
        XCTAssertTrue(none.isEmpty)
        let empty = try await engine.search(doc, pageIndex: 0, term: "")
        XCTAssertTrue(empty.isEmpty)
    }
}
