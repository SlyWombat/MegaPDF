import XCTest
@testable import MegaPDF

/// Filling an AcroForm text field on iPhone and iPad (#701), through the model's real path:
/// a tap opens the editor, Done writes the value through `megapdf_form_set_text`, the value
/// survives save and reopen, and one undo restores the old value. `formtext.pdf` is the
/// shared fixture with one text field `fullname` at (100,600)-(300,620) on a 612x792 page.
@MainActor
final class FormTextTests: XCTestCase {

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: name, withExtension: "pdf") else {
            throw XCTSkip("fixture \(name).pdf missing from test bundle")
        }
        return try Data(contentsOf: url)
    }

    private func waitUntil(_ what: String, timeout: TimeInterval = 20,
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

    private func openModel() async throws -> (ViewerModel, URL) {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("formtext-\(UUID().uuidString).pdf")
        try fixture("formtext").write(to: url)
        let model = ViewerModel()
        model.pageCheck = { _, _ in .keepsLook }
        model.openPicked(url: url)
        try await ModelOpening.waitForTheDocument(model)
        return (model, url)
    }

    private func tapField(_ model: ViewerModel) async throws {
        // Centre of the widget (200, 610), as fractions of the 612x792 page.
        model.onPageTapped(index: 0, xFraction: 200.0 / 612.0, yFraction: (792.0 - 610.0) / 792.0)
        try await waitUntil("the form editor opens") { model.pendingFormText != nil }
    }

    private func settle(_ model: ViewerModel) async throws {
        try await waitUntil("the model settles") { !model.busy.isBlocked }
    }

    private func fieldValue(_ model: ViewerModel) async throws -> String {
        let doc = try XCTUnwrap(model.document)
        let fields = try await PdfEngine.shared.formTextFields(doc, pageIndex: 0)
        XCTAssertEqual(fields.count, 1)
        return fields.first?.value ?? "<none>"
    }

    func testTheEngineReportsTheTextFieldAndNotAsAButton() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("formtext"))
        defer { Task { await engine.close(doc) } }
        let text = try await engine.formTextFields(doc, pageIndex: 0)
        XCTAssertEqual(text.map(\.name), ["fullname"])
        XCTAssertEqual(text.first?.value, "")
        XCTAssertEqual(text.first?.rect.left ?? 0, 100, accuracy: 0.01)
        let buttons = try await engine.formFields(doc, pageIndex: 0)
        XCTAssertTrue(buttons.isEmpty, "a text field is not a checkbox")
    }

    func testTapFillSaveReopenAndUndo() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }

        try await tapField(model)
        XCTAssertEqual(model.pendingFormText?.name, "fullname")
        XCTAssertEqual(model.formTextDraft, "", "prefilled with the field's current value")

        model.formTextDraft = "Hélène Bélanger"
        model.commitFormText(model.formTextDraft)
        try await waitUntil("the edit is applied") { model.isDirty }
        try await settle(model)
        XCTAssertNil(model.pendingFormText)
        let filled = try await fieldValue(model)
        XCTAssertEqual(filled, "Hélène Bélanger")

        model.save()
        try await waitUntil("the save finishes") { !model.isSaving && !model.busy.isBlocked }
        XCTAssertFalse(model.isDirty)

        // Reopen the bytes on disk, outside the model.
        let engine = PdfEngine.shared
        let reopened = try await engine.open(try Data(contentsOf: url))
        let after = try await engine.formTextFields(reopened, pageIndex: 0)
        await engine.close(reopened)
        XCTAssertEqual(after.first?.value, "Hélène Bélanger", "the value survives save and reopen")

        // A second tap prefills with what is there now, and one undo takes back the one edit.
        try await tapField(model)
        XCTAssertEqual(model.formTextDraft, "Hélène Bélanger")
        model.cancelFormText()
        model.undo()
        try await settle(model)
        let undone = try await fieldValue(model)
        XCTAssertEqual(undone, "", "undo restores the previous value in one step")
        XCTAssertFalse(model.canUndo, "the fill was one undo step")
    }

    func testAnUnchangedValueIsNotAnEdit() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }
        try await tapField(model)
        model.commitFormText(model.formTextDraft)
        try await settle(model)
        XCTAssertFalse(model.isDirty)
        XCTAssertFalse(model.canUndo)
    }

    func testCancelLeavesTheFieldAlone() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }
        try await tapField(model)
        model.formTextDraft = "never written"
        model.cancelFormText()
        try await settle(model)
        XCTAssertNil(model.pendingFormText)
        XCTAssertFalse(model.isDirty)
        let value = try await fieldValue(model)
        XCTAssertEqual(value, "")
    }
}
