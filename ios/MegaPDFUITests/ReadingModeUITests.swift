import XCTest

/// Reading mode, driven in a real window (#506 tier 1, #512 tier 2).
///
/// The promises this suite exists for are all promises about a running app, and none of
/// them can be kept by reading the source:
///
/// - **The hidden chrome really leaves the accessibility tree.** `XCUIElement` queries
///   *are* the accessibility tree, so `exists == false` on Close, Save and ⋯ is the honest
///   form of "a screen reader cannot swipe to them". Reading mode is meant to be the
///   screen-reader-friendly view, so a focus trap inside it would be worse than not
///   shipping it — and a `.toolbar(.hidden)` that turned out to hide a bar visually while
///   leaving its buttons reachable would look perfect in a screenshot.
/// - **A single tap toggles the bar, and only inside reading mode.**
/// - **The two ways out step back exactly one level**, so a find bar open over the reading
///   view is what the first swipe closes, not the mode under it.
/// - **The bar never fades while a screen reader is running**, which is the one rule where
///   getting it wrong strands exactly the people the feature is for.
///
/// **Where a gesture goes matters (#465, #531).** Every tap and double tap here is aimed at
/// `viewerPinchProbe` — the invisible, untouchable rectangle well inside the page that
/// `ViewerView` builds under `-uiTestZoomProbes`. Aimed at the page element, a synthesised
/// touch is placed from the element's frame inset 50 points, and once the page is larger
/// than the viewport that frame is the whole window: the touch lands on whatever is 50
/// points from the bottom, which here is the reading bar itself. A test that tapped the
/// bar and concluded the page's tap handler did nothing would be measuring its own aim.
/// `viewerReadingProbe` is read for the same reason `viewerZoomProbe` is: it says what the
/// app did, not what the screen looked like.
///
/// Two launch arguments are test-only levers, both built nowhere else:
/// `-uiTestZoomProbes` (#531) puts the probes in the tree; `-uiTestPinReadingBar` makes
/// the app read "a screen reader is running" as true. VoiceOver cannot be turned on from
/// inside a UI test, so without the second the pinning rule could only be argued. It
/// substitutes that one read and nothing downstream of it.
final class ReadingModeUITests: XCTestCase {

    private var app: XCUIApplication!

    override func setUp() {
        continueAfterFailure = false
        app = XCUIApplication()
        app.launchArguments = ["-screenshot", "viewer",
                               "-AppleLanguages", "(en)", "-AppleLocale", "en_US",
                               "-uiTestZoomProbes"]
    }

    // MARK: - reading what the app says about itself

    private func dump(_ message: String) -> String {
        print("READING MODE DUMP (\(message)):\n\(app.debugDescription)")
        return message
    }

    private func page(timeout: TimeInterval = 30) -> XCUIElement {
        let page = app.descendants(matching: .any)
            .matching(NSPredicate(format: "label == 'Page 1'")).firstMatch
        XCTAssertTrue(appears(page, timeout: timeout), dump("the demo document did not open"))
        return page
    }

