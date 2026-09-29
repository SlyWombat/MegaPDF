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
/// screen, so the pixels are the honest witness. Since #465 the app's own committed zoom
/// is read alongside them (`viewerZoomProbe`), so a failure can say whether the gesture
/// reached the app at all instead of only that the screen did not move.
///
/// **Where the fingers go matters (#465).** Both pinches are aimed at `viewerPinchProbe`,
/// an invisible, untouchable rectangle well inside the page that `ViewerView` builds for
/// this test alone. Aimed at the page element instead, `XCUIElement.pinch` puts its lower
/// synthetic touch 50pt from the bottom of the screen as soon as the page is bigger than
/// the viewport — on an iPhone that is inside the bottom toolbar, which takes the touch,
/// and the magnify gesture never sees a second finger. That is what made the closing pinch
/// measure as a dead no-op on every iPhone while iPad passed: a test artefact, not a bug
/// in the app. `ViewerView.zoomProbes` has the long version.
///
/// In the MegaPDFDemo scheme with the other UI tests; ios-ci.yml runs it on the
/// simulator after the unit tests (#405), on an iPhone and an iPad (#465) — the two
/// layouts differ in exactly the chrome this test turned out to be sensitive to.
final class ViewerZoomUITests: XCTestCase {

    private var app: XCUIApplication!

    override func setUp() {
        continueAfterFailure = false
        app = XCUIApplication()
        // Explicitly English: this one reads no wording, but the demo document and the
        // launch state should not depend on the machine's language.
        app.launchArguments = ["-screenshot", "viewer",
                               "-AppleLanguages", "(en)", "-AppleLocale", "en_US",
                               // The two probes above; nothing else passes this (#465).
                               "-uiTestZoomProbes"]
    }

    /// The first page of the demo agreement. The pixel comparison is cropped to its frame;
    /// the gestures are aimed at `pinchProbe()`, not at this.
    private func page(timeout: TimeInterval = 30) -> XCUIElement {
        let page = app.descendants(matching: .any)
            .matching(NSPredicate(format: "label == 'Page 1'")).firstMatch
        XCTAssertTrue(page.waitForExistence(timeout: timeout), "the demo document did not open")
        return page
    }

    /// The rectangle the pinches are synthesised from (#465). Re-queried for each gesture:
    /// it is laid out from the viewport, so it does not move, but a stale reference is one
    /// less thing to wonder about when this fails.
    private func pinchProbe() -> XCUIElement {
        let probe = app.otherElements["viewerPinchProbe"]
        XCTAssertTrue(probe.waitForExistence(timeout: 10),
                      "the pinch probe is missing — did the -uiTestZoomProbes launch argument "
                      + "survive, and does ViewerView still build zoomProbes? (#465)")
        return probe
    }

