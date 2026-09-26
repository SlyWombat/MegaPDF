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

    /// Picks Export as Markdown from the More menu, then the system export sheet's own Save --
    /// the same iOS-26-has-no-name-field dance `FilesEndToEndUITests.export()` uses.
    private func exportMarkdown() {
        let more = app.buttons["viewerMore"].firstMatch
        XCTAssertTrue(more.waitForExistence(timeout: 10), dump("no More menu"))
        more.tap()
        let button = app.buttons["viewerExportMarkdown"].firstMatch
        XCTAssertTrue(button.waitForExistence(timeout: 5), dump("no Export as Markdown in More"))
        button.tap()

        let bar = app.navigationBars["FullDocumentManagerViewControllerNavigationBar"]
        XCTAssertTrue(bar.waitForExistence(timeout: 30), dump("the export sheet did not appear"))
        let save = bar.buttons["Save"]
        XCTAssertTrue(save.waitForExistence(timeout: 10), dump("no Save on the export sheet"))
        save.tap()
        let keepBoth = app.buttons["Keep Both"].firstMatch
        if keepBoth.waitForExistence(timeout: 3) { keepBoth.tap() }
    }

    /// #386's whole point, driven for real: the exported file is really produced, and the app
    /// calls the result "Exported" rather than "Saved" -- the wording #386 asked for so a
    /// one-way text export is never mistaken for a save of the document itself.
    func testExportAsMarkdownProducesARealFile() {
        app.launchEnvironment["MEGAPDF_UITEST_PDF_BASE64"] =
            onePagePdfBase64(text: "MegaPDF Markdown Export Test")
        app.launch()
        let page = app.descendants(matching: .any).matching(NSPredicate(format: "label == 'Page 1'")).firstMatch
        XCTAssertTrue(page.waitForExistence(timeout: 30), dump("the document did not open"))

        exportMarkdown()

        let alert = app.alerts.firstMatch
        XCTAssertTrue(alert.waitForExistence(timeout: 30), dump("no completion alert"))
        XCTAssertTrue(alert.staticTexts["Exported"].waitForExistence(timeout: 5),
                      dump("the completion alert did not say \"Exported\""))
        alert.buttons.firstMatch.tap()
    }
}
