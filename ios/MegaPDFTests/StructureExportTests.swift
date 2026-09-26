import XCTest
@testable import MegaPDF

/// The #386 Save-As-Markdown binding (`PdfEngine+Structure.swift`): `megapdf_write_text` over
/// contract 9, checked against the SAME goldens core_tests.cpp's `test_write_markdown_golden`/
/// `test_write_text_golden` compare the native writer to (core/tests/expected/structure/*,
/// #353/#357) -- byte-identical output is directly checkable because both call the core with
/// plain default options (`megapdf_write_options opt{}`, core_tests.cpp's own comment on why:
/// keep_lines' line-break decisions are close to the .blocks goldens' documented cross-platform
/// bounds tolerance, so every golden here deliberately leaves it off). `PdfEngine.writeText`'s
/// own defaults are exactly that zero-initialised struct (see PdfTextWriteOptions), so no
/// options need overriding here to match.
///
/// Fixtures are copied, not symlinked, from tools/gen_structure_fixtures.py's output
/// (tests/MegaPDF.Core.Tests/Fixtures/structure) and its goldens (core/tests/expected/structure),
/// prefixed `structure-` to keep them apart from this bundle's other, unrelated fixtures.
final class StructureExportTests: XCTestCase {

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: "structure-\(name)", withExtension: "pdf") else {
            throw XCTSkip("fixture structure-\(name).pdf missing from test bundle")
        }
        return try Data(contentsOf: url)
    }

    private func golden(_ name: String, _ ext: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: "structure-\(name)", withExtension: ext) else {
            throw XCTSkip("golden structure-\(name).\(ext) missing from test bundle")
        }
        return try Data(contentsOf: url)
    }

    /// Every fixture this file carries a golden pair for.
    private static let cases = ["headings", "lists", "furniture", "tabular-headings"]

    func testMarkdownMatchesGoldenByteForByte() async throws {
        let engine = PdfEngine.shared
        for name in Self.cases {
            let doc = try await engine.open(try fixture(name))
            let written = try await engine.writeText(doc, format: .markdown)
            await engine.close(doc)
            let expected = try golden(name, "md")
            XCTAssertEqual(written, expected, "\(name).md did not match its golden byte-for-byte")
        }
    }

    func testTextMatchesGoldenByteForByte() async throws {
        let engine = PdfEngine.shared
        for name in Self.cases {
            let doc = try await engine.open(try fixture(name))
            let written = try await engine.writeText(doc, format: .text)
            await engine.close(doc)
            let expected = try golden(name, "txt")
            XCTAssertEqual(written, expected, "\(name).txt did not match its golden byte-for-byte")
        }
    }

    /// `furniture.pdf` has a running header/footer (design §7's list); the default options
    /// (`keepFurniture == false`) must suppress it exactly as the CLI's own default does --
    /// the golden itself is the check, but this spells out WHY it is short.
    func testFurnitureIsSuppressedByDefault() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("furniture"))
        let written = try await engine.writeText(doc, format: .markdown)
        await engine.close(doc)
        let text = String(decoding: written, as: UTF8.self)
        XCTAssertFalse(text.isEmpty)
        XCTAssertEqual(text, String(decoding: try golden("furniture", "md"), as: UTF8.self))
    }

    /// `megapdf_structure_load`/`_free`/the block accessors (#386's other half of contract 9):
    /// `headings.pdf` must infer at least one HEADING block (MEGAPDF_BLOCK_HEADING == 1), with
    /// non-empty text, and the handle must free cleanly.
    func testLoadStructureFindsHeadingBlocks() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("headings"))
        let pageCount = await engine.pageCount(doc)
        let structure = try await engine.loadStructure(doc, firstPage: 0, pageCount: pageCount)
        let count = await engine.blockCount(structure)
        XCTAssertGreaterThan(count, 0, "expected at least one block")

        var sawHeading = false
        for i in 0..<count {
            guard let block = await engine.block(structure, at: i) else { continue }
            if block.kind == 1 {   // MEGAPDF_BLOCK_HEADING
                let text = await engine.blockText(structure, at: i)
                XCTAssertFalse(text.isEmpty, "a heading block with no text")
                sawHeading = true
            }
        }
        XCTAssertTrue(sawHeading, "headings.pdf produced no HEADING block")

        await engine.freeStructure(structure)
        await engine.close(doc)
    }
}