    /// The zoom the app has committed, read off `viewerZoomProbe`'s label.
    private func committedZoom() -> Double {
        let probe = app.descendants(matching: .any)
            .matching(NSPredicate(format: "identifier == 'viewerZoomProbe'")).firstMatch
        guard probe.waitForExistence(timeout: 10) else {
            XCTFail("the zoom probe is missing (#465)")
            return .nan
        }
        let text = probe.label.replacingOccurrences(of: "zoom ", with: "")
        guard let value = Double(text) else {
            XCTFail("the zoom probe read '\(probe.label)', which is not a zoom")
            return .nan
        }
        return value
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
        XCTAssertEqual(committedZoom(), 1, accuracy: 0.01, "the document did not open at 1×")

        // Fingers apart — the gesture the bug was about. The velocity's sign has to
        // agree with the scale: positive for a pinch out (scale > 1), negative for a
        // pinch in (scale < 1), or XCTest throws NSInvalidArgumentException (#405).
        // What is asserted is the direction, not a value. Since #530 the gesture is a
        // UIPinchGestureRecognizer, which reports the scale a synthesised pinch was asked
        // for fairly closely (2.5 arrives as about 2.53, where MagnificationGesture used
        // to run all the way into the 4× clamp) — but that is XCTest's arithmetic, not a
        // promise of the app's, and a test that pinned it down would be measuring XCTest.
        pinchProbe().pinch(withScale: 2.5, velocity: 2.0)
        Thread.sleep(forTimeInterval: 1.5)   // scroll indicators fade
        let zoomed = pixels(of: rect)
        let zoomedZoom = committedZoom()
        XCTAssertGreaterThan(difference(atOne, zoomed), 0.05,
                             "a pinch out did not change the page (#336); the app's zoom is "
                             + "\(zoomedZoom)")
        XCTAssertGreaterThan(zoomedZoom, 1.25,
                             "a pinch out did not reach the app's zoom at all (#336)")

        // And closed again, twice: a synthesised pinch in takes about 2.5× off at a time,
        // and from the 4× clamp that is two of them to be back at 1×. Both are measured,
        // because the first one is the gesture #465 was filed about.
        pinchProbe().pinch(withScale: 0.4, velocity: -2.0)
        Thread.sleep(forTimeInterval: 1.5)
        let halfClosed = pixels(of: rect)
        let halfClosedZoom = committedZoom()
        // This is the assertion the old test did not have. It compared `zoomed` against
        // `closed` and asked them to be CLOSE, which a pinch in that does nothing at all
        // satisfies perfectly — `closed` is then the same picture, difference 0. Asking
        // that the page moved is the one thing a no-op can never pass (#436).
        XCTAssertGreaterThan(difference(zoomed, halfClosed), 0.05,
                             "a pinch in did not change the page at all — the app's zoom went "
                             + "from \(zoomedZoom) to \(halfClosedZoom)")
        XCTAssertLessThan(halfClosedZoom, zoomedZoom - 0.25,
                          "a pinch in did not take any zoom back off: \(zoomedZoom) → "
                          + "\(halfClosedZoom)")

        pinchProbe().pinch(withScale: 0.4, velocity: -2.0)
        Thread.sleep(forTimeInterval: 1.5)
        let closed = pixels(of: rect)
        XCTAssertEqual(committedZoom(), 1, accuracy: 0.01,
                       "pinching in twice from the 4× clamp did not come back to 1×")
        // All the way off means the picture it started as. Safe to demand exactly that
        // here, unlike mid-gesture: 1× is a clamp, and the page is shorter than the
        // viewport, so there is only one place it can be.
        XCTAssertLessThan(difference(atOne, closed), 0.05,
                          "back at 1× the page is not the picture it started as")
    }
    /// The point between the fingers stays put (#530).
    ///
    /// The test above asks whether a pinch zooms at all. This one asks whether it zooms
    /// about the right place, which is the whole of #530: before the fix the page grew
    /// about its own top-left corner, so a word under the fingers slid down and to the
    /// right by hundreds of points and usually off the screen.
    ///
    /// **Measured off the page's own frame, in fractions of it**, which is the same
    /// arithmetic the issue was reported with. `Page 1`'s accessibility frame is the page
    /// image's real frame in window points, unclipped, at every zoom — at 4x on an iPhone
    /// it reads far wider and taller than the screen. So a point on the page can be named
    /// as a fraction of that frame once, before the pinch, and looked up again afterwards:
    /// if the zoom anchored where the fingers were, the same fraction is still under them.
    ///
    /// The tolerance is 24 points against an error the issue measured at 262: this is not a
    /// check that has to split hairs to fail. A synthesised pinch's own centre is the
    /// probe's centre, so that is the point being followed.
    func testAPinchKeepsThePointBetweenTheFingersPut() {
        app.launch()
        _ = page()
        Thread.sleep(forTimeInterval: 2)

        let probe = pinchProbe()
        let fingers = CGPoint(x: probe.frame.midX, y: probe.frame.midY)
        let atOne = page().frame
        XCTAssertEqual(committedZoom(), 1, accuracy: 0.01, "the document did not open at 1x")
        print("ZOOM ANCHOR: fingers \(fingers), page at 1x \(atOne)")
        let fraction = CGPoint(x: (fingers.x - atOne.minX) / atOne.width,
                              y: (fingers.y - atOne.minY) / atOne.height)

        // Out, which is the direction the issue was reported in.
        probe.pinch(withScale: 2.5, velocity: 2.0)
        Thread.sleep(forTimeInterval: 1.5)
        let zoomedIn = page().frame
        let zoomedZoom = committedZoom()
        print("ZOOM ANCHOR: after a pinch out, zoom \(zoomedZoom), page \(zoomedIn)")
        XCTAssertGreaterThan(zoomedZoom, 1.25,
                             "a pinch out did not reach the app's zoom at all (#336)")
        let outNow = CGPoint(x: zoomedIn.minX + fraction.x * zoomedIn.width,
                            y: zoomedIn.minY + fraction.y * zoomedIn.height)
        XCTAssertEqual(outNow.y, fingers.y, accuracy: 24,
                       "a pinch out moved the page under the fingers down the screen by "
                       + "\(outNow.y - fingers.y) points (#530): zoom \(zoomedZoom), the page "
                       + "was \(atOne) and is \(zoomedIn)")
        XCTAssertEqual(outNow.x, fingers.x, accuracy: 24,
                       "a pinch out moved the page under the fingers across the screen by "
                       + "\(outNow.x - fingers.x) points (#530): zoom \(zoomedZoom), the page "
                       + "was \(atOne) and is \(zoomedIn)")

        // And in, from a zoomed and scrolled position — the second half of the issue's
        // measurements, where the offset was left alone and then clamped.
        let beforeIn = page().frame
        let fractionIn = CGPoint(x: (fingers.x - beforeIn.minX) / beforeIn.width,
                                y: (fingers.y - beforeIn.minY) / beforeIn.height)
        probe.pinch(withScale: 0.4, velocity: -2.0)
        Thread.sleep(forTimeInterval: 1.5)
        let zoomedOut = page().frame
        let closedZoom = committedZoom()
        print("ZOOM ANCHOR: after a pinch in, zoom \(closedZoom), page \(zoomedOut)")
        XCTAssertLessThan(closedZoom, zoomedZoom - 0.25,
                          "a pinch in did not take any zoom back off: \(zoomedZoom) -> "
                          + "\(closedZoom)")
        let inNow = CGPoint(x: zoomedOut.minX + fractionIn.x * zoomedOut.width,
                           y: zoomedOut.minY + fractionIn.y * zoomedOut.height)
        XCTAssertEqual(inNow.y, fingers.y, accuracy: 24,
                       "a pinch in moved the page under the fingers up or down the screen by "
                       + "\(inNow.y - fingers.y) points (#530): zoom \(closedZoom), the page "
                       + "was \(beforeIn) and is \(zoomedOut)")
        XCTAssertEqual(inNow.x, fingers.x, accuracy: 24,
                       "a pinch in moved the page under the fingers across the screen by "
                       + "\(inNow.x - fingers.x) points (#530): zoom \(closedZoom), the page "
                       + "was \(beforeIn) and is \(zoomedOut)")
    }
}
