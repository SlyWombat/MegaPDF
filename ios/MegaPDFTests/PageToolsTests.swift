import CoreGraphics
import XCTest
@testable import MegaPDF

/// The page tools against a real document (#174, core contract 10): the binding, the five
/// reversible operations, the two engine limits, and the undo ordering #429 and #441 were both
/// about.
///
/// Every document here is built in this file rather than taken from the fixtures directory, so
/// each test says what it is standing on. The pages are deliberately **different sizes**, because
/// that is the only thing about a page this layer can see: a reorder is only observable if the
/// pages can be told apart, and a rotation is observable as a swapped width and height.
///
/// Every engine read is hoisted into a `let` before it is asserted on. `XCTAssertEqual` takes its
/// arguments as autoclosures, which cannot carry an `await`, so an assertion with an engine call
/// inside it does not compile — worth stating, because the shape reads like it should.
@MainActor
final class PageToolsTests: XCTestCase {

    // MARK: - documents

    /// A PDF whose pages are the sizes given, each carrying a line of text.
    ///
    /// `spacedPages` get 4 pt of character spacing, which is the shape of content PDFium's writer
    /// declines to regenerate (#118/#128) — the page the layout guard exists for, used below to
    /// show that the guard bites for a text edit and cannot bite for a page operation.
    private func pdf(pageSizes: [CGSize], spacedPages: Set<Int> = [],
                     fieldNamed field: String? = nil) -> Data {
        var pdf = "%PDF-1.4\n"
        var offsets: [Int] = []
        func add(_ body: String) {
            offsets.append(pdf.utf8.count)
            pdf += "\(offsets.count) 0 obj\n\(body)\nendobj\n"
        }
        func stream(_ body: String) -> String {
            "<< /Length \(body.utf8.count) >>\nstream\n\(body)\nendstream"
        }
        let pageCount = pageSizes.count
        // 1 catalog, 2 pages, 3 font, then two objects per page, then (optionally) a widget.
        let kids = (0..<pageCount).map { "\(4 + $0 * 2) 0 R" }.joined(separator: " ")
        let widgetObject = 4 + pageCount * 2
        let acroForm = field == nil ? ""
            : " /AcroForm << /Fields [\(widgetObject) 0 R] /DA (/Helv 0 Tf 0 g) "
              + "/DR << /Font << /Helv 3 0 R >> >> >>"
        add("<< /Type /Catalog /Pages 2 0 R\(acroForm) >>")
        add("<< /Type /Pages /Kids [\(kids)] /Count \(pageCount) >>")
        add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
        for (index, size) in pageSizes.enumerated() {
            let annots = (field != nil && index == 0) ? " /Annots [\(widgetObject) 0 R]" : ""
            add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 \(Int(size.width)) \(Int(size.height))] "
                + "/Resources << /Font << /F1 3 0 R >> >> /Contents \(5 + index * 2) 0 R\(annots) >>")
            let spacing = spacedPages.contains(index) ? "4 Tc " : ""
            add(stream("BT /F1 18 Tf \(spacing)40 \(Int(size.height) - 60) Td (Page \(index + 1)) Tj ET"))
        }
        if let field {
            add("<< /Type /Annot /Subtype /Widget /FT /Tx /T (\(field)) /V (here) "
                + "/DA (/Helv 12 Tf 0 g) /Rect [40 40 240 60] /F 4 /P 4 0 R >>")
        }
        let xref = pdf.utf8.count
        pdf += "xref\n0 \(offsets.count + 1)\n0000000000 65535 f \n"
        for offset in offsets { pdf += String(format: "%010d 00000 n \n", offset) }
        pdf += "trailer\n<< /Size \(offsets.count + 1) /Root 1 0 R >>\nstartxref\n\(xref)\n%%EOF\n"
        return Data(pdf.utf8)
    }

    /// A one-page form whose two text widgets take their name from a **parent** field
    /// ("person"), which is how LiveCycle and most authoring tools write forms — the shape a
    /// page copy could not carry before PDFium patch 0033, and which it still cannot *rename*,
    /// because the name is not on the widget.
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
        add("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] /DA (/Helv 0 Tf 0 g) "
            + "/DR << /Font << /Helv 4 0 R >> >> >> >>")
        add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
        add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [7 0 R 8 0 R] >>")
        add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
        add(stream("", "BT /F1 14 Tf 72 720 Td (Two fields under one parent) Tj ET"))
        add("<< /FT /Tx /T (person) /Kids [7 0 R 8 0 R] >>")
        add("<< /Type /Annot /Subtype /Widget /Parent 6 0 R /T (first) /V (Ada) "
            + "/DA (/Helv 12 Tf 0 g) /Rect [100 600 300 620] /F 4 /P 3 0 R /AP << /N 9 0 R >> >>")
        add("<< /Type /Annot /Subtype /Widget /Parent 6 0 R /T (last) /V (Lovelace) "
            + "/DA (/Helv 12 Tf 0 g) /Rect [100 560 300 580] /F 4 /P 3 0 R /AP << /N 10 0 R >> >>")
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

    /// The four-page document most of these run on: every page a different width, so the order is
    /// readable off the sizes alone.
    private let fourSizes = [CGSize(width: 300, height: 400),
                             CGSize(width: 400, height: 400),
                             CGSize(width: 500, height: 400),
                             CGSize(width: 600, height: 400)]

    private func widths(_ engine: PdfEngine, _ doc: PdfDocument) async throws -> [Int] {
        var out: [Int] = []
        let count = await engine.pageCount(doc)
        for index in 0..<count {
            out.append(Int(try await engine.pageSize(doc, index: index).width))
        }
        return out
    }

    private func staged(_ name: String) -> URL {
        FileManager.default.temporaryDirectory
            .appendingPathComponent("pagetools-\(name)-\(UUID().uuidString).pdf")
    }

    // MARK: - rotate

    func testRotatingAPageSetsItsRotationAndUndoPutsItBack() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(pdf(pageSizes: fourSizes))
        defer { Task { await engine.close(doc) } }
        let history = EditHistory()

        let before = try await engine.pageRotation(doc, pageIndex: 1)
        XCTAssertEqual(before, 0)

        try await history.perform(RotatePagesOperation(pages: [1], quarterTurns: 1), engine, doc)
        let after = try await engine.pageRotation(doc, pageIndex: 1)
        XCTAssertEqual(after, 1)

        _ = try await history.undo(engine, doc)
        let undone = try await engine.pageRotation(doc, pageIndex: 1)
        XCTAssertEqual(undone, 0, "undo turns it back")

        _ = try await history.redo(engine, doc)
        let redone = try await engine.pageRotation(doc, pageIndex: 1)
        XCTAssertEqual(redone, 1)
    }

    /// The size the app lays out from follows the rotation (#439, contract 10's Coordinates) — the
    /// one page change whose shifts are empty and whose sizes are all wrong afterwards.
    func testATurnedPageReportsTheRotatedSize() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(pdf(pageSizes: [CGSize(width: 800, height: 200)]))
        defer { Task { await engine.close(doc) } }

        try await engine.rotatePage(doc, pageIndex: 0, quarterTurns: 1)
        let rotated = try await engine.pageSize(doc, index: 0)
        XCTAssertEqual(Int(rotated.width), 200, "a turned landscape page reports portrait")
        XCTAssertEqual(Int(rotated.height), 800)

        try await engine.rotatePage(doc, pageIndex: 0, quarterTurns: -1)
        let back = try await engine.pageSize(doc, index: 0)
        XCTAssertEqual(Int(back.width), 800)
    }

    /// "Turn these three pages" is one press of Undo, not three.
    func testTurningASelectionIsOneUndoStep() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(pdf(pageSizes: fourSizes))
        defer { Task { await engine.close(doc) } }
        let history = EditHistory()

        try await history.perform(RotatePagesOperation(pages: [0, 1, 2], quarterTurns: -1), engine, doc)
        for page in 0...2 {
            let rotation = try await engine.pageRotation(doc, pageIndex: page)
            XCTAssertEqual(rotation, 3, "one quarter turn anticlockwise is /Rotate 3")
        }
        let untouched = try await engine.pageRotation(doc, pageIndex: 3)
        XCTAssertEqual(untouched, 0, "page 4 was not asked")

        _ = try await history.undo(engine, doc)
        XCTAssertFalse(history.canUndo, "one operation, one step")
        for page in 0...2 {
            let rotation = try await engine.pageRotation(doc, pageIndex: page)
            XCTAssertEqual(rotation, 0)
        }
    }

    // MARK: - delete, and the page the core keeps

    func testDeletingPagesTakesExactlyThoseAndUndoPutsThePagesThemselvesBack() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(pdf(pageSizes: fourSizes))
        defer { Task { await engine.close(doc) } }
        let history = EditHistory()

        let operation = DeletePagesOperation(pages: [1, 3])
        try await history.perform(operation, engine, doc)
        let afterDelete = try await widths(engine, doc)
        XCTAssertEqual(afterDelete, [300, 500], "pages 2 and 4 are gone and the rest close up")

        _ = try await history.undo(engine, doc)
        let afterUndo = try await widths(engine, doc)
        XCTAssertEqual(afterUndo, [300, 400, 500, 600],
                       "each page comes back at the index it was taken from, not appended")
        XCTAssertTrue(operation.heldPages.isEmpty, "the core consumed the handles")

        _ = try await history.redo(engine, doc)
        let afterRedo = try await widths(engine, doc)
        XCTAssertEqual(afterRedo, [300, 500])
        XCTAssertEqual(operation.heldPages.count, 2, "a redo takes fresh handles")
    }

    /// The page that comes back is the page itself — content and field values included — which is
    /// what `megapdf_page_restore` is for, and the difference between an undo and a blank page.
    func testTheRestoredPageIsThePageItselfNotABlankOne() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(pdf(pageSizes: [CGSize(width: 612, height: 792),
                                                       CGSize(width: 400, height: 400)],
                                            fieldNamed: "who"))
        defer { Task { await engine.close(doc) } }
        let history = EditHistory()

        let fieldsBefore = try await engine.fieldNames(doc, pageIndex: 0)
        XCTAssertEqual(fieldsBefore, ["who"])
        let linesBefore = try await engine.textLines(doc, pageIndex: 0).map(\.text)
        XCTAssertTrue(linesBefore.contains { $0.contains("Page 1") }, "the fixture's own line")

        try await history.perform(DeletePagesOperation(pages: [0]), engine, doc)
        let shrunk = await engine.pageCount(doc)
        XCTAssertEqual(shrunk, 1)

        _ = try await history.undo(engine, doc)
        let grown = await engine.pageCount(doc)
        XCTAssertEqual(grown, 2)
        let fieldsAfter = try await engine.fieldNames(doc, pageIndex: 0)
        XCTAssertEqual(fieldsAfter, ["who"], "its form field comes back with it, by name")
        let linesAfter = try await engine.textLines(doc, pageIndex: 0).map(\.text)
        XCTAssertEqual(linesAfter, linesBefore, "and its content, line for line")
    }

    func testTheLastPageMayNotBeDeletedAndSaysWhichRuleThatIs() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(pdf(pageSizes: [CGSize(width: 300, height: 400)]))
        defer { Task { await engine.close(doc) } }

        do {
            _ = try await engine.deletePage(doc, pageIndex: 0)
            XCTFail("a PDF must keep a page")
        } catch let error as PageToolError {
            XCTAssertEqual(error.refusal, .lastPage,
                           "not the generic engine refusal: this is a rule a person can act on")
        }
        let count = await engine.pageCount(doc)
        XCTAssertEqual(count, 1, "and nothing was changed")
        XCTAssertEqual(ViewerModel.sentence(for: .lastPage), "A PDF has to keep at least one page.")
    }

    /// The shape of #429 and #441: an operation naming something the engine no longer has must
    /// fail **loudly**. Both of those bugs were a history holding a stale identifier, the call
    /// quietly answering "no", and the Undo the person pressed doing nothing — which left the
    /// history one step out, so the *next* press took back something else.
    func testASpentRemovedPageHandleThrowsRatherThanQuietlyDoingNothing() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(pdf(pageSizes: fourSizes))
        defer { Task { await engine.close(doc) } }

        let removed = try await engine.deletePage(doc, pageIndex: 2)
        XCTAssertTrue(removed.isHeld)
        try await engine.restorePage(doc, removed, at: 2)
        XCTAssertFalse(removed.isHeld, "the core consumed it")

        do {
            try await engine.restorePage(doc, removed, at: 0)
            XCTFail("a spent handle must throw")
        } catch let error as PageToolError {
            XCTAssertEqual(error.refusal, .spentPage)
        }
        let order = try await widths(engine, doc)
        XCTAssertEqual(order, [300, 400, 500, 600],
                       "and the document is exactly as the first restore left it")

        // Discarding a handle the restore consumed is harmless, and so is discarding twice: the
        // undo history throws operations away without knowing what they are still holding.
        await engine.discardRemovedPage(removed)
        await engine.discardRemovedPage(removed)
    }

    /// A page the core is holding is freed when its operation leaves the history for good — the
    /// redo branch here. On a phone, a long session of deletes would otherwise keep every deleted
    /// page alive for as long as the document was open.
    func testTheHistoryReportsOperationsThatLeaveForGoodSoTheirPagesCanBeFreed() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(pdf(pageSizes: fourSizes))
        defer { Task { await engine.close(doc) } }
        let history = EditHistory()
        let dropped = DroppedBox()
        history.onDropped = { dropped.add($0) }

        let delete = DeletePagesOperation(pages: [3])
        try await history.perform(delete, engine, doc)
        _ = try await history.undo(engine, doc)
        XCTAssertEqual(delete.heldPages.count, 0, "the undo restored it, so nothing is held")

        let again = DeletePagesOperation(pages: [2])
        try await history.perform(again, engine, doc)
        XCTAssertTrue(dropped.contains(delete),
                      "a new edit threw away the redo branch, and the delete went with it")

        dropped.reset()
        history.clear()
        XCTAssertTrue(dropped.contains(again), "and clearing the history reports it too")
        XCTAssertEqual(again.heldPages.count, 1, "which is the page the caller now has to free")
        await engine.discardRemovedPage(again.heldPages[0])
    }

    /// What `EditHistory.onDropped` reported, collected without capturing a mutable local in an
    /// escaping closure.
    @MainActor
    private final class DroppedBox {
        private var operations: [PdfEditOperation] = []
        func add(_ more: [PdfEditOperation]) { operations.append(contentsOf: more) }
        func reset() { operations.removeAll() }
        func contains(_ operation: PdfEditOperation) -> Bool {
            operations.contains { $0 === operation }
        }
    }

    // MARK: - move

    func testMovingAPageReordersTheDocumentAndUndoPutsItBack() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(pdf(pageSizes: fourSizes))
        defer { Task { await engine.close(doc) } }
        let history = EditHistory()

        try await history.perform(MovePageOperation(from: 0, to: 2), engine, doc)
        let moved = try await widths(engine, doc)
        XCTAssertEqual(moved, [400, 500, 300, 600],
                       "page 1 stands third; the two it passed came back one each")

        _ = try await history.undo(engine, doc)
        let back = try await widths(engine, doc)
        XCTAssertEqual(back, [300, 400, 500, 600])
    }

    // MARK: - insert

    func testABlankPageArrivesTheSizeItWasAskedForAndUndoRemovesIt() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(pdf(pageSizes: fourSizes))
        defer { Task { await engine.close(doc) } }
        let history = EditHistory()

        try await history.perform(
            InsertBlankPageOperation(at: 2, widthPoints: 200, heightPoints: 100), engine, doc)
        let withBlank = try await widths(engine, doc)
        XCTAssertEqual(withBlank, [300, 400, 200, 500, 600])
        let inserted = try await engine.pageSize(doc, index: 2)
        XCTAssertEqual(Int(inserted.height), 100)
        let lines = try await engine.textLines(doc, pageIndex: 2)
        XCTAssertTrue(lines.isEmpty, "and it is blank")

        _ = try await history.undo(engine, doc)
        let back = try await widths(engine, doc)
        XCTAssertEqual(back, [300, 400, 500, 600])
    }

    func testAnAppendingInsertIsAllowedAtThePageCount() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(pdf(pageSizes: fourSizes))
        defer { Task { await engine.close(doc) } }
        try await engine.insertBlankPage(doc, at: 4, widthPoints: 111, heightPoints: 222)
        let appended = try await widths(engine, doc)
        XCTAssertEqual(appended, [300, 400, 500, 600, 111])
    }

    // MARK: - combine

    func testImportingPagesInsertsThemAndUndoTakesExactlyThoseOff() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(pdf(pageSizes: fourSizes))
        defer { Task { await engine.close(doc) } }
        let history = EditHistory()

        let other = staged("other")
        try pdf(pageSizes: [CGSize(width: 111, height: 400),
                            CGSize(width: 222, height: 400)]).write(to: other)
        defer { try? FileManager.default.removeItem(at: other) }

        let operation = ImportPagesOperation(path: other.path, insertAt: 1)
        try await history.perform(operation, engine, doc)
        XCTAssertEqual(operation.imported, 2)
        let combined = try await widths(engine, doc)
        XCTAssertEqual(combined, [300, 111, 222, 400, 500, 600])
        XCTAssertEqual(operation.shifts(reverted: false), [.inserted(at: 1, count: 2)])

        _ = try await history.undo(engine, doc)
        let undone = try await widths(engine, doc)
        XCTAssertEqual(undone, [300, 400, 500, 600],
                       "exactly the pages that arrived come off, and nothing else")

        _ = try await history.redo(engine, doc)
        let redone = try await widths(engine, doc)
        XCTAssertEqual(redone, [300, 111, 222, 400, 500, 600],
                       "a redo imports them again from the file they came from")
    }

    func testImportingOnlyTheChosenPagesKeepsTheOrderAsked() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(pdf(pageSizes: [CGSize(width: 300, height: 400)]))
        defer { Task { await engine.close(doc) } }

        let other = staged("pick")
        try pdf(pageSizes: [CGSize(width: 111, height: 400),
                            CGSize(width: 222, height: 400),
                            CGSize(width: 333, height: 400)]).write(to: other)
        defer { try? FileManager.default.removeItem(at: other) }

        let imported = try await engine.importPages(doc, from: other.path, pages: [2, 0], insertAt: 1)
        XCTAssertEqual(imported, 2)
        let order = try await widths(engine, doc)
        XCTAssertEqual(order, [300, 333, 111],
                       "in the order the list gave them, not the order the file has them")
    }

    /// **The engine limit that is surfaced rather than hidden.** A widget whose name lives on a
    /// parent field cannot be renamed out of the way, so an import into a document that already
    /// has a field of that name is refused **whole** — the alternative is a document whose fields
    /// quietly lost their names and values.
    func testAFieldHierarchyWhoseNameIsTakenRefusesTheWholeImportAndChangesNothing() async throws {
        let engine = PdfEngine.shared
        // This document already has a top-level field called "person".
        let doc = try await engine.open(pdf(pageSizes: [CGSize(width: 300, height: 400)],
                                            fieldNamed: "person"))
        defer { Task { await engine.close(doc) } }

        let other = staged("hierarchy")
        try parentFieldsPdf().write(to: other)
        defer { try? FileManager.default.removeItem(at: other) }

        do {
            _ = try await engine.importPages(doc, from: other.path, insertAt: 1)
            XCTFail("a clashing field hierarchy must be refused whole")
        } catch let error as PageToolError {
            XCTAssertEqual(error.refusal, .fieldHierarchy)
        }
        let count = await engine.pageCount(doc)
        XCTAssertEqual(count, 1, "nothing was added")
        let fields = try await engine.fieldNames(doc, pageIndex: 0)
        XCTAssertEqual(fields, ["person"], "and this document's own field is untouched")

        // And it reaches the person as a sentence that says what happened and that nothing
        // changed, rather than "the change failed".
        let sentence = ViewerModel.sentence(for: .fieldHierarchy)
        XCTAssertTrue(sentence.contains("form field"), sentence)
        XCTAssertTrue(sentence.contains("added nothing"), sentence)
    }

    /// The same hierarchy, where the name is *free*, imports and keeps its names — so the refusal
    /// above is the narrow case this build's PDFium actually has, not a blanket refusal.
    func testAFieldHierarchyWhoseNameIsFreeImportsAndKeepsItsNames() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(pdf(pageSizes: [CGSize(width: 300, height: 400)],
                                            fieldNamed: "somebody_else"))
        defer { Task { await engine.close(doc) } }

        let other = staged("hierarchy-free")
        try parentFieldsPdf().write(to: other)
        defer { try? FileManager.default.removeItem(at: other) }

        let imported = try await engine.importPages(doc, from: other.path, insertAt: 1)
        XCTAssertEqual(imported, 1)
        let names = try await engine.fieldNames(doc, pageIndex: 1)
        XCTAssertEqual(names, ["person.first", "person.last"])
    }

    // MARK: - extract

    func testExtractingPagesWritesThemInOrderAndLeavesTheDocumentAlone() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(pdf(pageSizes: fourSizes))
        defer { Task { await engine.close(doc) } }

        let out = staged("extract")
        defer { try? FileManager.default.removeItem(at: out) }
        try await engine.extractPages(doc, pages: [2, 0], to: out)

        let extracted = try await engine.open(file: out)
        defer { Task { await engine.close(extracted) } }
        let extractedWidths = try await widths(engine, extracted)
        XCTAssertEqual(extractedWidths, [500, 300], "in the order asked for")
        let sourceWidths = try await widths(engine, doc)
        XCTAssertEqual(sourceWidths, [300, 400, 500, 600],
                       "an extract is a copy: the document is unchanged")
    }

    // MARK: - permissions (ADR-004's pending amendment, #558)

    /// Assemble is its own bit: a form meant to be filled in but not restructured grants filling
    /// and withholds assembly, and the two answers have to differ.
    func testAssembleIsItsOwnBit() {
        func capabilities(_ permissions: PdfPermissions) -> DocumentCapabilities {
            DocumentCapabilities(security: PdfSecurity(isEncrypted: true, revision: 6,
                                                      permissions: permissions,
                                                      hasFullAccess: false))
        }
        let fillOnly = capabilities([.print, .fillForms, .copy])
        XCTAssertTrue(fillOnly.canFillForms)
        XCTAssertFalse(fillOnly.canAssemblePages, "filling a form is not restructuring it")
        XCTAssertTrue(fillOnly.canExtractPages)

        let assembleOnly = capabilities([.print, .assemble])
        XCTAssertTrue(assembleOnly.canAssemblePages, "assemble alone is enough")
        XCTAssertFalse(assembleOnly.canExtractPages, "and it is not copy")

        let modifyOnly = capabilities([.modify])
        XCTAssertTrue(modifyOnly.canAssemblePages, "modify grants it too, as the core's check does")

        XCTAssertTrue(DocumentCapabilities(security: .unprotected).canAssemblePages)
        XCTAssertTrue(DocumentCapabilities(security: .unprotected).canExtractPages)
    }

    /// Every page operation is gated on assemble, through the one backstop behind every entry
    /// point (`DocumentCapabilities.allows`).
    func testEveryPageOperationIsGatedOnAssemble() {
        let noAssemble = DocumentCapabilities(
            security: PdfSecurity(isEncrypted: true, revision: 6,
                                  permissions: [.print, .fillForms], hasFullAccess: false))
        let operations: [PdfEditOperation] = [
            RotatePagesOperation(pages: [0], quarterTurns: 1),
            DeletePagesOperation(pages: [0]),
            MovePageOperation(from: 0, to: 1),
            InsertBlankPageOperation(at: 0, widthPoints: 10, heightPoints: 10),
            ImportPagesOperation(path: "/nowhere.pdf", insertAt: 0),
        ]
        for operation in operations {
            XCTAssertFalse(noAssemble.allows(operation), "\(operation.name) must ask assemble")
            XCTAssertFalse(DocumentCapabilities.isFillingOperation(operation),
                           "\(operation.name) is a page tool, not a form-filling tool (#457)")
        }
        let withAssemble = DocumentCapabilities(
            security: PdfSecurity(isEncrypted: true, revision: 6,
                                  permissions: [.print, .assemble], hasFullAccess: false))
        for operation in operations {
            XCTAssertTrue(withAssemble.allows(operation))
        }
    }

    /// A document that forbids assembly refuses every page change, and says which refusal it was.
    /// This is the rule the proposed ADR-004 amendment writes down and the core already enforces.
    func testADocumentThatForbidsAssemblyRefusesEveryPageChange() async throws {
        let engine = PdfEngine.shared
        let plain = try await engine.open(pdf(pageSizes: fourSizes))
        let locked = staged("no-assemble")
        defer { try? FileManager.default.removeItem(at: locked) }
        var permissions = PdfPermissions.all
        permissions.remove(.assemble)
        permissions.remove(.modify)
        try await engine.save(plain, userPassword: "open-me", ownerPassword: "owner-only",
                              permissions: permissions, to: locked)
        await engine.close(plain)

        let doc = try await engine.open(file: locked, password: "open-me")
        defer { Task { await engine.close(doc) } }
        let security = await engine.security(doc)
        XCTAssertFalse(security.hasFullAccess, "opened as the user, not the owner")
        let capabilities = DocumentCapabilities(security: security)
        XCTAssertFalse(capabilities.canAssemblePages, "the app does not offer what the core refuses")

        do {
            try await engine.rotatePage(doc, pageIndex: 0, quarterTurns: 1)
            XCTFail("the core refuses a page change without assemble or modify")
        } catch let error as PageToolError {
            XCTAssertEqual(error.refusal, .restricted)
        }
        let rotation = try await engine.pageRotation(doc, pageIndex: 0)
        XCTAssertEqual(rotation, 0, "and nothing was changed")
    }

    // MARK: - contract 10's own promises

    /// An open page handle follows its page, and a handle on a deleted page answers -1 and still
    /// renders. The app holds no handle across a page change — every read here opens a page, uses
    /// it and closes it — so this is the half of the contract that would otherwise be taken on
    /// trust.
    func testAPageHandleFollowsItsPageAndADeletedOneAnswersMinusOne() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(pdf(pageSizes: fourSizes))
        defer { Task { await engine.close(doc) } }

        let afterMove = try await engine.pageIndexFollowing(doc, openedAt: 0,
                                                            applying: .move(from: 0, to: 2))
        XCTAssertEqual(afterMove, 2, "the handle's index follows its page")
        let afterInsert = try await engine.pageIndexFollowing(doc, openedAt: 0,
                                                              applying: .insertBlank(at: 0))
        XCTAssertEqual(afterInsert, 1)

        let other = try await engine.open(pdf(pageSizes: fourSizes))
        defer { Task { await engine.close(other) } }
        let afterDelete = try await engine.pageIndexFollowing(other, openedAt: 1,
                                                              applying: .delete(1))
        XCTAssertEqual(afterDelete, -1, "a deleted page's handle answers -1 from then on")
    }

    /// **The layout guard is not on this path, and this is the check that says so rather than the
    /// claim.**
    ///
    /// The #118/#128 guard judges an edit that makes PDFium rewrite a page's content stream. This
    /// page's text carries 4 pt of character spacing, which is the shape it was built for — and
    /// the first half of this test shows the guard *is* awake on it. The second half runs every
    /// page operation over that same page: each one goes through, because a rotation sets
    /// `/Rotate` and the rest move whole page objects. Contract 10 lists no `MEGAPDF_ERR_LAYOUT`
    /// among its statuses, `PageToolRefusal` has no case for one, and no dialog is invented for a
    /// state that cannot happen.
    func testNoPageOperationCanBeDeclinedByTheLayoutGuard() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(pdf(pageSizes: [CGSize(width: 612, height: 792),
                                                       CGSize(width: 400, height: 400)],
                                            spacedPages: [0]))
        defer { Task { await engine.close(doc) } }

        // The guard is awake on this page: a text edit on it is judged at all.
        let lines = try await engine.textLines(doc, pageIndex: 0)
        let run = try XCTUnwrap(lines.first?.runs.first)
        let verdict = try await engine.layoutVerdict(doc, pageIndex: 0, objectIndex: run.objectIndex)
        XCTAssertNotNil(verdict, "the page was judged, so the guard is on duty here")

        // And every page operation on that same page goes through.
        try await engine.rotatePage(doc, pageIndex: 0, quarterTurns: 1)
        try await engine.movePage(doc, from: 0, to: 1)
        try await engine.insertBlankPage(doc, at: 0, widthPoints: 100, heightPoints: 100)
        let other = staged("spaced-source")
        try pdf(pageSizes: [CGSize(width: 150, height: 150)], spacedPages: [0]).write(to: other)
        defer { try? FileManager.default.removeItem(at: other) }
        let imported = try await engine.importPages(doc, from: other.path, insertAt: 0)
        XCTAssertEqual(imported, 1)
        let out = staged("spaced-extract")
        defer { try? FileManager.default.removeItem(at: out) }
        try await engine.extractPages(doc, pages: nil, to: out)
        let removed = try await engine.deletePage(doc, pageIndex: 0)
        await engine.discardRemovedPage(removed)
    }

    /// **The undo-ordering claim #429 and #441 were about**, in page-tools shape: an edit made
    /// before a delete must still be taken back off *the page it went on*, not off whichever page
    /// took its index.
    func testUndoAfterTakingBackADeleteStillTakesBackTheRotationBeforeIt() async throws {
        let engine = PdfEngine.shared
        // Page 3 is the one that gets turned; page 1 is the one that gets deleted in front of it.
        let doc = try await engine.open(pdf(pageSizes: [CGSize(width: 300, height: 400),
                                                       CGSize(width: 400, height: 400),
                                                       CGSize(width: 800, height: 200),
                                                       CGSize(width: 600, height: 400)]))
        defer { Task { await engine.close(doc) } }
        let history = EditHistory()

        try await history.perform(RotatePagesOperation(pages: [2], quarterTurns: 1), engine, doc)
        let turned = try await widths(engine, doc)
        XCTAssertEqual(turned, [300, 400, 200, 600], "the landscape page reads portrait now")

        try await history.perform(DeletePagesOperation(pages: [0]), engine, doc)
        let shifted = try await widths(engine, doc)
        XCTAssertEqual(shifted, [400, 200, 600], "the turned page is page 2 now")
        let stillTurned = try await engine.pageRotation(doc, pageIndex: 1)
        XCTAssertEqual(stillTurned, 1)

        _ = try await history.undo(engine, doc)   // the delete
        let restored = try await widths(engine, doc)
        XCTAssertEqual(restored, [300, 400, 200, 600])

        _ = try await history.undo(engine, doc)   // the rotation
        let unturned = try await widths(engine, doc)
        XCTAssertEqual(unturned, [300, 400, 800, 600],
                       "the rotation came off the page it went on")
        for page in 0..<4 {
            let rotation = try await engine.pageRotation(doc, pageIndex: page)
            XCTAssertEqual(rotation, 0, "and no page is left turned")
        }
    }

    /// The same sequence forward again, because a redo is where a stale identifier shows up second.
    func testRedoingForwardThroughADeleteLandsOnTheSamePagesAgain() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(pdf(pageSizes: [CGSize(width: 300, height: 400),
                                                       CGSize(width: 800, height: 200),
                                                       CGSize(width: 600, height: 400)]))
        defer { Task { await engine.close(doc) } }
        let history = EditHistory()

        try await history.perform(RotatePagesOperation(pages: [1], quarterTurns: 1), engine, doc)
        try await history.perform(DeletePagesOperation(pages: [0]), engine, doc)
        _ = try await history.undo(engine, doc)
        _ = try await history.undo(engine, doc)
        _ = try await history.redo(engine, doc)
        _ = try await history.redo(engine, doc)
        let forward = try await widths(engine, doc)
        XCTAssertEqual(forward, [200, 600])
        let rotation = try await engine.pageRotation(doc, pageIndex: 0)
        XCTAssertEqual(rotation, 1)
    }

    // MARK: - the name an extract offers, and the sentences

    func testTheExtractNameSaysWhichPagesTheseAre() {
        XCTAssertEqual(ViewerModel.extractName(documentName: "Report.pdf", pages: [2], of: 9),
                       "Report page 3")
        XCTAssertEqual(ViewerModel.extractName(documentName: "Report.pdf", pages: [1, 2, 3], of: 9),
                       "Report pages 2-4")
        XCTAssertEqual(ViewerModel.extractName(documentName: "Report.pdf", pages: [0, 4], of: 9),
                       "Report 2 pages", "a selection that is not a run is a count")
        XCTAssertEqual(ViewerModel.extractName(documentName: "Report.pdf", pages: [0, 1], of: 2),
                       "Report 2 pages", "and so is the whole document")
        XCTAssertEqual(ViewerModel.extractName(documentName: "", pages: [0], of: 1),
                       "Document page 1")
    }

    /// Every refusal has a sentence, and none of them is empty or the name of a code.
    func testEveryRefusalHasASentence() {
        let refusals: [PageToolRefusal] = [.restricted, .sourceNeedsPassword, .sourceRestricted,
                                           .fieldHierarchy, .lastPage, .file, .redactionPoisoned,
                                           .spentPage, .engine]
        for refusal in refusals {
            let sentence = ViewerModel.sentence(for: refusal)
            XCTAssertFalse(sentence.isEmpty, "\(refusal) has no sentence")
            XCTAssertTrue(sentence.hasSuffix("."), "\(refusal) is not a sentence: \(sentence)")
            XCTAssertFalse(sentence.contains("MEGAPDF_"), "\(refusal) leaks a status code")
        }
    }
}
