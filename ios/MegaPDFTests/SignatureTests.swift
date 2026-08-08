import XCTest
@testable import MegaPDF

/// Cleanup pixel-math parity (SDD §6.2 contract) — mirrors the Android and
/// desktop test expectations exactly.
final class SignatureProcessorTests: XCTestCase {

    private func argb(_ a: UInt32, _ r: UInt32, _ g: UInt32, _ b: UInt32) -> UInt32 {
        (a << 24) | (r << 16) | (g << 8) | b
    }

    func testNearWhiteBecomesTransparentInkStays() {
        let white = argb(255, 250, 250, 250)
        let ink = argb(255, 30, 30, 120)
        let out = SignatureProcessor.removeWhiteBackground([white, ink])
        XCTAssertEqual(out[0] >> 24, 0)
        XCTAssertEqual(out[1], ink)
    }

    func testLuminanceCutoffWithBgrWeights() {
        let green = argb(255, 0, 255, 0)   // luminance ≈ 149.7 → kept
        let gray = argb(255, 240, 240, 240)  // 240 → removed
        let out = SignatureProcessor.removeWhiteBackground([green, gray])
        XCTAssertEqual(out[0], green)
        XCTAssertEqual(out[1] >> 24, 0)
    }

    func testTrimCropsToInkPlusMargin() {
        let w = 30, h = 20
        var pixels = [UInt32](repeating: 0, count: w * h)
        pixels[10 * w + 12] = argb(255, 0, 0, 0)
        let trimmed = SignatureProcessor.trimToInk(pixels, width: w, height: h)
        XCTAssertEqual(trimmed.width, 9)
        XCTAssertEqual(trimmed.height, 9)
        XCTAssertEqual(trimmed.pixels[4 * 9 + 4], argb(255, 0, 0, 0))
    }

    func testTrimClampsAtEdges() {
        let w = 10, h = 10
        var pixels = [UInt32](repeating: 0, count: w * h)
        pixels[0] = argb(255, 0, 0, 0)
        let trimmed = SignatureProcessor.trimToInk(pixels, width: w, height: h)
        XCTAssertEqual(trimmed.width, 5)
        XCTAssertEqual(trimmed.height, 5)
    }

    func testNothingVisibleKeepsImage() {
        let pixels = [UInt32](repeating: 10 << 24, count: 25)  // alpha 10 ≤ 16
        let trimmed = SignatureProcessor.trimToInk(pixels, width: 5, height: 5)
        XCTAssertEqual(trimmed.width, 5)
        XCTAssertEqual(trimmed.height, 5)
    }

    func testTransparencyDetection() {
        XCTAssertTrue(SignatureProcessor.hasTransparency([0]))
        XCTAssertFalse(SignatureProcessor.hasTransparency(
            [argb(255, 10, 10, 10), argb(252, 200, 200, 200)]))
    }
}

/// Engine stamp round-trip (#22) — mirrors Android's SignatureStampTest.
final class SignatureStampTests: XCTestCase {

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: name, withExtension: "pdf") else {
            throw XCTSkip("fixture \(name).pdf missing from test bundle")
        }
        return try Data(contentsOf: url)
    }

    /// 12x8 opaque dark block with a transparent left column.
    private func testPixels(w: Int = 12, h: Int = 8) -> [UInt32] {
        (0..<(w * h)).map { i in
            i % w == 0 ? 0 : (0xFF << 24) | (0x20 << 16) | (0x30 << 8) | 0x90
        }
    }

    func testPlacePersistReadbackRemove() async throws {
        let engine = PdfEngine.shared
        let rect = PdfRect(left: 100, bottom: 500, right: 190, top: 560)
        let doc = try await engine.open(try fixture("fixture"))
        try await engine.addImageStamp(doc, pageIndex: 0, pixels: testPixels(),
                                       pixelWidth: 12, pixelHeight: 8,
                                       rect: rect, id: "sig:ios-test-1")
        var stamps = try await engine.stamps(doc, pageIndex: 0)
            .filter { $0.id.hasPrefix("sig:") }
        XCTAssertEqual(stamps.count, 1)
        XCTAssertEqual(stamps[0].rect.left, 100.0, accuracy: 0.5)
        XCTAssertEqual(stamps[0].rect.top, 560.0, accuracy: 0.5)

        let saved = try await engine.save(doc)
        await engine.close(doc)

        // Interop contract: identifiable by MegaPDF_Id after save/reopen,
        // native-res readback works, remove works.
        let reopened = try await engine.open(saved)
        stamps = try await engine.stamps(reopened, pageIndex: 0)
            .filter { $0.id.hasPrefix("sig:") }
        XCTAssertEqual(stamps.map(\.id), ["sig:ios-test-1"])

        let image = try await engine.stampImage(reopened, pageIndex: 0,
                                                annotIndex: stamps[0].annotIndex)
        XCTAssertNotNil(image)
        XCTAssertEqual(image?.width, 12)
        XCTAssertEqual(image?.height, 8)
        XCTAssertTrue(image!.pixels.contains { ($0 >> 24) > 0 },
                      "readback should contain visible pixels")

        try await engine.removeAnnot(reopened, pageIndex: 0,
                                     annotIndex: stamps[0].annotIndex)
        let remaining = try await engine.stamps(reopened, pageIndex: 0)
        await engine.close(reopened)
        XCTAssertTrue(remaining.filter { $0.id.hasPrefix("sig:") }.isEmpty)
    }
}
