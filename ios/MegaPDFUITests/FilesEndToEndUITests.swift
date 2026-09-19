import XCTest

/// The file flows only a real file can exercise, through the real Files picker (#146).
///
/// Every other UI test opens a document from the bundle or from bytes in the launch
/// environment, with no file behind it, so none of them can save, save a copy, change the
/// password or open a large file. These do: `tools/ios-files-e2e.sh` stages the fixtures in
/// the simulator's "On My iPhone", runs one test at a time, and between them reads what the
/// app wrote back out of that folder with qpdf and poppler — never PDFium, the engine that
/// did the writing.
///
/// Skipped unless `E2E_FILES=1` (xcodebuild passes it as `TEST_RUNNER_E2E_FILES`), because
/// without the staged fixtures there is nothing to open. Numbered, because the script runs
/// them in order and checks the files in between.
final class FilesEndToEndUITests: XCTestCase {

    private var app: XCUIApplication!

    /// The password the protection tests set and then remove. A fixture value, not a secret.
    private let password = "e2e-pass-1"

    override func setUpWithError() throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["E2E_FILES"] == "1",
                          "needs the fixtures tools/ios-files-e2e.sh stages")
        continueAfterFailure = false
        app = XCUIApplication()
        app.launchArguments = ["-AppleLanguages", "(en)", "-AppleLocale", "en_US"]
        app.launch()
    }

    // MARK: - the flows

    /// Mark the canary line, Save, answer "Overwrite the original". The script then runs the
    /// leak searches over e2e-redact.pdf as it now sits in On My iPhone.
    func test1_redactAndOverwriteTheOriginal() {
        open("e2e-redact")
        arm("viewerRedact")
        // The canary is alone on the line at y=600 (20 pt Helvetica, x 72..~222); the
        // KEEP lines sit at 700 and 500, well outside the drag.
        drag(from: (64, 626), to: (250, 590))
        XCTAssertTrue(app.descendants(matching: .any)["Marked for redaction"].waitForExistence(timeout: 5),
                      dump("the drag marked nothing"))
        tapSave()
        answer("Overwrite the original")
        // One alert: "Saved", with what was removed under it.
        let read = acknowledgeAlerts(until: "Saved", timeout: 30)
        XCTAssertTrue(read.contains("1 area redacted: 13 characters"),
                      "the Saved alert does not say what was removed: \(read)")
        closeDocument()
    }

    /// The same redaction, finished through "Save as a copy": the copy goes to On My iPhone
    /// beside the original, and the original stays as it was.
    func test2_redactAndSaveACopy() {
        open("e2e-redact2")
        arm("viewerRedact")
        drag(from: (64, 626), to: (250, 590))
        XCTAssertTrue(app.descendants(matching: .any)["Marked for redaction"].waitForExistence(timeout: 5),
                      dump("the drag marked nothing"))
        tapSave()
        answer("Save as a copy")
        // No alert before the export sheet: one there kept the sheet from appearing (#279).
        export()
        let read = acknowledgeAlerts(until: "Saved", timeout: 30)
        XCTAssertTrue(read.contains("1 area redacted: 13 characters"),
                      "the Saved alert does not say what was removed: \(read)")
        closeDocument(discardIfAsked: true)
    }

    /// The control: a copy saved with nothing marked, which the searches must catch.
    func test3_controlSaveACopy() {
        open("e2e-control")
        saveACopy()
        acknowledgeAlerts(until: "Saved", timeout: 30)
        closeDocument()
    }

    /// Save writes in place; Save a copy writes elsewhere and leaves the original alone.
    func test4_saveAndSaveACopy() {
        open("e2e-save")
        addText("E2E SAVED TEXT", at: (72, 600))
        tapSave()
        acknowledgeAlerts(until: "Saved", timeout: 30)
        addText("E2E COPY TEXT", at: (72, 540))
        saveACopy()
        acknowledgeAlerts(until: "Saved", timeout: 30)
        closeDocument(discardIfAsked: true)
    }

    /// Set a password. The script checks the file is encrypted before the next test.
    func test5_setPassword() {
        open("e2e-protect")
        more("Password…")
        let field = app.secureTextFields["newPasswordField"].firstMatch
        XCTAssertTrue(field.waitForExistence(timeout: 10), dump("no Set password sheet"))
        field.tap(); field.typeText(password)
        let confirm = app.secureTextFields["confirmPasswordField"].firstMatch
        confirm.tap(); confirm.typeText(password)
        app.buttons["securityCommit"].firstMatch.tap()
        XCTAssertTrue(waitForGone(app.buttons["securityCommit"].firstMatch, timeout: 30),
                      dump("the Set password sheet did not close"))
        acknowledgeAlerts(until: nil, timeout: 3)
        closeDocument()
    }

    /// Reopen: a wrong password is refused and says so, the right one opens it, then Remove
    /// takes the protection off. The script checks the file is unencrypted afterwards (#241).
    func test6_wrongPasswordThenRemove() {
        open("e2e-protect", expectPage: false)
        let alert = app.alerts["Password required"]
        XCTAssertTrue(alert.waitForExistence(timeout: 20), dump("no password prompt on a protected file"))
        alert.secureTextFields.firstMatch.typeText("wrong-password")
        alert.buttons["Open"].tap()
        let again = app.alerts["Password required"]
        XCTAssertTrue(again.waitForExistence(timeout: 20), dump("the prompt did not come back"))
        XCTAssertTrue(again.staticTexts["That password didn't work. Try again."].waitForExistence(timeout: 5),
                      dump("a wrong password was not refused in words"))
        print("E2E wrong password: refused, the prompt reads \"That password didn't work. Try again.\"")
        again.secureTextFields.firstMatch.typeText(password)
        again.buttons["Open"].tap()
        _ = page()
        more("Password…")
        let remove = app.buttons["Remove"].firstMatch
        XCTAssertTrue(remove.waitForExistence(timeout: 10), dump("no Change/Remove choice"))
        remove.tap()
        let commit = app.buttons["securityCommit"].firstMatch
        XCTAssertTrue(commit.waitForExistence(timeout: 5))
        XCTAssertEqual(commit.label, "Remove")
        commit.tap()
        XCTAssertTrue(waitForGone(commit, timeout: 30), dump("the Password sheet did not close"))
        acknowledgeAlerts(until: nil, timeout: 3)
        closeDocument()
    }

    /// A 1 GB document: open, search, Save a copy, each timed.
    func test7_oneGigabyteFile() {
        let t0 = Date()
        open("big-1gb", pageTimeout: 180)
        let opened = Date().timeIntervalSince(t0)
        print(String(format: "E2E 1GB open: %.2f s", opened))

        app.buttons["Find in document"].firstMatch.tap()
        let field = app.textFields["Find in document"].firstMatch
        XCTAssertTrue(field.waitForExistence(timeout: 10))
        let t1 = Date()
        field.typeText("lighthouse")
        let counter = app.staticTexts.matching(NSPredicate(format: "label ENDSWITH ' of 32'")).firstMatch
        XCTAssertTrue(counter.waitForExistence(timeout: 300), dump("no \"of 32\" after searching"))
        let searched = Date().timeIntervalSince(t1)
        print(String(format: "E2E 1GB search: \"%@\" in %.2f s", counter.label, searched))
        let tip = app.buttons["Continue"].firstMatch
        if tip.exists { tip.tap() }
        app.buttons["Done"].firstMatch.tap()

        let t2 = Date()
        saveACopy(exporterTimeout: 300)
        acknowledgeAlerts(until: "Saved", timeout: 300)
        print(String(format: "E2E 1GB save a copy: %.2f s (from the command to Saved)", Date().timeIntervalSince(t2)))
        closeDocument()
    }

    // MARK: - steps

    private func pause(_ seconds: Double) {
        RunLoop.current.run(until: Date(timeIntervalSinceNow: seconds))
    }

    /// The accessibility tree, printed, so a failure says what was on screen.
    private func dump(_ message: String) -> String {
        print("E2E DUMP (\(message)):\n\(app.debugDescription)")
        return message
    }

    private func page() -> XCUIElement {
        let page = app.descendants(matching: .any).matching(NSPredicate(format: "label == 'Page 1'")).firstMatch
        XCTAssertTrue(page.waitForExistence(timeout: 60), dump("the document did not open"))
        return page
    }

    private func pageElement(timeout: TimeInterval) -> XCUIElement? {
        let page = app.descendants(matching: .any).matching(NSPredicate(format: "label == 'Page 1'")).firstMatch
        return page.waitForExistence(timeout: timeout) ? page : nil
    }

    private func point(_ x: Double, _ yFromBottom: Double) -> XCUICoordinate {
        page().coordinate(withNormalizedOffset: CGVector(dx: x / 612.0, dy: (792.0 - yFromBottom) / 792.0))
    }

    private func drag(from a: (Double, Double), to b: (Double, Double)) {
        point(a.0, a.1).press(forDuration: 0.3, thenDragTo: point(b.0, b.1),
                              withVelocity: .slow, thenHoldForDuration: 0.2)
        pause(1.0)
    }

    /// Open PDF, then the file by name in the picker: in its Recents if it is there,
    /// otherwise under Browse → On My iPhone.
    private func open(_ name: String, expectPage: Bool = true, pageTimeout: TimeInterval = 60) {
        let openButton = app.buttons["Open PDF"].firstMatch
        XCTAssertTrue(openButton.waitForExistence(timeout: 20), dump("not on the home screen"))
        openButton.tap()
        let file = app.descendants(matching: .any)
            .matching(NSPredicate(format: "label == %@ OR label BEGINSWITH %@", name, name + ",")).firstMatch
        var found = file.waitForExistence(timeout: 6)
        if !found {
            for place in ["Browse", "On My iPhone"] {
                let item = app.descendants(matching: .any).matching(NSPredicate(format: "label == %@", place)).firstMatch
                if item.waitForExistence(timeout: 4) { item.tap(); pause(1.5) }
                if file.waitForExistence(timeout: 4) { found = true; break }
            }
        }
        XCTAssertTrue(found, dump("\(name) is not visible in the Files picker"))
        file.tap()
        if expectPage {
            XCTAssertNotNil(pageElement(timeout: pageTimeout), dump("\(name) did not open"))
        }
    }

    private func arm(_ identifier: String) {
        let tool = app.buttons[identifier].firstMatch
        XCTAssertTrue(tool.waitForExistence(timeout: 10), dump("no \(identifier)"))
        tool.tap()
        acknowledgeAlerts(until: nil, timeout: 2)
    }

    private func addText(_ text: String, at p: (Double, Double)) {
        app.buttons["Add text"].firstMatch.tap()
        acknowledgeAlerts(until: nil, timeout: 2)   // "Tap the page where the text should go"
        point(p.0, p.1).tap()
        let field = app.textFields["Text"].firstMatch
        XCTAssertTrue(field.waitForExistence(timeout: 10), dump("the Add text sheet did not open"))
        field.tap()
        field.typeText(text)
        app.buttons["Add"].firstMatch.tap()
        pause(1.5)
    }

    /// Save sits in the navigation bar, or folds into the system overflow on a narrow phone.
    private func tapSave() {
        let save = app.navigationBars.buttons["Save"].firstMatch
        if save.waitForExistence(timeout: 5) && save.isHittable {
            save.tap()
        } else {
            app.buttons["More"].firstMatch.tap(); pause(1.0)
            app.buttons["Save"].firstMatch.tap()
        }
    }

    private func more(_ item: String) {
        let more = app.buttons["viewerMore"].firstMatch
        XCTAssertTrue(more.waitForExistence(timeout: 10), dump("no More menu"))
        more.tap()
        let button = app.buttons[item].firstMatch
        XCTAssertTrue(button.waitForExistence(timeout: 5), dump("no \(item) in More"))
        button.tap()
    }

    private func answer(_ choice: String) {
        let button = app.descendants(matching: .button)[choice]
        XCTAssertTrue(button.waitForExistence(timeout: 10), dump("no \(choice) in the question"))
        button.tap()
    }

    private func saveACopy(exporterTimeout: TimeInterval = 30) {
        more("Save a copy")
        export(timeout: exporterTimeout)
    }

    /// The system export sheet. It has no name field on iOS 26: the copy takes the
    /// document's name, and a clash is answered Keep Both so the original is never replaced.
    /// The script finds the copy as the one new file in On My iPhone.
    private func export(timeout: TimeInterval = 30) {
        let bar = app.navigationBars["FullDocumentManagerViewControllerNavigationBar"]
        XCTAssertTrue(bar.waitForExistence(timeout: timeout), dump("the export sheet did not appear"))
        pause(1.0)
        if !bar.staticTexts["On My iPhone"].exists {
            let browse = bar.buttons["Browse"]
            if browse.exists { browse.tap(); pause(1.2) }
            let here = app.descendants(matching: .any).matching(NSPredicate(format: "label == 'On My iPhone'")).firstMatch
            if here.waitForExistence(timeout: 4) { here.tap(); pause(1.2) }
        }
        let save = app.navigationBars["FullDocumentManagerViewControllerNavigationBar"].buttons["Save"]
        XCTAssertTrue(save.waitForExistence(timeout: 10), dump("no Save on the export sheet"))
        save.tap()
        let keepBoth = app.buttons["Keep Both"].firstMatch
        if keepBoth.waitForExistence(timeout: 3) { keepBoth.tap() }
    }

    /// Dismisses alerts as they come. With a title, waits until an alert with that title has
    /// been dismissed, and fails if it never appears.
    @discardableResult
    private func acknowledgeAlerts(until title: String?, timeout: TimeInterval) -> [String] {
        var read: [String] = []
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            let alert = app.alerts.firstMatch
            if alert.waitForExistence(timeout: 1) {
                let label = alert.label
                let texts = alert.staticTexts.allElementsBoundByIndex.map(\.label)
                print("E2E alert: \(label) — \(texts.joined(separator: " | "))")
                read += texts
                alert.buttons.firstMatch.tap()
                pause(0.6)
                if let title, label == title { return read }
            } else if title == nil {
                return read
            }
        }
        if let title { XCTFail(dump("no \"\(title)\" alert")) }
        return read
    }

    private func closeDocument(discardIfAsked: Bool = false) {
        let close = app.navigationBars.buttons["Close"].firstMatch
        XCTAssertTrue(close.waitForExistence(timeout: 30), dump("no Close"))
        // Close waits while a save or an open is still running (#145).
        let deadline = Date().addingTimeInterval(300)
        while !close.isEnabled && Date() < deadline { pause(0.5) }
        close.tap()
        if discardIfAsked {
            let discard = app.buttons["Discard"].firstMatch
            if discard.waitForExistence(timeout: 2) { discard.tap() }
        }
        XCTAssertTrue(app.buttons["Open PDF"].firstMatch.waitForExistence(timeout: 30), dump("Close did not go home"))
    }

    private func waitForGone(_ element: XCUIElement, timeout: TimeInterval) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if !element.exists { return true }
            pause(0.3)
        }
        return false
    }
}
