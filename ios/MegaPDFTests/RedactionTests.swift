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
        XCTAssertGreaterThan(made, 0, "a drag across text should mark the text")
        var count = await engine.redactionMarkCount(doc)
        XCTAssertEqual(count, made)

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
        XCTAssertEqual(grown, 0, "no text there, so nothing to grow to glyphs")
        let markId = try await engine.markForRedaction(doc, pageIndex: 0, rect: empty)
        XCTAssertGreaterThanOrEqual(markId, 0)
        let marked = await engine.redactionMarkCount(doc)
        XCTAssertEqual(marked, 1)
    }

    /// The summary and the refusal are looked up by their `%@` literals, and in French they
    /// have to find French. Written with `{0}` in the catalogue they matched nothing and read
    /// in English (#282). Read out of the built app's own .lproj folders.
    func testTheSummaryAndTheRefusalAreTranslated() throws {
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
                XCTAssertNotEqual(value, key, "\(lang): \(key) reads in English")
            }
            let two = bundle.localizedString(forKey: "%@ areas redacted: %@", value: nil, table: nil)
            XCTAssertEqual(String(format: two, "2", "13 caractères"), "2 zones caviardées\u{00A0}: 13 caractères")
        }
    }
}
