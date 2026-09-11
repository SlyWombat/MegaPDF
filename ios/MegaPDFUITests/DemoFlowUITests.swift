import XCTest

/// Drives the real app through the fill-check-sign story for an App Store
/// preview video. It is not a test of correctness — the unit tests are — but a
/// choreography: `xcrun simctl io <udid> recordVideo` runs alongside it and the
/// pauses are there so the recording reads at a human pace.
///
/// The app is launched in `-screenshot story` mode, which opens the bundled
/// demo agreement *unfilled* with the "Mega W." signature already in the
/// library, so the video fills in the same document the store screenshots show. `DEMO_LANG`
/// (passed by xcodebuild as `TEST_RUNNER_DEMO_LANG`) selects the catalogue the
/// same way ios-screenshots.yml does: `fr-CA` or `fr`, anything else is English.
///
/// Every page coordinate is a fraction of the page, derived from the demo
/// agreement's layout in `tools/gen_test_fixtures.py` (612x792 points), so the
/// same taps land on iPhone and iPad.
final class DemoFlowUITests: XCTestCase {

    private var app: XCUIApplication!

    override func setUp() {
        continueAfterFailure = false
        app = XCUIApplication()
        app.launchArguments = ["-screenshot", "story"]
        switch ProcessInfo.processInfo.environment["DEMO_LANG"] {
        case "fr-CA": app.launchArguments += ["-AppleLanguages", "(fr-CA)", "-AppleLocale", "fr_CA"]
        case "fr":    app.launchArguments += ["-AppleLanguages", "(fr)", "-AppleLocale", "fr_FR"]
        default: break
        }
    }

    // Toolbar and sheet labels are looked up by accessibility identifier where
    // the app sets one and by localized label otherwise; the French runs need
    // the French words, so both are tabled here.
    private struct Labels {
        let sign, addText, find, findField, nextMatch, done, textField, add, save: String
        let signatureName = "Mega W."
        let searchTerm: String
        let printedName = "Jane Whitfield"

        static func forLanguage(_ lang: String?) -> Labels {
            switch lang {
            case "fr-CA", "fr":
                return Labels(sign: "Signer", addText: "Ajouter du texte", find: "Rechercher dans le document",
                              findField: "Rechercher dans le document", nextMatch: "Résultat suivant",
                              done: "Terminé", textField: "Texte", add: "Ajouter", save: "Enregistrer",
                              searchTerm: "location")
            default:
                return Labels(sign: "Sign", addText: "Add text", find: "Find in document",
                              findField: "Find in document", nextMatch: "Next match",
                              done: "Done", textField: "Text", add: "Add", save: "Save",
                              searchTerm: "rental")
            }
        }
    }

    private func pause(_ seconds: Double) {
        RunLoop.current.run(until: Date(timeIntervalSinceNow: seconds))
    }

    private func page() -> XCUIElement {
        let page = app.descendants(matching: .any).matching(NSPredicate(format: "label == %@", "Page 1")).firstMatch
        XCTAssertTrue(page.waitForExistence(timeout: 20), "the demo agreement did not open")
        return page
    }

    /// Taps a point on the demo page given in PDF points (origin bottom-left).
    private func tapPage(x: Double, yFromBottom: Double) {
        let p = page()
        p.coordinate(withNormalizedOffset: CGVector(dx: x / 612.0, dy: (792.0 - yFromBottom) / 792.0)).tap()
    }

