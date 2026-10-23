import XCTest
@testable import MegaPDF

/// #593: Shrink for email on iOS. The core's own suite covers the rules, the progress, the flag
/// and the lock (`core_tests.cpp`, `test_shrink_progress_and_cancel`). What only this binding can
/// say: that the ImageIO encoder is reached through the C callback and its JPEG lands in the
/// document, that BGRA arrives as BGRA (not with red and blue swapped), that a flag raised from
/// Swift stops the run, and that the model offers a smaller copy while the open document stays
/// exactly what it was.
@MainActor
final class ShrinkTests: XCTestCase {

    /// `pages` pages, each drawing one 300 × 300 px DeviceRGB 8 bpc picture, uncompressed
    /// (270,000 bytes), at 72 × 72 pt: oversized for its 150 dpi target of 150 px. A noisy blue
    /// (R 30–61, G 70–101, B 185–216). Built as bytes: the pixels are not UTF-8.
    static func largeImagePdf(pages: Int) -> Data {
        let side = 300
        var pixels = [UInt8](repeating: 0, count: side * side * 3)
        // Noise over the blue, so a save's Flate cannot squeeze it under the size floors: a flat
        // picture saves at 289 bytes and is then not worth shrinking (measured for #593).
        var seed: UInt32 = 593
        func noise() -> UInt8 { seed = seed &* 1103515245 &+ 12345; return UInt8((seed >> 16) & 31) }
        for i in stride(from: 0, to: pixels.count, by: 3) {
            pixels[i] = 30 + noise(); pixels[i + 1] = 70 + noise(); pixels[i + 2] = 185 + noise()
        }
        var pdf: [UInt8] = Array("%PDF-1.4\n".utf8)
        var offsets: [Int] = []
        func add(_ head: String, stream: [UInt8]? = nil) {
            offsets.append(pdf.count)
            pdf += Array("\(offsets.count) 0 obj\n\(head)".utf8)
            if let stream { pdf += Array("\nstream\n".utf8) + stream + Array("\nendstream".utf8) }
            pdf += Array("\nendobj\n".utf8)
        }
        let kids = (0..<pages).map { "\(3 + $0 * 3) 0 R" }.joined(separator: " ")
        add("<< /Type /Catalog /Pages 2 0 R >>")
        add("<< /Type /Pages /Kids [\(kids)] /Count \(pages) >>")
        for index in 0..<pages {
            let page = 3 + index * 3
            add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im1 \(page + 1) 0 R >> >> /Contents \(page + 2) 0 R >>")
            add("<< /Type /XObject /Subtype /Image /Width \(side) /Height \(side) /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length \(pixels.count) >>",
                stream: pixels)
            let body = Array("q 72 0 0 72 100 600 cm /Im1 Do Q".utf8)
            add("<< /Length \(body.count) >>", stream: body)
        }
        let xref = pdf.count
        var tail = "xref\n0 \(offsets.count + 1)\n0000000000 65535 f \n"
        for offset in offsets { tail += String(format: "%010d 00000 n \n", offset) }
        tail += "trailer\n<< /Size \(offsets.count + 1) /Root 1 0 R >>\nstartxref\n\(xref)\n%%EOF\n"
        pdf += Array(tail.utf8)
        return Data(pdf)
    }

    private final class Seen: @unchecked Sendable {
        private let lock = NSLock()
        private var calls: [[Int]] = []
        func add(_ done: Int, _ total: Int) { lock.lock(); calls.append([done, total]); lock.unlock() }
        var all: [[Int]] { lock.lock(); defer { lock.unlock() }; return calls }
    }

    func testShrinkReplacesTheOversizedImage() async throws {
        let engine = PdfEngine.shared
        let input = Self.largeImagePdf(pages: 1)
        let doc = try await engine.open(input)
        let seen = Seen()
        let outcome = await engine.shrinkImages(doc, stop: CoreCancelFlag()) { done, total in seen.add(done, total) }
        XCTAssertEqual(outcome, ShrinkOutcome(considered: 1, replaced: 1, stopped: false))
        XCTAssertEqual(seen.all, [[0, 1], [1, 1]])
        let saved = try await engine.save(doc)
        XCTAssertLessThan(saved.count, input.count)

        // Still the fixture's blue, give or take the JPEG: swapped channels would read
        // about (200, 85, 45). The picture is at x 100...172, y 600...672 in PDF space.
        let image = try await engine.render(doc, index: 0, pixelWidth: 612, pixelHeight: 792)
        let (r, g, b) = try XCTUnwrap(Self.rgb(of: image, x: 136, y: 792 - 636))
        // Within 35 of the fixture's mean (45, 85, 200) on every channel: a swap reads about
        // (200, 85, 45), and a misaligned byte order about (85, 45, 255).
        XCTAssertTrue(abs(r - 45) < 35 && abs(g - 85) < 35 && abs(b - 200) < 35,
                      "expected the fixture's blue, got (\(r), \(g), \(b))")
        await engine.close(doc)
    }

