import XCTest

/// Puts a capture iPad simulator in *Full Screen Apps* (Settings → Multitasking &
/// Gestures), which is not a test of MegaPDF but a step of the store-capture rig.
///
/// In *Windowed Apps* iPadOS 26 draws a window-resize grabber in the bottom-right
/// corner of every app, and it was in every iPad listing image and every iPad preview
/// frame of the first 2.0 set — identical everywhere, so no comparison could see it
/// (capture-gate-report.md §6, finding 2). `tools/ios-screenshots.sh` and
/// `tools/ios-demo-video.sh` run this once per iPad before shooting.
///
/// Driven through Settings rather than a defaults key because the setting is what a
/// person changes, and the key behind it is private and has moved between releases.
/// Settings is always in the simulator's system language, which the rig leaves
/// English; only the app is launched in French.
final class CaptureSimulatorSetupUITests: XCTestCase {

    func testIPadRunsAppsFullScreen() throws {
        guard UIDevice.current.userInterfaceIdiom == .pad else {
            throw XCTSkip("the grabber is an iPad thing")
        }
        continueAfterFailure = false
        let settings = XCUIApplication(bundleIdentifier: "com.apple.Preferences")
        settings.terminate()
        settings.launch()

        let row = settings.staticTexts["Multitasking & Gestures"]
        var tries = 0
        while !row.waitForExistence(timeout: 2) && tries < 8 {
            settings.swipeUp()
            tries += 1
        }
        XCTAssertTrue(row.exists, "no Multitasking & Gestures in Settings")
        row.tap()

        let fullScreen = settings.staticTexts["Full Screen Apps"].firstMatch
        XCTAssertTrue(fullScreen.waitForExistence(timeout: 10), "no Full Screen Apps choice")
        fullScreen.tap()
        // Let SpringBoard take the change before the next app launches.
        Thread.sleep(forTimeInterval: 3)
        settings.terminate()
    }
}
