import XCTest
@testable import MegaPDF

/// #710: contract 11 (text selection) through the Swift binding, on the core's own
/// `structure/selection.pdf` (tools/gen_selection_fixture.py; a committed copy sits beside these
/// tests): page 1 a title over two columns, page 2 ligatures and line-end hyphens. The core's
/// suite holds the rules; this holds the binding to them, the selection value logic the handles
/// use, and the copy gate.
final class TextSelectionTests: XCTestCase {

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: name, withExtension: "pdf") else {
            XCTFail("fixture \(name).pdf missing from test bundle")
            throw XCTSkip("fixture \(name).pdf missing")
        }
        return try Data(contentsOf: url)
    }

    private func selectable(page: Int) async throws -> (PdfSelectableText, PdfDocument) {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("selection"))
        return (try await engine.loadSelectable(doc, pageIndex: page), doc)
    }

    private func centre(_ r: PdfRect) -> (x: Double, y: Double) { (r.centerX, r.centerY) }

    func testTheWholePageReadsDownEachColumnInTurn() async throws {
        let (text, doc) = try await selectable(page: 0)
        defer { Task { await PdfEngine.shared.close(doc) } }
        XCTAssertEqual(
            text.text(from: 0, to: text.length),
            "Selection Columns\n" +
            "Left one alpha\nLeft two bravo and more\nLeft three charlie\nLeft four delta\nLeft five echo\n" +
            "Right one golf\nRight two hotel\nRight three india\nRight four juliet\nRight five kilo")
    }

    func testADragAcrossTheColumnsFollowsThem() async throws {
        let (text, doc) = try await selectable(page: 0)
        defer { Task { await PdfEngine.shared.close(doc) } }
        let lines = text.rects(from: 0, to: text.length)
        XCTAssertEqual(lines.count, 11)
        // Crop space, bottom-left origin: the title is the topmost line, so it has the largest bottom.
        XCTAssertGreaterThan(lines[0].bottom, lines[1].bottom)
        XCTAssertEqual(lines[1].left, 72, accuracy: 1.5)
        XCTAssertEqual(lines[6].left, 330, accuracy: 1.5)   // "Right one golf"

        let left2 = lines[2], right2 = lines[7]
        let anchor = try XCTUnwrap(text.caret(atX: left2.left + 0.5, y: centre(left2).y, tolerance: 4))
        let focus = try XCTUnwrap(text.caret(atX: right2.right + 30, y: centre(right2).y))
        XCTAssertEqual(text.text(from: anchor, to: focus),
                       "Left two bravo and more\nLeft three charlie\nLeft four delta\nLeft five echo\n" +
                       "Right one golf\nRight two hotel")
        // Either order reads the same, and draws one rectangle per line.
        XCTAssertEqual(text.text(from: focus, to: anchor), text.text(from: anchor, to: focus))
        XCTAssertEqual(text.rects(from: focus, to: anchor).count, 6)
    }

    func testAWordALineAndNothing() async throws {
        let (text, doc) = try await selectable(page: 0)
        defer { Task { await PdfEngine.shared.close(doc) } }
        let lines = text.rects(from: 0, to: text.length)

        let line = try XCTUnwrap(text.line(atX: centre(lines[3]).x, y: centre(lines[3]).y))
        XCTAssertEqual(text.text(from: line.lowerBound, to: line.upperBound), "Left three charlie")

        let word = try XCTUnwrap(text.word(atX: lines[3].right - 2, y: centre(lines[3]).y))
        XCTAssertEqual(text.text(from: word.lowerBound, to: word.upperBound), "charlie")

        // Far from any text: nothing within a press's tolerance, though a handle drag still has a caret.
        XCTAssertNil(text.caret(atX: 300, y: 190, tolerance: 4))
        XCTAssertNil(text.character(atX: 300, y: 190, tolerance: 4))
        XCTAssertNil(text.word(atX: 300, y: 190))
        XCTAssertNotNil(text.caret(atX: 300, y: 190))
    }

    func testLigaturesExpandAndLineEndHyphensAreKept() async throws {
        let (text, doc) = try await selectable(page: 1)
        defer { Task { await PdfEngine.shared.close(doc) } }
        XCTAssertEqual(text.text(from: 0, to: text.length),
                       "The final flow test\nWe test hyphen-\nation here.\nA well-\nknown word.")
    }

    func testASnapshotOutlivesItsDocument() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("selection"))
        let text = try await engine.loadSelectable(doc, pageIndex: 0)
        await engine.close(doc)
        XCTAssertGreaterThan(text.length, 0)
        XCTAssertTrue(text.text(from: 0, to: text.length).hasPrefix("Selection Columns"))
        // A closed document refuses a new load rather than crashing.
        do { _ = try await engine.loadSelectable(doc, pageIndex: 0); XCTFail("loaded a closed document") }
        catch { /* expected */ }
    }

    // MARK: the selection value the handles move

    func testDraggingTheEndHandleKeepsTheStart() {
        var s = PageTextSelection(pageIndex: 0, word: 10..<15)
        s.drag(.end, to: 30, length: 100)
        XCTAssertEqual(s.start, 10); XCTAssertEqual(s.end, 30)
    }

    func testDraggingTheStartHandlePastTheEndFlipsInsteadOfCollapsing() {
        var s = PageTextSelection(pageIndex: 0, word: 10..<15)
        s.drag(.start, to: 40, length: 100)
        XCTAssertEqual(s.start, 15); XCTAssertEqual(s.end, 40)
        // The far end stays where it was dragged to; the start handle can come back past it.
        s.drag(.start, to: 2, length: 100)
        XCTAssertEqual(s.start, 2); XCTAssertEqual(s.end, 40)
    }

    func testACaretIsClampedToThePage() {
        var s = PageTextSelection(pageIndex: 0, word: 10..<15)
        s.drag(.end, to: 500, length: 100)
        XCTAssertEqual(s.end, 100)
        s.drag(.start, to: -4, length: 100)
        XCTAssertEqual(s.start, 0)
    }

    func testHandlesSitOnTheFirstAndLastLine() {
        let rects = [PdfRect(left: 72, bottom: 660, right: 200, top: 672),
                     PdfRect(left: 72, bottom: 640, right: 150, top: 652)]
        let handles = SelectionGeometry.handlePoints(for: rects)
        XCTAssertEqual(handles?.start, PdfPoint(x: 72, y: 672))
        XCTAssertEqual(handles?.end, PdfPoint(x: 150, y: 640))
        XCTAssertEqual(SelectionGeometry.bounds(of: rects),
                       PdfRect(left: 72, bottom: 640, right: 200, top: 672))
        XCTAssertNil(SelectionGeometry.bounds(of: []))
    }

    // MARK: the copy gate

    func testACopyForbiddingDocumentStillSelectsButCannotCopy() {
        let noCopy = PdfSecurity(isEncrypted: true, revision: 3,
                                 permissions: PdfPermissions.all.subtracting(.copy), hasFullAccess: false)
        XCTAssertFalse(DocumentCapabilities(security: noCopy).canCopyText)
        // The owner's open may do anything, whatever the bits say.
        let owner = PdfSecurity(isEncrypted: true, revision: 3,
                                permissions: PdfPermissions.all.subtracting(.copy), hasFullAccess: true)
        XCTAssertTrue(DocumentCapabilities(security: owner).canCopyText)
        XCTAssertTrue(DocumentCapabilities(security: .unprotected).canCopyText)
    }
}
