import XCTest
@testable import MegaPDF

/// #476/#481 phase 1: a document carrying an existing digital signature is detected --
/// distinctly from a certification (`/DocMDP`) signature, which forbids modification
/// outright rather than merely being invalidated by it -- and the app warns before a save
/// path that would overwrite it, while leaving every other flow untouched. Fixtures are the
/// synthetic ones from `tools/gen_signature_fixtures.py` (never a real GPO document: staged
/// read-only at ~/pdf-public on kdocker3, not committed).
final class SignatureDetectionEngineTests: XCTestCase {

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: name, withExtension: "pdf") else {
            throw XCTSkip("fixture \(name).pdf missing from test bundle")
        }
        return try Data(contentsOf: url)
    }

    /// The Swift-bridging-layer counterpart of core's own signature-detection test
    /// (core/tests/core_tests.cpp, PR #486): proves `megapdf_document_flags()` crosses the
    /// bridging header intact for both bits, not the detection rule itself.
    func testApprovalSignatureSetsSignedButNotCertification() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("signed-approval"))
        defer { Task { await engine.close(doc) } }

        let flags = await engine.documentFlags(doc)
        XCTAssertTrue(flags.contains(.signed))
        XCTAssertFalse(flags.contains(.signedCertification))
    }

    func testCertifiedSignatureSetsBothBits() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("signed-certified"))
        defer { Task { await engine.close(doc) } }

        let flags = await engine.documentFlags(doc)
        XCTAssertTrue(flags.contains(.signed))
        XCTAssertTrue(flags.contains(.signedCertification))
    }

    /// The unsigned control (#481's brief): an ordinary document sets neither bit.
    func testAnUnsignedDocumentSetsNeitherBit() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        defer { Task { await engine.close(doc) } }

        let flags = await engine.documentFlags(doc)
        XCTAssertFalse(flags.contains(.signed))
        XCTAssertFalse(flags.contains(.signedCertification))
    }
}

/// The view-model half: the computed facts the Save/Save-a-copy/Password confirmations read,
/// and the once-per-open notice Save a copy gives on a signed document.
@MainActor
final class SignatureDetectionViewerModelTests: XCTestCase {

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: name, withExtension: "pdf") else {
            throw XCTSkip("fixture \(name).pdf missing from test bundle")
        }
        return try Data(contentsOf: url)
    }

    /// Polls `condition` on the main actor until it holds or `timeout` passes.
    private func waitUntil(_ what: String, timeout: TimeInterval = 15,
                           _ condition: @MainActor () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition() {
            if Date() > deadline {
                XCTFail("timed out waiting for: \(what)")
                throw CancellationError()
            }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
    }

    /// A model with `bytes` open from a writable temporary file, the same shape
    /// `DynamicXfaViewerModelTests.openModel` uses.
    private func openModel(_ bytes: Data) async throws -> (ViewerModel, URL) {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("signature-\(UUID().uuidString).pdf")
        try bytes.write(to: url)
        let model = ViewerModel()
        model.openPicked(url: url)
        try await waitUntil("the document opens") {
            if case .viewing = model.state { return model.document != nil && !model.busy.isBlocked }
            return false
        }
        return (model, url)
    }

    func testApprovalSignatureIsSignedButNotCertified() async throws {
        let (model, url) = try await openModel(try fixture("signed-approval"))
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        XCTAssertTrue(model.isSignedDocument)
        XCTAssertFalse(model.isCertifiedSignature)
    }

    func testCertifiedSignatureIsSignedAndCertified() async throws {
        let (model, url) = try await openModel(try fixture("signed-certified"))
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        XCTAssertTrue(model.isSignedDocument)
        XCTAssertTrue(model.isCertifiedSignature)
    }

    /// The unsigned control.
    func testAnUnsignedDocumentIsNeitherSignedNorCertified() async throws {
        let (model, url) = try await openModel(try fixture("fixture"))
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        XCTAssertFalse(model.isSignedDocument)
        XCTAssertFalse(model.isCertifiedSignature)
    }

    /// #481's "nothing is refused": Save a copy still produces a real file on a signed
    /// document -- the destructive path is Save itself (overwriting the original), not this
    /// one, which the view gates with its own confirmation (untestable here without a host
    /// view, the same reason #173's redaction confirmation has no unit test either).
    func testSaveACopyStillWorksOnACertifiedDocument() async throws {
        let (model, url) = try await openModel(try fixture("signed-certified"))
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        let copy = await model.exportFile(named: "copy.pdf")
        XCTAssertNotNil(copy)
        if let copy {
            XCTAssertTrue(FileManager.default.fileExists(atPath: copy.path))
        }
    }

    /// The quiet Save-a-copy notice (#481): fires once, the first time a copy is exported
    /// from a signed document's open, and never again for the same open.
    func testSaveACopyNotesTheSignatureOnceThenStaysQuiet() async throws {
        let (model, url) = try await openModel(try fixture("signed-approval"))
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        XCTAssertNil(model.notice)
        _ = await model.exportFile(named: "copy.pdf")
        XCTAssertEqual(model.notice, String(localized: "This document's digital signature doesn't carry over to the copy."))

        // `showNotice` clears itself after four seconds (ViewerModel.showNotice) -- waited
        // out here so the second export's silence is distinguishable from the first
        // notice simply still being up.
        try await Task.sleep(nanoseconds: 4_300_000_000)
        XCTAssertNil(model.notice, "the transient notice clears itself")
        _ = await model.exportFile(named: "copy2.pdf")
        XCTAssertNil(model.notice, "the notice is once per open, not once per copy")
    }

    /// Save a copy on an unsigned document never shows the notice.
    func testSaveACopyOnAnUnsignedDocumentNeverNotes() async throws {
        let (model, url) = try await openModel(try fixture("fixture"))
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        _ = await model.exportFile(named: "copy.pdf")
        XCTAssertNil(model.notice)
    }
}
