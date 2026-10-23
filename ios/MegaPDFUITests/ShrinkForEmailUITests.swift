import XCTest

/// #593: Shrink for email, driven for real: the More menu's row, the result sheet with Share
/// first, Save to Files through the system export sheet, and the alert that says what happened.
/// Opens a document built in memory through `MEGAPDF_UITEST_PDF_BASE64`, as
/// `MarkdownExportUITests` does.
///
/// No Stop here: a 300 px shrink takes milliseconds (CLAUDE.md, measure the duration). Stop is
/// proven at the engine (`ShrinkTests.testARaisedFlagStopsTheShrink`) and in the core.
final class ShrinkForEmailUITests: XCTestCase {

    private var app: XCUIApplication!

    override func setUp() {
        continueAfterFailure = false
        app = XCUIApplication()
        // English labels regardless of the simulator's language.
        app.launchArguments += ["-AppleLanguages", "(en)", "-AppleLocale", "en_US"]
        // So `DocumentOpening.wait` waits for the open to finish, not just for the page (#706).
        app.launchArguments += [PageInput.launchArgument]
    }

    /// One page drawing one 300 × 300 px uncompressed RGB picture at 72 × 72 pt: oversized for
    /// its 150 dpi target, so the shrink re-encodes it (the same document `ShrinkTests` builds).
    private func largeImagePdfBase64() -> String {
        let side = 300
        var pixels = [UInt8](repeating: 0, count: side * side * 3)
        // Noise over the blue, so a save's Flate cannot squeeze it under the size floors: a flat
        // picture saves at 289 bytes and is then not worth shrinking (measured for #593).
        var seed: UInt32 = 593
        func noise() -> UInt8 { seed = seed &* 1103515245 &+ 12345; return UInt8((seed >> 16) & 31) }
        for i in stride(from: 0, to: pixels.count, by: 3) {
            pixels[i] = 30 + noise(); pixels[i + 1] = 70 + noise(); pixels[i + 2] = 185 + noise()
        }
        var pdf: [UInt8] = Array("%PDF-1.4\n".utf8)
        var offsets: [Int] = []
        func add(_ head: String, stream: [UInt8]? = nil) {
            offsets.append(pdf.count)
            pdf += Array("\(offsets.count) 0 obj\n\(head)".utf8)
            if let stream { pdf += Array("\nstream\n".utf8) + stream + Array("\nendstream".utf8) }
            pdf += Array("\nendobj\n".utf8)
        }
        add("<< /Type /Catalog /Pages 2 0 R >>")
        add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
        add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im1 4 0 R >> >> /Contents 5 0 R >>")
        add("<< /Type /XObject /Subtype /Image /Width \(side) /Height \(side) /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length \(pixels.count) >>",
            stream: pixels)
        let body = Array("q 72 0 0 72 100 600 cm /Im1 Do Q".utf8)
        add("<< /Length \(body.count) >>", stream: body)
        let xref = pdf.count
        var tail = "xref\n0 \(offsets.count + 1)\n0000000000 65535 f \n"
        for offset in offsets { tail += String(format: "%010d 00000 n \n", offset) }
        tail += "trailer\n<< /Size \(offsets.count + 1) /Root 1 0 R >>\nstartxref\n\(xref)\n%%EOF\n"
        pdf += Array(tail.utf8)
        return Data(pdf).base64EncodedString()
    }

    /// The accessibility tree, printed, so a failure says what was on screen.
    private func dump(_ message: String) -> String {
        print("SHRINK DUMP (\(message)):\n\(app.debugDescription)")
        return message
    }

