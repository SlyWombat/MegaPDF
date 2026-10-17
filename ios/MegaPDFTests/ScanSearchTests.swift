import UIKit
import XCTest
@testable import MegaPDF

/// Find on a scanned page (#676), iOS phase 1: through the real view model with the recogniser
/// scripted, and then once through the real Vision recogniser on a page drawn for the purpose.
@MainActor
final class ScanSearchTests: XCTestCase {

    // MARK: - a document with no text on any page, opened the way the app opens one

    private func scanPDF(pageSizes: [CGSize]) -> Data {
        var pdf = "%PDF-1.4\n"
        var offsets: [Int] = []
        func add(_ body: String) {
            offsets.append(pdf.utf8.count)
            pdf += "\(offsets.count) 0 obj\n\(body)\nendobj\n"
        }
        let kids = (0..<pageSizes.count).map { "\(3 + $0 * 2) 0 R" }.joined(separator: " ")
        add("<< /Type /Catalog /Pages 2 0 R >>")
        add("<< /Type /Pages /Kids [\(kids)] /Count \(pageSizes.count) >>")
        for (index, size) in pageSizes.enumerated() {
            add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 \(Int(size.width)) \(Int(size.height))] "
                + "/Contents \(4 + index * 2) 0 R >>")
            // A filled rectangle and no text object: a page with something on it and no text layer.
            let content = "0.9 g 20 20 100 100 re f"
            add("<< /Length \(content.utf8.count) >>\nstream\n\(content)\nendstream")
        }
        let xref = pdf.utf8.count
        pdf += "xref\n0 \(offsets.count + 1)\n0000000000 65535 f \n"
        for offset in offsets { pdf += String(format: "%010d 00000 n \n", offset) }
        pdf += "trailer\n<< /Size \(offsets.count + 1) /Root 1 0 R >>\nstartxref\n\(xref)\n%%EOF\n"
        return Data(pdf.utf8)
    }

    private let sizes = [CGSize(width: 300, height: 400), CGSize(width: 400, height: 420),
                         CGSize(width: 500, height: 440), CGSize(width: 600, height: 460)]

    private func openModel() async throws -> (ViewerModel, URL) {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("scan-search-\(UUID().uuidString).pdf")
        try scanPDF(pageSizes: sizes).write(to: url)
        let model = ViewerModel()
        model.openPicked(url: url)
        try await ModelOpening.waitForTheDocument(model)
        return (model, url)
    }

    private func waitUntil(_ what: String, timeout: TimeInterval = 15,
                           _ condition: @MainActor () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition() {
            if Date() > deadline { XCTFail("timed out waiting for: \(what)"); throw CancellationError() }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
    }

    /// Counts what the model asks the recogniser to read, and answers "invoice N" for page N.
    private final class ScriptedReader {
        var reads: [Int] = []
        func read(_ index: Int) -> OcrPage {
            reads.append(index)
            let text = "invoice \(index + 1)"
            return OcrPage(lines: [OcrLine(text: text, words: [
                OcrWord(range: 0..<7, rect: PdfRect(left: 10, bottom: 10, right: 70, top: 30)),
                OcrWord(range: 8..<9, rect: PdfRect(left: 80, bottom: 10, right: 90, top: 30))])])
        }
    }

    private func scripted(_ model: ViewerModel) -> ScriptedReader {
        let reader = ScriptedReader()
        model.pageHasText = { _, _ in false }
        model.recognisePage = { _, index in reader.read(index) }
        return reader
    }

    // MARK: - through the model

    func testAScanIsReadOnceAndFoundFromWhatWasReadAfterwards() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }
        let reader = scripted(model)

        model.search(term: "invoice", debounce: false)
        try await waitUntil("every page is found") { model.searchMatches.count == 4 && !model.isSearching }
        XCTAssertTrue(model.searchMatches.allSatisfy(\.fromScan), "and each says it was read, not written")
        XCTAssertEqual(reader.reads.sorted(), [0, 1, 2, 3])

        model.search(term: "invoice 3", debounce: false)
        try await waitUntil("a narrower term") { model.searchMatches.count == 1 && !model.isSearching }
        XCTAssertEqual(model.searchMatches.first?.pageIndex, 2)
        XCTAssertEqual(reader.reads.count, 4, "the second search read nothing: it used the cache")
    }

    func testAPageWithATextLayerIsNeverRead() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }
        let reader = scripted(model)
        model.pageHasText = { _, _ in true }

        model.search(term: "invoice", debounce: false)
        try await waitUntil("the scan finishes") { !model.isSearching && model.busy.strip == nil }
        XCTAssertTrue(reader.reads.isEmpty, "Find already works on a page with a text layer")
        XCTAssertTrue(model.searchMatches.isEmpty)
    }

    func testWhatWasReadFollowsAMoveAndIsNotReadAgain() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }
        let reader = scripted(model)
        model.search(term: "invoice", debounce: false)
        try await waitUntil("every page is read") { model.searchMatches.count == 4 && !model.isSearching }

        model.movePage(from: 0, to: 2)
        try await waitUntil("the move lands") { model.pageSizes.map { Int($0.width) } == [400, 500, 300, 600] }

        model.search(term: "invoice 1", debounce: false)
        try await waitUntil("the hit is found") { model.searchMatches.count == 1 && !model.isSearching }
        XCTAssertEqual(model.searchMatches.first?.pageIndex, 2,
                       "invoice 1 went with its page; text left behind would be on the wrong one")
        XCTAssertEqual(reader.reads.count, 4, "a move reads nothing again")
    }

    func testADeletedPageIsGoneAndTheRestAreNotReadAgain() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }
        let reader = scripted(model)
        model.search(term: "invoice", debounce: false)
        try await waitUntil("every page is read") { model.searchMatches.count == 4 && !model.isSearching }

        model.deletePages([1])
        try await waitUntil("the delete lands") { model.pageSizes.count == 3 }
        model.search(term: "invoice 2", debounce: false)
        try await waitUntil("the search ends") { !model.isSearching }
        XCTAssertTrue(model.searchMatches.isEmpty, "page 2 left with its text")
        model.search(term: "invoice 3", debounce: false)
        try await waitUntil("page 3's text is found where it now stands") {
            model.searchMatches.first?.pageIndex == 1
        }
        XCTAssertEqual(reader.reads.count, 4)
    }

    func testNothingReadIsKeptOnceTheDocumentIsClosed() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }
        _ = scripted(model)
        model.search(term: "invoice", debounce: false)
        try await waitUntil("every page is read") { model.searchMatches.count == 4 && !model.isSearching }
        XCTAssertFalse(model.ocrCache.isEmpty)

        model.close()
        XCTAssertTrue(model.ocrCache.isEmpty, "recognised text lives and dies with the open document")
    }

    // MARK: - the real recogniser

    /// A page that is only a picture of text, which is what a scan is: drawn at a known place so
    /// the hit's rectangle can be checked against where the words really are.
    private func scannedPage() -> Data {
        let page = CGRect(x: 0, y: 0, width: 612, height: 792)
        let image = UIGraphicsImageRenderer(size: page.size).image { context in
            UIColor.white.setFill()
            context.fill(page)
            let attributes: [NSAttributedString.Key: Any] = [
                .font: UIFont.systemFont(ofSize: 44, weight: .regular), .foregroundColor: UIColor.black]
            NSString(string: "Invoice number 4821").draw(at: CGPoint(x: 72, y: 100), withAttributes: attributes)
            NSString(string: "Total due on receipt").draw(at: CGPoint(x: 72, y: 300), withAttributes: attributes)
        }
        return UIGraphicsPDFRenderer(bounds: page).pdfData { context in
            context.beginPage()
            image.draw(in: page)
        }
    }

    func testVisionReadsAPageThatIsOnlyAPictureAndSaysWhereTheWordIs() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(scannedPage())
        defer { Task { await engine.close(doc) } }

        let hasText = await engine.pageHasTextLayer(doc, index: 0)
        XCTAssertFalse(hasText, "a picture of text has no text layer: that is the trigger")
        let direct = try await engine.search(doc, pageIndex: 0, term: "invoice")
        XCTAssertTrue(direct.isEmpty, "the engine's own search finds nothing on a scan")

        let recognised = await engine.recognise(doc, index: 0)
        let read = try XCTUnwrap(recognised)
        let hits = read.matches(of: "invoice")
        XCTAssertEqual(hits.count, 1, "the words read were: \(read.lines.map(\.text))")
        let rect = try XCTUnwrap(hits.first?.rects.first)
        // Drawn at x=72, 100 points from the top of a 792-point page, 44 pt type: the word's top
        // edge is near 792 - 100 = 692 in PDF space, and it starts near x = 72.
        XCTAssertEqual(rect.left, 72, accuracy: 20)
        XCTAssertEqual(rect.top, 692, accuracy: 30)
        XCTAssertGreaterThan(rect.right - rect.left, 100, "wider than a letter, narrower than the page")
        XCTAssertLessThan(rect.right - rect.left, 300)
    }
}
