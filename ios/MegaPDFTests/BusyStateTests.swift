import XCTest
@testable import MegaPDF

/// A clock the test moves by hand, so the 0.5 s / 0.3 s rule is checked exactly.
final class ManualBusyScheduler: BusyScheduler {
    private final class Item: BusyScheduled {
        let due: TimeInterval
        let order: Int
        let action: @MainActor () -> Void
        var cancelled = false
        init(due: TimeInterval, order: Int, action: @escaping @MainActor () -> Void) {
            self.due = due
            self.order = order
            self.action = action
        }
        func cancel() { cancelled = true }
    }

    private(set) var now: TimeInterval = 0
    private var items: [Item] = []
    private var order = 0

    func schedule(after delay: TimeInterval, _ action: @escaping @MainActor () -> Void) -> BusyScheduled {
        order += 1
        let item = Item(due: now + max(0, delay), order: order, action: action)
        items.append(item)
        return item
    }

    /// Moves the clock to `time`, running whatever falls due on the way, in order.
    @MainActor
    func advance(to time: TimeInterval) {
        while let next = items.filter({ !$0.cancelled && $0.due <= time + 1e-9 })
                                .min(by: { ($0.due, $0.order) < ($1.due, $1.order) }) {
            items.removeAll { $0 === next }
            now = max(now, next.due)
            next.action()
        }
        items.removeAll { $0.cancelled }
        now = time
    }
}

/// #145: the busy state's timing and its tap guard.
@MainActor
final class BusyStateTests: XCTestCase {

    private var clock: ManualBusyScheduler!
    private var announced: [String] = []
    private var busy: BusyState!

    override func setUp() async throws {
        clock = ManualBusyScheduler()
        announced = []
        busy = BusyState(scheduler: clock, announce: { [unowned self] in self.announced.append($0) })
    }

    func testTheStripAppearsOnlyAfterHalfASecond() throws {
        _ = try XCTUnwrap(busy.begin(.saving, scope: .document, blocksFileCommands: true))
        XCTAssertTrue(busy.isBlocked, "the control is disabled at once")
        XCTAssertTrue(busy.blocksFileCommands)
        clock.advance(to: 0.49)
        XCTAssertNil(busy.strip, "no indicator before 0.5 s")
        clock.advance(to: 0.5)
        XCTAssertEqual(busy.strip?.label, .saving)
        XCTAssertNil(busy.pageIndicator)
        XCTAssertEqual(announced, [BusyLabel.saving.text], "the strip is announced when it appears")
    }

    func testWorkThatEndsQuicklyNeverShowsAnIndicator() throws {
        let token = try XCTUnwrap(busy.begin(.saving, scope: .document))
        clock.advance(to: 0.3)
        busy.end(token)
        XCTAssertFalse(busy.isBlocked)
        clock.advance(to: 2)
        XCTAssertNil(busy.strip)
        XCTAssertTrue(announced.isEmpty)
    }

    func testAnIndicatorOnceShownStaysAtLeastThreeTenths() throws {
        let token = try XCTUnwrap(busy.begin(.saving, scope: .document))
        clock.advance(to: 0.5)
        XCTAssertNotNil(busy.strip)
        clock.advance(to: 0.6)
        busy.end(token)
        XCTAssertFalse(busy.isBlocked, "the work itself is over at once")
        XCTAssertNotNil(busy.strip, "shown 0.1 s ago: it stays")
        clock.advance(to: 0.79)
        XCTAssertNotNil(busy.strip)
        clock.advance(to: 0.8)
        XCTAssertNil(busy.strip, "gone once it has been up 0.3 s")
    }

    func testAnIndicatorShownLongEnoughGoesAtOnce() throws {
        let token = try XCTUnwrap(busy.begin(.saving, scope: .document))
        clock.advance(to: 1.0)
        busy.end(token)
        XCTAssertNil(busy.strip)
    }

    func testTheLabelFollowsTheWorkWithoutRestartingTheDelay() throws {
        let token = try XCTUnwrap(busy.begin(.saving, scope: .document))
        clock.advance(to: 0.3)
        busy.update(token, label: .checkingSavedFile)
        clock.advance(to: 0.5)
        XCTAssertEqual(busy.strip?.label, .checkingSavedFile)
        busy.update(token, label: .saving)
        XCTAssertEqual(busy.strip?.label, .saving)
    }

    func testRepeatTapsAreIgnoredWhileBlockingWorkRuns() throws {
        let save = try XCTUnwrap(busy.begin(.saving, scope: .document, blocksFileCommands: true))
        XCTAssertNil(busy.begin(.saving, scope: .document), "a second Save is ignored")
        XCTAssertNil(busy.begin(.applying, scope: .page(0, nil)), "an edit waits for the save")
        let search = busy.begin(.searching, scope: .document, blocking: false)
        XCTAssertNotNil(search, "search doesn't wait, and doesn't block")
        busy.end(save)
        XCTAssertFalse(busy.isBlocked, "search alone doesn't block editing")
        XCTAssertNotNil(busy.begin(.applying, scope: .page(0, nil)))
    }

    func testPageWorkShowsASpinnerOnThePageNotTheStrip() throws {
        let rect = PdfRect(left: 10, bottom: 20, right: 30, top: 40)
        _ = try XCTUnwrap(busy.begin(.checkingPage, scope: .page(2, rect)))
        XCTAssertFalse(busy.blocksFileCommands)
        clock.advance(to: 0.5)
        XCTAssertNil(busy.strip)
        XCTAssertEqual(busy.pageIndicator?.label, .checkingPage)
        XCTAssertEqual(busy.pageIndicator?.scope, .page(2, rect))
        XCTAssertTrue(announced.isEmpty, "only the strip is announced")
    }

    func testNoSpinnerWhileTheWorkWaitsOnThePerson() throws {
        let token = try XCTUnwrap(busy.begin(.checkingPage, scope: .page(0, nil)))
        clock.advance(to: 0.5)
        XCTAssertNotNil(busy.pageIndicator)
        // The warning goes up: the spinner goes (after its 0.3 s), the edit stays blocking.
        busy.update(token, showsIndicator: false)
        XCTAssertTrue(busy.isBlocked)
        clock.advance(to: 0.8)
        XCTAssertNil(busy.pageIndicator)
        // Continue: applying gets its own half-second grace.
        busy.update(token, label: .applying, showsIndicator: true)
        clock.advance(to: 1.29)
        XCTAssertNil(busy.pageIndicator)
        clock.advance(to: 1.3)
        XCTAssertEqual(busy.pageIndicator?.label, .applying)
    }

    func testClosingTheDocumentEndsPageWorkButNotAnOpen() throws {
        _ = try XCTUnwrap(busy.begin(.opening, scope: .document, blocking: false))
        _ = try XCTUnwrap(busy.begin(.applying, scope: .page(0, nil)))
        clock.advance(to: 0.5)
        XCTAssertNotNil(busy.pageIndicator)
        busy.endPageWork()
        XCTAssertNil(busy.pageIndicator)
        XCTAssertFalse(busy.isBlocked)
        XCTAssertEqual(busy.strip?.label, .opening)
    }

    func testRunEndsTheWorkWhenTheBodyThrows() async {
        struct Boom: Error {}
        do {
            _ = try await busy.run(.saving, scope: .document) { _ in throw Boom() }
            XCTFail("expected the error")
        } catch {}
        XCTAssertTrue(busy.works.isEmpty)
    }
}
