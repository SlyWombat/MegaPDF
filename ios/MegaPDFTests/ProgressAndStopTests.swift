import Combine
import XCTest
@testable import MegaPDF

/// Progress and Stop on long work, on iPhone and iPad (#145's P2 half).
///
/// #563 measured what is actually slow, on the desktops, against synthetic large fixtures —
/// shrink worst at 39.7 s, then combine, thumbnails, save, extract and search between 1.2 and
/// 4.8 s, and rotate/delete/move/insert under a third of a second. That measurement is not
/// repeated here; what matters for this platform is which of those operations iOS *has*:
///
///  * **shrink** — the worst by a factor of eight — **does not exist on iOS at all.**
///  * **search** is the one piece of work here with an honest denominator: a Swift per-page
///    loop over the whole document. It gets progress and Stop.
///  * **extract** is one engine call, so there is no count — but `megapdf_pages_extract` takes
///    a cancel flag and leaves nothing at the output path when it is raised, so it gets Stop
///    with no progress, exactly as the desktops decided.
///  * **save** is left alone on purpose: there is no honest denominator (the output's size is
///    not known until it is written) and a half-saved file is not a thing to leave behind. A
///    fake bar would be worse than none. One check below holds that in place, so Stop can
///    never become a way round the unsaved-changes question.
///  * **combine, rotate, delete, move, insert** have no interior at which a flag could be
///    read, or finish inside the 0.5 s before anything is drawn. They already report in the
///    strip — see `testEveryPageToolReportsInTheStripRatherThanOnAPageItMayHaveRemoved`, which
///    is the iOS half of the defect both desktop passes found.
@MainActor
final class ProgressAndStopTests: XCTestCase {

    // MARK: - the shared layer

    /// A clock the test drives, so the 0.5 s / 0.3 s rule is exercised without waiting.
    private final class ManualScheduler: BusyScheduler {
        var now: TimeInterval = 0
        private var pending: [(at: TimeInterval, item: Item)] = []

        final class Item: BusyScheduled {
            var cancelled = false
            let action: @MainActor () -> Void
            init(_ action: @escaping @MainActor () -> Void) { self.action = action }
            func cancel() { cancelled = true }
        }

        func schedule(after delay: TimeInterval, _ action: @escaping @MainActor () -> Void) -> BusyScheduled {
            let item = Item(action)
            pending.append((now + delay, item))
            return item
        }

        @MainActor
        func advance(to time: TimeInterval) {
            now = time
            let due = pending.filter { $0.at <= time }
            pending.removeAll { $0.at <= time }
            for entry in due where !entry.item.cancelled { entry.item.action() }
        }
    }

    private func state(_ scheduler: ManualScheduler) -> BusyState {
        BusyState(scheduler: scheduler, announce: { _ in })
    }

    func testWorkThatCannotCountKeepsAnIndeterminateBarAndNoStop() {
        let clock = ManualScheduler()
        let busy = state(clock)
        let token = busy.begin(.saving, scope: .document)
        XCTAssertNotNil(token)
        clock.advance(to: 0.5)

        let strip = busy.strip
        XCTAssertNotNil(strip, "the strip should be up after the 0.5 s threshold")
        XCTAssertNil(strip?.progress, "no progress means an indeterminate bar, as before #145")
        XCTAssertEqual(strip?.cancellable, false)
        XCTAssertFalse(busy.canStop, "work that was given no way to stop must offer no Stop")
        busy.requestStop()
        XCTAssertFalse(busy.isStopping, "and pressing Stop on it must do nothing at all")
    }

    func testProgressIsPublishedAsAFractionAndInWordsTheAppChose() throws {
        let clock = ManualScheduler()
        let busy = state(clock)
        let token = try XCTUnwrap(busy.begin(
            .searching, scope: .document, blocking: false,
            progressFormat: { done, total in "page \(done) of \(total)" }))
        clock.advance(to: 0.5)

        busy.report(token, done: 25, total: 100)
        let strip = try XCTUnwrap(busy.strip)
        XCTAssertEqual(strip.progress, BusyProgress(done: 25, total: 100))
        XCTAssertEqual(strip.progress?.fraction, 0.25)
        XCTAssertEqual(strip.progressText, "page 25 of 100",
                       "the numbers are the busy state's and the wording is the app's")
    }

