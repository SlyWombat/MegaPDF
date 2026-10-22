import XCTest

/// #386: Save As -> Markdown, driven for real through the More menu and the system export
/// sheet. Opens a small in-memory document via the same `MEGAPDF_UITEST_PDF_BASE64` launch-
/// environment mechanism `BodyTextEditUITests` uses, so this needs no pre-staged Files
/// fixture and runs unconditionally (unlike `FilesEndToEndUITests`' `E2E_FILES`-gated suite).
final class MarkdownExportUITests: XCTestCase {

    private var app: XCUIApplication!

    override func setUp() {
        continueAfterFailure = false
        app = XCUIApplication()
        // English labels regardless of the simulator's language.
        app.launchArguments += ["-AppleLanguages", "(en)", "-AppleLocale", "en_US"]
        // So `DocumentOpening.wait` waits for the open to finish, not just for the page (#706).
        app.launchArguments += [PageInput.launchArgument]
        // The "Exported" confirmation is a self-clearing banner (#631); pinned, it outlives the wait.
        app.launchArguments += ["-uiTestPinNotices"]
    }

    /// One page, one line of real text -- enough for the Markdown writer to have something to
    /// say, and small enough to type straight into a content stream.
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
        print("MARKDOWN EXPORT DUMP (\(message)):\n\(app.debugDescription)")
        return message
    }

    /// Picks Export as Markdown from the More menu, then the system export sheet's own Save.
    ///
    /// #706: on hosted runners this failed seven times as "no Save on the export sheet" with
    /// the sheet still on its Browse root, and twice as "the export sheet did not appear" with
    /// the row tapped and nothing following. `ExportSheet.save` is the first; the second is a
    /// row tapped while still disabled by the open (#145), which the enabled wait below and the
    /// open's own end (`DocumentOpening.wait`) both answer.
    private func exportMarkdown() {
        let button = app.buttons["viewerExportMarkdown"].firstMatch
        if let failure = PageInput.openMoreMenu(in: app, for: button) {
            XCTFail(dump(failure))
            return
        }
        XCTAssertTrue(PageInput.waitEnabled(button), dump("Export as Markdown stayed disabled"))
        button.tap()
        if let failure = ExportSheet.save(in: app) {
            XCTFail(dump(failure))
        }
    }

    /// #386's whole point, driven for real: the exported file is really produced, and the app
    /// calls the result "Exported" rather than "Saved" -- the wording #386 asked for so a
    /// one-way text export is never mistaken for a save of the document itself.
    func testExportAsMarkdownProducesARealFile() {
        app.launchEnvironment["MEGAPDF_UITEST_PDF_BASE64"] =
            onePagePdfBase64(text: "MegaPDF Markdown Export Test")
        app.launch()
        let page = app.descendants(matching: .any).matching(NSPredicate(format: "label == 'Page 1'")).firstMatch
        XCTAssertTrue(DocumentOpening.wait(for: page, in: app), dump(DocumentOpening.why(app, "the document did not open")))

        exportMarkdown()

        if let failure = ExportSheet.completionNotice(in: app, expected: "Exported") {
            XCTFail(dump(failure))
        }
    }
}
