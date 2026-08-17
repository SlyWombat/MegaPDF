import XCTest
import CoreGraphics
@testable import MegaPDF

/// CropBox coordinate parity (#28/#30) against the shared `cropped.pdf` fixture —
/// mirrors `TextSearchTest.searchRectsAreRelativeToTheCropBox` on Android and
/// `SearchCropBoxTests` on Windows.
///
/// The fixture's MediaBox is [0 0 612 792] and its CropBox is [0 100 612 700], so
/// the visible page is 612 x 600 and every user-space y is 100 too high.
final class CropBoxTests: XCTestCase {

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: name, withExtension: "pdf") else {
            throw XCTSkip("fixture \(name).pdf missing from test bundle")
        }
        return try Data(contentsOf: url)
    }

    func testPageSizeIsTheCropBox() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("cropped"))
        defer { Task { await engine.close(doc) } }

        let size = try await engine.pageSize(doc, index: 0)
        XCTAssertEqual(size.width, 612.0, accuracy: 1.0)
        XCTAssertEqual(size.height, 600.0, accuracy: 1.0)
    }

    func testSearchRectsAreRelativeToTheCropBox() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("cropped"))
        defer { Task { await engine.close(doc) } }

        let matches = try await engine.search(doc, pageIndex: 0, term: "megapdf")
        let rect = try XCTUnwrap(matches.first?.rects.first)

        // Text sits at user-space y=650, i.e. 50pt below the crop top -> ~550 in
        // crop space. Before the fix this came back as 650 on a 600pt-tall page.
        XCTAssertTrue(rect.bottom >= 0.0 && rect.top <= 600.0,
                      "rect must sit inside the visible page, was \(rect)")
        XCTAssertTrue(rect.bottom < 556.0 && rect.top > 544.0,
                      "rect should straddle the baseline 50pt below the crop top, was \(rect)")
    }

    /// End-to-end: a mark placed at crop-space coordinates must actually render
    /// there. The square sits near the *bottom* of the visible page, which in
    /// user space is below the crop box entirely — so before the fix the mark was
    /// written off-page and nothing appeared at all.
    func testCheckMarkPlacedInCropSpaceRenders() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("cropped"))
        defer { Task { await engine.close(doc) } }

        let before = try await engine.render(doc, index: 0, pixelWidth: 612, pixelHeight: 600)
        let (textHalf, farHalf) = Self.darkHalves(before)
        XCTAssertGreaterThan(textHalf, 0, "the fixture's text should render somewhere")
        XCTAssertEqual(farHalf, 0, "nothing should be drawn in the empty half yet")

        // Crop-space square near the bottom edge of the visible page.
        try await engine.addCheckMark(
            doc, pageIndex: 0,
            square: PdfRect(left: 72, bottom: 60, right: 85, top: 73),
            id: "mark:crop-test")

        let after = try await engine.render(doc, index: 0, pixelWidth: 612, pixelHeight: 600)
        let (_, farHalfAfter) = Self.darkHalves(after)
        XCTAssertGreaterThan(farHalfAfter, 0,
                             "the mark must render in the half where it was asked for")
    }

    /// Dark-pixel counts for the two halves of the image, ordered (half containing
    /// the fixture's text, the other half). Comparing halves rather than absolute
    /// rows keeps the assertion independent of bitmap row order.
    private static func darkHalves(_ image: CGImage) -> (Int, Int) {
        let w = image.width, h = image.height
        var buf = [UInt8](repeating: 0, count: w * h * 4)
        buf.withUnsafeMutableBytes { raw in
            guard let ctx = CGContext(
                data: raw.baseAddress, width: w, height: h,
                bitsPerComponent: 8, bytesPerRow: w * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return }
            ctx.draw(image, in: CGRect(x: 0, y: 0, width: CGFloat(w), height: CGFloat(h)))
        }
        var first = 0, second = 0
        for row in 0..<h {
            for col in 0..<w {
                let i = (row * w + col) * 4
                guard buf[i] < 128, buf[i + 1] < 128, buf[i + 2] < 128 else { continue }
                if row < h / 2 { first += 1 } else { second += 1 }
            }
        }
        // The fixture draws one line of text and nothing else, so whichever half
        // has ink before any edit is the text half.
        return first >= second ? (first, second) : (second, first)
    }
}
