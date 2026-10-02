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

    /// Waits for a control to stop being disabled. Everything that writes is disabled while a
    /// save or the page check after an edit is still running (#145), and XCUITest taps a
    /// disabled control without complaining — `FilesEndToEndUITests` learned that the hard
    /// way, as "the export sheet did not appear" when the tap had simply been swallowed.
    private func waitEnabled(_ element: XCUIElement, _ what: String, timeout: TimeInterval = 60) {
        let deadline = Date().addingTimeInterval(timeout)
        while !element.isEnabled && Date() < deadline {
            RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.5))
        }
        XCTAssertTrue(element.isEnabled, dump("\(what) stayed disabled"))
    }

    /// Picks a row out of the viewer's More menu, by identifier, waiting for it to be enabled.
    private func tapInMoreMenu(identifier: String, label: String) {
        let more = app.buttons["viewerMore"].firstMatch
        XCTAssertTrue(more.waitForExistence(timeout: 15), dump("no More menu"))
        more.tap()
        var row = app.buttons[identifier].firstMatch
        if !row.waitForExistence(timeout: 5) {
            // The identifier is the contract; the label is what the e2e suite has always used,
            // kept as a fallback so a lost identifier fails as "the row is missing" rather than
            // as the absent sheet this test is about.
            row = app.buttons[label].firstMatch
            XCTAssertTrue(row.waitForExistence(timeout: 5), dump("no \(label) in More"))
        }
        waitEnabled(row, "the \(label) row")
        row.tap()
    }

    /// The system export sheet, and its Save. **This is the #589 assertion**: with two
    /// exporters on one view, the sheet for whichever lost simply never came up, and the wait
    /// below is what notices. iOS 26's sheet has no name field, so a clash is answered Keep
    /// Both and the name is the OS's business.
    private func saveOnTheExportSheet(_ what: String) {
        let bar = app.navigationBars["FullDocumentManagerViewControllerNavigationBar"]
        XCTAssertTrue(bar.waitForExistence(timeout: 30),
                      dump("the \(what) export sheet did not appear"))
        let save = bar.buttons["Save"]
        XCTAssertTrue(save.waitForExistence(timeout: 15), dump("no Save on the \(what) export sheet"))
        save.tap()
        let keepBoth = app.buttons["Keep Both"].firstMatch
        if keepBoth.waitForExistence(timeout: 3) { keepBoth.tap() }
    }

    /// Waits for the app's own completion alert and returns its title, then dismisses it.
    /// Which word it is matters as much as that it appeared: "Saved" is a copy of the
    /// document, "Exported" is the one-way Markdown export (#386), and with one exporter
    /// serving both kinds, a result delivered to the wrong completion would say the wrong one.
    @discardableResult
    private func acknowledgeCompletion(expected: String) -> String {
        let alert = app.alerts.firstMatch
        XCTAssertTrue(alert.waitForExistence(timeout: 30), dump("no completion alert"))
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
