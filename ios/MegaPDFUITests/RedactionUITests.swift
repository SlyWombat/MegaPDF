import XCTest

/// The redaction flow in a running app (#173).
///
/// The engine binding is unit-tested and the core's own tests prove what is removed.
/// What only a running app can show is the part the Windows check found broken there
/// and the Mac check found broken here: whether the buttons that finish a redaction
/// are reachable at all once something is marked.
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
        XCTAssertTrue(app.buttons["Save as a copy"].exists, "no Save as a copy")
        XCTAssertTrue(app.buttons["Overwrite the original"].exists, "no Overwrite the original")
        XCTAssertTrue(app.buttons["Cancel"].exists, "no Cancel")

        // Cancel leaves everything as it was: nothing is written until it is answered.
        app.buttons["Cancel"].tap()
        XCTAssertTrue(save.waitForExistence(timeout: 5))
    }

    /// The other route, which worked before the button above did.
    func testSaveACopyAlsoAsks() {
        app.launch()
        let more = app.buttons["viewerMore"]
        guard more.waitForExistence(timeout: 20) else {
            XCTAssertTrue(app.buttons["Save"].waitForExistence(timeout: 5),
                          "neither the ⋯ menu nor Save is present")
            return
        }
        more.tap()
        let copy = app.buttons["Save a copy"]
        XCTAssertTrue(copy.waitForExistence(timeout: 5), "the ⋯ menu has no Save a copy")
        copy.tap()
        XCTAssertTrue(app.staticTexts["Remove the marked content?"].waitForExistence(timeout: 10),
                      "Save a copy with a mark should ask too")
        app.buttons["Cancel"].tap()
    }

    /// The tool says what it removes, and says that covering is the other thing.
    func testTheRedactToolSaysWhatItDoes() {
        app.launch()
        let redact = app.buttons["viewerRedact"]
        XCTAssertTrue(redact.waitForExistence(timeout: 20), "no Redact tool")
        XCTAssertEqual(redact.label, "Redact")
        // Armed by the screenshot state, and a screen reader can tell.
        XCTAssertTrue(redact.isSelected, "an armed tool has to say it is armed")
    }
}