    /// Whether an element turns up — polled, where this suite used to say `waitForExistence`.
    ///
    /// `XCUIElement.waitForExistence` costs a flat second of XCTest waiter overhead per call
    /// even when the element is already on screen (measured on the Mac mini: 1.049 s for
    /// `timeout: 1`, 1.067 s for `timeout: 30`, against 0.021 s to read the same element
    /// through `snapshot()`). Paid once that is nothing; paid inside `waitForProbe`'s loop it
    /// was the whole budget — five seconds bought four looks at the app (#599).
    private func appears(_ element: XCUIElement, timeout: TimeInterval = 10) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        repeat {
            if element.exists { return true }
            Thread.sleep(forTimeInterval: 0.1)
        } while Date() < deadline
        return false
    }

    /// The rectangle every synthesised touch here is aimed at (#465).
    private func pageProbe() -> XCUIElement {
        let probe = app.otherElements["viewerPinchProbe"]
        XCTAssertTrue(appears(probe),
                      dump("the pinch probe is missing — did -uiTestZoomProbes survive? (#465)"))
        return probe
    }

    /// The floating bar's container.
    ///
    /// Queried by identifier across every element type rather than as an `otherElement`:
    /// `.accessibilityElement(children: .contain)` over a row of buttons surfaces as a
    /// `StaticText` carrying the group's own name ("Reading controls"), not as an `Other`,
    /// and a query that guessed at the type reported the bar missing while every one of
    /// its buttons was on screen and tappable.
    private func readingBar() -> XCUIElement {
        app.descendants(matching: .any)
            .matching(NSPredicate(format: "identifier == 'readingBar'")).firstMatch
    }

    /// `"reading on bar off page 0 tint Normal"`, as the app itself has it.
    ///
    /// One element snapshot and no existence wait, for the reason given on `appears` (#599).
    /// A probe that has momentarily gone answers `""`, which `waitForProbe` reports as a state
    /// the app never left rather than as a missing element.
    private lazy var readingProbeElement: XCUIElement = app.descendants(matching: .any)
        .matching(NSPredicate(format: "identifier == 'viewerReadingProbe'")).firstMatch

    private func readingProbe() -> String {
        (try? readingProbeElement.snapshot())?.label ?? ""
    }

    private lazy var zoomProbeElement: XCUIElement = app.descendants(matching: .any)
        .matching(NSPredicate(format: "identifier == 'viewerZoomProbe'")).firstMatch

    private func committedZoom() -> Double {
        guard let label = (try? zoomProbeElement.snapshot())?.label else {
            XCTFail(dump("the zoom probe is missing (#465)"))
            return .nan
        }
        return Double(label.replacingOccurrences(of: "zoom ", with: "")) ?? .nan
    }

    /// Polls the committed zoom until it is past `floor`.
    ///
    /// The zoom used to be read **once**, after a fixed one-second sleep, which is how
    /// `testExitComesBackToTheChromeAndToTheSamePlace` went red on `main` with "the double tap
    /// did not zoom in" at a measured 1.0x: the gesture had arrived and the animation had not
    /// committed yet. A second was simply the wrong unit — the commit is a spring, and nothing
    /// says a loaded runner finishes one inside a second. Reading it on a poll asserts the same
    /// thing without naming a duration.
    @discardableResult
    private func waitForZoom(above floor: Double, timeout: TimeInterval = 10) -> Double {
        let deadline = Date().addingTimeInterval(timeout)
        var last = Double.nan
        var looks = 0
        repeat {
            looks += 1
            last = committedZoom()
            if last > floor { return last }
            Thread.sleep(forTimeInterval: 0.1)
        } while Date() < deadline
        XCTFail(dump("the zoom never went past \(floor) in \(looks) looks over "
                     + "\(String(format: "%.0f", timeout)) s — it measures \(last)"))
        return last
    }

    /// Polls the reading probe until it says `expected`, so a passing assertion never
    /// depends on how long a SwiftUI transition took.
    @discardableResult
    private func waitForProbe(_ expected: String, timeout: TimeInterval = 5) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        var last = ""
        var looks = 0
        repeat {
            looks += 1
            last = readingProbe()
            if last.contains(expected) { return true }
            Thread.sleep(forTimeInterval: 0.1)
        } while Date() < deadline
        XCTFail(dump("the app never reported '\(expected)' in \(looks) looks over "
                     + "\(String(format: "%.0f", timeout)) s — it says '\(last)'"))
        return false
    }

    private func enterReadingMode() {
        let more = app.buttons["viewerMore"].firstMatch
        XCTAssertTrue(appears(more), dump("no More menu"))
        more.tap()
        let row = app.buttons["viewerReadingMode"].firstMatch
        XCTAssertTrue(appears(row, timeout: 5), dump("the ⋯ menu has no Reading mode row"))
        row.tap()
        waitForProbe("reading on")
    }

    // MARK: - tier 1

    /// The proof-of-done line that matters most: the chrome is not merely invisible, it is
    /// gone from the tree a screen reader walks.
    func testReadingModeTakesTheChromeOutOfTheAccessibilityTreeAndLeavesThePage() {
        // Pinned only so the bar is still there to be asserted on after five tree
        // queries; nothing about the chrome's removal depends on it.
        app.launchArguments += ["-uiTestPinReadingBar"]
        app.launch()
        _ = page()

        // The chrome is there to begin with, so "gone" below means something.
        XCTAssertTrue(appears(app.buttons["Close"].firstMatch),
                      dump("there was no Close to hide"))
        XCTAssertTrue(app.buttons["viewerMore"].firstMatch.exists, dump("there was no ⋯ to hide"))

        enterReadingMode()

        // `exists` is a question about the accessibility tree, which is exactly the
        // question #506 asks: not "is it drawn" but "can VoiceOver swipe to it".
        XCTAssertFalse(app.buttons["Close"].firstMatch.exists,
                       dump("Close is still reachable inside reading mode (#506)"))
        XCTAssertFalse(app.buttons["Save"].firstMatch.exists,
                       dump("Save is still reachable inside reading mode (#506)"))
        XCTAssertFalse(app.buttons["viewerMore"].firstMatch.exists,
                       dump("the ⋯ menu is still reachable inside reading mode (#506)"))
        XCTAssertFalse(app.buttons["Sign"].firstMatch.exists,
                       dump("the tool bar is still reachable inside reading mode (#506)"))
        XCTAssertFalse(app.buttons["Undo"].firstMatch.exists,
                       dump("the tool bar is still reachable inside reading mode (#506)"))

        // What is left: the page, and the bar.
        XCTAssertTrue(app.descendants(matching: .any)
            .matching(NSPredicate(format: "label == 'Page 1'")).firstMatch.exists,
                      dump("the page went with the chrome"))
        XCTAssertTrue(appears(readingBar(), timeout: 5),
                      dump("no floating bar inside reading mode"))
        XCTAssertTrue(app.buttons["readingExit"].firstMatch.exists,
                      dump("the floating bar has no way out"))
    }

    /// Decision 2 on #168: tap-to-toggle *inside* reading mode only. The double tap keeps
    /// zooming, because the two gestures were always exclusive and that has not changed.
    func testASingleTapTogglesTheBarAndTheDoubleTapStillZooms() {
        // Pinned, so the bar's disappearance can only be the tap's doing and never the
        // idle fade arriving mid-assertion.
        app.launchArguments += ["-uiTestPinReadingBar"]
        app.launch()
        _ = page()
        enterReadingMode()
        waitForProbe("bar on")

        pageProbe().tap()
        waitForProbe("bar off")
        XCTAssertFalse(readingBar().exists,
                       dump("a hidden bar is still in the accessibility tree"))

        pageProbe().tap()
        waitForProbe("bar on")
        XCTAssertTrue(appears(readingBar(), timeout: 5),
                      dump("a second tap did not bring the bar back"))

        let before = committedZoom()
        pageProbe().doubleTap()
        Thread.sleep(forTimeInterval: 1)
        XCTAssertGreaterThan(committedZoom(), before + 0.5,
                             dump("double-tap zoom stopped working inside reading mode"))
    }

    /// The invariant the whole feature is built on (plan §1): leaving comes back to the
    /// page view at the same place. Zoom is the part a probe can state exactly.
    func testExitComesBackToTheChromeAndToTheSamePlace() {
        app.launchArguments += ["-uiTestPinReadingBar"]
        app.launch()
        _ = page()

        // Somewhere that is not the default, so "the same place" is a claim with content.
        pageProbe().doubleTap()
        let zoomBefore = waitForZoom(above: 1.5)
        let pageBefore = readingProbe().components(separatedBy: " page ").last ?? ""

        enterReadingMode()
        XCTAssertEqual(committedZoom(), zoomBefore, accuracy: 0.0001,
                       dump("entering reading mode moved the page"))

        app.buttons["readingExit"].firstMatch.tap()
        waitForProbe("reading off")

        XCTAssertTrue(appears(app.buttons["Close"].firstMatch, timeout: 5),
                      dump("the chrome did not come back"))
        XCTAssertEqual(committedZoom(), zoomBefore, accuracy: 0.0001,
                       dump("leaving reading mode moved the page"))
        XCTAssertEqual(readingProbe().components(separatedBy: " page ").last ?? "", pageBefore,
                       dump("leaving reading mode landed on a different page"))
        // Nothing was unsaved by any of it, so nothing was asked on the way out.
        XCTAssertFalse(app.alerts.firstMatch.exists,
                       dump("leaving reading mode asked a question"))
    }

    /// The second way out, and the ladder it shares with the first (plan §7). The find bar
    /// is the one piece of chrome allowed over the reading view, so the first swipe must
    /// close *it* — a swipe that took the mode away under an open find bar is how a user
    /// loses what they were in the middle of.
    func testTheEdgeSwipeClosesTheFindBarFirstAndThenLeavesReadingMode() {
        app.launchArguments += ["-uiTestPinReadingBar"]
        app.launch()
        _ = page()
        enterReadingMode()

        app.buttons["readingFind"].firstMatch.tap()
        let done = app.buttons["Done"].firstMatch
        XCTAssertTrue(appears(done, timeout: 5),
                      dump("Find is unreachable from inside reading mode (#506)"))

        swipeFromTheLeadingEdge()
        XCTAssertFalse(appears(done, timeout: 2),
                       dump("the first swipe did not close the find bar"))
        XCTAssertTrue(readingProbe().contains("reading on"),
                      dump("the first swipe left reading mode with the find bar still open"))

        swipeFromTheLeadingEdge()
        waitForProbe("reading off")
        XCTAssertTrue(appears(app.buttons["Close"].firstMatch, timeout: 5),
                      dump("the chrome did not come back after the edge swipe"))
    }

    /// The rule that decides whether reading mode is usable by the people it is for. Four
    /// seconds is twice the idle timeout, so a bar that was going to fade has had two
    /// chances to.
    func testTheBarNeverFadesWhileAScreenReaderIsRunning() {
        app.launchArguments += ["-uiTestPinReadingBar"]
        app.launch()
        _ = page()
        enterReadingMode()

        let bar = readingBar()
        XCTAssertTrue(appears(bar, timeout: 5), dump("no floating bar to pin"))
        Thread.sleep(forTimeInterval: 4)
        XCTAssertTrue(bar.exists,
                      dump("the bar faded with a screen reader running (#506) — a VoiceOver "
                           + "user has no way to ask for it back"))
        XCTAssertTrue(readingProbe().contains("bar on"))
    }

    /// And the other half of the same rule: without a screen reader it does fade, which is
    /// what makes the chrome-free view chrome-free.
    func testTheBarFadesOnItsOwnWhenNoScreenReaderIsRunning() {
        app.launch()
        _ = page()
        enterReadingMode()
        // Deliberately not asserting that the bar was up first. It is — the mode shows it
        // on entry — but the idle timeout is two seconds and an accessibility-tree query
        // is not always faster than that, so an "it appeared" assertion here would be a
        // race against the very fade the test is about. That the bar appears at all is
        // `testTheBarNeverFades…`'s and `testASingleTap…`'s business, both of which pin it.
        // Nothing here touches the screen, so the only thing that can take the bar away is
        // the fade.
        waitForProbe("bar off", timeout: 8)
        XCTAssertFalse(readingBar().exists,
                       dump("a faded bar is still in the accessibility tree"))
    }

    /// The iPad's own entry point (#172 gave it a tool strip; #506 puts reading mode on
    /// it). A no-op assertion on a phone, which has no strip and keeps the ⋯ row as its
    /// only way in — this suite runs on one of each width class for exactly that reason,
    /// the same split #465 ended up needing.
    func testOnARegularWidthLayoutTheToolStripCarriesReadingModeToo() {
        app.launch()
        _ = page()
        let strip = app.otherElements["viewerToolStrip"].firstMatch
        guard appears(strip, timeout: 5) else {
            // Compact width: the ⋯ row is the entry point, and every other test here
            // drives it.
            XCTAssertTrue(app.buttons["viewerMore"].firstMatch.exists,
                          dump("no tool strip and no ⋯ menu"))
            return
        }
        let button = app.buttons["viewerReadingModeButton"].firstMatch
        XCTAssertTrue(appears(button, timeout: 5),
                      dump("the iPad's tool strip has no Reading mode button (#506/#172)"))
        button.tap()
        waitForProbe("reading on")
        XCTAssertFalse(app.otherElements["viewerToolStrip"].firstMatch.exists,
                       dump("the iPad's tool strip is still reachable inside reading mode"))
    }

    // MARK: - tier 2

    /// The Settings sheet exists, carries the Reading section, and says what night does to
    /// pictures — which is a decision (#168 decision 3), and the copy is where it is kept.
    func testTheReadingSettingsSayWhatNightDoesToPictures() {
        app.launch()
        _ = page()

        app.buttons["viewerMore"].firstMatch.tap()
        let row = app.buttons["viewerSettings"].firstMatch
        XCTAssertTrue(appears(row, timeout: 5), dump("the ⋯ menu has no Settings row"))
        row.tap()

        let pageColours = app.descendants(matching: .any)
            .matching(NSPredicate(format: "label CONTAINS 'Page colours'")).firstMatch
        XCTAssertTrue(appears(pageColours, timeout: 5),
                      dump("Settings has no Page colours control"))
        XCTAssertTrue(app.switches["settingsOpenInReadingMode"].firstMatch.exists,
                      dump("Settings has no Open documents in reading mode toggle"))
        XCTAssertTrue(app.staticTexts["Night inverts the page, pictures included."].exists,
                      dump("the night trade-off is not stated in the settings copy (#512)"))
    }

    // MARK: - gestures

    /// A drag that starts on the leading edge, which is what
    /// `UIScreenEdgePanGestureRecognizer` waits for. Slow and deliberate: a flick from the
    /// very edge is often delivered as a swipe with no beginning.
    private func swipeFromTheLeadingEdge() {
        let start = app.coordinate(withNormalizedOffset: CGVector(dx: 0.001, dy: 0.5))
        let end = app.coordinate(withNormalizedOffset: CGVector(dx: 0.8, dy: 0.5))
        start.press(forDuration: 0.1, thenDragTo: end)
        Thread.sleep(forTimeInterval: 0.6)
    }
}