    func testFillCheckSignStory() {
        let lang = ProcessInfo.processInfo.environment["DEMO_LANG"]
        let labels = Labels.forLanguage(lang)
        app.launch()
        _ = page()
        // The toolbar must be in the requested language, or the recording is
        // an English video under a French file name.
        XCTAssertTrue(app.buttons[labels.sign].firstMatch.waitForExistence(timeout: 5),
                      "toolbar is not in the requested language (\(lang ?? "en"))")
        pause(2.5)

        // 1. Tick two of the three boxes.
        tapPage(x: 78.5, yFromBottom: 590.5)
        pause(1.5)
        tapPage(x: 78.5, yFromBottom: 564.5)
        pause(2.0)

        // 2. Sign: pick the library signature, then tap the signature line.
        app.buttons[labels.sign].firstMatch.tap()
        let signature = app.buttons[labels.signatureName].firstMatch
        XCTAssertTrue(signature.waitForExistence(timeout: 5), "the seeded signature is not in the library")
        pause(1.5)
        signature.tap()
        // Picking shows a one-line hint ("Tap the page where the signature
        // should go") as an alert; let it read, then dismiss it before the tap
        // that places the signature, or the tap lands on the alert instead.
        let hint = app.alerts.firstMatch
        if hint.waitForExistence(timeout: 2) {
            pause(1.5)
            hint.buttons.firstMatch.tap()
        }
        pause(1.0)
        tapPage(x: 190, yFromBottom: 430)
        pause(2.5)

        // 3. Type the printed name under the signature line.
        app.buttons[labels.addText].firstMatch.tap()
        // Same one-line hint as for the signature ("Tap the page where the text
        // should go"); let it read, dismiss it, then tap.
        let textHint = app.alerts.firstMatch
        if textHint.waitForExistence(timeout: 2) {
            pause(1.5)
            textHint.buttons.firstMatch.tap()
        }
        pause(0.8)
        tapPage(x: 72, yFromBottom: 372)
        let field = app.textFields[labels.textField].firstMatch
        XCTAssertTrue(field.waitForExistence(timeout: 5), "the Add text sheet did not open")
        field.tap()
        pause(0.5)
        field.typeText(labels.printedName)
        pause(1.2)
        app.buttons[labels.add].firstMatch.tap()
        pause(2.5)

        // 4. Find every "rental" on the page and step through the matches.
        app.buttons[labels.find].firstMatch.tap()
        let findField = app.textFields[labels.findField].firstMatch
        XCTAssertTrue(findField.waitForExistence(timeout: 5), "the find bar did not open")
        findField.typeText(labels.searchTerm)
        pause(2.0)
        let next = app.buttons[labels.nextMatch].firstMatch
        if next.waitForExistence(timeout: 3) {
            next.tap(); pause(1.2)
            next.tap(); pause(1.2)
        }
        app.buttons[labels.done].firstMatch.tap()
        pause(1.5)

        // Hold the finished page. Save is deliberately not tapped: the demo
        // document is opened from the bundle with no file behind it, so a save
        // would raise the Files picker over the final frame.
        pause(3.0)
    }

    /// Diagnostic: the viewer's accessibility tree once a document is dirty.
    /// Run alone with -only-testing; the dump goes to the xcodebuild log.
    func testDumpViewerHierarchy() {
        app.launch()
        _ = page()
        tapPage(x: 78.5, yFromBottom: 590.5)
        pause(1.0)
        print("HIERARCHY-BEGIN")
        print(app.debugDescription)
        print("HIERARCHY-END")
    }

    // MARK: - App Review walkthrough

    /// The recording App Review asked for under Guideline 2.1
    /// (docs/app-review-notes.md, item 1): every step of the core flow in one
    /// take, from a cold launch, on a real file opened through the Files
    /// picker. tools/ios-review-video.sh stages docs/review/MegaPDF-Test-Form.pdf
    /// into the simulator's "On My iPhone" first. Normal launch, no demo mode:
    /// what the reviewer sees is exactly what a user gets.
    ///
    /// The test form (tools/gen_review_form.py) is 612x792 with three printed
    /// squares at x=72, y=564/538/512 (bottom-left origin), two AcroForm
    /// checkboxes at x=72, y=438 and 412, a signature rule at y=300, and the
    /// word "insurance" four times.
    func testAppReviewWalkthrough() {
        app.launchArguments = []   // a normal launch: no demo mode
        app.launch()
        pause(2.0)

        // 2. Open PDF through the Files picker.
        app.buttons["Open PDF"].firstMatch.tap()
        pause(2.5)
        openTestFormInPicker(app)
        _ = page()
        pause(2.0)

        // 3. Tick two boxes: a printed square and a real form checkbox.
        tapPage(x: 78.5, yFromBottom: 570.5); pause(1.5)
        tapPage(x: 79.5, yFromBottom: 445.5); pause(1.8)

        // 4. Clear one and tick it again.
        tapPage(x: 78.5, yFromBottom: 570.5); pause(1.5)
        tapPage(x: 78.5, yFromBottom: 570.5); pause(1.8)

        // 5. Sign: draw, save, pick, place, drag onto the line.
        app.buttons["Sign"].firstMatch.tap(); pause(1.5)
        app.buttons["Draw"].firstMatch.tap(); pause(1.5)
        drawSignature(app); pause(1.0)
        lastButton(app, "Save").tap(); pause(1.0)
        // Saving closes the library and confirms with a "Signature added" alert;
        // read it, dismiss it, then open the library again to pick the entry.
        let added = app.alerts.firstMatch
        if added.waitForExistence(timeout: 3) { pause(1.2); added.buttons.firstMatch.tap() }
        pause(0.8)
        app.buttons["Sign"].firstMatch.tap(); pause(1.5)
        let drawn = app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'Signature'")).firstMatch
        XCTAssertTrue(drawn.waitForExistence(timeout: 5), "the drawn signature is not in the library")
        drawn.tap()
        let hint = app.alerts.firstMatch
        if hint.waitForExistence(timeout: 2) { pause(1.2); hint.buttons.firstMatch.tap() }
        pause(0.8)
        tapPage(x: 190, yFromBottom: 380); pause(1.5)          // placed a little high…
        let p = page()
        let from = p.coordinate(withNormalizedOffset: CGVector(dx: 190 / 612.0, dy: (792 - 380) / 792.0))
        let to = p.coordinate(withNormalizedOffset: CGVector(dx: 190 / 612.0, dy: (792 - 335) / 792.0))
        from.press(forDuration: 0.4, thenDragTo: to)            // …then dragged down onto the line
        pause(1.5)
        tapPage(x: 500, yFromBottom: 150); pause(1.5)           // deselect

        // 6. Search and step through the matches.
        app.buttons["Find in document"].firstMatch.tap()
        let field = app.textFields["Find in document"].firstMatch
        XCTAssertTrue(field.waitForExistence(timeout: 5))
        field.typeText("insurance"); pause(1.0)
        // A fresh simulator shows the "slide to type" keyboard tip once.
        let tip = app.buttons["Continue"].firstMatch
        if tip.waitForExistence(timeout: 1) { tip.tap() }
        pause(1.5)
        let next = app.buttons["Next match"].firstMatch
        if next.waitForExistence(timeout: 3) { next.tap(); pause(1.2); next.tap(); pause(1.2) }
        app.buttons["Done"].firstMatch.tap(); pause(1.2)

        // 7. Save, see the confirmation, close, reopen from Recents. On iOS 26
        // the iPhone toolbar folds Save into the system "More" button.
        if app.buttons["Save"].firstMatch.exists && app.buttons["Save"].firstMatch.isHittable {
            app.buttons["Save"].firstMatch.tap()
        } else {
            app.buttons["More"].firstMatch.tap(); pause(1.2)
            app.buttons["Save"].firstMatch.tap()
        }
        pause(2.5)
        let ok = app.alerts.firstMatch
        if ok.waitForExistence(timeout: 3) { pause(1.0); ok.buttons.firstMatch.tap() }
        pause(1.0)
        app.buttons["Close"].firstMatch.tap(); pause(2.0)
        let recent = app.buttons.matching(NSPredicate(format: "label CONTAINS 'MegaPDF-Test-Form'")).firstMatch
        XCTAssertTrue(recent.waitForExistence(timeout: 5), "the file is not in Recents")
        recent.tap()
        _ = page(); pause(3.0)

        // 8. The Photos picker appears (no permission prompt) and is dismissed.
        app.buttons["Sign"].firstMatch.tap(); pause(1.5)
        app.buttons["Photos"].firstMatch.tap(); pause(3.0)
        let cancel = lastButton(app, "Cancel")
        if cancel.waitForExistence(timeout: 5) { cancel.tap() }
        pause(1.5)
        app.buttons["Close"].firstMatch.tap(); pause(2.5)
    }

