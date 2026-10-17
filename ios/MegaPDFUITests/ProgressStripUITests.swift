import XCTest

/// The busy strip's bindings, in a running app (#145): a determinate bar, a count that says
/// which page a scan is on, and a Stop a finger can reach.
///
/// The view model's side is unit-tested (`ProgressAndStopTests`). What only a running app can
/// show is the part #412's lesson was about — the view model can be right while the bindings
/// are not — and, on a phone, whether the button in a strip that thin is actually hittable.
///
/// `-uiTestSlowSearch` holds each page of the scan until the test presses Stop, because every
/// document that ships in the bundle is scanned inside the strip's own 0.5 s threshold and there
/// would otherwise be nothing on screen to look at.
///
/// It holds until released and not for a duration, and that took three attempts (#674). The demo
/// document is ONE page, so a hold of N seconds per page is a total budget of N seconds, inside
/// which this test makes about eight accessibility round trips before it can tap Stop. Three
/// seconds lost that race on a CI runner ("Failed to get matching snapshot"); ten seconds was
/// raised to fix it and lost it again, three runs in four, on hosted runners that are three to
/// four times slower than the Mac mini. A scan that finishes first is not stopped, so the failure
/// reads as a missing "Search stopped." or a missing strip, which is the strip correctly gone.
/// Stop cancelling the search is the release, so there is no number to get wrong; a 120 s ceiling
/// in the app only keeps a failed assertion from hanging the job.
final class ProgressStripUITests: XCTestCase {

    private var app: XCUIApplication!

    override func setUp() {
        continueAfterFailure = false
        app = XCUIApplication()
        app.launchArguments = ["-screenshot", "viewer", "-uiTestSlowSearch",
                               "-AppleLanguages", "(en)", "-AppleLocale", "en_US"]
    }

    func testAScanShowsItsCountAndCanBeStoppedFromTheStrip() {
        app.launch()

        let page = app.descendants(matching: .any)
            .matching(NSPredicate(format: "label == 'Page 1'")).firstMatch
        XCTAssertTrue(DocumentOpening.wait(for: page, in: app), DocumentOpening.why(app, "the demo document did not open"))

        app.buttons["Find in document"].tap()
        let field = app.textFields["Find in document"]
        XCTAssertTrue(field.waitForExistence(timeout: 10), "no find field")
        field.tap()
        field.typeText("the")

        // By identifier across every type: which element type a SwiftUI stack with an
        // accessibility identifier bridges to is not the thing under test.
        let strip = app.descendants(matching: .any).matching(identifier: "busyStrip").firstMatch
        if !strip.waitForExistence(timeout: 15) {
            XCTFail("a scan that takes seconds showed no busy strip. Tree: " + app.debugDescription)
        }
        let count = app.descendants(matching: .any).matching(identifier: "busyCount").firstMatch
        XCTAssertTrue(count.waitForExistence(timeout: 15),
                      "the strip has no count line, so it never says where the scan has got to")
        XCTAssertTrue(count.label.range(of: #"Page \d+ of \d+"#, options: .regularExpression) != nil,
                      "the count should name the page and the total; it said \(count.label)")

        let stop = app.buttons["busyStop"]
        XCTAssertTrue(stop.waitForExistence(timeout: 10), "a scan this long offers no way out")
        XCTAssertTrue(stop.isEnabled, "the Stop is there but insensitive while work is running")
        // The finger test, not the pointer one: a button in a strip this thin has to be a real
        // target. 44 points is the platform's own floor.
        XCTAssertGreaterThanOrEqual(stop.frame.height, 44,
                                    "the Stop is too short for a finger: \(stop.frame)")
        XCTAssertTrue(stop.isHittable, "the Stop cannot be tapped where it is")

        stop.tap()

        XCTAssertTrue(app.staticTexts["Search stopped."].waitForExistence(timeout: 15),
                      "stopping a scan said nothing")
        // And what it found is still on screen: the find bar's own count, not "No results".
        XCTAssertFalse(app.staticTexts["No results"].exists,
                       "a stopped scan threw away the matches it had already found")
        let gone = XCTNSPredicateExpectation(
            predicate: NSPredicate(format: "exists == false"), object: strip)
        XCTAssertEqual(XCTWaiter.wait(for: [gone], timeout: 15), .completed,
                       "the strip stayed up after the work it was reporting stopped")
    }
}
