import XCTest
@testable import MegaPDF

/// #145: the shared, early #139 page check — the coordinator on its own, the engine's check run
/// off the actor, and the model's change flow (budget, warning, one question at a time) and D3.
@MainActor
final class PageCheckTests: XCTestCase {

    // MARK: - helpers

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

    /// A check that never answers on its own; it notices cancellation.
    private final class HangingCheck {
        var calls: [Int] = []
        var cancelled: [Int] = []

        @MainActor
        func run(_ page: Int) async -> PageCheckAnswer {
            calls.append(page)
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 10_000_000)
            }
            cancelled.append(page)
            return .cancelled
        }
    }

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: name, withExtension: "pdf") else {
            throw XCTSkip("fixture \(name).pdf missing from test bundle")
        }
        return try Data(contentsOf: url)
    }

    /// One Letter page with `content` as its content stream and Helvetica as /F1.
    private func onePagePdf(_ content: String) -> Data {
        var pdf = "%PDF-1.4\n"
        var offsets: [Int] = []
        func add(_ body: String) {
            offsets.append(pdf.utf8.count)
            pdf += "\(offsets.count) 0 obj\n\(body)\nendobj\n"
        }
        add("<< /Type /Catalog /Pages 2 0 R >>")
        add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
        add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>")
        add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
        add("<< /Length \(content.utf8.count) >>\nstream\n\(content)\nendstream")
        let xref = pdf.utf8.count
        pdf += "xref\n0 \(offsets.count + 1)\n0000000000 65535 f \n"
        for offset in offsets { pdf += String(format: "%010d 00000 n \n", offset) }
        pdf += "trailer\n<< /Size \(offsets.count + 1) /Root 1 0 R >>\nstartxref\n\(xref)\n%%EOF\n"
        return Data(pdf.utf8)
    }

    private let plainPage = "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET 0 0 1 rg 72 500 200 40 re f"
    private let clippedPage = "BT /F1 24 Tf 72 700 Td (Spaced report) Tj ET q BT 7 Tr /F1 72 Tf 72 480 Td (CLIP) Tj ET 0 0 1 rg 60 460 400 100 re f Q"

    /// A model with `bytes` open from a writable temporary file.
    private func openModel(_ bytes: Data, check: @escaping @MainActor (PdfDocument, Int) async -> PageCheckAnswer,
                           budget: TimeInterval = 1.5) async throws -> (ViewerModel, URL) {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("pagecheck-\(UUID().uuidString).pdf")
        try bytes.write(to: url)
        let model = ViewerModel()
        model.pageCheck = check
        model.pageChecks.budget = budget
        model.openPicked(url: url)
        try await waitUntil("the document opens") {
            if case .viewing = model.state { return model.document != nil && !model.busy.isBlocked }
            return false
        }
        return (model, url)
    }

    private func textBoxCount(_ model: ViewerModel) async throws -> Int {
        let doc = try XCTUnwrap(model.document)
        return try await PdfEngine.shared.textBoxes(doc, pageIndex: 0).count
    }

    // MARK: - coordinator

    func testACheckPastItsBudgetIsCancelledAndThePageAppliesAndSettles() async throws {
        let check = HangingCheck()
        let checks = PageCheckCoordinator(budget: 0.1) { await check.run($0) }
        let started = Date()
        let outcome = await checks.outcome(for: 0)
        XCTAssertEqual(outcome, .apply, "nothing is refused, and there is no warning")
        XCTAssertLessThan(Date().timeIntervalSince(started), 5)
        XCTAssertTrue(checks.isSettled(0))
        try await waitUntil("the check is cancelled") { check.cancelled == [0] }
        let again = await checks.outcome(for: 0)
        XCTAssertEqual(again, .apply)
        XCTAssertEqual(check.calls, [0], "a settled page is never checked again")
    }

    func testAPageThatWouldChangeWarnsAndIsNotSettled() async {
        let checks = PageCheckCoordinator(budget: 5) { _ in .wouldChange }
        let first = await checks.outcome(for: 0)
        XCTAssertEqual(first, .warn)
        XCTAssertFalse(checks.isSettled(0), "Cancel must be able to ask again")
        checks.settle(0)
        let second = await checks.outcome(for: 0)
        XCTAssertEqual(second, .apply, "Continue settles it")
    }

    func testAnEarlyCheckThatKeepsItsLookSettlesThePageWithoutAWait() async throws {
        var calls = 0
        let checks = PageCheckCoordinator(budget: 5) { _ in
            calls += 1
            return .keepsLook
        }
        checks.start(3)
        checks.start(3)
        try await waitUntil("the early check settles the page") { checks.isSettled(3) }
        checks.start(3)
        let outcome = await checks.outcome(for: 3)
        XCTAssertEqual(outcome, .apply)
        XCTAssertEqual(calls, 1, "one shared check per page")
    }

    func testStartingAnotherPageCancelsTheUnfinishedCheck() async throws {
        let check = HangingCheck()
        let checks = PageCheckCoordinator(budget: 5) { await check.run($0) }
        checks.start(0)
        checks.start(1)
        XCTAssertEqual(checks.runningPage, 1)
        try await waitUntil("page 0's check is cancelled") { check.cancelled.contains(0) }
        XCTAssertFalse(check.cancelled.contains(1))
        checks.reset()
        try await waitUntil("reset cancels page 1") { check.cancelled.contains(1) }
    }

    func testAPageAChangeIsWaitingOnKeepsItsCheck() async throws {
        let checks = PageCheckCoordinator(budget: 5) { page in
            try? await Task.sleep(nanoseconds: 300_000_000)
            return Task.isCancelled ? .cancelled : .wouldChange
        }
        async let outcome = checks.outcome(for: 0)
        try await Task.sleep(nanoseconds: 50_000_000)
        checks.start(1)   // scrolling on while the change waits
        XCTAssertEqual(checks.runningPage, 0)
        let result = await outcome
        XCTAssertEqual(result, .warn, "the waiting change still gets its answer")
    }

    func testResetAbandonsAChangeWaitingOnACheck() async throws {
        let check = HangingCheck()
        let checks = PageCheckCoordinator(budget: 10) { await check.run($0) }
        async let outcome = checks.outcome(for: 0)
        try await Task.sleep(nanoseconds: 50_000_000)
        checks.reset()
        let result = await outcome
        XCTAssertEqual(result, .abandoned)
        XCTAssertFalse(checks.isSettled(0))
    }

    // MARK: - engine: the check off the actor

    func testTheEngineCheckAnswersOffTheActor() async throws {
        let engine = PdfEngine.shared
        let clipped = try await engine.open(onePagePdf(clippedPage))
        let plain = try await engine.open(onePagePdf(plainPage))
        let wouldChange = await engine.pageCheck(clipped, pageIndex: 0)
        let keepsLook = await engine.pageCheck(plain, pageIndex: 0)
        XCTAssertEqual(wouldChange, .wouldChange)
        XCTAssertEqual(keepsLook, .keepsLook)
        let noSuchPage = await engine.pageCheck(plain, pageIndex: 7)
        XCTAssertEqual(noSuchPage, .unjudged, "no such page")
        await engine.close(clipped)
        await engine.close(plain)
        let afterClose = await engine.pageCheck(plain, pageIndex: 0)
        XCTAssertEqual(afterClose, .cancelled, "a closed document is never touched")
    }

    func testACancelledTaskStopsTheEngineCheck() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(onePagePdf(clippedPage))
        let task = Task { await engine.pageCheck(doc, pageIndex: 0) }
        task.cancel()
        let answer = await task.value
        XCTAssertTrue(answer == .cancelled || answer == .wouldChange, "\(answer)")
        await engine.close(doc)
    }

    func testClosingWaitsForARunningCheck() async throws {
        let engine = PdfEngine.shared
        for _ in 0..<5 {
            let doc = try await engine.open(try fixture("microbit-v2-schematic"))
            let check = Task.detached { await engine.pageCheck(doc, pageIndex: 0) }
            try await Task.sleep(nanoseconds: 5_000_000)
            await engine.close(doc)
            let answer = await check.value
            XCTAssertNotEqual(answer, .unjudged, "the check either finished or was stopped by the close")
            // After the close, calls on the document are refused rather than reaching freed memory.
            let size = try? await engine.pageSize(doc, index: 0)
            XCTAssertNil(size)
        }
    }

    // MARK: - model: the change flow

    func testCancelAppliesNothingAndOnlyOneQuestionIsAskedAtATime() async throws {
        let (model, url) = try await openModel(onePagePdf(plainPage), check: { _, _ in .wouldChange })
        defer { try? FileManager.default.removeItem(at: url) }
        let before = try await textBoxCount(model)

        model.pendingText = PendingText(pageIndex: 0, x: 100, y: 300)
        model.commitText("First", fontSize: 12, fontName: "Helvetica")
        try await waitUntil("the warning shows") { model.pageRewriteWarning != nil }
        XCTAssertTrue(model.busy.isBlocked, "further edits wait while the question is up")

        // A second change while the first question is up is blocked, not queued or dropped.
        model.pendingText = PendingText(pageIndex: 0, x: 100, y: 200)
        model.commitText("Second", fontSize: 12, fontName: "Helvetica")
        XCTAssertNotNil(model.pendingText, "the second sheet stays open with its text")
        model.startTextPlacement()
        XCTAssertFalse(model.isPlacingText, "tools wait too")

        model.answerPageRewriteWarning(false)
        try await waitUntil("the change is over") { !model.busy.isBlocked }
        XCTAssertNil(model.pageRewriteWarning)
        let after = try await textBoxCount(model)
        XCTAssertEqual(after, before, "Cancel applies nothing")
        XCTAssertFalse(model.isDirty)
        XCTAssertFalse(model.pageChecks.isSettled(0), "the page asks again next time")

        // Continue this time: the change applies and the page is settled.
        model.commitText("Second", fontSize: 12, fontName: "Helvetica")
        try await waitUntil("the warning shows again") { model.pageRewriteWarning != nil }
        model.answerPageRewriteWarning(true)
        try await waitUntil("the change applies") { !model.busy.isBlocked }
        let applied = try await textBoxCount(model)
        XCTAssertEqual(applied, before + 1)
        XCTAssertTrue(model.isDirty)
        XCTAssertTrue(model.pageChecks.isSettled(0))
        model.close()
    }

    func testAChangeWaitsAtMostTheBudgetThenAppliesWithoutAWarning() async throws {
        let check = HangingCheck()
        let (model, url) = try await openModel(onePagePdf(plainPage), check: { _, page in await check.run(page) },
                                               budget: 0.3)
        defer { try? FileManager.default.removeItem(at: url) }
        let before = try await textBoxCount(model)

        var warned = false
        model.pendingText = PendingText(pageIndex: 0, x: 100, y: 300)
        model.commitText("Note", fontSize: 12, fontName: "Helvetica")
        XCTAssertTrue(model.busy.isBlocked, "further edits wait at once")
        try await waitUntil("Checking this page… while it waits") {
            model.busy.works.first?.label == .checkingPage
        }
        let deadline = Date().addingTimeInterval(10)
        while model.busy.isBlocked && Date() < deadline {
            if model.pageRewriteWarning != nil { warned = true }
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        XCTAssertFalse(model.busy.isBlocked)
        XCTAssertFalse(warned, "a check past its budget never warns")
        let after = try await textBoxCount(model)
        XCTAssertEqual(after, before + 1, "the change applied")
        XCTAssertTrue(model.pageChecks.isSettled(0))
        try await waitUntil("the check was cancelled") { check.cancelled == [0] }
        model.close()
    }

    func testTheRealCheckWarnsOnAPageThatWouldChange() async throws {
        let (model, url) = try await openModel(onePagePdf(clippedPage), check: { doc, page in
            await PdfEngine.shared.pageCheck(doc, pageIndex: page)
        }, budget: 30)
        defer { try? FileManager.default.removeItem(at: url) }
        model.pendingText = PendingText(pageIndex: 0, x: 300, y: 300)
        model.commitText("Note", fontSize: 12, fontName: "Helvetica")
        try await waitUntil("the warning shows") { model.pageRewriteWarning != nil }
        model.answerPageRewriteWarning(true)
        try await waitUntil("the change applies") { !model.busy.isBlocked }
        XCTAssertTrue(model.isDirty)
        model.close()
    }

    // MARK: - D3

    func testASaveMarksTheDocumentSavedOnlyIfNothingChangedWhileItRan() async throws {
        let (model, url) = try await openModel(try fixture("fixture"), check: { _, _ in .keepsLook })
        defer { try? FileManager.default.removeItem(at: url) }

        model.noteDocumentChanged()
        model.save()
        XCTAssertTrue(model.busy.blocksFileCommands, "editing, Close and file commands wait at once")
        XCTAssertTrue(model.closeBlocked)
        model.noteDocumentChanged()   // an edit lands while the save runs
        try await waitUntil("the save finishes") { !model.isSaving && !model.busy.isBlocked }
        XCTAssertTrue(model.isDirty, "the edit made during the save is still unsaved")

        model.save()
        try await waitUntil("the second save finishes") { !model.isSaving && !model.busy.isBlocked }
        XCTAssertFalse(model.isDirty, "nothing changed during this one")
        XCTAssertEqual(model.statusMessage, String(localized: "Saved"))
        model.close()
    }

    func testSaveACopyMarksSavedOnlyIfNothingChangedSinceItsBytesWereMade() async throws {
        let (model, url) = try await openModel(try fixture("fixture"), check: { _, _ in .keepsLook })
        defer { try? FileManager.default.removeItem(at: url) }

        model.noteDocumentChanged()
        let data = await model.exportData()
        XCTAssertNotNil(data)
        model.noteDocumentChanged()
        model.markSavedCopy()
        XCTAssertTrue(model.isDirty)

        _ = await model.exportData()
        model.markSavedCopy()
        XCTAssertFalse(model.isDirty)
        model.close()
    }
}
