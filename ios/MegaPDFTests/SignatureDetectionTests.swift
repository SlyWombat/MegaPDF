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

    // --- Removing a dead signature (#576): the Swift-bridging-layer counterpart of core's
    // own behaviour (field tree + value + widget all removed, FPDFDoc_RemoveFormField),
    // which core_tests.cpp already covers against the real GPO corpus -- this proves it
    // crosses the bridging header intact, and that a reopened saved file genuinely stops
    // reporting as signed when the removal was asked for.

    func testRemovingSignaturesOnASignedDocumentClearsTheFlag() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("signed-approval"))
        defer { Task { await engine.close(doc) } }

        let before = await engine.documentFlags(doc)
        XCTAssertTrue(before.contains(.signed))
        let removed = await engine.removeDigitalSignatures(doc)
        XCTAssertTrue(removed)
        let after = await engine.documentFlags(doc)
        XCTAssertFalse(after.contains(.signed))
    }

    func testTheSavedFileGenuinelyCarriesNoSignatureOnceRemoved() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("signed-approval"))
        await engine.removeDigitalSignatures(doc)
        let bytes = try await engine.save(doc)
        await engine.close(doc)

        let reopened = try await engine.open(bytes)
        defer { Task { await engine.close(reopened) } }
        let flags = await engine.documentFlags(reopened)
        XCTAssertFalse(flags.contains(.signed),
                        "a save nobody asked to keep the signature must not still report one")
    }

    /// #576's own worry, held here the same way the Windows and Android self-tests hold it:
    /// a removal creeping into the ordinary save path is the one thing this feature decided
    /// against.
    func testASaveThatDoesNotAskToRemoveTheSignatureKeepsIt() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("signed-approval"))
        let bytes = try await engine.save(doc)
        await engine.close(doc)

        let reopened = try await engine.open(bytes)
        defer { Task { await engine.close(reopened) } }
        let flags = await engine.documentFlags(reopened)
        XCTAssertTrue(flags.contains(.signed),
                       "a save nobody asked to remove the signature from must still report one (now invalid)")
    }

    func testRemovingSignaturesOnAnUnsignedDocumentReportsNothingRemoved() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        defer { Task { await engine.close(doc) } }

        let removed = await engine.removeDigitalSignatures(doc)
        XCTAssertFalse(removed)
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
        try await ModelOpening.waitForTheDocument(model)
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

    /// The quiet Save-a-copy notice (#481, #576): fires once, the first time a copy is
    /// exported from a signed document's open, and never again for the same open. This is the
    /// kept-signature wording -- #576 corrected it from "doesn't carry over to the copy",
    /// which was untrue: `FPDF_SaveAsCopy` re-serialises the signature dictionary into the
    /// copy, so it does carry over; only its validity does not.
    func testSaveACopyNotesTheSignatureOnceThenStaysQuiet() async throws {
        let (model, url) = try await openModel(try fixture("signed-approval"))
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        XCTAssertNil(model.notice)
        _ = await model.exportFile(named: "copy.pdf")
        XCTAssertEqual(model.notice, String(localized: "The copy carries the original's digital signature, and it is no longer valid."))

        // `showNotice` clears itself after four seconds (ViewerModel.showNotice) -- waited
        // out here so the second export's silence is distinguishable from the first
        // notice simply still being up.
        try await Task.sleep(nanoseconds: 4_300_000_000)
        XCTAssertNil(model.notice, "the transient notice clears itself")
        _ = await model.exportFile(named: "copy2.pdf")
        XCTAssertNil(model.notice, "the notice is once per open, not once per copy")
    }

    /// #576: the signed-save question's Save-a-copy row removes the signature by default --
    /// the quiet notice says so, the view model's own flags reflect it immediately, and the
    /// saved file genuinely carries no signature when reopened through a fresh engine.
    func testSaveACopyRemovingTheSignatureNotesItWasRemoved() async throws {
        let (model, url) = try await openModel(try fixture("signed-approval"))
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        XCTAssertNil(model.notice)
        let copy = await model.exportFile(named: "copy.pdf", removeSignature: true)
        XCTAssertNotNil(copy)
        XCTAssertEqual(model.notice, String(localized: "Saved without the document's digital signature. The document you opened is unchanged."))
        XCTAssertFalse(model.isSignedDocument, "removal reflects immediately in the view model's own flags")

        guard let copy else { return }
        let engine = PdfEngine.shared
        let reopened = try await engine.open(file: copy)
        defer { Task { await engine.close(reopened) } }
        let flags = await engine.documentFlags(reopened)
        XCTAssertFalse(flags.contains(.signed), "the saved copy genuinely carries no signature when reopened")
    }

    /// #576's own worry, held here the same way the Windows and Android self-tests hold it: an
    /// export that does not ask for removal must still carry the (now invalid) signature -- a
    /// removal creeping into the ordinary export path is the one thing this feature decided
    /// against.
    func testSaveACopyNotRemovingKeepsTheSignature() async throws {
        let (model, url) = try await openModel(try fixture("signed-approval"))
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        let copy = await model.exportFile(named: "copy.pdf", removeSignature: false)
        XCTAssertNotNil(copy)
        XCTAssertTrue(model.isSignedDocument, "not removing it leaves the view model still reporting signed")

        guard let copy else { return }
        let engine = PdfEngine.shared
        let reopened = try await engine.open(file: copy)
        defer { Task { await engine.close(reopened) } }
        let flags = await engine.documentFlags(reopened)
        XCTAssertTrue(flags.contains(.signed), "the saved copy still carries the (now invalid) signature")
    }

    /// Save a copy on an unsigned document never shows the notice.
    func testSaveACopyOnAnUnsignedDocumentNeverNotes() async throws {
        let (model, url) = try await openModel(try fixture("fixture"))
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        _ = await model.exportFile(named: "copy.pdf")
        XCTAssertNil(model.notice)
    }
}