    func testShrinkForEmailOffersShareAndSave() {
        app.launchEnvironment["MEGAPDF_UITEST_PDF_BASE64"] = largeImagePdfBase64()
        app.launch()
        let page = app.descendants(matching: .any).matching(NSPredicate(format: "label == 'Page 1'")).firstMatch
        XCTAssertTrue(DocumentOpening.wait(for: page, in: app), dump(DocumentOpening.why(app, "the document did not open")))

        let row = app.buttons["viewerShrink"].firstMatch
        if let failure = PageInput.openMoreMenu(in: app, for: row) {
            XCTFail(dump(failure))
            return
        }
        XCTAssertTrue(PageInput.waitEnabled(row), dump("Shrink for email stayed disabled"))
        row.tap()

        // The result sheet: its title, Was/now, and Share offered before Save to Files.
        XCTAssertTrue(app.navigationBars["Smaller copy ready"].waitForExistence(timeout: 60),
                      dump("the result sheet did not appear"))
        XCTAssertTrue(app.staticTexts["shrinkReadySizes"].exists, dump("no Was/now line"))
        let share = app.buttons["shrinkShare"].firstMatch
        let save = app.buttons["shrinkSaveToFiles"].firstMatch
        XCTAssertTrue(share.exists, dump("no Share on the result sheet"))
        XCTAssertTrue(save.exists, dump("no Save to Files on the result sheet"))
        XCTAssertLessThan(share.frame.minY, save.frame.minY, dump("Share is not first"))

        save.tap()
        if let failure = ExportSheet.save(in: app) {
            XCTFail(dump(failure))
            return
        }
        guard let alert = ExportSheet.completionAlert(in: app) else {
            XCTFail(dump("no completion alert"))
            return
        }
        XCTAssertTrue(alert.staticTexts["Smaller copy saved"].waitForExistence(timeout: 5),
                      dump("the completion alert did not say \"Smaller copy saved\""))
        alert.buttons.firstMatch.tap()
    }

    /// A copy made to be emailed is the worst place for marked-but-unapplied content to go, and
    /// the phones shrink what is on screen (D6), so with a mark pending Shrink for email asks the
    /// redaction question first, exactly as Export as Markdown does (coordinator, 2026-10-22).
    /// Cancel is the safe answer: no result sheet, and nothing was started behind the question.
    /// `-screenshot redact` opens the demo agreement with one line marked, as `RedactionUITests`
    /// relies on.
    func testShrinkForEmailWithAMarkAsksTheRedactionQuestionAndCancelMakesNothing() {
        app.launchArguments = ["-screenshot", "redact",
                               "-AppleLanguages", "(en)", "-AppleLocale", "en_US"]
        app.launch()

        let row = app.buttons["viewerShrink"].firstMatch
        if let failure = PageInput.openMoreMenu(in: app, for: row) {
            XCTFail(dump(failure))
            return
        }
        XCTAssertTrue(PageInput.waitEnabled(row), dump("Shrink for email stayed disabled"))
        row.tap()

        XCTAssertTrue(app.staticTexts["Remove the marked content?"].waitForExistence(timeout: 10),
                      dump("Shrink for email with a mark did not ask the redaction question"))
        // The shrink is one of the question's answers, beside Cancel. A confirmation dialog's
        // buttons are looked for anywhere in the app (an action sheet on the phone).
        XCTAssertTrue(app.descendants(matching: .button)["Shrink for email"].waitForExistence(timeout: 5),
                      dump("the question has no Shrink for email"))
        // Cancel is how the question is answered no. Where the dialog is a popover (iOS 26 on
        // the phone, measured on the Mac mini's simulator) it draws no Cancel row, and a tap
        // outside it is the cancel: the full-screen dismiss region, tapped by its corner so the
        // tap lands outside the popover.
        let cancel = app.descendants(matching: .button)["Cancel"].firstMatch
        if cancel.exists {
            cancel.tap()
        } else {
            let outside = app.otherElements["PopoverDismissRegion"].firstMatch
            XCTAssertTrue(outside.exists, dump("the question has no Cancel and no dismiss region"))
            outside.coordinate(withNormalizedOffset: CGVector(dx: 0.03, dy: 0.97)).tap()
        }

        // Cancelled: the question is gone, and no strip, no result sheet and no alert followed.
        let gone = NSPredicate(format: "exists == false")
        expectation(for: gone, evaluatedWith: app.staticTexts["Remove the marked content?"])
        waitForExpectations(timeout: 10)
        XCTAssertFalse(app.navigationBars["Smaller copy ready"].waitForExistence(timeout: 3),
                       dump("Cancel still produced a result sheet"))
        XCTAssertFalse(app.buttons["busyStop"].exists, dump("Cancel still started a shrink"))
    }
}
