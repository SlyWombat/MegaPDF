import XCTest

/// #589: *Save a copy* really presents the system export sheet, and really writes a file.
///
/// The bug this guards against was not a wrong export — it was **no sheet**. `ContentView`
/// carried two `.fileExporter` modifiers, one per format, and SwiftUI presented only the one
/// attached last: tapping *Save a copy* staged the copy, set its flag and nothing appeared, on
/// every document, with no error. The suite that drives the real sheet
/// (`FilesEndToEndUITests`, `tools/ios-files-e2e.sh`) is the only place it showed, and that
/// suite is `E2E_FILES`-gated and excluded from CI, so the bug shipped to `main` green.
///
/// So these tests are deliberately built to run **in CI**: the document comes from
/// `MEGAPDF_UITEST_PDF_BASE64` as `MarkdownExportUITests`' does, which needs no staged Files
/// fixture and no 1 GB file, and the destination is the export sheet's own default folder
/// rather than a folder the run had to prepare. What they cannot do is prove where the copy
/// landed or that the app can write there again — that is still `FilesEndToEndUITests`' job,
/// and `test4_saveAndSaveACopy` is where #572's adoption is proved. What they can prove is
/// the half that was broken: the sheet comes up, for both kinds of export, and each result
/// reaches its own completion.
final class SaveACopyExportUITests: XCTestCase {

    private var app: XCUIApplication!

    override func setUp() {
        continueAfterFailure = false
        app = XCUIApplication()
        // English labels regardless of the simulator's language.
        app.launchArguments += ["-AppleLanguages", "(en)", "-AppleLocale", "en_US"]
        // So `DocumentOpening.wait` waits for the open to finish, not just for the page (#706).
        app.launchArguments += [PageInput.launchArgument]
    }

    /// One page, one line of real text. Written out here rather than shared with
    /// `MarkdownExportUITests`: a helper file in this folder is a file the coverage map has to
    /// account for, and twenty lines of PDF is cheaper than that.
    private func onePagePdfBase64(text: String) -> String {
        var pdf = "%PDF-1.4\n"
        var offsets: [Int] = []
        func add(_ body: String) {
            offsets.append(pdf.utf8.count)
            pdf += "\(offsets.count) 0 obj\n\(body)\nendobj\n"
        }
        add("<< /Type /Catalog /Pages 2 0 R >>")
        add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
        add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>")
        add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
        let content = "BT /F1 24 Tf 72 700 Td (\(text)) Tj ET"
        add("<< /Length \(content.utf8.count) >>\nstream\n\(content)\nendstream")
        let xref = pdf.utf8.count
        pdf += "xref\n0 \(offsets.count + 1)\n0000000000 65535 f \n"
        for offset in offsets { pdf += String(format: "%010d 00000 n \n", offset) }
        pdf += "trailer\n<< /Size \(offsets.count + 1) /Root 1 0 R >>\nstartxref\n\(xref)\n%%EOF\n"
        return Data(pdf.utf8).base64EncodedString()
    }

    /// The accessibility tree, printed, so a failure says what was on screen.
    private func dump(_ message: String) -> String {
        print("SAVE A COPY DUMP (\(message)):\n\(app.debugDescription)")
        return message
    }

    private func launchWithADocument(_ text: String) {
        app.launchEnvironment["MEGAPDF_UITEST_PDF_BASE64"] = onePagePdfBase64(text: text)
        app.launch()
        let page = app.descendants(matching: .any).matching(NSPredicate(format: "label == 'Page 1'")).firstMatch
        XCTAssertTrue(DocumentOpening.wait(for: page, in: app), dump(DocumentOpening.why(app, "the document did not open")))
    }

    /// Picks a row out of the viewer's More menu, by identifier, waiting for it to be enabled:
    /// everything that writes is disabled while a save or the page check after an edit is still
    /// running (#145), and XCUITest taps a disabled control without complaining —
    /// `FilesEndToEndUITests` learned that the hard way, as "the export sheet did not appear"
    /// when the tap had simply been swallowed. The identifier is the contract; a lost one fails
    /// as "the row is missing" rather than as the absent sheet this test is about.
    private func tapInMoreMenu(identifier: String, label: String) {
        let row = app.buttons[identifier].firstMatch
        if let failure = PageInput.openMoreMenu(in: app, for: row) {
            XCTFail(dump("\(failure) (\(label))"))
            return
        }
        XCTAssertTrue(PageInput.waitEnabled(row), dump("the \(label) row stayed disabled"))
        row.tap()
    }

    /// The system export sheet, and its Save. **This is the #589 assertion**: with two
    /// exporters on one view, the sheet for whichever lost simply never came up, and
    /// `ExportSheet.save` is what notices. iOS 26's sheet has no name field, so a clash is
    /// answered Keep Both and the name is the OS's business.
    private func saveOnTheExportSheet(_ what: String) {
        if let failure = ExportSheet.save(in: app) {
            XCTFail(dump("\(failure) (\(what))"))
        }
    }

    /// Waits for the app's own completion alert and returns its title, then dismisses it.
    /// Which word it is matters as much as that it appeared: "Saved" is a copy of the
    /// document, "Exported" is the one-way Markdown export (#386), and with one exporter
    /// serving both kinds, a result delivered to the wrong completion would say the wrong one.
    @discardableResult
    private func acknowledgeCompletion(expected: String) -> String {
        guard let alert = ExportSheet.completionAlert(in: app) else {
            XCTFail(dump("no completion alert"))
            return ""
        }
        let title = alert.staticTexts.firstMatch.label
        XCTAssertTrue(alert.staticTexts[expected].waitForExistence(timeout: 5),
                      dump("the completion alert said \"\(title)\", not \"\(expected)\""))
        alert.buttons.firstMatch.tap()
        return title
    }

    /// Save a copy, end to end through the real sheet: the sheet appears, Save writes a file,
    /// and the app says "Saved".
    func testSaveACopyPresentsTheExportSheet() {
        launchWithADocument("MegaPDF Save A Copy Test")
        tapInMoreMenu(identifier: "viewerSaveCopy", label: "Save a copy")
        saveOnTheExportSheet("Save a copy")
        acknowledgeCompletion(expected: "Saved")
    }

    /// Both exports in one session, which is the shape #589 actually was: each worked alone
    /// and the two together did not. Markdown first, then Save a copy — the order in which the
    /// broken build looked healthiest, since the Markdown exporter was the one attached last
    /// and the only one that ever presented.
    func testBothExportsPresentTheirSheetInOneSession() {
        launchWithADocument("MegaPDF Both Exports Test")

        tapInMoreMenu(identifier: "viewerExportMarkdown", label: "Export as Markdown")
        saveOnTheExportSheet("Markdown")
        acknowledgeCompletion(expected: "Exported")

        tapInMoreMenu(identifier: "viewerSaveCopy", label: "Save a copy")
        saveOnTheExportSheet("Save a copy")
        acknowledgeCompletion(expected: "Saved")
    }
}