    func testProgressClampsAndSurvivesAZeroTotal() {
        XCTAssertEqual(BusyProgress(done: 0, total: 0).fraction, 0, "no division by zero")
        XCTAssertEqual(BusyProgress(done: 7, total: 3).fraction, 1, "and never past the end")
        XCTAssertEqual(BusyProgress(done: -1, total: 10).fraction, 0)
    }

    func testStopReachesTheWorkOnceAndSaysSoOnScreenAtOnce() throws {
        let clock = ManualScheduler()
        let busy = state(clock)
        var stops = 0
        let token = try XCTUnwrap(busy.begin(.searching, scope: .document, blocking: false,
                                             onStop: { stops += 1 }))
        clock.advance(to: 0.5)
        XCTAssertTrue(busy.canStop)
        XCTAssertFalse(busy.isStopping)

        busy.requestStop()
        XCTAssertEqual(stops, 1)
        XCTAssertTrue(busy.isStopping, "the button has to say Stopping… the moment it is pressed")
        XCTAssertFalse(busy.canStop, "and go insensitive, so it cannot be pressed twice")

        busy.requestStop()
        XCTAssertEqual(stops, 1, "a second press must not reach the work again")
        busy.end(token)
        XCTAssertFalse(busy.canStop)
    }

    /// #563's rule, which exists to stop a flicker: while the indicator lives out its 0.3 s
    /// minimum after the work has ended, Stop goes — there is nothing left to stop — but the
    /// progress **holds its last value** rather than falling back to an indeterminate bar.
    func testStopGoesWhenTheWorkEndsWhileTheBarHoldsItsLastValue() throws {
        let clock = ManualScheduler()
        let busy = state(clock)
        let token = try XCTUnwrap(busy.begin(
            .searching, scope: .document, blocking: false,
            progressFormat: { done, total in "\(done)/\(total)" },
            onStop: {}))
        clock.advance(to: 0.5)
        busy.report(token, done: 90, total: 100)
        XCTAssertTrue(busy.canStop)

        busy.end(token)
        XCTAssertFalse(busy.canStop, "nothing is running, so there is nothing to stop")
        let strip = try XCTUnwrap(busy.strip, "the strip stays up for its 0.3 s minimum")
        XCTAssertEqual(strip.progress, BusyProgress(done: 90, total: 100),
                       "the bar must hold its last value rather than flicker to indeterminate")

        clock.advance(to: 0.9)
        XCTAssertNil(busy.strip)
    }

    func testStopPicksTheNewestRunningStoppableWork() throws {
        let clock = ManualScheduler()
        let busy = state(clock)
        var stopped: [String] = []
        _ = try XCTUnwrap(busy.begin(.searching, scope: .document, blocking: false,
                                     onStop: { stopped.append("first") }))
        _ = try XCTUnwrap(busy.begin(.saving, scope: .document, blocking: false,
                                     onStop: { stopped.append("second") }))
        clock.advance(to: 0.5)
        busy.requestStop()
        XCTAssertEqual(stopped, ["second"],
                       "the strip reports the newest work, so that is what Stop means")
    }

    func testResetAndClosingADocumentForgetTheStopHandlers() throws {
        let clock = ManualScheduler()
        let busy = state(clock)
        var stops = 0
        _ = try XCTUnwrap(busy.begin(.applying, scope: .page(0, nil), onStop: { stops += 1 }))
        busy.endPageWork()
        busy.requestStop()
        XCTAssertEqual(stops, 0, "work that went with the document cannot be stopped afterwards")

        _ = try XCTUnwrap(busy.begin(.saving, scope: .document, onStop: { stops += 1 }))
        busy.reset()
        XCTAssertFalse(busy.canStop)
        busy.requestStop()
        XCTAssertEqual(stops, 0)
    }

