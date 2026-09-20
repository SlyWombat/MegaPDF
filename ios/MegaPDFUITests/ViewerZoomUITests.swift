import XCTest

/// Pinch to zoom, in a running app (#336).
///
/// It was reported from Android and checked here: the app's magnify gesture had been
/// competing with the scroll view's own pan, and losing — the pinch never reached the
/// zoom at all on either phone. Only a real gesture in a real window can tell whether
/// that is fixed, so this drives one and reads the page back off the screen.
///
/// The page is compared as pixels, the way `tools/android-qa/flows.py` does it for the
/// same flow, and for the same reason: a zoomed page's accessibility bounds are either
/// clipped to the display or in a coordinate space that no longer matches what is on
/// screen, so the pixels are the honest witness.
///
/// In the MegaPDFDemo scheme with the other UI tests, so `xcodebuild test -scheme
/// MegaPDF` in CI does not wait on a simulator launch.
final class ViewerZoomUITests: XCTestCase {

    private var app: XCUIApplication!

    override func setUp() {
        continueAfterFailure = false
        app = XCUIApplication()
        // Explicitly English: this one reads no wording, but the demo document and the
        // launch state should not depend on the machine's language.
        app.launchArguments = ["-screenshot", "viewer",
                               "-AppleLanguages", "(en)", "-AppleLocale", "en_US"]
    }

    /// The first page of the demo agreement, which is what a pinch is aimed at. The
    /// gesture itself is synthesised at the element's centre; the frame is also what
    /// the comparison is cropped to.
    private func page(timeout: TimeInterval = 30) -> XCUIElement {
        let page = app.descendants(matching: .any)
            .matching(NSPredicate(format: "label == 'Page 1'")).firstMatch
        XCTAssertTrue(page.waitForExistence(timeout: timeout), "the demo document did not open")
        return page
    }

    /// The screen's pixels for `rect` (in points), as RGBA bytes.
    ///
    /// A crop rather than the whole screen: the status bar's clock is not a picture of
    /// the page, and two screenshots of a zoom taken a minute apart would differ by it
    /// whatever the zoom did.
    private func pixels(of rect: CGRect) -> [UInt8] {
        let image = XCUIScreen.main.screenshot().image
        guard let cg = image.cgImage else {
            XCTFail("no screenshot")
            return []
        }
        let scale = CGFloat(cg.width) / image.size.width
        let px = CGRect(x: rect.minX * scale, y: rect.minY * scale,
                        width: rect.width * scale, height: rect.height * scale)
            .integral
            .intersection(CGRect(x: 0, y: 0, width: cg.width, height: cg.height))
        guard !px.isEmpty, let crop = cg.cropping(to: px) else {
            XCTFail("the page's frame \(rect) is not on the screen")
            return []
        }
        let w = crop.width, h = crop.height
        var bytes = [UInt8](repeating: 0, count: w * h * 4)
        guard let ctx = CGContext(data: &bytes, width: w, height: h, bitsPerComponent: 8,
                                  bytesPerRow: w * 4, space: CGColorSpaceCreateDeviceRGB(),
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            XCTFail("no bitmap context for the page's pixels")
            return []
        }
        ctx.draw(crop, in: CGRect(x: 0, y: 0, width: w, height: h))
        return bytes
    }

    /// The fraction of pixels that differ by more than `tolerance` in any channel, 0...1.
    ///
    /// A tolerance rather than an equality test: two renders of the same page can differ
    /// by a shade along an edge, and a check that fails on that teaches nobody anything.
    /// A zoom is not a shade — it moves most of the page's pixels.
    private func difference(_ a: [UInt8], _ b: [UInt8], tolerance: Int = 8) -> Double {
        guard !a.isEmpty, a.count == b.count else { return 1 }
        var differing = 0
        for i in stride(from: 0, to: a.count, by: 4) {
            if abs(Int(a[i]) - Int(b[i])) > tolerance
                || abs(Int(a[i + 1]) - Int(b[i + 1])) > tolerance
                || abs(Int(a[i + 2]) - Int(b[i + 2])) > tolerance { differing += 1 }
        }
        return Double(differing) / Double(a.count / 4)
    }

    func testAPinchZoomsThePageAndClosingItComesBackOff() {
        app.launch()
        let firstPage = page()
        // The launch settles: the first frames can still be the launch screen, and the
        // page arrives after a render.
        Thread.sleep(forTimeInterval: 2)

        let rect = firstPage.frame
        let atOne = pixels(of: rect)
        XCTAssertFalse(atOne.isEmpty, "could not read the page's pixels")

        // Fingers apart — the gesture the bug was about. The direction is the scale;
        // both velocities are positive, which the API requires.
        firstPage.pinch(withScale: 2.5, velocity: 2.0)
        Thread.sleep(forTimeInterval: 1.5)   // scroll indicators fade
        let zoomed = pixels(of: rect)
        XCTAssertGreaterThan(difference(atOne, zoomed), 0.05,
                             "a pinch out did not change the page (#336)")

        // And closed again. What this asserts is that the zoom came back *off* — not
        // that the page landed on the exact frame it started on: a pinch in a scroll
        // view can leave it a few points from where it was, and a test that demanded
        // the same picture would fail on that rather than on the zoom.
        firstPage.pinch(withScale: 0.4, velocity: 2.0)
        Thread.sleep(forTimeInterval: 1.5)
        let closed = pixels(of: rect)
        XCTAssertLessThan(difference(zoomed, closed), 0.05,
                          "a pinch in left the page at the zoom the other way")
    }
}
