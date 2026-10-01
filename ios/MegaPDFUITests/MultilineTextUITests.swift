import XCTest

/// What the Add text field actually hands back for a line break (#4), read out of a running
/// app rather than assumed.
///
/// This exists because the same thing went wrong twice. Windows' `TextBox` returned a lone
/// `"\r"` (#564) and Android's field needed the same fold (#565), and both times a two-line
/// note silently stayed **one** text box with a control character inside it — the kind of
/// failure that looks like it worked. Rather than make a third guess about a toolkit, the
/// sheet carries a probe under `-uiTestTextProbes` that prints the field's contents as code
/// points, and this types a return into it and reads them.
///
/// The probe is inside the sheet on purpose: a presented sheet takes the accessibility tree
/// with it, so a probe behind it could not be queried while the field has focus. It is the
/// same device as #531's `viewerPinchProbe` and #540's `viewerReadingProbe` — the app saying
/// what it actually did, so a failure can tell "the gesture never arrived" from "it arrived
/// and did nothing".
final class MultilineTextUITests: XCTestCase {

    private var app: XCUIApplication!

    override func setUp() {
        continueAfterFailure = false
        app = XCUIApplication()
        // `-screenshot text` opens the demo agreement with the Add text sheet already up on a
        // chosen point, which is the state this needs and the one the capture rig poses.
        app.launchArguments = ["-screenshot", "text", "-uiTestTextProbes",
                               "-AppleLanguages", "(en)", "-AppleLocale", "en_US"]
    }

    func testTheFieldHandsBackANewlineForAReturnAndNotACarriageReturn() {
        app.launch()

        // By identifier across every type: a SwiftUI `TextField(axis: .vertical)` does not
        // necessarily bridge as `.textField`, and which element type it lands on is not the
        // thing under test.
        let field = app.descendants(matching: .any).matching(identifier: "textBoxField").firstMatch
        XCTAssertTrue(field.waitForExistence(timeout: 30), "the Add text sheet did not open")
        let probe = app.staticTexts["textBoxDraftProbe"]
        XCTAssertTrue(probe.waitForExistence(timeout: 10), "the draft probe is missing")
        let before = probe.label
        XCTAssertFalse(before.isEmpty, "the probe should report the prefilled name's code points")

        field.tap()
        // One return, then a word, so the probe shows what the break became *and* that typing
        // carried on in the same field rather than ending the edit.
        field.typeText("\nsecond")

        let deadline = Date().addingTimeInterval(10)
        while probe.label == before, Date() < deadline { Thread.sleep(forTimeInterval: 0.2) }
        let after = probe.label

        XCTAssertNotEqual(after, before,
                          "typing changed nothing: the return closed the field instead of starting a line")
        XCTAssertTrue(after.contains("000A"),
                      "a return in this field produced no newline — code points: \(after)")
        XCTAssertFalse(after.contains("000D"),
                       "the field hands back a CARRIAGE RETURN, the Windows trap (#564) — code points: \(after)")
        XCTAssertFalse(after.contains("2028"),
                       "the field hands back LINE SEPARATOR rather than a newline — code points: \(after)")
        XCTAssertFalse(after.contains("2029"),
                       "the field hands back PARAGRAPH SEPARATOR rather than a newline — code points: \(after)")
        // Whatever the break turned out to be, exactly one of them: a field that inserted two
        // would make a blank line, and a blank line would be an empty text box.
        let breaks = after.split(separator: " ").filter { $0 == "000A" }.count
        XCTAssertEqual(breaks, 1, "one return should be one line break — code points: \(after)")
    }
}
