import XCTest
@testable import MegaPDF

/// The arithmetic behind pinch-to-zoom's anchor (#530).
///
/// `ViewerZoomUITests.testAPinchKeepsThePointBetweenTheFingersPut` is the proof that the
/// real gesture, the real scroll view and the real layout agree; it needs a simulator and
/// fourteen seconds. These are the cases that arithmetic alone can settle — including the
/// two the other platforms' fixes got wrong first (the clamped ratio, #546, and the
/// forced-layout read, #534) and the pre-fix behaviour itself, so the numbers in the issue
/// are checked rather than quoted.
final class ZoomAnchorTests: XCTestCase {

    /// The measured case from the issue, in the scroll view's own points.
    ///
    /// A 400x800 viewport with a 400x520 page in it: at fit width the page is shorter than
    /// the viewport, so the stack is centred (#48) and its top sits 140 points down the
    /// content; at 4x it is 2080 tall, the content is taller than the viewport and the
    /// stack's top is the content's own origin. The fingers are 200 points below the top of
    /// the viewport, which is 200 points into the page.
    func testAPinchOutKeepsThePointUnderTheFingersWhereItWas() {
        let focus = CGPoint(x: 200, y: 340)
        let target = ZoomAnchor.reanchor(offset: .zero,
                                        focus: focus,
                                        oldOrigin: CGPoint(x: 0, y: 140),
                                        newOrigin: .zero,
                                        ratio: 4,
                                        minOffset: .zero,
                                        maxOffset: CGPoint(x: 1200, y: 1280))
        // 200 points into the page becomes 800 at 4x, and the offset has to take up the
        // difference: 800 - 340.
        XCTAssertEqual(target.y, 460, accuracy: 0.001)

        // Where the same point ends up on screen, with the correction and without it —
        // "without" being exactly what the app did before this fix.
        let onScreen = { (offset: CGFloat) in 0 + 200 * 4 - offset }
        XCTAssertEqual(onScreen(target.y), focus.y, accuracy: 0.001,
                       "the anchored offset does not put the point back under the fingers")
        XCTAssertEqual(onScreen(0) - focus.y, 460, accuracy: 0.001,
                       "leaving the offset alone is what slid the page down the screen")
    }

    /// Pinching in from a scrolled position, the second half of the issue's measurements.
    func testAPinchInKeepsThePointUnderTheFingersWhereItWas() {
        let offset = CGPoint(x: 600, y: 400)
        let focus = CGPoint(x: 200, y: 200)
        let target = ZoomAnchor.reanchor(offset: offset,
                                        focus: focus,
                                        oldOrigin: .zero,
                                        newOrigin: .zero,
                                        ratio: 0.5,
                                        minOffset: .zero,
                                        maxOffset: CGPoint(x: 400, y: 240))
        XCTAssertEqual(target.x, 200, accuracy: 0.001)
        XCTAssertEqual(target.y, 100, accuracy: 0.001)
        // The content point under the fingers was (800, 600) at 4x, so (400, 300) at 2x.
        XCTAssertEqual(400 - target.x, focus.x, accuracy: 0.001)
        XCTAssertEqual(300 - target.y, focus.y, accuracy: 0.001)
    }

    /// Both axes, not just the scrolling one: at 4x the page is four viewports wide, and a
    /// zoom that anchored only vertically would still throw the page off to one side.
    func testTheHorizontalAxisIsAnchoredToo() {
        let target = ZoomAnchor.reanchor(offset: .zero,
                                        focus: CGPoint(x: 200, y: 340),
                                        oldOrigin: CGPoint(x: 0, y: 140),
                                        newOrigin: .zero,
                                        ratio: 4,
                                        minOffset: .zero,
                                        maxOffset: CGPoint(x: 1200, y: 1280))
        XCTAssertEqual(target.x, 600, accuracy: 0.001)
        XCTAssertEqual(0 + 200 * 4 - target.x, 200, accuracy: 0.001)
    }

    /// The trap #546 called out: the ratio a gesture asks for and the ratio the zoom takes
    /// are different numbers the moment the clamp bites, and correcting by the request
    /// walks the page sideways while the zoom itself holds perfectly still.
    func testTheRatioIsTheOneTheZoomTookAndNotTheOneItWasAskedFor() {
        let focus = CGPoint(x: 200, y: 340)
        let args = (offset: CGPoint.zero, focus: focus,
                    oldOrigin: CGPoint(x: 0, y: 140), newOrigin: CGPoint.zero,
                    minOffset: CGPoint.zero, maxOffset: CGPoint(x: 1600, y: 1780))
        // A pinch out from 1x asking for 5x: the zoom stops at ReadingZoom.maximum.
        let committed = ReadingZoom.clamped(5, floor: ReadingZoom.fitWidth)
        XCTAssertEqual(committed, 4, accuracy: 0.001, "the ceiling moved; this test assumes 4x")
        let right = ZoomAnchor.reanchor(offset: args.offset, focus: args.focus,
                                       oldOrigin: args.oldOrigin, newOrigin: args.newOrigin,
                                       ratio: committed / 1,
                                       minOffset: args.minOffset, maxOffset: args.maxOffset)
        let wrong = ZoomAnchor.reanchor(offset: args.offset, focus: args.focus,
                                       oldOrigin: args.oldOrigin, newOrigin: args.newOrigin,
                                       ratio: 5 / 1,
                                       minOffset: args.minOffset, maxOffset: args.maxOffset)
        XCTAssertEqual(0 + 200 * 4 - right.y, focus.y, accuracy: 0.001,
                       "the committed ratio is what puts the point back")
        XCTAssertEqual(0 + 200 * 4 - wrong.y, focus.y - 200, accuracy: 0.001,
                       "the requested ratio slides the page by a fifth of the zoom it "
                       + "never actually took")
    }

