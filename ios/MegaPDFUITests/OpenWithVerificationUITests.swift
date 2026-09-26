import XCTest

/// #377: confirms MegaPDF is actually offered in the real Files app's share sheet for a PDF —
/// not only that Info.plist carries the right keys (that half is `xcodegen`'s own output,
/// inspected directly when #377 was implemented).
///
/// Skipped unless `OPEN_WITH_CHECK=1` (xcodebuild passes it as `TEST_RUNNER_OPEN_WITH_CHECK`),
/// because it needs a PDF already staged at "On My iPhone"/"On My iPad" named
/// `openwith-check.pdf` — the same `local_storage()` recipe `tools/ios-files-e2e.sh` uses to
/// find the simulator's real, on-device Files storage (not one of the other two folders that
/// share its name). Not run by CI's default `-scheme MegaPDF` scheme, the same as
/// `FilesEndToEndUITests`.
final class OpenWithVerificationUITests: XCTestCase {

    override func setUpWithError() throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["OPEN_WITH_CHECK"] == "1",
                          "needs openwith-check.pdf staged at On My iPhone/iPad")
        continueAfterFailure = false
    }

    func testMegaPDFIsOfferedForAPdfInFiles() throws {
        let files = XCUIApplication(bundleIdentifier: "com.apple.DocumentsApp")
        files.terminate()
        files.launch()

        let browse = files.buttons["Browse"]
        if browse.waitForExistence(timeout: 5) { browse.tap() }

        let onMyIPhone = files.staticTexts.matching(NSPredicate(format: "label BEGINSWITH 'On My'")).firstMatch
        XCTAssertTrue(onMyIPhone.waitForExistence(timeout: 10), "no \"On My …\" location in Browse")
        onMyIPhone.tap()

        let cell = files.staticTexts["openwith-check"].firstMatch
        XCTAssertTrue(cell.waitForExistence(timeout: 10), "openwith-check.pdf not visible — was it staged?")
        cell.press(forDuration: 1.2)

        let share = files.buttons["Share"].firstMatch
        XCTAssertTrue(share.waitForExistence(timeout: 5), "no Share row on the long-press menu")
        share.tap()

        let megapdf = files.staticTexts["MegaPDF"].firstMatch
        XCTAssertTrue(megapdf.waitForExistence(timeout: 10),
                      "MegaPDF is not offered in the share sheet for a PDF (#377)")
    }
}
