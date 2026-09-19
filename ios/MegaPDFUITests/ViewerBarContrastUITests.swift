import XCTest

/// The viewer's navigation bar and status bar are legible on the dark wall.
///
/// The viewer draws the page on `Brand.backdrop`, a dark grey, in either appearance.
/// In light mode the transparent bar over it drew the document's name and the status
/// bar's clock in black: 2.0 : 1, in every App Store listing image but `home`. Dark
/// mode was always right, so the check only means something in light mode — run it
/// after `xcrun simctl ui <udid> appearance light` (it passes in both).
///
/// Measured off the screen rather than asserted on a modifier: what failed was what
/// the pixels said, and a modifier can be present and not apply (a bar whose
/// background is hidden ignores its colour scheme).
///
/// In the MegaPDFDemo scheme with the other UI tests.
final class ViewerBarContrastUITests: XCTestCase {

    private var app: XCUIApplication!

    override func setUp() {
        continueAfterFailure = false
        app = XCUIApplication()
        app.launchArguments = ["-screenshot", "viewer"]
    }

    /// Relative luminance (WCAG) of every pixel in `rect`, given in points.
    private func luminances(in rect: CGRect, of shot: XCUIScreenshot) -> [Double] {
        guard let cg = shot.image.cgImage else { return [] }
        let scale = CGFloat(cg.width) / shot.image.size.width
        let px = CGRect(x: rect.minX * scale, y: rect.minY * scale,
                        width: rect.width * scale, height: rect.height * scale).integral
            .intersection(CGRect(x: 0, y: 0, width: cg.width, height: cg.height))
        guard let crop = cg.cropping(to: px) else { return [] }
        let w = crop.width, h = crop.height
        var bytes = [UInt8](repeating: 0, count: w * h * 4)
        let ctx = CGContext(data: &bytes, width: w, height: h, bitsPerComponent: 8,
                            bytesPerRow: w * 4, space: CGColorSpaceCreateDeviceRGB(),
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.draw(crop, in: CGRect(x: 0, y: 0, width: w, height: h))
        func lin(_ c: UInt8) -> Double {
            let s = Double(c) / 255
            return s <= 0.04045 ? s / 12.92 : pow((s + 0.055) / 1.055, 2.4)
        }
        return stride(from: 0, to: bytes.count, by: 4).map {
            0.2126 * lin(bytes[$0]) + 0.7152 * lin(bytes[$0 + 1]) + 0.0722 * lin(bytes[$0 + 2])
        }
    }

    private func contrast(_ a: Double, _ b: Double) -> Double {
        (max(a, b) + 0.05) / (min(a, b) + 0.05)
    }

    /// The title's ink against its bar: the darkest and the lightest pixel in the
    /// title's frame are the bar and the glyphs, whichever way round they are, so
    /// the check also requires the glyphs to be the light side.
    func testTheTitleAndTheClockAreLightOnTheDarkBar() {
        app.launch()
        let bar = app.navigationBars.firstMatch
        XCTAssertTrue(bar.waitForExistence(timeout: 20), "no navigation bar")
        let title = bar.staticTexts.firstMatch
        XCTAssertTrue(title.waitForExistence(timeout: 10), "no title in the bar")
        // Let the launch settle: the first frames can still be the launch screen.
        Thread.sleep(forTimeInterval: 2)
        let shot = XCUIScreen.main.screenshot()

        let t = luminances(in: title.frame, of: shot)
        XCTAssertFalse(t.isEmpty, "could not read the title's pixels")
        let tMin = t.min()!, tMax = t.max()!
        XCTAssertLessThan(tMin, 0.2, "the bar behind the title is not the dark wall (min luminance \(tMin))")
        XCTAssertGreaterThan(contrast(tMin, tMax), 4.5,
                             "the title's glyphs are \(contrast(tMin, tMax)) : 1 on the bar")
        // Black glyphs on the dark wall would still give the frame a spread; what they
        // cannot give it is a pixel much lighter than the wall.
        XCTAssertGreaterThan(tMax, 0.5, "the title is drawn dark on the dark bar (max luminance \(tMax))")

        // The status bar: the band above the navigation bar, left third (the clock),
        // clear of the Dynamic Island.
        let band = CGRect(x: 0, y: 0, width: bar.frame.width / 3, height: bar.frame.minY)
        let s = luminances(in: band, of: shot)
        XCTAssertFalse(s.isEmpty, "could not read the status bar's pixels")
        XCTAssertGreaterThan(s.max()!, 0.5,
                             "the status bar's clock is drawn dark on the dark wall (max luminance \(s.max()!))")
    }
}
