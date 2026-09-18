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

    /// The tool says what it is, and says when it is armed — which is all a screen
    /// reader has to go on, the mark itself being a faint translucent band.
    func testTheRedactToolSaysWhatItIsAndWhenItIsArmed() {
        app.launch()
        let redact = app.buttons["viewerRedact"]
        XCTAssertTrue(redact.waitForExistence(timeout: 20), "no Redact tool")
        XCTAssertEqual(redact.label, "Redact")

        // The screenshot state arms it a moment after the mark lands.
        let armed = expectation(for: NSPredicate(format: "isSelected == true"),
                                evaluatedWith: redact)
        wait(for: [armed], timeout: 10)
    }
}
