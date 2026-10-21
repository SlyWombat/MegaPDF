import XCTest
@testable import MegaPDF

/// #712: the core's form-field flags and maximum length through the Swift binding, on #714's
/// `formflow.pdf` (tools/gen_test_fixtures.py; a committed copy sits beside these tests):
/// page 1 has two single-line fields ("first", "last"), a multi-line one ("notes") and a
/// checkbox; page 2 one more single-line field.
final class FormFieldFlagsTests: XCTestCase {

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: name, withExtension: "pdf") else {
            XCTFail("fixture \(name).pdf missing from test bundle")
            throw XCTSkip("fixture \(name).pdf missing")
        }
        return try Data(contentsOf: url)
    }

    func testOnlyTheNotesFieldTakesReturns() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("formflow"))
        defer { Task { await engine.close(doc) } }

        let fields = try await engine.formTextFields(doc, pageIndex: 0)
        XCTAssertEqual(fields.map(\.name), ["first", "last", "notes"])
        XCTAssertEqual(fields.map(\.isMultiline), [false, false, true])
        XCTAssertEqual(fields.map(\.isReadOnly), [false, false, false])
        XCTAssertEqual(fields.map(\.isComb), [false, false, false])
        XCTAssertEqual(fields.map(\.maxLength), [0, 0, 0], "no /MaxLen in the fixture")

        let second = try await engine.formTextFields(doc, pageIndex: 1)
        XCTAssertEqual(second.map(\.name), ["second"])
        XCTAssertFalse(second[0].isMultiline)
    }

    func testTheFlagBitsAreTheCores() {
        var field = PdfFormTextField(name: "f", value: "", rect: PdfRect(left: 0, bottom: 0, right: 1, top: 1))
        field.flags = 0x1 | 0x4   // READONLY | COMB, the core's bits
        XCTAssertTrue(field.isReadOnly)
        XCTAssertTrue(field.isComb)
        XCTAssertFalse(field.isMultiline)
        XCTAssertFalse(field.isMasked)
    }

    func testAMaxLengthCutsTheDraftAndZeroIsNoLimit() {
        XCTAssertEqual(FormTextLimit.apply(5, to: "abcdefgh"), "abcde")
        XCTAssertEqual(FormTextLimit.apply(5, to: "abc"), "abc")
        XCTAssertNil(FormTextLimit.apply(0, to: "abcdefgh"))
        // Characters, not code units: an emoji is one.
        XCTAssertEqual(FormTextLimit.apply(2, to: "a😀bc"), "a😀")
    }
}
