import XCTest
@testable import MegaPDF

/// Whether a page tile is a drag source, and the one lever that takes that away (#599).
///
/// Small, like `NoticeLifetimeTests`, and here for the same kind of reason: a flag that
/// silently stopped matching would take a suite's sixty-second stalls back without failing
/// anything, because the thing it buys is *speed and quiescence*, not an assertion. So the
/// matching is pinned here rather than inferred from a green lane.
final class PageTileDragTests: XCTestCase {

    func testATileIsADragSourceForEveryOrdinaryLaunch() {
        XCTAssertTrue(PageTileDrag.isADragSource(arguments: []))
        XCTAssertTrue(PageTileDrag.isADragSource(arguments: ["-screenshot", "viewer"]))
        // The other UI-test levers are not this one: a suite that wants the probes still
        // gets a tile it can drag.
        XCTAssertTrue(PageTileDrag.isADragSource(arguments: ["-uiTestZoomProbes"]))
        XCTAssertTrue(PageTileDrag.isADragSource(arguments: ["-uiTestPinNotices"]))
        // Near misses are not the lever either.
        XCTAssertTrue(PageTileDrag.isADragSource(arguments: ["uiTestNoTileDrag"]))
        XCTAssertTrue(PageTileDrag.isADragSource(arguments: ["-uiTestNoTileDrag=1"]))
    }

    func testTheLeverOneUITestSuitePassesTakesItAway() {
        XCTAssertFalse(PageTileDrag.isADragSource(arguments: ["-uiTestNoTileDrag"]))
        XCTAssertFalse(PageTileDrag.isADragSource(
            arguments: ["-AppleLanguages", "(en)", "-uiTestZoomProbes", "-uiTestNoTileDrag"]))
    }

    /// And the app itself is never launched with it: the flag is a test's to pass, and a build
    /// that somehow shipped it would ship a grid nobody could reorder by hand.
    func testThisProcessIsNotAUITestThatAskedForIt() {
        XCTAssertTrue(PageTileDrag.inThisProcess)
    }
}
