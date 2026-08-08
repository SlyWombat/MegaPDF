import XCTest
@testable import MegaPDF

/// iOS engine foundation tests — mirrors Android's PdfEngineTest against the
/// same shared fixtures (tools/gen_test_fixtures.py).
final class PdfEngineTests: XCTestCase {

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: name, withExtension: "pdf") else {
            throw XCTSkip("fixture \(name).pdf missing from test bundle")
        }
        return try Data(contentsOf: url)
    }

    func testOpensAndReportsGeometry() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        defer { Task { await engine.close(doc) } }

        let count = await engine.pageCount(doc)
        XCTAssertEqual(count, 2)
        let size = try await engine.pageSize(doc, index: 0)
        XCTAssertEqual(size.width, 612.0, accuracy: 0.01)   // US Letter
        XCTAssertEqual(size.height, 792.0, accuracy: 0.01)
    }

    func testRenderProducesInk() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        defer { Task { await engine.close(doc) } }

        let image = try await engine.render(doc, index: 0, pixelWidth: 306, pixelHeight: 396)
        XCTAssertEqual(image.width, 306)

        // Count non-white pixels via a bitmap context readback.
        var pixels = [UInt32](repeating: 0, count: 306 * 396)
        let ctx = CGContext(
            data: &pixels, width: 306, height: 396, bitsPerComponent: 8,
            bytesPerRow: 306 * 4, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: 306, height: 396))
        let inked = pixels.filter { $0 != 0xFFFFFFFF }.count
        XCTAssertGreaterThan(inked, 100, "expected ink on the page")
    }

    func testSaveRoundTrips() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        let saved = try await engine.save(doc)
        await engine.close(doc)
        XCTAssertFalse(saved.isEmpty)

        let reopened = try await engine.open(saved)
        let count = await engine.pageCount(reopened)
        await engine.close(reopened)
        XCTAssertEqual(count, 2)
    }

    func testInvalidBytesThrow() async throws {
        let engine = PdfEngine.shared
        do {
            _ = try await engine.open(Data("not a pdf".utf8))
            XCTFail("expected PdfError.load")
        } catch let error as PdfError {
            if case .passwordRequired = error { XCTFail("unexpected password error") }
        }
    }

    func testStampInteropIdsReadBack() async throws {
        // The MegaPDF_Id contract (SDD §6.2): stamps written by other platforms
        // must be identifiable here.
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("stamped"))
        defer { Task { await engine.close(doc) } }

        let stamps = try await engine.stamps(doc, pageIndex: 0)
        XCTAssertEqual(Set(stamps.map(\.id)), ["sig:interop-1", "mark:interop-2"])
        let sig = stamps.first { $0.id == "sig:interop-1" }!
        XCTAssertEqual(sig.rect.left, 100.0, accuracy: 0.5)
        XCTAssertEqual(sig.rect.top, 560.0, accuracy: 0.5)
    }
}
