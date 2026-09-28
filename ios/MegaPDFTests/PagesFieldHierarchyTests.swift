import XCTest
@testable import MegaPDF

/// #469: the Swift-layer counterpart of core's own field-hierarchy coverage
/// (core/tests/core_tests.cpp's test_page_tools, #452/#463) -- proves that
/// MEGAPDF_PDFIUM_PATCHES actually reaches megapdf_core.cpp on iOS (ios/project.yml,
/// ios/scripts/fetch-pdfium.sh), not just that the app links a patched PDFium. Before
/// #469, iOS defined nothing, so megapdf_core.cpp's `#ifndef MEGAPDF_PDFIUM_PATCHES`
/// default of 0 applied and megapdf_pages_extract refused this fixture with
/// MEGAPDF_ERR_FIELDS regardless of the linked PDFium's own patch level (33, this
/// release, tools/pdfium/patches/0033).
final class PagesFieldHierarchyTests: XCTestCase {

    /// core/tests/core_tests.cpp's `parent_fields_pdf()`: a one-page form whose two text
    /// widgets ("first", "last") are kids of a parent field "person" -- the top-level name
    /// lives on the parent dictionary, not on the widgets, which is the shape a page copy
    /// could not carry before PDFium patch 0033.
    private func parentFieldsPdf() -> Data {
        var pdf = "%PDF-1.4\n"
        var offsets: [Int] = []
        func add(_ body: String) {
            offsets.append(pdf.utf8.count)
            pdf += "\(offsets.count) 0 obj\n\(body)\nendobj\n"
        }
        func stream(_ dict: String, _ body: String) -> String {
            "<< \(dict) /Length \(body.utf8.count) >>\nstream\n\(body)\nendstream"
        }
        add("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 4 0 R >> >> >> >>")
        add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
        add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [7 0 R 8 0 R] >>")
        add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
        add(stream("", "BT /F1 14 Tf 72 720 Td (Two fields under one parent) Tj ET"))
        add("<< /FT /Tx /T (person) /Kids [7 0 R 8 0 R] >>")
        add("<< /Type /Annot /Subtype /Widget /Parent 6 0 R /T (first) /V (Ada) /DA (/Helv 12 Tf 0 g) /Rect [100 600 300 620] /F 4 /P 3 0 R /AP << /N 9 0 R >> >>")
        add("<< /Type /Annot /Subtype /Widget /Parent 6 0 R /T (last) /V (Lovelace) /DA (/Helv 12 Tf 0 g) /Rect [100 560 300 580] /F 4 /P 3 0 R /AP << /N 10 0 R >> >>")
        add(stream("/Type /XObject /Subtype /Form /BBox [0 0 200 20] /Resources << /Font << /Helv 4 0 R >> >>",
                    "0.13 G 1 w 0.5 0.5 199 19 re S BT /Helv 12 Tf 0 g 2 5 Td (Ada) Tj ET"))
        add(stream("/Type /XObject /Subtype /Form /BBox [0 0 200 20] /Resources << /Font << /Helv 4 0 R >> >>",
                    "0.13 G 1 w 0.5 0.5 199 19 re S BT /Helv 12 Tf 0 g 2 5 Td (Lovelace) Tj ET"))
        let xref = pdf.utf8.count
        pdf += "xref\n0 \(offsets.count + 1)\n0000000000 65535 f \n"
        for offset in offsets { pdf += String(format: "%010d 00000 n \n", offset) }
        pdf += "trailer\n<< /Size \(offsets.count + 1) /Root 1 0 R >>\nstartxref\n\(xref)\n%%EOF\n"
        return Data(pdf.utf8)
    }

    /// #469: with MEGAPDF_PDFIUM_PATCHES correctly wired to the fetched release's own
    /// patch count, extracting a page whose fields sit in a /Parent hierarchy succeeds
    /// -- rather than refusing with MEGAPDF_ERR_FIELDS -- and the extracted field keeps
    /// its qualified name and value. The same case core/tests/core_tests.cpp proves for
    /// the desktop and Android builds ("pages: extracting a page with fields in a
    /// hierarchy now succeeds").
    func testExtractingAHierarchicalFieldPageSucceeds() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(parentFieldsPdf())
        defer { Task { await engine.close(doc) } }

        let before = try await engine.fieldNames(doc, pageIndex: 0)
        XCTAssertEqual(before, ["person.first", "person.last"], "the fixture's own names")

        let out = FileManager.default.temporaryDirectory
            .appendingPathComponent("pages-extract-hierarchy-\(UUID().uuidString).pdf")
        defer { try? FileManager.default.removeItem(at: out) }

        let status = await engine.extractPages(doc, to: out)
        XCTAssertEqual(status, MEGAPDF_OK, "extracting the hierarchical-field page should succeed, not refuse")
        XCTAssertTrue(FileManager.default.fileExists(atPath: out.path), "and the file should have been written")

        let extracted = try await engine.open(file: out)
        defer { Task { await engine.close(extracted) } }
        let count = await engine.pageCount(extracted)
        XCTAssertEqual(count, 1, "the extract opens with its one page")

        let names = try await engine.fieldNames(extracted, pageIndex: 0)
        XCTAssertEqual(names, ["person.first", "person.last"], "the extracted page's fields keep their names")
    }

    /// The negative control this build must never hit: a stock (unpatched) PDFium, or a
    /// build where MEGAPDF_PDFIUM_PATCHES was never wired in (#469's actual bug), refuses
    /// the whole page rather than writing a file that silently dropped the hierarchy.
    func testAFieldHierarchyIsNeverSilentlyDropped() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(parentFieldsPdf())
        defer { Task { await engine.close(doc) } }

        let out = FileManager.default.temporaryDirectory
            .appendingPathComponent("pages-extract-hierarchy-control-\(UUID().uuidString).pdf")
        defer { try? FileManager.default.removeItem(at: out) }

        let status = await engine.extractPages(doc, to: out)
        XCTAssertTrue(status == MEGAPDF_OK || status == MEGAPDF_ERR_FIELDS,
                      "extract must either succeed cleanly or refuse whole -- never anything else")
        if status == MEGAPDF_ERR_FIELDS {
            XCTAssertFalse(FileManager.default.fileExists(atPath: out.path), "a refusal writes nothing")
        }
    }
}
