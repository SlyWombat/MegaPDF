import XCTest

/// Editing the document's own text (#113), driven through the real UI on documents
/// built for each tier: a standard-font heading the edit can keep its font for (tier 1),
/// a Symbol-font heading no Latin text fits (tier 2, the substitution notice), and a page
/// with no text at all (tier 3, the scanned-image hint). The PDFs are handed to the app
/// in the launch environment, which only Debug builds read.
final class BodyTextEditUITests: XCTestCase {

    private var app: XCUIApplication!

    override func setUp() {
        continueAfterFailure = false
        app = XCUIApplication()
        // English labels regardless of the simulator's language.
        app.launchArguments += ["-AppleLanguages", "(en)", "-AppleLocale", "en_US"]
    }

    // MARK: - documents

    /// One page; `content` is the page's content stream, `font` the /F1 resource.
    private func pdf(content: String, font: String?) -> String {
        var pdf = "%PDF-1.4\n"
        var offsets: [Int] = []
        func add(_ body: String) {
            offsets.append(pdf.utf8.count)
            pdf += "\(offsets.count) 0 obj\n\(body)\nendobj\n"
        }
        let resources = font == nil ? "<< >>" : "<< /Font << /F1 4 0 R >> >>"
        add("<< /Type /Catalog /Pages 2 0 R >>")
        add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
        add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources \(resources) /Contents 5 0 R >>")
        add(font ?? "<< >>")
        add("<< /Length \(content.utf8.count) >>\nstream\n\(content)\nendstream")
        let xref = pdf.utf8.count
        pdf += "xref\n0 \(offsets.count + 1)\n0000000000 65535 f \n"
        for offset in offsets { pdf += String(format: "%010d 00000 n \n", offset) }
        pdf += "trailer\n<< /Size \(offsets.count + 1) /Root 1 0 R >>\nstartxref\n\(xref)\n%%EOF\n"
        return Data(pdf.utf8).base64EncodedString()
    }

    private var helveticaHeading: String {
        pdf(content: "BT /F1 24 Tf 72 700 Td (Quarterly report) Tj ET",
            font: "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
    }

    private var symbolHeading: String {
        pdf(content: "BT /F1 24 Tf 72 700 Td (abgd) Tj ET",
            font: "<< /Type /Font /Subtype /Type1 /BaseFont /Symbol >>")
    }

    private var pictureOnly: String {
        pdf(content: "0.8 g 72 400 468 300 re f", font: nil)
    }

    // MARK: - helpers

    private func launch(with base64: String) -> XCUIElement {
        app.launchEnvironment["MEGAPDF_UITEST_PDF_BASE64"] = base64
        app.launch()
        let page = app.descendants(matching: .any).matching(NSPredicate(format: "label == %@", "Page 1")).firstMatch
        XCTAssertTrue(page.waitForExistence(timeout: 20), "the test document did not open")
        return page
    }

    /// Taps a point on the page given in PDF points (origin bottom-left), then waits out
    /// the double-tap-to-zoom window so the single tap is delivered.
    private func tap(_ page: XCUIElement, x: Double, yFromBottom: Double) {
        page.coordinate(withNormalizedOffset: CGVector(dx: x / 612.0, dy: (792.0 - yFromBottom) / 792.0)).tap()
    }

    private func retype(_ text: String) {
        // A vertical-axis TextField is exposed as a text field or a text view depending on
        // the OS, so look it up by identifier across element types — and only after
        // waiting, never by probing which type exists before the sheet has appeared.
        let field = app.descendants(matching: .any).matching(identifier: "bodyTextField").firstMatch
        XCTAssertTrue(field.waitForExistence(timeout: 10), "tapping the line did not open the text editor")
        field.tap()
        if let current = field.value as? String, !current.isEmpty {
            field.typeText(String(repeating: XCUIKeyboardKey.delete.rawValue, count: current.count + 2))
        }
        field.typeText(text)
        app.buttons["bodyTextSave"].tap()
    }

    // MARK: - tiers

    func testTier1_aLineTheFontCanCarryIsRetypedWithNoNotice() {
        let page = launch(with: helveticaHeading)
        tap(page, x: 120, yFromBottom: 708)
        retype("Annual report")
        XCTAssertFalse(app.otherElements["noticeBanner"].waitForExistence(timeout: 2)
                       || app.staticTexts["noticeBanner"].exists,
                       "an edit in the document's own font must not show the substitution notice")
        XCTAssertTrue(app.buttons["Undo"].isEnabled || app.buttons["Undo"].exists, "the edit is on the undo stack")
    }

    func testTier2_aFontThatCannotShowTheTextIsSubstitutedWithANotice() {
        let page = launch(with: symbolHeading)
        tap(page, x: 90, yFromBottom: 708)
        retype("Hello")
        let banner = app.staticTexts["noticeBanner"]
        XCTAssertTrue(banner.waitForExistence(timeout: 5), "a substituted font must be announced")
        XCTAssertTrue(banner.label.contains("standard font"), banner.label)
    }

    func testTier3_aPageWithNoTextSaysItIsAScannedImage() {
        let page = launch(with: pictureOnly)
        tap(page, x: 300, yFromBottom: 550)
        let banner = app.staticTexts["noticeBanner"]
        XCTAssertTrue(banner.waitForExistence(timeout: 5), "a picture-only page must explain why nothing happened")
        XCTAssertTrue(banner.label.contains("scanned"), banner.label)
        XCTAssertFalse(app.descendants(matching: .any).matching(identifier: "bodyTextField").firstMatch.exists,
                       "there is no text to edit")
    }
}