    /// The topmost of several same-named buttons (a sheet's Save above the
    /// viewer's Save): XCUITest lists them in hierarchy order, sheets last.
    private func lastButton(_ app: XCUIApplication, _ label: String) -> XCUIElement {
        let all = app.buttons.matching(NSPredicate(format: "label == %@", label)).allElementsBoundByIndex
        return all.last ?? app.buttons[label].firstMatch
    }

    /// Finds MegaPDF-Test-Form in the document picker: in Recents if it is
    /// there, otherwise under Browse → On My iPhone.
    private func openTestFormInPicker(_ app: XCUIApplication) {
        let file = app.descendants(matching: .any)
            .matching(NSPredicate(format: "label CONTAINS 'MegaPDF-Test-Form'")).firstMatch
        if file.waitForExistence(timeout: 5) { file.tap(); return }
        for name in ["Browse", "On My iPhone", "On My iPad"] {
            let item = app.descendants(matching: .any).matching(NSPredicate(format: "label == %@", name)).firstMatch
            if item.waitForExistence(timeout: 3) { item.tap(); pause(1.5) }
            if file.waitForExistence(timeout: 3) { file.tap(); return }
        }
        XCTFail("MegaPDF-Test-Form.pdf is not visible in the Files picker")
    }

    /// Two strokes across the drawing canvas, the second one a loop, so it
    /// reads as a signature rather than a line.
    private func drawSignature(_ app: XCUIApplication) {
        let canvas = app.otherElements.matching(NSPredicate(format: "label == 'Signature canvas'")).firstMatch
        let area: XCUIElement = canvas.exists ? canvas : app.windows.firstMatch
        func pt(_ x: Double, _ y: Double) -> XCUICoordinate {
            area.coordinate(withNormalizedOffset: CGVector(dx: x, dy: y))
        }
        // Slow drags: at the default velocity the gesture samples so few points
        // that the canvas records dots, not a line.
        let strokes: [[(Double, Double)]] = [
            [(0.12, 0.62), (0.30, 0.30), (0.42, 0.66)],
            [(0.44, 0.66), (0.62, 0.30), (0.88, 0.60)],
        ]
        for stroke in strokes {
            for (a, b) in zip(stroke, stroke.dropFirst()) {
                pt(a.0, a.1).press(forDuration: 0.05, thenDragTo: pt(b.0, b.1),
                                   withVelocity: .slow, thenHoldForDuration: 0.05)
            }
            pause(0.4)
        }
    }
}
