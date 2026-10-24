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

    private func launch(_ fixture: String = "selection") throws -> XCUIElement {
        let bundle = Bundle(for: Self.self)
        let url = try XCTUnwrap(bundle.url(forResource: fixture, withExtension: "pdf"),
                                "\(fixture).pdf is not in the UI test bundle")
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

    /// Sends VoiceOver's adjust gesture (swipe up = increment, swipe down = decrement) to an
    /// element, the way assistive technology does, and returns whether it was delivered.
    ///
    /// **XCTest has no public API for this.** `XCUIElement` can adjust a slider or a picker
    /// wheel (`adjust(toNormalizedSliderPosition:)`), both of which refuse any other element
    /// type, and the handle is an `Other`. What those two send underneath is the accessibility
    /// client's increment and decrement action, so this sends the same action to the handle
    /// through the same client, found by name. If a future Xcode renames them, this returns
    /// false and the test fails saying so, rather than skipping.
    private func adjust(_ element: XCUIElement, forward: Bool) -> Bool {
        typealias ActionFactory = @convention(c) (AnyClass, Selector, Int) -> AnyObject?
        typealias Perform = @convention(c) (AnyObject, Selector, AnyObject, AnyObject, AnyObject?, UnsafeMutableRawPointer?) -> Bool
        guard element.waitForExistence(timeout: 10),
              let snapshot = element.value(forKey: "lastSnapshot") as? NSObject,
              snapshot.responds(to: NSSelectorFromString("accessibilityElement")),
              let axElement = snapshot.value(forKey: "accessibilityElement") as? NSObject,
              let platform = axElement.value(forKey: "originPlatform") as? Int,
              let actions = NSClassFromString("XCUIAccessibilityAction") as? NSObject.Type,
              let client = XCUIDevice.shared.value(forKey: "accessibilityInterface") as? NSObject
        else { return false }
        let factory = NSSelectorFromString(forward ? "incrementActionForPlatform:" : "decrementActionForPlatform:")
        let perform = NSSelectorFromString("performAction:onElement:value:error:")
        guard actions.responds(to: factory), client.responds(to: perform),
              let make = actions.method(for: factory),
              let send = client.method(for: perform)
        else { return false }
        let action = unsafeBitCast(make, to: ActionFactory.self)(actions, factory, platform)
        guard let action else { return false }
        return unsafeBitCast(send, to: Perform.self)(client, perform, action, axElement, nil, nil)
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

    /// VoiceOver cannot drag a handle, so the handle is adjustable (#710): one step of the
    /// adjust gesture moves that end by a word. `selection.pdf`'s line is "Left two bravo and
    /// more", so the end handle of "bravo" steps to "bravo and".
    func testTheEndHandleExtendsByAWordWithoutADrag() throws {
        UIPasteboard.general.string = "untouched"
        let page = try launch()
        longPressBravo(page)
        let end = app.descendants(matching: .any)["selectionHandleEnd"]
        XCTAssertTrue(end.waitForExistence(timeout: 15), "a long press on a word did not select it")
        XCTAssertEqual(end.value as? String, "bravo", "the handle's value is the selected text")

        XCTAssertTrue(adjust(end, forward: true), "the end handle does not offer the adjust action")
        let extended = NSPredicate(format: "value == %@", "bravo and")
        expectation(for: extended, evaluatedWith: end)
        waitForExpectations(timeout: 10)

        let copy = app.descendants(matching: .any).matching(NSPredicate(format: "label == 'Copy'")).firstMatch
        XCTAssertTrue(copy.waitForExistence(timeout: 15), "the edit menu never offered Copy after the step")
        copy.tap()
        XCTAssertEqual(readPasteboard(), "bravo and", "Copy puts the stepped selection on the pasteboard")
    }

    /// A hardware keyboard's Command-C reaches the selection through the responder chain (#710),
    /// with no menu involved: only an iPad has one to type on.
    func testCommandCCopiesTheSelection() throws {
        try XCTSkipUnless(UIDevice.current.userInterfaceIdiom == .pad,
                          "Command-C is a hardware keyboard's; the phones have none to type on")
        UIPasteboard.general.string = "untouched"
        let page = try launch()
        longPressBravo(page)
        XCTAssertTrue(app.descendants(matching: .any)["selectionHandleStart"].waitForExistence(timeout: 15),
                      "a long press on a word did not select it")
        // The edit menu is up, as it is when a person reaches for the keyboard; the key must
        // not need it dismissed, and must not need Copy to be tapped.
        let copy = app.descendants(matching: .any).matching(NSPredicate(format: "label == 'Copy'")).firstMatch
        XCTAssertTrue(copy.waitForExistence(timeout: 15), "the edit menu never offered Copy")

        app.typeKey("c", modifierFlags: .command)
        XCTAssertEqual(readPasteboard(), "bravo", "Command-C puts the selected word on the pasteboard as plain text")
    }

    /// A document that forbids copying still selects, but its Copy item is disabled and the
    /// reason is shown (#710). `owner-only.pdf` opens without a password and allows nothing; its
    /// first page is a 24 pt title at x = 72, baseline 720 pt from the bottom: "MegaPDF engine
    /// fixture - page 1", "engine" spanning x = 187 to 259.
    func testANoCopyDocumentSelectsButCopyIsDisabled() throws {
        UIPasteboard.general.string = "untouched"
        let page = try launch("owner-only")
        point(page, x: 222, y: 727).press(forDuration: 0.9)

        XCTAssertTrue(app.descendants(matching: .any)["selectionHandleStart"].waitForExistence(timeout: 15),
                      "a long press on a word of a no-copy document did not select it")
        let copy = app.descendants(matching: .any).matching(NSPredicate(format: "label == 'Copy'")).firstMatch
        XCTAssertTrue(copy.waitForExistence(timeout: 15), "the edit menu has no Copy item (it should be there, disabled)")
        // Not `copy.isEnabled`: XCUITest reports a UIKit-disabled edit-menu item as enabled
        // (measured: the snapshot says enabled = 1 while the screenshot shows Copy greyed), so
        // the check is what a person's tap on it does, and the notice below.
        copy.tap()

        let banner = app.staticTexts["noticeBanner"]
        XCTAssertTrue(banner.waitForExistence(timeout: 15), "nothing says why Copy is disabled")
        XCTAssertEqual(banner.label, "The author of this document asked that nothing be copied out of it.")
        XCTAssertEqual(readPasteboard(), "untouched", "tapping the disabled Copy must copy nothing")
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
