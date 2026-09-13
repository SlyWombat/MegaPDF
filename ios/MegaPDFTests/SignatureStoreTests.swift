import XCTest
@testable import MegaPDF

/// The on-disk signature library: add, rename (#100), delete, and the index
/// surviving all three in order.
final class SignatureStoreTests: XCTestCase {

    private var dir: URL!

    override func setUpWithError() throws {
        dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("megapdf-signature-store-\(UUID().uuidString)")
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    private func blob(_ w: Int = 8, _ h: Int = 4) -> CGImage {
        let pixels = [UInt32](repeating: 0xFF20_2020, count: w * h)
        return PixelBuffers.image(from: pixels, width: w, height: h)!
    }

    func testRenameKeepsOrderFileAndPixels() throws {
        let store = SignatureStore(dir: dir)
        let first = try XCTUnwrap(store.add(displayName: "Signature 1", image: blob()))
        let second = try XCTUnwrap(store.add(displayName: "Signature 2", image: blob(12, 6)))

        store.rename(id: first.id, displayName: "Dave")

        let entries = store.load()
        XCTAssertEqual(entries.map(\.displayName), ["Dave", "Signature 2"])
        XCTAssertEqual(entries[0].id, first.id)
        XCTAssertEqual(entries[0].fileName, first.fileName)
        XCTAssertEqual(entries[0].pixelWidth, 8)
        XCTAssertEqual(entries[1], second)
        // The PNG is untouched: a fresh store over the same directory still reads it.
        XCTAssertEqual(SignatureStore(dir: dir).loadImage(entries[0])?.width, 8)
    }

    func testRenameUnknownIdIsANoOp() throws {
        let store = SignatureStore(dir: dir)
        let entry = try XCTUnwrap(store.add(displayName: "Signature 1", image: blob()))
        store.rename(id: "nope", displayName: "Nobody")
        XCTAssertEqual(store.load(), [entry])
    }

    func testDeleteRemovesEntryAndFile() throws {
        let store = SignatureStore(dir: dir)
        let entry = try XCTUnwrap(store.add(displayName: "Signature 1", image: blob()))
        XCTAssertNotNil(store.loadImage(entry))
        store.delete(id: entry.id)
        XCTAssertTrue(store.load().isEmpty)
        XCTAssertNil(store.loadImage(entry))
    }
}
