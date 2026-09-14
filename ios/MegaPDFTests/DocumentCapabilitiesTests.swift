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
        XCTAssertTrue(caps.canChangeSecurity)
        XCTAssertFalse(caps.isRestricted)
    }

    func testRestrictedWithNoPermissionsAllowsNothing() {
        let caps = restricted([])
        XCTAssertFalse(caps.canEditContent)
        XCTAssertFalse(caps.canSign)
        XCTAssertFalse(caps.canFillForms)
        XCTAssertFalse(caps.canChangeSecurity)
        XCTAssertTrue(caps.isRestricted)
    }

    func testEachPermissionGatesItsTools() {
        let modify = restricted(.modify)
        XCTAssertTrue(modify.canEditContent)
        XCTAssertFalse(modify.canSign)
        XCTAssertFalse(modify.canFillForms)

        // Annotate also allows form filling.
        let annotate = restricted(.annotate)
        XCTAssertFalse(annotate.canEditContent)
        XCTAssertTrue(annotate.canSign)
        XCTAssertTrue(annotate.canFillForms)

        let forms = restricted(.fillForms)
        XCTAssertFalse(forms.canEditContent)
        XCTAssertFalse(forms.canSign)
        XCTAssertTrue(forms.canFillForms)

        // Printing and copying unlock no editing tool.
        let other = restricted([.print, .copy, .printHighQuality])
        XCTAssertFalse(other.canEditContent || other.canSign || other.canFillForms)
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
        XCTAssertFalse(forms.allows(mark))
        XCTAssertFalse(forms.allows(text))

        let annotate = restricted(.annotate)
        XCTAssertTrue(annotate.allows(toggle))
        XCTAssertTrue(annotate.allows(mark))
        XCTAssertFalse(annotate.allows(text))

        let modify = restricted(.modify)
        XCTAssertFalse(modify.allows(toggle))
        XCTAssertFalse(modify.allows(mark))
        XCTAssertTrue(modify.allows(text))

        let nothing = restricted([])
        XCTAssertFalse(nothing.allows(toggle) || nothing.allows(mark) || nothing.allows(text))
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