    /// A zoom that did not move — a pinch already at the ceiling, or a button press with
    /// nowhere left to go — must leave the offset exactly where it is, or the page creeps
    /// every time it is asked to do nothing.
    func testAZoomThatDidNotMoveLeavesTheOffsetAlone() {
        let offset = CGPoint(x: 123, y: 456)
        let target = ZoomAnchor.reanchor(offset: offset,
                                        focus: CGPoint(x: 200, y: 300),
                                        oldOrigin: .zero, newOrigin: .zero,
                                        ratio: 1,
                                        minOffset: .zero,
                                        maxOffset: CGPoint(x: 1000, y: 1000))
        XCTAssertEqual(target.x, offset.x, accuracy: 0.001)
        XCTAssertEqual(target.y, offset.y, accuracy: 0.001)
    }

    /// The answer is an offset a scroll view can actually hold: the top of a document
    /// cannot be anchored below the top of the viewport, however far the fingers were from
    /// the page's own middle.
    func testTheAnswerIsClampedToWhatTheScrollViewCanHold() {
        let high = ZoomAnchor.reanchor(offset: CGPoint(x: 0, y: 1000),
                                       focus: CGPoint(x: 200, y: 100),
                                       oldOrigin: .zero, newOrigin: .zero,
                                       ratio: 4,
                                       minOffset: .zero,
                                       maxOffset: CGPoint(x: 1200, y: 1280))
        XCTAssertEqual(high.y, 1280, accuracy: 0.001, "not past the end of the content")

        let low = ZoomAnchor.reanchor(offset: .zero,
                                      focus: CGPoint(x: 200, y: 340),
                                      oldOrigin: CGPoint(x: 0, y: 140),
                                      newOrigin: CGPoint(x: 0, y: 140),
                                      ratio: 0.5,
                                      minOffset: CGPoint(x: 0, y: -60),
                                      maxOffset: CGPoint(x: 0, y: -60))
        XCTAssertEqual(low.y, -60, accuracy: 0.001,
                       "a content inset is the smallest offset there is, not zero")
    }

    /// Nothing sensible can be done with a ratio that is not a ratio, and a viewer that
    /// scrolled to NaN would be a document that could never be scrolled again.
    func testANonsenseRatioIsRefusedRatherThanApplied() {
        let offset = CGPoint(x: 10, y: 20)
        for ratio in [CGFloat(0), -1, .nan, .infinity] {
            let target = ZoomAnchor.reanchor(offset: offset,
                                             focus: CGPoint(x: 5, y: 5),
                                             oldOrigin: .zero, newOrigin: .zero,
                                             ratio: ratio,
                                             minOffset: .zero,
                                             maxOffset: CGPoint(x: 100, y: 100))
            XCTAssertEqual(target.x, offset.x, accuracy: 0.001, "ratio \(ratio)")
            XCTAssertEqual(target.y, offset.y, accuracy: 0.001, "ratio \(ratio)")
        }
    }

    /// The floor #540 lowered is still a floor, and it is the one the pinch clamps to.
    func testTheZoomFloorReadingModeLoweredIsRespected() {
        // Fit page on a tall page can ask for less than fit width; the floor comes down
        // with it, and a pinch in may then go there but no further.
        let floor = ReadingZoom.fitPage(viewport: CGSize(width: 800, height: 600),
                                        page: CGSize(width: 612, height: 792))
        XCTAssertLessThan(floor, ReadingZoom.fitWidth, "fit page on a portrait page is under 1x")
        XCTAssertEqual(ReadingZoom.clamped(floor / 4, floor: floor), floor, accuracy: 0.0001)
        // And with no preset chosen, fit width is the floor a pinch in stops at.
        XCTAssertEqual(ReadingZoom.clamped(0.1, floor: ReadingZoom.fitWidth),
                       ReadingZoom.fitWidth, accuracy: 0.0001)
        XCTAssertEqual(ReadingZoom.clamped(99, floor: ReadingZoom.fitWidth),
                       ReadingZoom.maximum, accuracy: 0.0001)
    }
}
