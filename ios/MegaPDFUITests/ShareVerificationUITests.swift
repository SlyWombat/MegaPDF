import XCTest

/// #378: drives the real app to prove Share actually produces a working
/// `UIActivityViewController` from a real file, on both idioms — the iPad popover-crash risk is
/// the specific thing this exists to catch — and that the unsaved-changes alert offers the right
/// wording for Share.
///
/// Skipped unless `SHARE_CHECK=1` (xcodebuild passes it as `TEST_RUNNER_SHARE_CHECK`), because
/// it needs a PDF already staged at "On My iPhone"/"On My iPad" named `share-check.pdf` — the
/// same `local_storage()` recipe `tools/ios-files-e2e.sh` uses. Not run by CI's default
/// `-scheme MegaPDF` scheme, the same as `FilesEndToEndUITests`.
final class ShareVerificationUITests: XCTestCase {

    private var app: XCUIApplication!

    override func setUpWithError() throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["SHARE_CHECK"] == "1",
                          "needs share-check.pdf staged at On My iPhone/iPad")
        continueAfterFailure = false
        app = XCUIApplication()
        app.launchArguments = ["-AppleLanguages", "(en)", "-AppleLocale", "en_US"]
        app.launch()
    }

    private func snap(_ name: String) {
        let attachment = XCTAttachment(screenshot: app.screenshot())
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }

    private func open(_ name: String) {
        let openButton = app.buttons["Open PDF"].firstMatch
        XCTAssertTrue(openButton.waitForExistence(timeout: 20), "not on the home screen")
        openButton.tap()
        let file = app.descendants(matching: .any)
            .matching(NSPredicate(format: "label == %@ OR label BEGINSWITH %@", name, name + ",")).firstMatch
        var found = file.waitForExistence(timeout: 6)
        if !found {
            for place in ["Browse", "On My iPhone", "On My iPad"] {
                let item = app.descendants(matching: .any).matching(NSPredicate(format: "label == %@", place)).firstMatch
                if item.waitForExistence(timeout: 4) { item.tap() }
                if file.waitForExistence(timeout: 4) { found = true; break }
            }
        }
        XCTAssertTrue(found, "\(name) is not visible in the Files picker")
        file.tap()
    }

    private func more(_ item: String) {
        let more = app.buttons["viewerMore"].firstMatch
        XCTAssertTrue(more.waitForExistence(timeout: 10), "no More menu")
        more.tap()
        let button = app.buttons[item].firstMatch
        XCTAssertTrue(button.waitForExistence(timeout: 5), "no \(item) in More")
        button.tap()
    }

    /// The core iPad risk (#378): tapping Share must not crash for lack of a popover anchor,
    /// and the resulting sheet has to actually be reachable (not off-screen or zero-size).
    func testShareShowsAWorkingActivitySheet() throws {
        open("share-check")
        sleep(3)
        snap("01-document-open")
        more("Share")
        sleep(2)
        snap("02-after-tapping-share")

        XCTAssertEqual(app.state, .runningForeground, "the app crashed or backgrounded")
        let stillShowingMore = app.buttons["Password…"].firstMatch.exists
        XCTAssertFalse(stillShowingMore, "the More menu is still up — Share did not present anything")
    }

    /// Fable's #378 wording fix, 2026-09-26: with unsaved changes, Share's second button reads
    /// "Share without saving", not "Discard" — proven against the real dialog, not only the
    /// model's routing.
    func testShareWithUnsavedChangesOffersShareWithoutSaving() throws {
        open("share-check")
        sleep(3)
        // Dirty the document: Add text, tap a spot on the page, type something, commit.
        app.buttons["Add text"].firstMatch.tap()
        sleep(1)   // "Tap the page where the text should go"
        let page = app.descendants(matching: .any).matching(NSPredicate(format: "label == 'Page 1'")).firstMatch
        XCTAssertTrue(page.waitForExistence(timeout: 10), "no Page 1 to tap")
        page.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).tap()
        let field = app.textFields["Text"].firstMatch
        XCTAssertTrue(field.waitForExistence(timeout: 10), "the Add text sheet did not open")
        field.tap()
        field.typeText("share-check")
        app.buttons["Add"].firstMatch.tap()
        sleep(1)

        more("Share")
        snap("03-unsaved-changes-alert-for-share")

        XCTAssertTrue(app.staticTexts["This document has unsaved changes. They won't be in the shared copy unless you save first."]
            .waitForExistence(timeout: 5), "wrong/missing message for the Share case")
        let shareWithoutSaving = app.buttons["Share without saving"].firstMatch
        XCTAssertTrue(shareWithoutSaving.waitForExistence(timeout: 5), "no \"Share without saving\" button")
        XCTAssertFalse(app.buttons["Discard"].firstMatch.exists, "\"Discard\" must not appear for Share")

        shareWithoutSaving.tap()
        sleep(2)
        snap("04-after-share-without-saving")
        XCTAssertEqual(app.state, .runningForeground, "the app crashed or backgrounded")
    }
}
