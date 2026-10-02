import XCTest
@testable import MegaPDF

/// Whiteout on iPhone and iPad (#3), end to end: the engine binding, the two history
/// operations, and the whole lifecycle through `ViewerModel` — arm, drag to place, tap to
/// select, drag to move, resize by the corner, remove, undo.
///
/// **iOS had no whiteout at all before this.** #3 is titled for move and resize chrome,
/// which describes where Mac, Linux and Windows started; `PdfEngine+Redaction.swift` said in
/// as many words that "iOS has no whiteout", and `megapdf_add_whiteout` /
/// `megapdf_whiteouts` — in the core since contract 5 — had simply never been bound here.
/// That is the same shape of gap Android turned out to have (#565), so these checks cover
/// the feature rather than only the chrome.
///
/// The property each check is about is the one that separates a whiteout from a redaction:
/// **it covers, it does not remove.** The text under a whiteout is still in the file, which
/// is why taking the whiteout off brings it back — and why this must never be offered as a
/// way to hide something before sending it (#173 is that, and it asks first).
@MainActor
final class WhiteoutTests: XCTestCase {

    // MARK: - helpers

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: name, withExtension: "pdf") else {
            throw XCTSkip("fixture \(name).pdf missing from test bundle")
        }
        return try Data(contentsOf: url)
    }

    /// Polls `condition` on the main actor until it holds or `timeout` passes.
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

    /// A model with the demo agreement open from a writable temporary file, its #139 page
    /// check answered "keeps its look" so no warning stands between a gesture and the page.
    /// The warning has its own tests (`PageCheckTests`); these are about the tool.
    private func openModel(_ name: String = "demo",
                           check: @escaping @MainActor (PdfDocument, Int) async -> PageCheckAnswer = { _, _ in .keepsLook })
    async throws -> (ViewerModel, URL) {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("whiteout-\(UUID().uuidString).pdf")
        try fixture(name).write(to: url)
        let model = ViewerModel()
        model.pageCheck = check
        model.openPicked(url: url)
        try await ModelOpening.waitForTheDocument(model)
        return (model, url)
    }

    private func whiteouts(_ model: ViewerModel, page: Int = 0) async throws -> [PdfWhiteout] {
        let doc = try XCTUnwrap(model.document)
        return try await PdfEngine.shared.whiteouts(doc, pageIndex: page)
    }

    private func pageSize(_ model: ViewerModel, page: Int = 0) throws -> CGSize {
        guard case let .viewing(_, sizes) = model.state else {
            XCTFail("no document open"); throw CancellationError()
        }
        return sizes[page]
    }

    /// Taps the centre of `rect` the way the page view does: fractions of the page's own box.
    private func tap(_ model: ViewerModel, _ rect: PdfRect, page: Int = 0) throws {
        let size = try pageSize(model, page: page)
        let x = (rect.left + rect.right) / 2 / Double(size.width)
        let y = 1 - (rect.bottom + rect.top) / 2 / Double(size.height)
        model.onPageTapped(index: page, xFraction: x, yFraction: y)
    }

    private func settle(_ model: ViewerModel, _ what: String) async throws {
        try await waitUntil(what) { !model.busy.isBlocked }
    }

    private let area = PdfRect(left: 100, bottom: 500, right: 300, top: 540)

    // MARK: - the engine binding

    func testAFreshPageCarriesNoWhiteoutsAndAddingOneReportsWhereItLanded() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("demo"))
        defer { Task { await engine.close(doc) } }

        let before = try await engine.whiteouts(doc, pageIndex: 0)
        XCTAssertTrue(before.isEmpty, "the demo agreement should carry no whiteouts")

        let index = try await engine.addWhiteout(doc, pageIndex: 0, bounds: area)
        XCTAssertGreaterThanOrEqual(index, 0, "adding a whiteout must report its object index")

        let after = try await engine.whiteouts(doc, pageIndex: 0)
        XCTAssertEqual(after.count, 1)
        XCTAssertEqual(after.first?.objectIndex, index,
                       "the whiteout must be readable back at the index adding reported")
        let rect = try XCTUnwrap(after.first?.rect)
        XCTAssertEqual(rect.left, area.left, accuracy: 0.5)
        XCTAssertEqual(rect.bottom, area.bottom, accuracy: 0.5)
        XCTAssertEqual(rect.right, area.right, accuracy: 0.5)
        XCTAssertEqual(rect.top, area.top, accuracy: 0.5)
    }

    /// The whole difference from a redaction, in one check: the words under it are still in
    /// the file after a save, and the whiteout is there to be taken off again.
    func testAWhiteoutCoversRatherThanRemovesAndSurvivesASaveAndReopen() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("demo"))
        let lines = try await engine.textLines(doc, pageIndex: 0)
        let line = try XCTUnwrap(lines.max { $0.text.count < $1.text.count },
                                 "the demo agreement has no text to cover")
        let covered = line.text

        _ = try await engine.addWhiteout(doc, pageIndex: 0, bounds: line.rect)
        let bytes = try await engine.save(doc)
        await engine.close(doc)

        let reopened = try await engine.open(bytes)
        defer { Task { await engine.close(reopened) } }
        let carried = try await engine.whiteouts(reopened, pageIndex: 0)
        XCTAssertEqual(carried.count, 1, "a whiteout is page content and must be written to the file")
        let text = try await engine.textLines(reopened, pageIndex: 0).map(\.text).joined(separator: "\n")
        XCTAssertTrue(text.contains(covered),
                      "a whiteout must COVER the text, not remove it — that is what Redact is for")
    }

    func testWhiteoutsComeBackInObjectOrderSoTheLastOneIsTheOneOnTop() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("demo"))
        defer { Task { await engine.close(doc) } }

        let lower = try await engine.addWhiteout(doc, pageIndex: 0, bounds: area)
        let upper = try await engine.addWhiteout(
            doc, pageIndex: 0,
            bounds: PdfRect(left: 120, bottom: 505, right: 280, top: 535))
        let found = try await engine.whiteouts(doc, pageIndex: 0)
        XCTAssertEqual(found.map(\.objectIndex), [lower, upper],
                       "object order is what tells a tap which whiteout is on top")
    }

    // MARK: - the history operations

    func testPlacingUndoingAndRedoingAWhiteout() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("demo"))
        defer { Task { await engine.close(doc) } }

        let place = WhiteoutOperation(pageIndex: 0, rect: area, adding: true)
        try await place.apply(engine, doc)
        XCTAssertGreaterThanOrEqual(place.currentObjectIndex, 0)
        var count = try await engine.whiteouts(doc, pageIndex: 0).count
        XCTAssertEqual(count, 1)

        try await place.revert(engine, doc)
        count = try await engine.whiteouts(doc, pageIndex: 0).count
        XCTAssertEqual(count, 0, "undo must take the whiteout off the page")

        try await place.apply(engine, doc)
        count = try await engine.whiteouts(doc, pageIndex: 0).count
        XCTAssertEqual(count, 1, "redo must put it back")
    }

    func testMovingAndResizingAWhiteoutAndUndoingTheWholeGesture() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("demo"))
        defer { Task { await engine.close(doc) } }

        let index = try await engine.addWhiteout(doc, pageIndex: 0, bounds: area)
        // Moved AND resized in one gesture, which is what a corner drag does: #3's
        // acceptance is "drag/resize a whiteout, undo restores the prior rect".
        let moved = PdfRect(left: 150, bottom: 400, right: 420, top: 470)
        let move = MoveWhiteoutOperation(pageIndex: 0, objectIndex: index, from: area, to: moved)

        try await move.apply(engine, doc)
        var found = try await engine.whiteouts(doc, pageIndex: 0)
        XCTAssertEqual(found.count, 1, "a move must not leave the old rectangle behind")
        var one = try XCTUnwrap(found.first, "the move left no whiteout on the page at all")
        XCTAssertEqual(one.rect.left, moved.left, accuracy: 0.5)
        XCTAssertEqual(one.rect.top, moved.top, accuracy: 0.5)
        XCTAssertEqual(one.rect.right - one.rect.left,
                       moved.right - moved.left, accuracy: 0.5, "the resize must land too")
        XCTAssertEqual(move.currentObjectIndex, one.objectIndex,
                       "the operation must report where the whiteout ended up")

        try await move.revert(engine, doc)
        found = try await engine.whiteouts(doc, pageIndex: 0)
        XCTAssertEqual(found.count, 1)
        one = try XCTUnwrap(found.first, "the undo left no whiteout on the page at all")
        XCTAssertEqual(one.rect.left, area.left, accuracy: 0.5, "undo must restore the prior rect")
        XCTAssertEqual(one.rect.top, area.top, accuracy: 0.5)
        XCTAssertEqual(move.currentObjectIndex, one.objectIndex)
    }

    /// The sharpest check of the re-anchoring, as #564 found on Windows: a removal straight
    /// after a move, where the index the selection started with is guaranteed stale.
    func testRemovingAWhiteoutRightAfterMovingItTakesOffTheRightOne() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("demo"))
        defer { Task { await engine.close(doc) } }

        let keep = PdfRect(left: 60, bottom: 100, right: 160, top: 140)
        _ = try await engine.addWhiteout(doc, pageIndex: 0, bounds: keep)
        let index = try await engine.addWhiteout(doc, pageIndex: 0, bounds: area)

        let moved = PdfRect(left: 300, bottom: 600, right: 460, top: 650)
        let move = MoveWhiteoutOperation(pageIndex: 0, objectIndex: index, from: area, to: moved)
        try await move.apply(engine, doc)

        let remove = WhiteoutOperation(pageIndex: 0, rect: moved,
                                       objectIndex: move.currentObjectIndex, adding: false)
        try await remove.apply(engine, doc)

        let left = try await engine.whiteouts(doc, pageIndex: 0)
        XCTAssertEqual(left.count, 1, "exactly one whiteout should be left")
        let survivor = try XCTUnwrap(left.first, "the removal took off both whiteouts")
        XCTAssertEqual(survivor.rect.left, keep.left, accuracy: 0.5,
                       "the removal took off the wrong rectangle: the stale index was used")
    }

    // MARK: - the lifecycle, through the view model

    func testTheToolStartsOffAndArmingItPutsRedactAwayAndSaysWhatToDo() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        XCTAssertFalse(model.whiteoutMode, "the tool must not be armed by default")
        XCTAssertTrue(model.canWhiteout, "an unprotected document may be covered")

        model.toggleRedactMode()
        XCTAssertTrue(model.redactMode)
        model.toggleWhiteoutMode()
        XCTAssertTrue(model.whiteoutMode)
        XCTAssertFalse(model.redactMode,
                       "both tools want the next drag on the page, so arming one must put the other away")
        // Through the catalogue, not the English: the whole suite runs again in fr-CA, and a
        // test that asserted the English words would fail there for the wrong reason.
        XCTAssertEqual(model.statusMessage,
                       String(localized: "Drag across what should be covered"),
                       "an armed tool whose gesture is a drag has to say so")

        model.toggleWhiteoutMode()
        XCTAssertFalse(model.whiteoutMode)
        XCTAssertNil(model.statusMessage, "disarming must take its own prompt down")
    }

    func testADragCoversTheAreaLeavesTheDocumentUnsavedAndDisarmsTheTool() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        model.toggleWhiteoutMode()
        model.placeWhiteout(pageIndex: 0, rect: area)
        XCTAssertFalse(model.whiteoutMode, "the tool disarms itself, like Redact's drag")
        try await settle(model, "the whiteout lands")

        let found = try await whiteouts(model)
        XCTAssertEqual(found.count, 1, "the drag should have covered the area")
        XCTAssertTrue(model.isDirty, "covering part of a page is an unsaved change")
        XCTAssertTrue(model.canUndo, "and it has to be undoable")
        XCTAssertNil(model.selectedWhiteout,
                     "placing does not select: selecting is the separate tap, as with a mark")
    }

    func testATapSelectsAWhiteoutAndASecondTapLetsItGo() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        model.placeWhiteout(pageIndex: 0, rect: area)
        try await settle(model, "the whiteout lands")
        let placed = try await whiteouts(model)
        let cover = try XCTUnwrap(placed.first)

        try tap(model, cover.rect)
        try await waitUntil("the tap selects the whiteout") { model.selectedWhiteout != nil }
        XCTAssertEqual(model.selectedWhiteout?.objectIndex, cover.objectIndex)

        try tap(model, cover.rect)
        try await waitUntil("the second tap lets it go") { model.selectedWhiteout == nil }
    }

    func testATapAwayFromAWhiteoutClearsTheSelection() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        model.placeWhiteout(pageIndex: 0, rect: area)
        try await settle(model, "the whiteout lands")
        let placed = try await whiteouts(model)
        let cover = try XCTUnwrap(placed.first)
        try tap(model, cover.rect)
        try await waitUntil("selected") { model.selectedWhiteout != nil }

        // The bottom-left corner of the page: no whiteout, and far from the agreement's text.
        model.onPageTapped(index: 0, xFraction: 0.02, yFraction: 0.98)
        try await waitUntil("the ✕ does not linger over a page the user has moved on from") {
            model.selectedWhiteout == nil
        }
    }

    /// #3's own words: "chrome is remove-only; repositioning means redraw". This is the check
    /// that it is not, and that the move re-anchors — the selection has to end up on the
    /// whiteout's new object index, or the next Remove takes off something else.
    func testTheSelectionMovesResizesAndReAnchorsOnTheNewObjectIndex() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        model.placeWhiteout(pageIndex: 0, rect: area)
        try await settle(model, "the whiteout lands")
        let placed = try await whiteouts(model)
        let cover = try XCTUnwrap(placed.first)
        try tap(model, cover.rect)
        try await waitUntil("selected") { model.selectedWhiteout != nil }
        let before = try XCTUnwrap(model.selectedWhiteout)

        let moved = PdfRect(left: 150, bottom: 400, right: 420, top: 470)
        model.commitWhiteoutRect(moved)
        try await settle(model, "the move lands")

        let after = try await whiteouts(model)
        XCTAssertEqual(after.count, 1, "a move must not leave the old rectangle behind")
        let moved0 = try XCTUnwrap(after.first, "the move left no whiteout on the page at all")
        XCTAssertEqual(moved0.rect.left, moved.left, accuracy: 0.5,
                       "the drag did not land: the cover is where it was")
        XCTAssertEqual(moved0.rect.right - moved0.rect.left, moved.right - moved.left,
                       accuracy: 0.5, "the corner grip resizes as well as moves")

        let selection = try XCTUnwrap(model.selectedWhiteout,
                                      "the overlay must stay on the whiteout it just moved")
        XCTAssertEqual(selection.objectIndex, moved0.objectIndex,
                       "the selection must re-anchor on where the whiteout is NOW")
        XCTAssertEqual(selection.rect.left, moved.left, accuracy: 0.5)
        _ = before
    }

    func testARemovalTakesTheWhiteoutOffAndUndoBringsItBack() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        model.placeWhiteout(pageIndex: 0, rect: area)
        try await settle(model, "the whiteout lands")
        let placed = try await whiteouts(model)
        let cover = try XCTUnwrap(placed.first)
        try tap(model, cover.rect)
        try await waitUntil("selected") { model.selectedWhiteout != nil }

        model.removeSelectedWhiteout()
        try await settle(model, "the removal lands")
        var found = try await whiteouts(model)
        XCTAssertEqual(found.count, 0, "the ✕ must take the whiteout off")

        model.undo()
        try await settle(model, "the undo lands")
        found = try await whiteouts(model)
        XCTAssertEqual(found.count, 1, "undo must put it back")
        let back = try XCTUnwrap(found.first, "undo did not put the whiteout back")
        XCTAssertEqual(back.rect.left, area.left, accuracy: 0.5)
    }

    /// Move, then undo, through the model: one gesture is one undo step, and it restores the
    /// prior rect rather than merely removing the whiteout.
    func testUndoAfterAMoveRestoresThePriorRectAsOneStep() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        model.placeWhiteout(pageIndex: 0, rect: area)
        try await settle(model, "the whiteout lands")
        let placed = try await whiteouts(model)
        let cover = try XCTUnwrap(placed.first)
        try tap(model, cover.rect)
        try await waitUntil("selected") { model.selectedWhiteout != nil }

        model.commitWhiteoutRect(PdfRect(left: 150, bottom: 400, right: 420, top: 470))
        try await settle(model, "the move lands")

        model.undo()
        try await settle(model, "the undo lands")
        let found = try await whiteouts(model)
        XCTAssertEqual(found.count, 1, "undoing a move must not remove the whiteout")
        let restored = try XCTUnwrap(found.first, "undoing a move removed the whiteout")
        XCTAssertEqual(restored.rect.left, area.left, accuracy: 0.5, "the prior rect must come back")
        XCTAssertEqual(restored.rect.top, area.top, accuracy: 0.5)

        model.redo()
        try await settle(model, "the redo lands")
        let again = try await whiteouts(model)
        let redone = try XCTUnwrap(again.first, "redo left no whiteout on the page")
        XCTAssertEqual(redone.rect.left, 150, accuracy: 0.5, "redo must move it again")
    }

    func testUndoingAPlacementTakesTheWhiteoutOffAgain() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        model.placeWhiteout(pageIndex: 0, rect: area)
        try await settle(model, "the whiteout lands")
        var count = try await whiteouts(model).count
        XCTAssertEqual(count, 1)

        model.undo()
        try await settle(model, "the undo lands")
        count = try await whiteouts(model).count
        XCTAssertEqual(count, 0, "undo must take a placement back")

        model.redo()
        try await settle(model, "the redo lands")
        count = try await whiteouts(model).count
        XCTAssertEqual(count, 1, "and redo must put it back")
    }

    func testReadingModePutsTheToolAwayAndDropsTheSelection() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        model.placeWhiteout(pageIndex: 0, rect: area)
        try await settle(model, "the whiteout lands")
        let placed = try await whiteouts(model)
        let cover = try XCTUnwrap(placed.first)
        try tap(model, cover.rect)
        try await waitUntil("selected") { model.selectedWhiteout != nil }
        model.toggleWhiteoutMode()
        XCTAssertTrue(model.whiteoutMode)

        model.setReadingMode(true)
        XCTAssertFalse(model.whiteoutMode,
                       "an armed tool would fire on the first tap after Exit (plan §2)")
        XCTAssertNil(model.selectedWhiteout, "and its chrome goes with the rest of the chrome")
    }

    /// The permission backstop (#131, ADR-004): covering rewrites the page's own content, so
    /// it needs `modify` — the same permission Redact needs, and the same one the engine
    /// refuses without.
    func testADocumentThatForbidsChangesCannotBeCovered() async throws {
        let (model, url) = try await openModel("owner-only")
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        guard !model.canWhiteout else {
            throw XCTSkip("owner-only.pdf grants modify in this build; nothing to assert")
        }
        model.placeWhiteout(pageIndex: 0, rect: area)
        try await settle(model, "the refusal lands")
        let count = try await whiteouts(model).count
        XCTAssertEqual(count, 0,
                       "a document that forbids changes must not be covered")
    }

    /// The capability map is the backstop behind every gated entry point (#131), and both
    /// whiteout operations have to be named in it: covering needs `modify`, and a form that
    /// only allows *filling in* must not be covered. An operation the map does not know falls
    /// into its default, which demands every permission at once — so an unlisted whiteout
    /// would look right on an unprotected document and refuse on a fillable form.
    func testCoveringNeedsModifyRatherThanFormFilling() {
        let fillOnly = DocumentCapabilities(
            security: PdfSecurity(isEncrypted: true, revision: 6,
                                  permissions: [.fillForms, .annotate], hasFullAccess: false))
        XCTAssertTrue(fillOnly.canAddText, "the control: this open may add text")
        XCTAssertFalse(fillOnly.canEditContent)
        XCTAssertFalse(fillOnly.allows(WhiteoutOperation(pageIndex: 0, rect: area, adding: true)),
                       "covering rewrites the page's own content, so filling in is not enough")
        XCTAssertFalse(fillOnly.allows(MoveWhiteoutOperation(pageIndex: 0, objectIndex: 1,
                                                            from: area, to: area)))

        let modify = DocumentCapabilities(
            security: PdfSecurity(isEncrypted: true, revision: 6,
                                  permissions: [.modify], hasFullAccess: false))
        XCTAssertTrue(modify.allows(WhiteoutOperation(pageIndex: 0, rect: area, adding: true)),
                      "and with modify it is allowed — not left to the catch-all that wants everything")
        XCTAssertTrue(modify.allows(MoveWhiteoutOperation(pageIndex: 0, objectIndex: 1,
                                                         from: area, to: area)))
    }
}
