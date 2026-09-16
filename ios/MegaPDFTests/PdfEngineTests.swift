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

    private func scratch(_ name: String, _ data: Data) throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("\(UUID().uuidString)-\(name)")
        try data.write(to: url)
        return url
    }

    // #147/#148: a document opened from its file is read on demand, and a save streams into a
    // file that opens again like the document.
    func testOpensFromAFileAndSavesIntoOne() async throws {
        let engine = PdfEngine.shared
        let url = try scratch("fixture.pdf", try fixture("fixture"))
        defer { try? FileManager.default.removeItem(at: url) }
        let doc = try await engine.open(file: url)
        let count = await engine.pageCount(doc)
        XCTAssertEqual(count, 2)
        let reads = await engine.reads(doc, file: url)
        XCTAssertTrue(reads)

        let saved = FileManager.default.temporaryDirectory.appendingPathComponent("\(UUID().uuidString)-saved.pdf")
        defer { try? FileManager.default.removeItem(at: saved) }
        try await engine.save(doc, to: saved)
        let reopened = try await engine.open(file: saved, like: doc)
        let reopenedCount = await engine.pageCount(reopened)
        await engine.close(reopened)
        await engine.close(doc)
        XCTAssertEqual(reopenedCount, 2)

        do {
            _ = try await engine.open(file: FileManager.default.temporaryDirectory.appendingPathComponent("missing-\(UUID().uuidString).pdf"))
            XCTFail("a missing file opened")
        } catch PdfError.load(let code) {
            XCTAssertEqual(code, Int(FPDF_ERR_FILE))
        }
    }

    // #147: before the file a document reads is written in place, the document moves onto a
    // copy (a clone on APFS) and keeps reading what it was opened on.
    func testReadFromCopySurvivesTheFileBeingWrittenInPlace() async throws {
        let engine = PdfEngine.shared
        let url = try scratch("in-place.pdf", try fixture("fixture"))
        defer { try? FileManager.default.removeItem(at: url) }
        let doc = try await engine.open(file: url)
        try await engine.readFromCopy(doc)
        let stillReads = await engine.reads(doc, file: url)
        XCTAssertFalse(stillReads)

        let handle = try FileHandle(forWritingTo: url)
        try handle.write(contentsOf: Data("overwritten in place".utf8))
        try handle.truncate(atOffset: 20)
        try handle.close()

        _ = try await engine.pageSize(doc, index: 1)
        let saved = try await engine.save(doc)
        await engine.close(doc)
        let reopened = try await engine.open(saved)
        let count = await engine.pageCount(reopened)
        await engine.close(reopened)
        XCTAssertEqual(count, 2)
    }

    func testProtectedDocumentSavesStillProtectedAndReadsBack() async throws {
        // #132: the save check reopened the copy without the password, and the copy is
        // still protected, so every protected save failed.
        let engine = PdfEngine.shared
        let unlock = "u123"   // tools/gen_test_fixtures.py
        let doc = try await engine.open(try fixture("encrypted"), password: unlock)
        let saved = try await engine.save(doc)
        do {
            let stray = try await engine.open(saved)
            await engine.close(stray)
            XCTFail("the saved copy should still be protected")
        } catch PdfError.passwordRequired {
        }
        let reopened = try await engine.open(saved, like: doc)
        let count = await engine.pageCount(reopened)
        await engine.close(reopened)
        await engine.close(doc)
        XCTAssertEqual(count, 1)
    }

    func testNewSecurityNeedsItsPasswordAndGrantsOnlyWhatWasAllowed() async throws {
        // #131: a copy saved with new AES-256 security, then one with none.
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        let initial = await engine.security(doc)
        XCTAssertEqual(initial, .unprotected)
        let locked = try await engine.save(doc, userPassword: "new-user", ownerPassword: "new-owner",
                                           permissions: .print)
        await engine.close(doc)

        do {
            let stray = try await engine.open(locked)
            await engine.close(stray)
            XCTFail("the copy should need a password")
        } catch PdfError.passwordRequired {
        }

        let asUser = try await engine.open(locked, password: "new-user")
        let userSecurity = await engine.security(asUser)
        await engine.close(asUser)
        XCTAssertEqual(userSecurity,
                       PdfSecurity(isEncrypted: true, revision: 6, permissions: .print, hasFullAccess: false))

        let asOwner = try await engine.open(locked, password: "new-owner")
        let ownerSecurity = await engine.security(asOwner)
        let unprotected = try await engine.saveWithoutSecurity(asOwner)
        await engine.close(asOwner)
        XCTAssertTrue(ownerSecurity.hasFullAccess)

        let reopened = try await engine.open(unprotected)
        let reopenedSecurity = await engine.security(reopened)
        await engine.close(reopened)
        XCTAssertEqual(reopenedSecurity, .unprotected)
    }

    func testRestrictedDocumentRefusesToChangeItsSecurity() async throws {
        // #131: owner-only.pdf opens without a password but allows nothing.
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("owner-only"))
        let security = await engine.security(doc)
        XCTAssertTrue(security.isEncrypted)
        XCTAssertFalse(security.hasFullAccess)
        XCTAssertFalse(security.allows(.print))
        do {
            _ = try await engine.saveWithoutSecurity(doc)
            XCTFail("only the owner may remove the security")
        } catch PdfError.restricted {
        }
        await engine.close(doc)
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
