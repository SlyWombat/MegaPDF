import XCTest
@testable import MegaPDF

/// The permission policy behind the editing tools (#131, ADR-004 §2).
final class DocumentCapabilitiesTests: XCTestCase {

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: name, withExtension: "pdf") else {
            throw XCTSkip("fixture \(name).pdf missing from test bundle")
        }
        return try Data(contentsOf: url)
    }

    private func restricted(_ permissions: PdfPermissions) -> DocumentCapabilities {
        DocumentCapabilities(security: PdfSecurity(isEncrypted: true, revision: 6,
                                                   permissions: permissions, hasFullAccess: false))
    }

    func testUnprotectedDocumentAllowsEverything() {
        let caps = DocumentCapabilities(security: .unprotected)
        XCTAssertTrue(caps.canEditContent)
        XCTAssertTrue(caps.canSign)
        XCTAssertTrue(caps.canFillForms)
        XCTAssertTrue(caps.canAddText)
        XCTAssertTrue(caps.canChangeSecurity)
        XCTAssertFalse(caps.isRestricted)
    }

    func testFullAccessAllowsEverythingWhateverTheBits() {
        // The owner's open: its bits are the document's, but it may change the security itself.
        let caps = DocumentCapabilities(security: PdfSecurity(isEncrypted: true, revision: 6,
                                                              permissions: .print, hasFullAccess: true))
        XCTAssertTrue(caps.canEditContent)
        XCTAssertTrue(caps.canSign)
        XCTAssertTrue(caps.canFillForms)
        XCTAssertTrue(caps.canAddText)
        XCTAssertTrue(caps.canChangeSecurity)
        XCTAssertFalse(caps.isRestricted)
    }

    func testRestrictedWithNoPermissionsAllowsNothing() {
        let caps = restricted([])
        XCTAssertFalse(caps.canEditContent)
        XCTAssertFalse(caps.canSign)
        XCTAssertFalse(caps.canFillForms)
        XCTAssertFalse(caps.canAddText)
        XCTAssertFalse(caps.canChangeSecurity)
        XCTAssertTrue(caps.isRestricted)
    }

    func testEachPermissionGatesItsTools() {
        // Modify changes the document itself, and may add text boxes; it fills in nothing.
        let modify = restricted(.modify)
        XCTAssertTrue(modify.canEditContent)
        XCTAssertTrue(modify.canAddText)
        XCTAssertFalse(modify.canSign)
        XCTAssertFalse(modify.canFillForms)

        // A form that allows filling lets people fill in everything it offers: fields,
        // check marks, signatures and text boxes — but not the document's own text.
        let forms = restricted(.fillForms)
        XCTAssertFalse(forms.canEditContent)
        XCTAssertTrue(forms.canSign)
        XCTAssertTrue(forms.canFillForms)
        XCTAssertTrue(forms.canAddText)
        XCTAssertFalse(forms.canChangeSecurity)

        // Annotate implies form filling, so it allows the same.
        XCTAssertEqual(restricted(.annotate), forms)

        // Printing and copying unlock no editing tool.
        let other = restricted([.print, .copy, .printHighQuality])
        XCTAssertFalse(other.canEditContent || other.canSign || other.canFillForms || other.canAddText)
        XCTAssertTrue(other.isRestricted)
    }

    func testOperationsNeedTheirOwnPermission() {
        let square = PdfRect(left: 72, bottom: 600, right: 84, top: 612)
        let toggle = FieldToggleOperation(pageIndex: 0, x: 10, y: 10)
        let mark = MarkOperation(pageIndex: 0, square: square, id: "mark:test", adding: true)
        let text = TextBoxOperation(pageIndex: 0, id: "text:test", text: "Hi", fontSize: 12,
                                    x: 72, y: 72, adding: true)

        let forms = restricted(.fillForms)
        XCTAssertTrue(forms.allows(toggle))
        XCTAssertTrue(forms.allows(mark))
        XCTAssertTrue(forms.allows(text))

        let annotate = restricted(.annotate)
        XCTAssertTrue(annotate.allows(toggle))
        XCTAssertTrue(annotate.allows(mark))
        XCTAssertTrue(annotate.allows(text))

        let modify = restricted(.modify)
        XCTAssertFalse(modify.allows(toggle))
        XCTAssertFalse(modify.allows(mark))
        XCTAssertTrue(modify.allows(text))

        let nothing = restricted([])
        XCTAssertFalse(nothing.allows(toggle) || nothing.allows(mark) || nothing.allows(text))
    }

    func testEveryTextBoxOperationNeedsCanAddText() {
        let edit = EditTextBoxOperation(pageIndex: 0, id: "text:test",
                                        from: TextBoxStyle(text: "Hi"),
                                        to: TextBoxStyle(text: "Hello", fontSize: 14),
                                        x: 72, y: 72)
        let move = MoveTextBoxOperation(pageIndex: 0, id: "text:test",
                                        from: (x: 72, y: 72), to: (x: 90, y: 90))
        let remove = TextBoxOperation(pageIndex: 0, id: "text:test", text: "Hi", fontSize: 12,
                                      x: 72, y: 72, adding: false)
        let operations: [PdfEditOperation] = [edit, move, remove]

        // Fill forms grants text boxes without modify; nothing grants neither.
        let forms = restricted(.fillForms)
        XCTAssertFalse(forms.canEditContent)
        let nothing = restricted([])
        for operation in operations {
            XCTAssertTrue(forms.allows(operation), operation.name)
            XCTAssertTrue(restricted(.modify).allows(operation), operation.name)
            XCTAssertFalse(nothing.allows(operation), operation.name)
        }
    }

    func testOwnerOnlyFixtureIsRestrictedUntilTheOwnerUnlocksIt() async throws {
        // owner-only.pdf opens without a password but allows nothing.
        let engine = PdfEngine.shared
        let bytes = try fixture("owner-only")
        let doc = try await engine.open(bytes)
        let caps = DocumentCapabilities(security: await engine.security(doc))
        await engine.close(doc)
        XCTAssertTrue(caps.isRestricted)
        XCTAssertFalse(caps.canEditContent)
        XCTAssertFalse(caps.canSign)
        XCTAssertFalse(caps.canFillForms)
        XCTAssertFalse(caps.canChangeSecurity)

        let owner = "o-restricted"   // tools/gen_security_fixtures.sh
        let unlocked = try await engine.open(bytes, password: owner)
        let ownerCaps = DocumentCapabilities(security: await engine.security(unlocked))
        await engine.close(unlocked)
        XCTAssertFalse(ownerCaps.isRestricted)
        XCTAssertTrue(ownerCaps.canChangeSecurity)
        XCTAssertTrue(ownerCaps.canEditContent)
    }
}
