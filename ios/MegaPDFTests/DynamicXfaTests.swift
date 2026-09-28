import XCTest
@testable import MegaPDF

/// #456/#457: a dynamic-XFA document — built to be filled in Adobe Reader, which PDFium (and
/// so MegaPDF) cannot render — is detected distinctly from a hybrid-XFA one, and a filling
/// tool armed on it explains rather than silently doing nothing. Fixtures are the synthetic
/// ones from `tools/gen_xfa_fixtures.py` (never a real Canadian government form: Crown
/// copyright, local-only per Dave's 2026-09-27 decision).
final class DynamicXfaEngineTests: XCTestCase {

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: name, withExtension: "pdf") else {
            throw XCTSkip("fixture \(name).pdf missing from test bundle")
        }
        return try Data(contentsOf: url)
    }

    /// The JNI/bridging-layer counterpart of core's own `test_dynamic_xfa`
    /// (core/tests/core_tests.cpp, PR #459): proves `megapdf_document_flags()` crosses the
    /// Swift bridging header intact, not the detection rule itself.
    func testDynamicXfaFixtureSetsTheFlag() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("dynamic-xfa"))
        defer { Task { await engine.close(doc) } }

        let flags = await engine.documentFlags(doc)
        XCTAssertTrue(flags.contains(.dynamicXFA))
        // #457: still opens, still reports a plausible page count, still renders -- only
        // filling is unavailable, which is the app's job, not the engine's.
        let count = await engine.pageCount(doc)
        XCTAssertEqual(count, 1)
        let size = try await engine.pageSize(doc, index: 0)
        XCTAssertGreaterThan(size.width, 0)
        XCTAssertGreaterThan(size.height, 0)
    }

    func testHybridXfaFixtureDoesNotSetTheFlag() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("hybrid-xfa"))
        defer { Task { await engine.close(doc) } }

        let flags = await engine.documentFlags(doc)
        XCTAssertFalse(flags.contains(.dynamicXFA))
    }

    func testAnOrdinaryFormDocumentDoesNotSetTheFlag() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("forms"))
        defer { Task { await engine.close(doc) } }

        let flags = await engine.documentFlags(doc)
        XCTAssertFalse(flags.contains(.dynamicXFA))
    }

    func testAnOrdinaryDocumentWithNoFormAtAllDoesNotSetTheFlag() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        defer { Task { await engine.close(doc) } }

        let flags = await engine.documentFlags(doc)
        XCTAssertFalse(flags.contains(.dynamicXFA))
    }
}

/// The view-model half: the persistent state the banner reads, and the intercept that
/// explains rather than silently doing nothing when Sign or Add text is armed.
@MainActor
final class DynamicXfaViewerModelTests: XCTestCase {

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
    /// `PageCheckTests.openModel` uses.
    private func openModel(_ bytes: Data) async throws -> (ViewerModel, URL) {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("dynamic-xfa-\(UUID().uuidString).pdf")
        try bytes.write(to: url)
        let model = ViewerModel()
        model.openPicked(url: url)
        try await waitUntil("the document opens") {
            if case .viewing = model.state { return model.document != nil && !model.busy.isBlocked }
            return false
        }
        return (model, url)
    }

    private let sampleEntry = SignatureEntry(id: "sig-1", displayName: "Test", fileName: "nonexistent.png",
                                             pixelWidth: 10, pixelHeight: 10, createdEpochMs: 0)

    func testDynamicXfaDocumentOpensAndIsFlagged() async throws {
        let (model, url) = try await openModel(try fixture("dynamic-xfa"))
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        XCTAssertTrue(model.isDynamicXfa)
        guard case let .viewing(_, pageSizes) = model.state else {
            return XCTFail("expected .viewing")
        }
        XCTAssertEqual(pageSizes.count, 1, "the placeholder page still loads (#457)")
    }

    func testHybridXfaDocumentIsNotFlagged() async throws {
        let (model, url) = try await openModel(try fixture("hybrid-xfa"))
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        XCTAssertFalse(model.isDynamicXfa)
    }

    /// #457: everything else keeps working -- Save a copy and Export as Markdown, the two
    /// whole-document exports, both still produce a real file on a dynamic-XFA document.
    func testEverythingButFillingStillWorksOnADynamicXfaDocument() async throws {
        let (model, url) = try await openModel(try fixture("dynamic-xfa"))
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        let copy = await model.exportFile(named: "copy.pdf")
        XCTAssertNotNil(copy, "Save a copy must still work")
        if let copy {
            XCTAssertTrue(FileManager.default.fileExists(atPath: copy.path))
        }

        let markdown = await model.exportMarkdownFile(named: "export.md")
        XCTAssertNotNil(markdown, "Export as Markdown must still work")
        if let markdown {
            let text = try String(contentsOf: markdown, encoding: .utf8)
            XCTAssertFalse(text.isEmpty, "the placeholder's own text still extracts")
        }
    }

    /// Arming Sign on a dynamic-XFA document explains rather than arming placement (#457):
    /// the button stays enabled -- this is not a permissions refusal -- but tapping it says
    /// why nothing happens next, rather than silently doing nothing.
    func testArmingSignOnADynamicXfaDocumentExplainsInsteadOfArming() async throws {
        let (model, url) = try await openModel(try fixture("dynamic-xfa"))
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        XCTAssertTrue(model.capabilities.canSign, "the tool stays armable, not greyed out")
        model.startPlacement(sampleEntry)
        XCTAssertNil(model.pendingSignature, "placement must not be armed")
        XCTAssertEqual(model.notice, String(localized: "This form needs Adobe Reader to fill in."))
    }

    /// Same for Add text (#457).
    func testArmingAddTextOnADynamicXfaDocumentExplainsInsteadOfArming() async throws {
        let (model, url) = try await openModel(try fixture("dynamic-xfa"))
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        XCTAssertTrue(model.capabilities.canAddText, "the tool stays armable, not greyed out")
        model.startTextPlacement()
        XCTAssertFalse(model.isPlacingText, "placement mode must not be entered")
        XCTAssertEqual(model.notice, String(localized: "This form needs Adobe Reader to fill in."))
    }

    /// Hybrid XFA is completely unaffected (#457's "done when"): arming Sign and Add text
    /// on a hybrid document behaves exactly as it does on any ordinary document.
    func testHybridXfaDocumentArmsFillingToolsNormally() async throws {
        let (model, url) = try await openModel(try fixture("hybrid-xfa"))
        defer { try? FileManager.default.removeItem(at: url); model.close() }

        model.startPlacement(sampleEntry)
        XCTAssertNotNil(model.pendingSignature, "signature placement arms normally on a hybrid document")
        model.cancelPlacement()

        model.startTextPlacement()
        XCTAssertTrue(model.isPlacingText, "text placement arms normally on a hybrid document")
        model.cancelTextPlacement()
    }
}
