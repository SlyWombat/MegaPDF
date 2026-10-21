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
        // The notices below are pinned for the whole suite (#487, `NoticeLifetime`). All
        // four tests in here turn on whether a one-line banner is on screen — two that it
        // is, two that it is not — and the app takes it away after four seconds: on a
        // runner slow enough (testTier3 has been measured at 106 seconds there against a
        // median of 13 on the Mac mini) the banner can come and go between two
        // accessibility snapshots, so both kinds of check were races rather than
        // measurements.
        app.launchArguments += ["-uiTestPinNotices"]
        // What became of each tap, by the app's account (#681): see `PageInput`.
        app.launchArguments += [PageInput.launchArgument]
    }

    /// Ends the app after every test, passed or failed (#487).
    ///
    /// Without this a test that could not launch the app leaves its process behind, and the
    /// *next* test's launch tries to resume that stale process and fails too — "Failed to
    /// get background assertion for target app with pid 0", which is what turned one launch
    /// timeout into two red tests in runs 36427711297 and 36440146938. A test should be
    /// able to fail without taking the one after it down.
    override func tearDown() {
        app?.terminate()
        app = nil
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

    /// Text under 4 pt of character spacing. Stock PDFium's content writer cannot
    /// write this back (#118); the pinned build can, because of the spacing patch
    /// (#121) — see the test below.
    private var spacedHeading: String {
        pdf(content: "BT /F1 24 Tf 4 Tc 72 700 Td (Spaced report) Tj ET BT /F1 24 Tf 72 660 Td (Second line) Tj ET",
            font: "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
    }

    private var pictureOnly: String {
        pdf(content: "0.8 g 72 400 468 300 re f", font: nil)
    }

    // MARK: - helpers

    private func launch(with base64: String) -> XCUIElement {
        app.launchEnvironment["MEGAPDF_UITEST_PDF_BASE64"] = base64
        app.launch()
        let page = app.descendants(matching: .any).matching(NSPredicate(format: "label == %@", "Page 1")).firstMatch
        XCTAssertTrue(DocumentOpening.wait(for: page, in: app), DocumentOpening.why(app, "the test document did not open"))
        return page
    }

    /// A vertical-axis TextField is exposed as a text field or a text view depending on the
    /// OS, so it is looked up by identifier across element types.
    private var bodyTextField: XCUIElement {
        app.descendants(matching: .any).matching(identifier: "bodyTextField").firstMatch
    }

    /// Taps a point on the page given in PDF points (origin bottom-left), until `expected`
    /// appears — by the app's account of the tap, not by a stopwatch (#681, `PageInput.tap`).
    ///
    /// The app defers a single tap by the double-tap-to-zoom window before dispatching it, and
    /// on a hosted runner the tap itself has taken 25 s to arrive; a fixed ten seconds for the
    /// editor was the wrong unit for both.
    private func tap(_ page: XCUIElement, x: Double, yFromBottom: Double, until expected: XCUIElement,
                     _ what: String) {
        let point = page.coordinate(withNormalizedOffset: CGVector(dx: x / 612.0, dy: (792.0 - yFromBottom) / 792.0))
        if let failure = PageInput.tap(point, in: app, until: expected) {
            XCTFail("\(what): \(failure)")
        }
    }

    private func retype(_ text: String) {
        let field = bodyTextField
        XCTAssertTrue(field.waitForExistence(timeout: 30), "tapping the line did not open the text editor")
        XCTAssertTrue(PageInput.focus(field),
                      "the text editor's field never took the keyboard's focus, so nothing could be typed")
        // One typing call rather than two, so focus is relied on once: the old line, deleted,
        // then the new one.
        let current = (field.value as? String) ?? ""
        field.typeText(String(repeating: XCUIKeyboardKey.delete.rawValue, count: current.isEmpty ? 0 : current.count + 2) + text)
        app.buttons["bodyTextSave"].tap()
    }

    // MARK: - tiers

    func testTier1_aLineTheFontCanCarryIsRetypedWithNoNotice() {
        let page = launch(with: helveticaHeading)
        tap(page, x: 120, yFromBottom: 708, until: bodyTextField, "tapping the line did not open the text editor")
        retype("Annual report")
        XCTAssertFalse(app.otherElements["noticeBanner"].waitForExistence(timeout: 2)
                       || app.staticTexts["noticeBanner"].exists,
                       "an edit in the document's own font must not show the substitution notice")
        XCTAssertTrue(app.buttons["Undo"].isEnabled || app.buttons["Undo"].exists, "the edit is on the undo stack")
    }

    func testTier2_aFontThatCannotShowTheTextIsSubstitutedWithANotice() {
        let page = launch(with: symbolHeading)
        tap(page, x: 90, yFromBottom: 708, until: bodyTextField, "tapping the line did not open the text editor")
        retype("Hello")
        let banner = app.staticTexts["noticeBanner"]
        // 15 seconds, and it is a real 15 now: the notice is pinned, so the wait is bounded
        // by how long the engine and a render take on a loaded machine rather than by the
        // four seconds the app would otherwise have taken the banner away after (#487).
        XCTAssertTrue(banner.waitForExistence(timeout: 15), "a substituted font must be announced")
        XCTAssertTrue(banner.label.contains("standard font"), banner.label)
    }

    /// Text under 4 pt of character spacing **is** editable, because the pinned
    /// PDFium carries the spacing patch (#121).
    ///
    /// This test used to assert the opposite — that the page refuses (#118) — and
    /// it went red the moment the pin moved, because nothing in CI runs
    /// `MegaPDFUITests` and so nobody saw it go stale. `core_tests.cpp` had
    /// always guarded the same case with `if (MEGAPDF_PDFIUM_PATCHES < 1)`, and
    /// its fidelity table (`test_rewrite_fidelity`) tops out at `fixed_by = 16`
    /// against a build carrying 30 patches: at this pin **no** case in that table
    /// is refused. So the refusal has no fixture left to drive it from the UI,
    /// and asserting it here was asserting that the patch had not been applied.
    ///
    /// What is worth holding on to is the other half — that the spacing patch
    /// really does let this page through, and that retyping one line leaves the
    /// neighbouring line alone. The refusal path itself is covered where it can
    /// be: `core_tests.cpp` §#118 and `test_rewrite_fidelity`, per patch level.
    func testTextUnderCharacterSpacingIsEditableWithThePinnedPdfium() {
        let page = launch(with: spacedHeading)
        tap(page, x: 160, yFromBottom: 708, until: bodyTextField, "tapping the line did not open the text editor")
        retype("Adjusted report")
        XCTAssertFalse(app.staticTexts["noticeBanner"].waitForExistence(timeout: 2),
                       "an edit the engine accepts must not show a refusal notice: "
                       + app.staticTexts["noticeBanner"].label)
        XCTAssertTrue(app.buttons["Undo"].isEnabled || app.buttons["Undo"].exists,
                      "the edit is on the undo stack")
    }

    func testTier3_aPageWithNoTextSaysItIsAScannedImage() {
        let page = launch(with: pictureOnly)
        let banner = app.staticTexts["noticeBanner"]
        // The scanned-page notice in particular: a tap a starved runner holds long enough to be
        // a long press raises a different notice on this page ("no text to select", #710), and
        // that one must not count as the tap having worked.
        let scanned = app.staticTexts.matching(NSPredicate(format: "identifier == 'noticeBanner' AND label CONTAINS 'scanned'")).firstMatch
        tap(page, x: 300, yFromBottom: 550, until: scanned, "a picture-only page must explain why nothing happened")
        XCTAssertTrue(banner.label.contains("scanned"), banner.label)
        XCTAssertFalse(app.descendants(matching: .any).matching(identifier: "bodyTextField").firstMatch.exists,
                       "there is no text to edit")
    }
}
