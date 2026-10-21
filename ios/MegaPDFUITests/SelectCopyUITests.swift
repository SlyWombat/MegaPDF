import XCTest

/// Long-pressing a word on the page selects it, the edit menu offers Copy, and Copy puts the
/// word on the pasteboard as plain text (#710). The document is the core's `selection.pdf`
/// (two columns, 612 x 792 pt; the committed copy in MegaPDFTests, handed to the app in the
/// launch environment like `FormTextUITests`): "Left two bravo and more" is the second line of
/// the left column, baseline 660 pt from the bottom, set in 12 pt at x = 72.
///
/// The paste-back reads `UIPasteboard` from the test runner, which is a different process from
/// the app: iOS may ask the runner for "Allow Paste", which `readPasteboard` answers.
final class SelectCopyUITests: XCTestCase {

    private var app: XCUIApplication!

    override func setUp() {
        continueAfterFailure = false
        app = XCUIApplication()
        app.launchArguments += ["-AppleLanguages", "(en)", "-AppleLocale", "en_US", "-uiTestPinNotices"]
    }

    override func tearDown() {
        app?.terminate()
        app = nil
    }

    private func launch() throws -> XCUIElement {
        let bundle = Bundle(for: Self.self)
        let url = try XCTUnwrap(bundle.url(forResource: "selection", withExtension: "pdf"),
                                "selection.pdf is not in the UI test bundle")
        app.launchEnvironment["MEGAPDF_UITEST_PDF_BASE64"] = try Data(contentsOf: url).base64EncodedString()
        app.launch()
        let page = app.descendants(matching: .any).matching(NSPredicate(format: "label == %@", "Page 1")).firstMatch
        XCTAssertTrue(DocumentOpening.wait(for: page, in: app), DocumentOpening.why(app, "the test document did not open"))
        return page
    }

    /// A point on the page in PDF points, origin bottom-left.
    private func point(_ page: XCUIElement, x: Double, y: Double) -> XCUICoordinate {
        page.coordinate(withNormalizedOffset: CGVector(dx: x / 612.0, dy: (792.0 - y) / 792.0))
    }

    /// "bravo" in "Left two bravo and more": about 8.5 characters of 6.7 pt in from x = 72.
    private func longPressBravo(_ page: XCUIElement) {
        point(page, x: 72 + 8.5 * 6.7, y: 664).press(forDuration: 0.9)
    }

    func testLongPressSelectsAWordAndCopyPutsItOnThePasteboard() throws {
        UIPasteboard.general.string = "untouched"
        let page = try launch()
        longPressBravo(page)

        // The selection's two handles are on the page, and the system menu offers Copy.
        XCTAssertTrue(app.descendants(matching: .any)["selectionHandleStart"].waitForExistence(timeout: 15),
                      "a long press on a word did not select it")
        XCTAssertTrue(app.descendants(matching: .any)["selectionHandleEnd"].exists)
        let copy = app.descendants(matching: .any).matching(NSPredicate(format: "label == 'Copy'")).firstMatch
        XCTAssertTrue(copy.waitForExistence(timeout: 15), "the edit menu never offered Copy")
        XCTAssertTrue(copy.isEnabled)
        copy.tap()

        XCTAssertEqual(readPasteboard(), "bravo", "Copy puts the selected word on the pasteboard as plain text")
    }

    /// Reads the pasteboard on a background thread while this one answers the system's "Allow
    /// Paste" prompt, which the runner raises for text another app copied: read on the main
    /// thread, the read blocks on the prompt that only this thread can tap (measured: the run
    /// hung for eighteen minutes).
    private func readPasteboard() -> String? {
        var result: String?
        let done = DispatchSemaphore(value: 0)
        DispatchQueue.global().async {
            result = UIPasteboard.general.string
            done.signal()
        }
        let springboard = XCUIApplication(bundleIdentifier: "com.apple.springboard")
        let deadline = Date().addingTimeInterval(30)
        while Date() < deadline {
            if done.wait(timeout: .now() + 0.5) == .success { return result }
            let allow = springboard.buttons["Allow Paste"]
            if allow.exists { allow.tap() }
        }
        return nil
    }

    func testATapPutsTheSelectionAway() throws {
        let page = try launch()
        longPressBravo(page)
        let handle = app.descendants(matching: .any)["selectionHandleStart"]
        XCTAssertTrue(handle.waitForExistence(timeout: 15))
        // A tap in blank paper dismisses the selection and does nothing else. The first tap may
        // be spent on dismissing the edit menu, as it is in any iOS text view, so tap until the
        // handle goes.
        point(page, x: 300, y: 300).tap()
        if handle.waitForNonExistence(timeout: 3) { return }
        print("TREE-AFTER-TAP1", app.debugDescription)
        point(page, x: 300, y: 300).tap()
        let gone = NSPredicate(format: "exists == false")
        expectation(for: gone, evaluatedWith: handle)
        waitForExpectations(timeout: 10)
    }
}
