import XCTest

/// The redaction flow in a running app (#173).
///
/// The engine binding is unit-tested and the core's own tests prove what is removed.
/// What only a running app can show is the part the Windows check found broken there
/// and this pass found broken here and on the Mac: whether the button that finishes a
/// redaction is reachable at all once something is marked. It was not — a mark leaves
/// the document clean by design, and Save was disabled on `isDirty` alone.
///
/// `-screenshot redact` opens the demo agreement with one line marked and the tool
/// armed, which is the state a person is in when they reach for Save.
///
/// In the MegaPDFDemo scheme with the other UI tests, so `xcodebuild test -scheme
/// MegaPDF` in CI does not wait on a simulator launch.
final class RedactionUITests: XCTestCase {

    private var app: XCUIApplication!

    override func setUp() {
        continueAfterFailure = false
        app = XCUIApplication()
        // Explicitly English: this one asserts wording.
        app.launchArguments = ["-screenshot", "redact",
                               "-AppleLanguages", "(en)", "-AppleLocale", "en_US"]
    }

    /// A confirmation dialog's buttons are not always in `app.buttons` on every idiom —
    /// on the phone it is an action sheet — so they are looked for anywhere in the app.
    private func button(_ label: String) -> XCUIElement {
        app.descendants(matching: .button)[label]
    }

    func testSaveIsReachableWithAMarkAndAsksBeforeRemoving() {
        app.launch()

        let save = app.buttons["Save"]
        XCTAssertTrue(save.waitForExistence(timeout: 20), "no Save button")
        // The point: a mark is not a change to the document, so the document is clean —
        // and Save still has to be the way to finish what was started.
        XCTAssertTrue(save.isEnabled,
                      "Save is disabled with an area marked, so the redaction cannot be finished from it")

        save.tap()

        XCTAssertTrue(app.staticTexts["Remove the marked content?"].waitForExistence(timeout: 10),
                      "Save with a mark should ask before removing anything")
        XCTAssertTrue(button("Save as a copy").waitForExistence(timeout: 5),
                      "the question has no Save as a copy")
        XCTAssertTrue(button("Overwrite the original").exists,
                      "the question has no Overwrite the original")
        XCTAssertTrue(app.staticTexts["Redaction permanently removes the marked content. This can't be undone after saving."].exists,
                      "the question does not say what it does")
    }

    /// The tool says what it is and whether it is armed — from the ⋯ menu, which is where
    /// it lives now (#328) — and it is armed the way a person arms it, through the menu,
    /// rather than posed by the launch state: the row's own state is what is under test.
    ///
    /// Two checks in one, because #328 moves the tool and must not lose the state #173
    /// added: it is not on the bottom bar any more, and it is still legible.
    ///
    /// The armed state used to ride on the bottom-bar button's accessibility value,
    /// which was measured to arrive there while `.isSelected` was dropped. A menu row
    /// is a different bridge — the state is the row's own (a Toggle's, in the platform's
    /// menu vocabulary) — so the row now says it twice, and this accepts either: the
    /// promise is that a screen-reader user is told, not which API carries it. A row
    /// that said nothing at all fails, in both directions: off is a state too.
    ///
    /// Not measured on a device yet: this file is in the MegaPDFDemo scheme, which CI
    /// does not build. The first Mac run of it is what proves the menu bridge.
    func testTheRedactToolIsInTheMenuAndSaysWhetherItIsArmed() {
        app.launch()

        // The move itself: a bottom bar with Redact on it again is #328 coming back.
        XCTAssertFalse(app.buttons["viewerRedact"].exists,
                       "Redact is on the bottom bar again; it belongs in the ⋯ menu (#328)")

        let page = app.descendants(matching: .any)
            .matching(NSPredicate(format: "label == 'Page 1'")).firstMatch
        XCTAssertTrue(page.waitForExistence(timeout: 30), "the demo document did not open")

        XCTAssertTrue(waitForArmed(false),
                      "an unarmed Redact row says nothing about its state")
        openMore().tap()                       // the row: arm it
        XCTAssertTrue(waitForArmed(true),
                      "arming Redact does not tell a screen reader it is armed")
        openMore().tap()                       // the row again: disarm
        XCTAssertTrue(waitForArmed(false),
                      "disarming Redact does not reach a screen reader either")
    }

    // MARK: - the ⋯ menu

    /// Opens the More menu and returns its Redact row.
    ///
    /// Found by its name rather than by identifier: a menu row's title is what the menu
    /// is built from, so it is the one thing the bridge always carries. `viewerRedact`
    /// is set on the row as well, for anything that can use it.
    private func openMore() -> XCUIElement {
        let more = app.buttons["viewerMore"]
        XCTAssertTrue(more.waitForExistence(timeout: 20), "no More menu")
        more.tap()
        let redact = app.buttons["Redact"]
        XCTAssertTrue(redact.waitForExistence(timeout: 10), "the ⋯ menu has no Redact row")
        return redact
    }

    /// Waits for the row to report its state, reopening the menu between reads: the row
    /// exists only while the menu is open, and the armed state arrives with a SwiftUI
    /// update after the tap or after the launch arms the tool.
    private func waitForArmed(_ want: Bool, timeout: TimeInterval = 20) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            let row = openMore()
            if (row.isSelected || row.value as? String == "On") == want { return true }
            // Anywhere closes the menu. On the page a plain tap marks nothing: a mark
            // is a drag, and a tap that short makes none.
            app.tap()
            Thread.sleep(forTimeInterval: 0.4)
        }
        return false
    }
}