    func testARaisedFlagStopsTheShrink() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(Self.largeImagePdf(pages: 3))
        let flag = try XCTUnwrap(CoreCancelFlag())
        // Raised after the first of three pictures, from the shrink's own thread.
        let outcome = await engine.shrinkImages(doc, stop: flag) { done, _ in if done == 1 { flag.raise() } }
        XCTAssertEqual(outcome?.stopped, true)
        XCTAssertEqual(outcome?.considered, 1)
        await engine.close(doc)
    }

    func testTheModelOffersASmallerCopyAndTheDocumentIsUnchanged() async throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("shrink-593-\(UUID().uuidString).pdf")
        let input = Self.largeImagePdf(pages: 1)
        try input.write(to: url)
        defer { try? FileManager.default.removeItem(at: url) }
        let model = ViewerModel()
        model.openPicked(url: url)
        try await ModelOpening.waitForTheDocument(model)
        let doc = try XCTUnwrap(model.document)
        let before = try await PdfEngine.shared.save(doc)

        await model.shrinkForEmail(named: url.lastPathComponent)

        let ready = try XCTUnwrap(model.shrinkReady, "a smaller copy is offered; status: \(model.statusMessage ?? "none")")
        XCTAssertLessThan(ready.after, ready.before)
        XCTAssertTrue(FileManager.default.fileExists(atPath: ready.url.path))
        // Named through the catalogue, so "… - réduit.pdf" under the French run (ios-ci.yml).
        let base = url.deletingPathExtension().lastPathComponent
        XCTAssertEqual(ready.url.lastPathComponent, String(localized: "\(base) - smaller") + ".pdf")
        // The open document is the one that was opened, its bytes as they were, and clean.
        XCTAssertTrue(model.document === doc)
        let after = try await PdfEngine.shared.save(doc)
        XCTAssertEqual(Self.withoutID(after), Self.withoutID(before))
        XCTAssertFalse(model.isDirty)
        XCTAssertEqual(try Data(contentsOf: url), input)

        // Done: the staged copy goes, and nothing is said.
        model.finishShrunkExport(saved: false)
        XCTAssertNil(model.shrinkReady)
        XCTAssertFalse(FileManager.default.fileExists(atPath: ready.url.path))
        XCTAssertNil(model.statusMessage)
    }

    /// D7: a copy that is not strictly smaller than the on-screen document (serialised, since the
    /// phones have no on-disk original once there are unsaved edits) is not offered. The sizes
    /// come through the model's `shrinkSizeOf` seam: the core replaces a picture only when it is
    /// at least 10% smaller and both files come from the same writer, so no fixture makes a real
    /// copy come out no smaller. EQUAL is "not smaller" too, so the seam reports the same size.
    func testACopyThatIsNotSmallerIsNotOffered() async throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("shrink-593-equal-\(UUID().uuidString).pdf")
        let input = Self.largeImagePdf(pages: 1)
        try input.write(to: url)
        defer { try? FileManager.default.removeItem(at: url) }
        let model = ViewerModel()
        model.openPicked(url: url)
        try await ModelOpening.waitForTheDocument(model)
        let doc = try XCTUnwrap(model.document)
        model.shrinkSizeOf = { _ in 1000 }

        await model.shrinkForEmail(named: url.lastPathComponent)

        XCTAssertNil(model.shrinkReady, "no result sheet for a copy that is not smaller")
        XCTAssertNil(model.stagedShrunkCopy, "no copy is left staged for Share or Save to Files")
        XCTAssertEqual(model.statusMessage, String(localized: "Shrinking wouldn't make this document any smaller."))
        // And the open document is still the one that was opened.
        XCTAssertTrue(model.document === doc)
        XCTAssertFalse(model.isDirty)
        XCTAssertEqual(try Data(contentsOf: url), input)
    }

    /// `data` without its trailer's `/ID[…]`: every save writes a fresh one, so two saves of an
    /// unchanged document differ there and only there (62 bytes of 225,133, measured for #593).
    private static func withoutID(_ data: Data) -> Data {
        guard let start = data.range(of: Data("/ID[".utf8), options: .backwards),
              let end = data.range(of: Data("]".utf8), in: start.upperBound..<data.endIndex) else { return data }
        var copy = data
        copy.removeSubrange(start.lowerBound..<end.upperBound)
        return copy
    }

    /// The RGB of one pixel of `image`, drawn into a known RGBA layout.
    private static func rgb(of image: CGImage, x: Int, y: Int) -> (Int, Int, Int)? {
        var pixel = [UInt8](repeating: 0, count: 4)
        guard let context = CGContext(data: &pixel, width: 1, height: 1, bitsPerComponent: 8, bytesPerRow: 4,
                                      space: CGColorSpaceCreateDeviceRGB(),
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        // Draw the image so that pixel (x, y), counted from the top, lands on the one pixel.
        context.draw(image, in: CGRect(x: -x, y: -(image.height - 1 - y), width: image.width, height: image.height))
        return (Int(pixel[0]), Int(pixel[1]), Int(pixel[2]))
    }
}
