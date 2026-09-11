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
        let labels = Labels.forLanguage(ProcessInfo.processInfo.environment["DEMO_LANG"])
        app.launch()
        _ = page()
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
}
