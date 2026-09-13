import XCTest
@testable import MegaPDF

/// Editing text that is already in the document (#113), through the shared core:
/// visual lines, the two tiers (#116) and the byte-identical undo (#117).
final class BodyTextTests: XCTestCase {

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: name, withExtension: "pdf") else {
            throw XCTSkip("fixture \(name).pdf missing from test bundle")
        }
        return try Data(contentsOf: url)
    }

    /// One page with one run in the non-embedded Symbol font. Symbol's encoding has
    /// no Latin letters, so no edit can stay in that font.
    private func symbolFontPdf() -> Data {
        var pdf = "%PDF-1.4\n"
        var offsets: [Int] = []
        func add(_ body: String) {
            offsets.append(pdf.utf8.count)
            pdf += "\(offsets.count) 0 obj\n\(body)\nendobj\n"
        }
        let content = "BT /F1 24 Tf 72 700 Td (abgd) Tj ET"
        add("<< /Type /Catalog /Pages 2 0 R >>")
        add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
        add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>")
        add("<< /Type /Font /Subtype /Type1 /BaseFont /Symbol >>")
        add("<< /Length \(content.utf8.count) >>\nstream\n\(content)\nendstream")
        let xref = pdf.utf8.count
        pdf += "xref\n0 \(offsets.count + 1)\n0000000000 65535 f \n"
        for offset in offsets { pdf += String(format: "%010d 00000 n \n", offset) }
        pdf += "trailer\n<< /Size \(offsets.count + 1) /Root 1 0 R >>\nstartxref\n\(xref)\n%%EOF\n"
        return Data(pdf.utf8)
    }

    func testLinesComeBackTopToBottom() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        defer { Task { await engine.close(doc) } }

        let lines = try await engine.textLines(doc, pageIndex: 0)
        XCTAssertEqual(lines.count, 2)
        XCTAssertEqual(lines.first?.text, "MegaPDF engine fixture - page 1")
        XCTAssertGreaterThan(lines[0].rect.top, lines[1].rect.top, "lines are ordered top to bottom")
    }

    func testAnEditTheRunsFontCanCarryStaysInThatFontAndUndoesExactly() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        defer { Task { await engine.close(doc) } }

        let initial = try await engine.textLines(doc, pageIndex: 0)
        let run = try XCTUnwrap(initial.first?.runs.first)
        let (outcome, original) = try await engine.setText(doc, pageIndex: 0, objectIndex: run.objectIndex,
                                                           text: "Symbols beyond original: XYZQ!?")
        XCTAssertEqual(outcome, .inPlace)
        let edited = try await engine.textLines(doc, pageIndex: 0)
        XCTAssertEqual(edited.first?.runs.first?.text, "Symbols beyond original: XYZQ!?")
        XCTAssertEqual(edited.first?.runs.first?.fontName, run.fontName, "tier 1 keeps the run's own font")

        try await engine.restoreOriginal(doc, pageIndex: 0, original, objectIndex: run.objectIndex)
        let back = try await engine.textLines(doc, pageIndex: 0)
        XCTAssertEqual(back.first?.runs.first, run, "undo puts the original run back exactly")
    }

    func testAFontThatCannotCarryTheTextIsSubstitutedAndUndoBringsTheOriginalBack() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(symbolFontPdf())
        defer { Task { await engine.close(doc) } }

        let initial = try await engine.textLines(doc, pageIndex: 0)
        let run = try XCTUnwrap(initial.first?.runs.first)
        let (outcome, original) = try await engine.setText(doc, pageIndex: 0, objectIndex: run.objectIndex, text: "Hello")
        XCTAssertEqual(outcome, .substituted, "Symbol cannot draw Latin text, so a standard face stands in")
        let substituted = try await engine.textLines(doc, pageIndex: 0)
        XCTAssertEqual(substituted.first?.runs.first?.text, "Hello")

        try await engine.restoreOriginal(doc, pageIndex: 0, original, objectIndex: run.objectIndex)
        let back = try await engine.textLines(doc, pageIndex: 0)
        XCTAssertEqual(back.first?.runs.first, run, "undo must bring back the Symbol run itself, not its text in another font")
    }

    func testAnEditSurvivesSaveAndReopen() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        let initial = try await engine.textLines(doc, pageIndex: 0)
        let run = try XCTUnwrap(initial.first?.runs.first)
        _ = try await engine.setText(doc, pageIndex: 0, objectIndex: run.objectIndex, text: "Retyped heading")
        let saved = try await engine.save(doc)
        await engine.close(doc)

        let reopened = try await engine.open(saved)
        defer { Task { await engine.close(reopened) } }
        let lines = try await engine.textLines(reopened, pageIndex: 0)
        XCTAssertEqual(lines.first?.text, "Retyped heading")
    }
}