    // MARK: - search, which is the one thing here with a denominator

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: name, withExtension: "pdf") else {
            throw XCTSkip("fixture \(name).pdf missing from test bundle")
        }
        return try Data(contentsOf: url)
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

    /// The schematic: 12 matches for "the" across its pages (#98), and enough pages for a scan
    /// to be watched part-way through.
    private func openModel(_ name: String = "microbit-v2-schematic") async throws -> (ViewerModel, URL) {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("progress-\(UUID().uuidString).pdf")
        try fixture(name).write(to: url)
        let model = ViewerModel()
        model.pageCheck = { _, _ in .keepsLook }
        model.openPicked(url: url)
        try await ModelOpening.waitForTheDocument(model)
        return (model, url)
    }

    func testAScanReportsWhichPageItIsOnAndOffersStop() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        var seen: [BusyWork] = []
        let sink = model.busy.$works.sink { works in
            seen.append(contentsOf: works.filter { $0.label == .searching })
        }
        defer { sink.cancel() }

        model.search(term: "the", debounce: false)
        try await waitUntil("the scan finishes") { !model.isSearching }

        XCTAssertFalse(seen.isEmpty, "the scan reported nothing at all")
        XCTAssertTrue(seen.allSatisfy { $0.cancellable },
                      "a scan of a whole document has to be stoppable throughout")
        let reports = seen.compactMap(\.progress)
        XCTAssertFalse(reports.isEmpty, "the scan never said which page it was on")
        XCTAssertTrue(reports.allSatisfy { $0.total == model.pageCount },
                      "the denominator is the document's pages, which is an honest one")
        XCTAssertEqual(reports.map(\.done), reports.map(\.done).sorted(),
                       "progress must only ever go forwards")
        XCTAssertEqual(reports.first?.done, 1,
                       "the count is 1-based: nobody says Page 0 of 5")
        XCTAssertEqual(reports.last?.done, model.pageCount,
                       "and it must reach the end rather than stop short of it")
        XCTAssertTrue(seen.contains { $0.progressText != nil },
                      "the count has to be readable, not just a bar")
        XCTAssertFalse(model.searchMatches.isEmpty)
    }

    /// A scan held at a page of the test's choosing, so the middle of one can be looked at.
    private final class HeldScan {
        private var release: CheckedContinuation<Void, Never>?
        private(set) var pagesStarted: [Int] = []
        private var waiting: CheckedContinuation<Void, Never>?

        @MainActor
        func page(_ index: Int) async {
            pagesStarted.append(index)
            if let waiting { self.waiting = nil; waiting.resume() }
            await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                release = continuation
            }
        }

        /// Waits until the scan has reached a page and is held there.
        @MainActor
        func waitForAPage() async {
            guard release == nil else { return }
            await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                waiting = continuation
            }
        }

        /// Lets the held page finish.
        @MainActor
        func letGo() {
            let continuation = release
            release = nil
            continuation?.resume()
        }
    }

    /// The division #563 drew, kept here: Stop keeps what the scan found, and dismissing the
    /// find bar clears it. On a phone that distinction is the whole point of a Stop button —
    /// closing the find bar was already a way to end a scan, and it throws the answer away.
    func testStoppingAScanKeepsWhatItFoundAndSaysSo() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        // The first page answers with a match and then holds, so the stop lands in the middle
        // of a scan that genuinely has something to keep.
        let held = HeldScan()
        let hit = PdfSearchMatch(rects: [PdfRect(left: 10, bottom: 10, right: 20, top: 20)])
        model.searchPage = { _, index, _ in
            await held.page(index)
            return index == 0 ? [hit] : []
        }

        model.search(term: "the", debounce: false)
        await held.waitForAPage()
        // The strip's own 0.5 s threshold still applies to stoppable work: there is no button
        // before there is a strip, which is why this waits for one rather than asserting that
        // Stop is on offer the instant the scan starts.
        try await waitUntil("the strip appears") { model.busy.strip?.label == .searching }
        // Through the strip's own button, not the model's method: the wiring is part of what
        // is under test.
        XCTAssertTrue(model.busy.canStop, "the strip must offer Stop while a scan is running")
        model.busy.requestStop()
        XCTAssertTrue(model.busy.isStopping, "and say so at once")
        held.letGo()
        try await waitUntil("the scan ends") { !model.isSearching }

        XCTAssertEqual(model.searchMatches.count, 1,
                       "a stopped scan keeps the matches it already had")
        XCTAssertEqual(model.currentMatchIndex, 0, "and they are navigable")
        XCTAssertEqual(model.notice, String(localized: "Search stopped."),
                       "a banner, not an alert: nothing went wrong and the result is on screen")
        XCTAssertLessThan(held.pagesStarted.count, model.pageCount,
                          "a stopped scan must not have gone on to read the rest of the document")
    }

    func testANewerTermStillClearsTheOlderScansMatches() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        model.search(term: "the", debounce: false)
        try await waitUntil("the first scan finishes") { !model.isSearching }
        XCTAssertFalse(model.searchMatches.isEmpty)

        model.search(term: "zzzznotinthisfile", debounce: false)
        try await waitUntil("the second scan finishes") { !model.isSearching }
        XCTAssertTrue(model.searchMatches.isEmpty,
                      "a superseded scan does not keep the older term's matches")
        XCTAssertNotEqual(model.notice, String(localized: "Search stopped."),
                          "being superseded is not being stopped, and must not say it was")
    }

    func testDismissingTheFindBarClearsEverything() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        model.search(term: "the", debounce: false)
        try await waitUntil("the scan finishes") { !model.isSearching }
        model.clearSearch()
        XCTAssertTrue(model.searchMatches.isEmpty)
        XCTAssertNil(model.currentMatchIndex)
        XCTAssertFalse(model.isSearching)
    }

    func testStoppingWhenNothingIsSearchingDoesNothing() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        model.stopSearch()
        XCTAssertNil(model.notice, "there was nothing to stop, so nothing is said")
        XCTAssertFalse(model.isSearching)
    }

    // MARK: - what is deliberately not stoppable

    /// A save offers no Stop, so pressing it can never become a way round the
    /// unsaved-changes question — and because there is no honest denominator for it and a
    /// half-saved file is not a thing to leave behind. #563 asserts the same thing on the
    /// desktops; this is the same promise on the phones.
    func testASaveOffersNoStop() async throws {
        let (model, url) = try await openModel("demo")
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        model.startTextPlacement()
        model.onPageTapped(index: 0, xFraction: 0.2, yFraction: 0.85)
        try await waitUntil("the text sheet opens") { model.pendingText != nil }
        model.commitText("something", fontSize: 12, fontName: PdfEngine.defaultFont)
        try await waitUntil("the edit lands") { !model.busy.isBlocked && model.isDirty }

        model.save()
        // The save may be quick; whatever is running under `.saving` must not offer a way out.
        for work in model.busy.works where work.label == .saving || work.label == .checkingSavedFile {
            XCTAssertFalse(work.cancellable, "a save must never offer Stop")
        }
        try await waitUntil("the save finishes") { !model.isSaving && !model.busy.isBlocked }
        XCTAssertFalse(model.isDirty)
    }

    /// The defect both desktop passes found, checked on this platform: every page operation
    /// reported nowhere at all, because the spinner was given a page index that cannot be
    /// drawn — for a delete, a page the operation removes.
    ///
    /// **iOS does not have it.** Every page tool reports in the document strip
    /// (`BusyScope.document`), never on a page, so there is no index to be wrong. This check
    /// is what keeps it that way: a page tool that took `.page(…)` would fail here.
    func testEveryPageToolReportsInTheStripRatherThanOnAPageItMayHaveRemoved() async throws {
        let (model, url) = try await openModel("demo")
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        // A sink, not a poll: a page tool begins its busy work inside its own task, so by the
        // time the call has returned there may be nothing left to see.
        var scopes: [BusyScope] = []
        let sink = model.busy.$works.sink { works in
            scopes.append(contentsOf: works.filter { $0.label == .applying }.map(\.scope))
        }
        defer { sink.cancel() }

        model.rotatePages([0], quarterTurns: 1)
        try await waitUntil("the rotate lands") { !model.busy.isBlocked && model.canUndo }

        model.insertBlankPage(at: 1)
        try await waitUntil("the insert lands") { !model.busy.isBlocked && model.pageCount > 1 }

        model.deletePages([1])
        try await waitUntil("the delete lands") { !model.busy.isBlocked && model.pageCount == 1 }

        XCTAssertFalse(scopes.isEmpty, "the page tools reported nothing at all")
        XCTAssertTrue(scopes.allSatisfy { $0.isDocument },
                      "a page tool must report in the strip: a structure change's page index is "
                      + "the first page it touched, and for a delete that page is gone — which "
                      + "is how it came to report nowhere on the desktops. Scopes: \(scopes)")
    }
}
