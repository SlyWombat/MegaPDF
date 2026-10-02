import XCTest
@testable import MegaPDF

/// Save a copy adopts the copy it wrote (#572).
///
/// The bug these were written against cleared the unsaved flag and changed nothing else, so a
/// test that asserted the flag would have passed while the app pointed at a file holding none of
/// the person's edits — and a later Save wrote their next change to that file rather than to the
/// copy they believed they were in. So nothing here is judged by the flag. Each test asserts
/// where the app is **pointed**: `sourceURL`, the name on screen, the file Recents offers, and —
/// the consequence that actually loses work — which file the next Save writes.
@MainActor
final class SaveACopyAdoptionTests: XCTestCase {

    // MARK: - helpers

    private func fixtureBytes() throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: "fixture", withExtension: "pdf") else {
            throw XCTSkip("fixture.pdf missing from test bundle")
        }
        return try Data(contentsOf: url)
    }

    /// A document open from a writable temporary file, the way `openPicked` opens a picked one.
    private func openModel() async throws -> (ViewerModel, URL) {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("572-opened-\(UUID().uuidString).pdf")
        try fixtureBytes().write(to: url)
        let model = ViewerModel()
        model.openPicked(url: url)
        try await ModelOpening.waitForTheDocument(model)
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

    private func displayName(_ model: ViewerModel) -> String? {
        if case let .viewing(name, _) = model.state { return name }
        return nil
    }

    /// How many pages the file on disk holds — which file the edits went to, read from the file
    /// itself rather than from the model that might be wrong about it.
    private func pagesOnDisk(_ url: URL) async throws -> Int {
        let doc = try await PdfEngine.shared.open(file: url)
        let count = await PdfEngine.shared.pageCount(doc)
        await PdfEngine.shared.close(doc)
        return count
    }

    /// An edit with a consequence a file can be measured for: one more page than before.
    private func insertAPage(into model: ViewerModel) async throws {
        let before = model.pageCount
        model.insertBlankPage(at: before)
        try await waitUntil("the page is inserted") {
            model.pageCount == before + 1 && !model.busy.isBlocked
        }
    }

    /// Everything the export sheet does once the person has chosen a place: it writes the staged
    /// file there and hands that URL back, which is what `ContentView`'s `.fileExporter`
    /// completion passes to `finishExport(savedTo:)`.
    private func sheetWrites(_ staged: URL, as name: String) throws -> URL {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("572-chosen-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let destination = folder.appendingPathComponent(name)
        try FileManager.default.copyItem(at: staged, to: destination)
        return destination
    }

    /// Save a copy, all the way through the sheet, to the place it was written.
    private func saveACopy(_ model: ViewerModel, as name: String) async throws -> URL {
        let exported = await model.exportFile(named: name)
        let staged = try XCTUnwrap(exported, "the copy is staged")
        let destination = try sheetWrites(staged, as: name)
        model.finishExport(savedTo: destination)
        return destination
    }

    // MARK: - the copy becomes the document

    func testSaveACopyPointsTheAppAtTheCopy() async throws {
        let (model, opened) = try await openModel()
        defer { try? FileManager.default.removeItem(at: opened) }
        let pagesBefore = model.pageCount
        try await insertAPage(into: model)

        let copy = try await saveACopy(model, as: "Rental Agreement signed.pdf")
        defer { try? FileManager.default.removeItem(at: copy.deletingLastPathComponent()) }

        XCTAssertEqual(model.sourceURL, copy,
                       "the copy is the document now — Save writes there, not to what was opened")
        XCTAssertEqual(displayName(model), "Rental Agreement signed.pdf",
                       "and the name on screen is the copy's, as it is on Android and the desktops")
        XCTAssertFalse(model.isDirty, "the copy holds the edits, so nothing is unsaved")

        let pagesInCopy = try await pagesOnDisk(copy)
        let pagesInOpened = try await pagesOnDisk(opened)
        XCTAssertEqual(pagesInCopy, pagesBefore + 1, "the copy has the edit")
        XCTAssertEqual(pagesInOpened, pagesBefore,
                       "and the file that was opened is untouched, as Save a copy promises")
        model.close()
    }

    /// The consequence that loses work: before #572 this Save went to the file that was opened,
    /// which the person had stopped working in and had been told nothing was unsaved about.
    func testTheNextSaveWritesToTheCopyNotToTheFileThatWasOpened() async throws {
        let (model, opened) = try await openModel()
        defer { try? FileManager.default.removeItem(at: opened) }
        let pagesBefore = model.pageCount
        try await insertAPage(into: model)

        let copy = try await saveACopy(model, as: "Rental Agreement signed.pdf")
        defer { try? FileManager.default.removeItem(at: copy.deletingLastPathComponent()) }

        try await insertAPage(into: model)   // a second edit, after the copy was made
        XCTAssertTrue(model.isDirty)
        model.save()
        try await waitUntil("the save finishes") { !model.isSaving && !model.busy.isBlocked }
        XCTAssertFalse(model.isDirty)
        XCTAssertEqual(model.statusMessage, String(localized: "Saved"),
                       "the save reported success, not a refusal to write where it now points")

        let pagesInCopy = try await pagesOnDisk(copy)
        let pagesInOpened = try await pagesOnDisk(opened)
        XCTAssertEqual(pagesInCopy, pagesBefore + 2,
                       "the Save went to the copy, which is the document now")
        XCTAssertEqual(pagesInOpened, pagesBefore,
                       "and never to the file that was opened")
        model.close()
    }

    /// Recents is how the copy is reached next launch, so it has to be the copy that is in it —
    /// Android's `saveAs` adds exactly this entry.
    func testTheCopyIsTheDocumentRecentsOffers() async throws {
        let (model, opened) = try await openModel()
        defer { try? FileManager.default.removeItem(at: opened) }
        try await insertAPage(into: model)

        let name = "Rental Agreement \(UUID().uuidString.prefix(8)).pdf"
        let copy = try await saveACopy(model, as: name)
        defer { try? FileManager.default.removeItem(at: copy.deletingLastPathComponent()) }

        // The entry is written off the main actor (the location is a stat, and for a cloud file
        // a round trip), and `close` is what re-reads the store onto the home screen.
        try await waitUntil("the copy is the newest recent document") {
            model.close()
            if case let .home(entries, _) = model.state { return entries.first?.displayName == name }
            return false
        }
    }

    /// Cancelling the sheet wrote nothing, so there is nothing to adopt: the app must still be
    /// pointed at the file it opened, and must still say the edits are unsaved.
    func testACancelledExportChangesNothing() async throws {
        let (model, opened) = try await openModel()
        defer { try? FileManager.default.removeItem(at: opened) }
        try await insertAPage(into: model)

        _ = await model.exportFile(named: "Rental Agreement.pdf")
        model.finishExport(savedTo: nil)

        XCTAssertEqual(model.sourceURL, opened)
        XCTAssertEqual(displayName(model), opened.lastPathComponent)
        XCTAssertTrue(model.isDirty, "nothing was written, so the edits are still unsaved")
        model.close()
    }
}
