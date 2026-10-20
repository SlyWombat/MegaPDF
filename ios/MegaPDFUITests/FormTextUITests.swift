import XCTest

/// Tapping a PDF form's text field opens an editor, and confirming writes the value (#701).
/// The document is `formtext.pdf` (tools/gen_test_fixtures.py), handed to the app in the
/// launch environment like `BodyTextEditUITests`: one AcroForm text field, "fullname", at
/// (100,600)-(300,620) on a 612 x 792 page.
///
/// What proves the value was *written* rather than merely typed is the second tap: the editor
/// is prefilled from the field's value read back out of the engine, so a typed-but-unwritten
/// value would come back empty. The saved file's `/V` is read separately, outside the app, by
/// the unit test `FormTextTests`.
final class FormTextUITests: XCTestCase {

    private static let formTextPdf = "JVBERi0xLjcKJeLjz9MKMSAwIG9iago8PCAvVHlwZSAvRm9udCAvU3VidHlwZSAvVHlwZTEgL0Jhc2VGb250IC9IZWx2ZXRpY2EgPj4KZW5kb2JqCjIgMCBvYmoKPDwgIC9MZW5ndGggNTkgPj4Kc3RyZWFtCkJUIC9GMSAxNCBUZiA3MiA3MjAgVGQgKFdyaXRlIHlvdXIgbmFtZSBpbiB0aGUgYm94LikgVGogRVQKCmVuZHN0cmVhbQplbmRvYmoKMyAwIG9iago8PCAvVHlwZSAvWE9iamVjdCAvU3VidHlwZSAvRm9ybSAvQkJveCBbMCAwIDIwMCAyMF0gL0xlbmd0aCA0MiA+PgpzdHJlYW0KMC4xMyAwLjEzIDAuMTMgUkcgMSB3IDAuNSAwLjUgMTk5IDE5IHJlIFMKCmVuZHN0cmVhbQplbmRvYmoKNCAwIG9iago8PCAvVHlwZSAvUGFnZSAvUGFyZW50IDYgMCBSIC9NZWRpYUJveCBbMCAwIDYxMiA3OTJdIC9SZXNvdXJjZXMgPDwgL0ZvbnQgPDwgL0YxIDEgMCBSID4+ID4+IC9Db250ZW50cyAyIDAgUiAvQW5ub3RzIFs1IDAgUl0gPj4KZW5kb2JqCjUgMCBvYmoKPDwgL1R5cGUgL0Fubm90IC9TdWJ0eXBlIC9XaWRnZXQgL0ZUIC9UeCAvVCAoZnVsbG5hbWUpIC9WICgpIC9EQSAoL0hlbHYgMTIgVGYgMCBnKSAvUmVjdCBbMTAwIDYwMCAzMDAgNjIwXSAvRiA0IC9QIDQgMCBSIC9BUCA8PCAvTiAzIDAgUiA+PiA+PgplbmRvYmoKNiAwIG9iago8PCAvVHlwZSAvUGFnZXMgL0tpZHMgWzQgMCBSXSAvQ291bnQgMSA+PgplbmRvYmoKNyAwIG9iago8PCAvVHlwZSAvQ2F0YWxvZyAvUGFnZXMgNiAwIFIgL0Fjcm9Gb3JtIDw8IC9GaWVsZHMgWzUgMCBSXSAvREEgKC9IZWx2IDEyIFRmIDAgZykgL0RSIDw8IC9Gb250IDw8IC9IZWx2IDEgMCBSID4+ID4+ID4+ID4+CmVuZG9iagp4cmVmCjAgOAowMDAwMDAwMDAwIDY1NTM1IGYgCjAwMDAwMDAwMTUgMDAwMDAgbiAKMDAwMDAwMDA4NSAwMDAwMCBuIAowMDAwMDAwMTk1IDAwMDAwIG4gCjAwMDAwMDAzMzYgMDAwMDAgbiAKMDAwMDAwMDQ3OCAwMDAwMCBuIAowMDAwMDAwNjM2IDAwMDAwIG4gCjAwMDAwMDA2OTMgMDAwMDAgbiAKdHJhaWxlcgo8PCAvU2l6ZSA4IC9Sb290IDcgMCBSID4+CnN0YXJ0eHJlZgo4MzAKJSVFT0YK"

    private var app: XCUIApplication!

    override func setUp() {
        continueAfterFailure = false
        app = XCUIApplication()
        app.launchArguments += ["-AppleLanguages", "(en)", "-AppleLocale", "en_US", "-uiTestPinNotices"]
    }

    override func tearDown() {
        app?.terminate()
        app = nil
    }

    private func launch() -> XCUIElement {
        app.launchEnvironment["MEGAPDF_UITEST_PDF_BASE64"] = Self.formTextPdf
        app.launch()
        let page = app.descendants(matching: .any).matching(NSPredicate(format: "label == %@", "Page 1")).firstMatch
        XCTAssertTrue(DocumentOpening.wait(for: page, in: app), DocumentOpening.why(app, "the test document did not open"))
        return page
    }

    /// Taps the middle of the widget, given in PDF points (origin bottom-left).
    private func tapField(_ page: XCUIElement) {
        page.coordinate(withNormalizedOffset: CGVector(dx: 200.0 / 612.0, dy: (792.0 - 610.0) / 792.0)).tap()
    }

    private func editor() -> XCUIElement {
        let field = app.descendants(matching: .any).matching(identifier: "formTextField").firstMatch
        XCTAssertTrue(field.waitForExistence(timeout: 15), "tapping the form field did not open the editor")
        return field
    }

    func testTapTypeConfirmThenTheFieldHoldsTheValue() {
        let page = launch()
        tapField(page)

        let field = editor()
        field.tap()
        XCTAssertTrue(app.keyboards.firstMatch.waitForExistence(timeout: 15),
                      "the keyboard never came up for the form editor")
        field.typeText("Dave Plumber")
        app.buttons["formTextDone"].tap()
        XCTAssertTrue(app.buttons["Undo"].waitForExistence(timeout: 15), "the fill is on the undo stack")
        XCTAssertTrue(app.buttons["Undo"].isEnabled)

        // Reopen: the field's own value, read back from the document, fills the editor.
        tapField(page)
        let again = editor()
        let value = (again.value as? String) ?? ""
        XCTAssertEqual(value, "Dave Plumber", "the confirmed value was written into the field")
        app.buttons["formTextCancel"].tap()
    }

    func testCancelWritesNothing() {
        let page = launch()
        tapField(page)
        let field = editor()
        field.tap()
        XCTAssertTrue(app.keyboards.firstMatch.waitForExistence(timeout: 15))
        field.typeText("discard me")
        app.buttons["formTextCancel"].tap()

        tapField(page)
        let again = editor()
        let value = (again.value as? String) ?? ""
        XCTAssertNotEqual(value, "discard me", "a cancelled edit must not reach the field")
        app.buttons["formTextCancel"].tap()
    }
}
