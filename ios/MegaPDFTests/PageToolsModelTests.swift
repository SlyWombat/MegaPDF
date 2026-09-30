import CoreGraphics
import XCTest
@testable import MegaPDF

/// The page tools through the **real view model** (#174): the half contract 10 says is the app's
/// own job.
///
/// The core keeps its own per-page state right and cannot see this side's — the page sizes the
/// list lays out from, the rendered images, the grid's thumbnails and selection, the search hits,
/// the #139 pages a person has already answered for. Everything here is a check that those follow
/// a page change, both ways through the history, because every one of them going wrong is silent:
/// the document is correct and the app draws, taps and searches the wrong page.
@MainActor
final class PageToolsModelTests: XCTestCase {

    // MARK: - a document on disk, opened the way the app opens one

    /// Pages of different sizes, so `model.pageSizes` is an identity for each page and a reorder is
    /// something a test can see.
    private func pdf(pageSizes: [CGSize]) -> Data {
        var pdf = "%PDF-1.4\n"
        var offsets: [Int] = []
        func add(_ body: String) {
            offsets.append(pdf.utf8.count)
            pdf += "\(offsets.count) 0 obj\n\(body)\nendobj\n"
        }
        let kids = (0..<pageSizes.count).map { "\(4 + $0 * 2) 0 R" }.joined(separator: " ")
        add("<< /Type /Catalog /Pages 2 0 R >>")
        add("<< /Type /Pages /Kids [\(kids)] /Count \(pageSizes.count) >>")
        add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
        for (index, size) in pageSizes.enumerated() {
            add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 \(Int(size.width)) \(Int(size.height))] "
                + "/Resources << /Font << /F1 3 0 R >> >> /Contents \(5 + index * 2) 0 R >>")
            // Every page says "needle" so a search finds one hit per page, and its own number so
            // the hit can be told from its neighbours'.
            let content = "BT /F1 18 Tf 30 \(Int(size.height) - 60) Td (needle \(index + 1)) Tj ET"
            add("<< /Length \(content.utf8.count) >>\nstream\n\(content)\nendstream")
        }
        let xref = pdf.utf8.count
        pdf += "xref\n0 \(offsets.count + 1)\n0000000000 65535 f \n"
        for offset in offsets { pdf += String(format: "%010d 00000 n \n", offset) }
        pdf += "trailer\n<< /Size \(offsets.count + 1) /Root 1 0 R >>\nstartxref\n\(xref)\n%%EOF\n"
        return Data(pdf.utf8)
    }

    private let fourSizes = [CGSize(width: 300, height: 400),
                             CGSize(width: 400, height: 420),
                             CGSize(width: 500, height: 440),
                             CGSize(width: 600, height: 460)]

    private func openModel(_ sizes: [CGSize]? = nil) async throws -> (ViewerModel, URL) {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("pages-model-\(UUID().uuidString).pdf")
        try pdf(pageSizes: sizes ?? fourSizes).write(to: url)
        let model = ViewerModel()
        model.openPicked(url: url)
        try await waitUntil("the document opens") {
            if case .viewing = model.state { return model.document != nil && !model.busy.isBlocked }
            return false
        }
        return (model, url)
    }

    private func waitUntil(_ what: String, timeout: TimeInterval = 15,
                           _ condition: @MainActor () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition() {
            if Date() > deadline {
                XCTFail("timed out waiting for: \(what)")
                throw CancellationError()
            }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
    }

    private func widths(_ model: ViewerModel) -> [Int] {
        model.pageSizes.map { Int($0.width) }
    }

    /// Page operations run in a `Task` off the caller, so a test waits for the page list to say the
    /// change landed rather than guessing at a delay.
    private func waitForWidths(_ model: ViewerModel, _ expected: [Int]) async throws {
        try await waitUntil("the page list becomes \(expected)") { self.widths(model) == expected }
    }

    // MARK: - the app follows the renumbering

    /// A delete, and everything the app keeps by page index afterwards.
    func testEverythingTheAppKeepsByPageIndexFollowsADelete() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }

        // Something index-keyed in each of the places that has to follow: a search over the
        // document (one hit per page), a selection in the grid, and thumbnails for the grid.
        model.search(term: "needle", debounce: false)
        try await waitUntil("the search finds every page") { model.searchMatches.count == 4 }
        model.setPagesOpen(true)
        model.updateThumbnailWindow(first: 0, last: 3)
        try await waitUntil("the thumbnails are drawn") { model.pageThumbnails.count == 4 }
        model.setPagesSelecting(true)
        model.togglePageSelection(2)
        model.togglePageSelection(3)
        let thumbnailOfPageThree = model.pageThumbnails[2]
        XCTAssertNotNil(thumbnailOfPageThree)

        model.deletePages([1])
        try await waitForWidths(model, [300, 500, 600])

        XCTAssertEqual(model.pageSelection, [1, 2],
                       "the selection followed the pages it was on, and did not move to page 1")
        XCTAssertEqual(model.searchMatches.map(\.pageIndex), [0, 1, 2],
                       "the hits came back one each, and page 2's hit left with page 2")
        XCTAssertEqual(model.pageThumbnails.count, 3)
        XCTAssertTrue(model.pageThumbnails[1] === thumbnailOfPageThree,
                      "page 3's own picture is now page 2's — not redrawn, and not another page's")
        XCTAssertTrue(model.isDirty, "a page change leaves the document unsaved")

        model.undo()
        try await waitForWidths(model, [300, 400, 500, 600])
        XCTAssertEqual(model.pageSelection, [2, 3], "and back again on the way out")
        XCTAssertEqual(model.searchMatches.map(\.pageIndex), [0, 1, 2, 3])
    }

    /// A move, the other direction: a run of pages shifts one way and one page jumps.
    func testTheSelectionAndTheSearchHitsFollowAMove() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }

        model.search(term: "needle 1", debounce: false)
        try await waitUntil("the first page's hit is found") { model.searchMatches.count == 1 }
        XCTAssertEqual(model.searchMatches.first?.pageIndex, 0)
        model.setPagesOpen(true)
        model.setPagesSelecting(true)
        model.togglePageSelection(0)

        model.movePage(from: 0, to: 2)
        try await waitForWidths(model, [400, 500, 300, 600])
        XCTAssertEqual(model.pageSelection, [2], "the selection went with the page")
        XCTAssertEqual(model.searchMatches.first?.pageIndex, 2, "and so did the hit on it")

        model.undo()
        try await waitForWidths(model, [300, 400, 500, 600])
        XCTAssertEqual(model.pageSelection, [0])
        XCTAssertEqual(model.searchMatches.first?.pageIndex, 0)
    }

    /// The #139 "already asked about this page" set is keyed by page index too. Without this the
    /// first delete would leave a page's answer sitting on whichever page took its index — so a
    /// page that *would* change silently stopped asking, which is the one thing that question is
    /// for. The desktops carry the same fix (`PageRegenerationWarnings.Renumber`).
    func testTheSettledPageWarningsFollowTheRenumbering() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }

        model.pageChecks.settle(2)
        model.pageChecks.settle(3)
        XCTAssertEqual(model.pageChecks.settled, [2, 3])

        model.deletePages([0])
        try await waitForWidths(model, [400, 500, 600])
        XCTAssertEqual(model.pageChecks.settled, [1, 2],
                       "the answers came back one page each, with the pages they were about")

        model.undo()
        try await waitForWidths(model, [300, 400, 500, 600])
        XCTAssertEqual(model.pageChecks.settled, [2, 3])
    }

    /// A rotation renumbers nothing and changes every size it touched — the one page change whose
    /// shifts are empty and whose page sizes are all wrong afterwards. "No shifts" must never be
    /// read as "nothing to do".
    func testTheSizesFollowARotationAndItsPictureIsRedrawn() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }
        model.setPagesOpen(true)
        model.updateThumbnailWindow(first: 0, last: 3)
        try await waitUntil("the thumbnails are drawn") { model.pageThumbnails.count == 4 }
        let before = model.pageThumbnails[1]

        model.rotatePages([1], quarterTurns: 1)
        try await waitForWidths(model, [300, 420, 500, 600])
        XCTAssertEqual(Int(model.pageSizes[1].height), 400, "and its height is the old width")
        try await waitUntil("its thumbnail is drawn again") {
            model.pageThumbnails[1] != nil && !(model.pageThumbnails[1] === before)
        }

        model.undo()
        try await waitForWidths(model, [300, 400, 500, 600])
    }

    /// Turning a selection is **one** change and one press of Undo, through the model's own
    /// pipeline.
    func testTurningASelectionThroughTheModelIsOneUndoStep() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }

        model.rotatePages([0, 1, 2], quarterTurns: 1)
        try await waitForWidths(model, [400, 420, 440, 600])
        XCTAssertTrue(model.canUndo)

        model.undo()
        try await waitForWidths(model, [300, 400, 500, 600])
        try await waitUntil("the history is empty again") { !model.canUndo }
    }

    // MARK: - insert, and the size a blank page takes

    func testABlankPageTakesTheSizeOfThePageInFrontOfIt() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }

        model.insertBlankPage(at: 2)
        try await waitForWidths(model, [300, 400, 400, 500, 600])
        XCTAssertEqual(Int(model.pageSizes[2].height), 420,
                       "the size of page 2, which it was inserted after")

        model.undo()
        try await waitForWidths(model, [300, 400, 500, 600])
    }

    // MARK: - the refusals, as the person sees them

    /// The one-page rule, said when Delete is pressed rather than left as a command that quietly
    /// does nothing — and said without asking the engine, because the app already knows.
    func testDeletingTheLastPageSaysSoAndChangesNothing() async throws {
        let (model, url) = try await openModel([CGSize(width: 300, height: 400)])
        defer { try? FileManager.default.removeItem(at: url) }

        model.deletePages([0])
        XCTAssertEqual(model.pageToolRefusal, "A PDF has to keep at least one page.")
        XCTAssertEqual(model.pageCount, 1)
        XCTAssertFalse(model.isDirty, "nothing was changed, so nothing is unsaved")
        XCTAssertFalse(model.canUndo, "and nothing went on the history")
    }

    /// Selecting every page and pressing Delete is the same rule, which is the way a person
    /// actually reaches it.
    func testDeletingEveryPageSaysTheSameRule() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }
        model.setPagesOpen(true)
        model.setPagesSelecting(true)
        model.selectAllPages()
        XCTAssertEqual(model.pageSelection.count, 4)

        model.deletePages(model.pageSelection.sorted())
        XCTAssertEqual(model.pageToolRefusal, "A PDF has to keep at least one page.")
        XCTAssertEqual(model.pageCount, 4)
    }

    // MARK: - the pages are chrome

    /// Reading mode is the chrome-free view, and the pages are chrome (#506). On the phone the
    /// sheet would sit over the page it is meant to be showing; on the iPad the sidebar is exactly
    /// the kind of panel this mode takes away. Either way it does not come back on the way out.
    func testEnteringReadingModeClosesThePages() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }

        model.setPagesOpen(true)
        model.setPagesSelecting(true)
        model.togglePageSelection(1)
        XCTAssertTrue(model.pagesOpen)

        model.setReadingMode(true)
        XCTAssertFalse(model.pagesOpen, "the pages are chrome, and reading mode takes chrome away")
        XCTAssertFalse(model.pagesSelecting)
        XCTAssertTrue(model.pageSelection.isEmpty)

        model.setReadingMode(false)
        XCTAssertFalse(model.pagesOpen, "and they do not come back, as an armed tool does not")
    }

    /// Leaving Select mode drops the selection: a set of highlighted pages nothing can be done to
    /// has no meaning.
    func testLeavingSelectModeDropsTheSelection() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }
        model.setPagesOpen(true)
        model.setPagesSelecting(true)
        model.togglePageSelection(0)
        model.togglePageSelection(1)
        XCTAssertEqual(model.pageSelection, [0, 1])
        model.togglePageSelection(0)
        XCTAssertEqual(model.pageSelection, [1], "a second tap takes a page out of the selection")

        model.setPagesSelecting(false)
        XCTAssertTrue(model.pageSelection.isEmpty)
    }

    /// Closing the document closes the pages, drops the selection and forgets the thumbnails: they
    /// belong to the document on screen, and the next one decides for itself.
    func testThePagesBelongToTheOpenDocument() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }
        model.setPagesOpen(true)
        model.updateThumbnailWindow(first: 0, last: 3)
        try await waitUntil("the thumbnails are drawn") { !model.pageThumbnails.isEmpty }
        model.setPagesSelecting(true)
        model.selectAllPages()

        model.close()
        XCTAssertFalse(model.pagesOpen)
        XCTAssertFalse(model.pagesSelecting)
        XCTAssertTrue(model.pageSelection.isEmpty)
        XCTAssertTrue(model.pageThumbnails.isEmpty,
                      "a previous document's thumbnails would be drawn over the next one's grid")
    }

    // MARK: - extract

    /// An extract stages a file for the export sheet, and — unlike Save a copy — it must not stand
    /// in for a save: it wrote a *different* file, and this document still has whatever unsaved
    /// changes it had.
    func testAnExtractStagesAFileAndNeverStandsInForASave() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }

        model.rotatePages([0], quarterTurns: 1)
        try await waitForWidths(model, [400, 400, 500, 600])
        XCTAssertTrue(model.isDirty)

        model.setPagesOpen(true)
        model.setPagesSelecting(true)
        model.togglePageSelection(1)
        model.togglePageSelection(2)
        model.startPageExport(pages: [1, 2], documentName: "Report.pdf")
        try await waitUntil("the extract is staged") { model.pageExport != nil }

        let export = try XCTUnwrap(model.pageExport)
        XCTAssertEqual(export.defaultName, "Report pages 2-3")
        XCTAssertEqual(export.pageCount, 2)
        XCTAssertTrue(FileManager.default.fileExists(atPath: export.url.path))
        let staged = export.url

        let extracted = try await PdfEngine.shared.open(file: staged)
        let count = await PdfEngine.shared.pageCount(extracted)
        XCTAssertEqual(count, 2, "the staged file holds the pages that were asked for")
        await PdfEngine.shared.close(extracted)

        XCTAssertEqual(widths(model), [400, 400, 500, 600], "the document itself is untouched")

        model.finishPageExport(saved: true)
        XCTAssertNil(model.pageExport)
        XCTAssertFalse(FileManager.default.fileExists(atPath: staged.path),
                       "the staged copy goes with the sheet")
        XCTAssertTrue(model.isDirty,
                      "an extract is not a save: the rotation is still unsaved")
    }

    // MARK: - what the grid may offer at all

    func testAnOrdinaryDocumentMayHaveItsPagesRearrangedAndCopiedOut() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }
        XCTAssertTrue(model.canAssemblePages)
        XCTAssertTrue(model.canExtractPages)
    }
}
