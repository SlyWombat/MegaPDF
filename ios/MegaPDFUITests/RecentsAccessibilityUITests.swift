import UIKit
import XCTest

/// What a screen reader is handed for the Recent list (#165, #2).
///
/// `RecentEntry.accessibilityLabel` is unit-tested; this checks the other half —
/// that the row actually carries it, as one element, so VoiceOver reads
/// "Rental Agreement.pdf, in iCloud Drive › Smith" and not the name, the place
/// and a warning glyph as three separate stops. Four rows share a file name in
/// the `-screenshot recents` list, which is the case the issue is about: without
/// the place, every one of them would announce identically.
///
/// In the MegaPDFDemo scheme, like the other UI tests, so `xcodebuild test
/// -scheme MegaPDF` in CI does not wait on a simulator launch.
final class RecentsAccessibilityUITests: XCTestCase {

    private var app: XCUIApplication!

    override func setUp() {
        continueAfterFailure = false
        app = XCUIApplication()
        app.launchArguments = ["-screenshot", "recents"]
    }

    func testEveryRecentRowAnnouncesWhereItsFileLives() {
        app.launch()

        let expected = [
            "Rental Agreement.pdf, in iCloud Drive › Smith",
            "Rental Agreement.pdf, in iCloud Drive › Jones",
            "Rental Agreement.pdf, in On My iPhone › Downloads",
            "Rental Agreement.pdf, not found, in iCloud Drive › Archive",
        ]

        // The device name follows the idiom, so an iPad run says "On My iPad".
        let onThisDevice = UIDevice.current.userInterfaceIdiom == .pad ? "On My iPad" : "On My iPhone"

        for label in expected {
            let wanted = label.replacingOccurrences(of: "On My iPhone", with: onThisDevice)
            XCTAssertTrue(app.buttons[wanted].waitForExistence(timeout: 10),
                          "no row announces \"\(wanted)\"")
        }

        // And the point of it: four rows, four different announcements.
        XCTAssertEqual(Set(expected).count, expected.count)
    }

    /// The long press is the only way to these on iOS — there is no hover, and an
    /// iPad with a pointer does not change that.
    func testARowOffersRemoveFromRecents() {
        app.launch()
        let row = app.buttons["Rental Agreement.pdf, in iCloud Drive › Smith"]
        XCTAssertTrue(row.waitForExistence(timeout: 10))
        row.press(forDuration: 1.2)
        XCTAssertTrue(app.buttons["Remove from Recents"].waitForExistence(timeout: 5),
                      "the context menu has no Remove from Recents")
        XCTAssertTrue(app.buttons["Show in Files"].exists,
                      "the context menu has no Show in Files")
    }
}
