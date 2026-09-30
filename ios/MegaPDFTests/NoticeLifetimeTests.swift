import XCTest
@testable import MegaPDF

/// The notice's lifetime, and the one lever that suspends it (#487).
///
/// Small, but it is the decision two UI tests stopped racing once it had a name: the app
/// takes a notice away after four seconds, and a test that waited five for it to appear was
/// asking to win a race rather than to make a measurement.
final class NoticeLifetimeTests: XCTestCase {

    func testANoticeClearsItselfForEveryOrdinaryLaunch() {
        XCTAssertTrue(NoticeLifetime.clearsItself(arguments: []))
        XCTAssertTrue(NoticeLifetime.clearsItself(arguments: ["-screenshot", "viewer"]))
        // Near misses are not the lever: nothing but the exact argument pins a notice.
        XCTAssertTrue(NoticeLifetime.clearsItself(arguments: ["-uiTestPinReadingBar"]))
        XCTAssertTrue(NoticeLifetime.clearsItself(arguments: ["uiTestPinNotices"]))
        XCTAssertTrue(NoticeLifetime.clearsItself(arguments: ["-uiTestPinNotices=1"]))
    }

    func testTheLeverOneUITestSuitePassesPinsIt() {
        XCTAssertFalse(NoticeLifetime.clearsItself(arguments: ["-uiTestPinNotices"]))
        XCTAssertFalse(NoticeLifetime.clearsItself(
            arguments: ["-AppleLanguages", "(en)", "-uiTestPinNotices"]))
    }

    /// What a person sees is still four seconds, whatever the tests do with it.
    func testTheVisibleDurationIsUnchangedAndItsNanosecondsAgree() {
        XCTAssertEqual(NoticeLifetime.visible, 4, accuracy: 0.0001)
        XCTAssertEqual(NoticeLifetime.visibleNanoseconds, 4_000_000_000)
    }
}
