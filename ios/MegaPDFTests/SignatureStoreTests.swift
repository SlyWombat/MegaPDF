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

    // MARK: - the desktop store's behaviour, all three platforms (#333)

    func testAddRefusesPastTheSoftLimit() throws {
        let store = SignatureStore(dir: dir)
        for i in 0..<SignatureStore.softLimit {
            XCTAssertNotNil(store.add(displayName: "Signature \(i)", image: blob()), "signature \(i)")
        }

        XCTAssertNil(store.add(displayName: "One too many", image: blob()))
        XCTAssertEqual(store.load().count, SignatureStore.softLimit)
        XCTAssertTrue(store.isFull)
        // Refused before anything was written: no image was left behind either.
        let images = try FileManager.default.contentsOfDirectory(atPath: dir.path)
            .filter { $0.hasSuffix(".png") }
        XCTAssertEqual(images.count, SignatureStore.softLimit)
    }

    func testLoadDropsAnEntryWhoseImageHasGone() throws {
        let store = SignatureStore(dir: dir)
        let kept = try XCTUnwrap(store.add(displayName: "Signature 1", image: blob()))
        let gone = try XCTUnwrap(store.add(displayName: "Signature 2", image: blob()))

        try FileManager.default.removeItem(at: dir.appendingPathComponent(gone.fileName))

        XCTAssertEqual(store.load().map(\.id), [kept.id])
        // And the next write drops it from the index itself, so it is not listed again.
        let third = try XCTUnwrap(store.add(displayName: "Signature 3", image: blob()))
        XCTAssertEqual(store.load().map(\.id), [kept.id, third.id])
    }

    /// The order the three platforms standardised on (#333): the index stops naming the
    /// image before the image goes, so a failed index write leaves the pair intact rather
    /// than a row pointing at nothing. A folder that will not take the new index is how
    /// that becomes visible — the image must still be there afterwards.
    func testDeleteTakesTheIndexEntryBeforeTheImage() throws {
        let store = SignatureStore(dir: dir)
        let entry = try XCTUnwrap(store.add(displayName: "Signature 1", image: blob()))

        try XCTSkipUnless(refusesNewFiles(), "this file system does not deny the folder's mode")
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: dir.path) }
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: dir.path)

        store.delete(id: entry.id)

        XCTAssertTrue(
            FileManager.default.fileExists(atPath: dir.appendingPathComponent(entry.fileName).path),
            "the image outlives a failed index write")
    }

    /// Makes the folder refuse new files, and says whether it now really does: a user the
    /// mode does not apply to (root in a container) is not denied at all.
    private func refusesNewFiles() throws -> Bool {
        let original = try FileManager.default.attributesOfItem(atPath: dir.path)[.posixPermissions]
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: dir.path)
        let probe = dir.appendingPathComponent("probe")
        let refused = !FileManager.default.createFile(atPath: probe.path, contents: Data())
        if !refused { try? FileManager.default.removeItem(at: probe) }
        try? FileManager.default.setAttributes([.posixPermissions: original], ofItemAtPath: dir.path)
        return refused
    }
}
