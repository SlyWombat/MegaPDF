import XCTest

/// About names both places a person can go (#730): Support and Source code, each a row that
/// opens the system browser. The address a row opens is asserted on `AboutLinks` in
/// MegaPDFTests/AboutLinksTests; a UI test cannot read a Link's destination, so this checks
/// that About is reachable and that both rows are on it, under their English labels.
final class AboutLinksUITests: XCTestCase {

    func testAboutOffersSupportAndSourceCode() {
        continueAfterFailure = false
        let app = XCUIApplication()
        app.launchArguments = ["-screenshot", "recents",
                               "-AppleLanguages", "(en)", "-AppleLocale", "en_US"]
        app.launch()

        let about = app.buttons["About MegaPDF"]
        XCTAssertTrue(about.waitForExistence(timeout: 10), "Home has no About button")
        about.tap()

        for label in ["Support", "Source code"] {
            let row = app.descendants(matching: .any).matching(
                NSPredicate(format: "label == %@ AND (elementType == %d OR elementType == %d)",
                            label, XCUIElement.ElementType.button.rawValue,
                            XCUIElement.ElementType.link.rawValue)).firstMatch
            XCTAssertTrue(row.waitForExistence(timeout: 5), "About has no \"\(label)\" row")
        }
    }
}
