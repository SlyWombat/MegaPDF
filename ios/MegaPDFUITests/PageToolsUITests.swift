import XCTest

/// The page tools, driven through the real UI on an iPhone and on an iPad (#174).
///
/// The promises here are all promises about a running app, and none of them can be kept by
/// reading the source:
///
/// - **The phone's grid arrives as a sheet that leaves the page on screen**, and the iPad's as a
///   sidebar beside it. Those are the two answers this leg chose, and "the document is still
///   there" is the whole reason for the first one — a claim a screenshot could fake and a source
///   read cannot check at all.
/// - **A page change really changes the document, and the app really follows it.** The probe
///   `viewerPagesProbe` reports the app's own page *sizes*, in order, which is why every document
///   here has pages of different sizes: a reorder is only observable if the pages can be told
///   apart, and a rotation is observable as a swapped width and height. Pixels say the screen
///   changed; this says what the app did.
/// - **One undo step per change, however many pages it touched.** Turning three pages and pressing
///   Undo once has to put all three back.
/// - **The engine's refusals reach the person as sentences.** The one-page rule is driven here;
///   the field-hierarchy refusal is driven in `PageToolsTests`, which can build the two forms it
///   needs.
///
/// **Where a gesture goes matters (#465, #531).** The taps here are aimed at tiles, which are
/// small and well inside the panel, and the one tap on the *page* goes to `viewerPinchProbe` for
/// #531's reason. **Drag-to-reorder is not in this suite**: a long press on a tile opens its
/// context menu, so a synthesised press-then-drag cannot express the one thing that tells the two
/// apart on a real device, which is movement. It lives in `PageDragUITests`, which says why at
/// length, and the reorder paths that *are* driven here are the ones a screen-reader user depends
/// on anyway — the menu's Move Earlier / Move Later and Move to….
final class PageToolsUITests: XCTestCase {

    private var app: XCUIApplication!

    override func setUp() {
        continueAfterFailure = false
        app = XCUIApplication()
        app.launchArguments = ["-AppleLanguages", "(en)", "-AppleLocale", "en_US",
                               "-uiTestZoomProbes", "-uiTestPinNotices"]
    }

    // MARK: - the document

    /// Four pages, every one a different size and none of them square, so the probe's size list is
    /// an identity for each page and a rotation shows up in it.
    private var fourPages: String {
        pdf(pageSizes: [(300, 400), (400, 420), (500, 440), (600, 460)])
    }

    private var onePage: String { pdf(pageSizes: [(300, 400)]) }

    private func pdf(pageSizes: [(Int, Int)]) -> String {
        var pdf = "%PDF-1.4\n"
        var offsets: [Int] = []
        func add(_ body: String) {
            offsets.append(pdf.utf8.count)
            pdf += "\(offsets.count) 0 obj\n\(body)\nendobj\n"
        }
        let kids = (0..<pageSizes.count).map { "\(4 + $0 * 2) 0 R" }.joined(separator: " ")
        add("<< /Type /Catalog /Pages 2 0 R >>")
        add("<< /Type /Pages /Kids [\(kids)] /Count \(pageSizes.count) >>")
        add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
        for (index, size) in pageSizes.enumerated() {
            add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 \(size.0) \(size.1)] "
                + "/Resources << /Font << /F1 3 0 R >> >> /Contents \(5 + index * 2) 0 R >>")
            let content = "BT /F1 24 Tf 30 \(size.1 - 60) Td (Page \(index + 1)) Tj ET"
            add("<< /Length \(content.utf8.count) >>\nstream\n\(content)\nendstream")
        }
        let xref = pdf.utf8.count
        pdf += "xref\n0 \(offsets.count + 1)\n0000000000 65535 f \n"
        for offset in offsets { pdf += String(format: "%010d 00000 n \n", offset) }
        pdf += "trailer\n<< /Size \(offsets.count + 1) /Root 1 0 R >>\nstartxref\n\(xref)\n%%EOF\n"
        return Data(pdf.utf8).base64EncodedString()
    }

    // MARK: - reading what the app says about itself

    private func dump(_ message: String) -> String {
        print("PAGE TOOLS DUMP (\(message)):\n\(app.debugDescription)")
        return message
    }

    @discardableResult
    private func launch(with base64: String) -> XCUIElement {
        app.launchEnvironment["MEGAPDF_UITEST_PDF_BASE64"] = base64
        app.launch()
        let page = documentPage()
        XCTAssertTrue(page.waitForExistence(timeout: 30), dump("the test document did not open"))
        return page
    }

    /// The document's first page, as an **image**.
    ///
    /// A page tile is labelled "Page 1" too — as it should be, since that is what VoiceOver ought
    /// to say about it — so a query by label alone is ambiguous the moment the grid is open, and
    /// the geometry checks below compare a page's frame with a tile's. The document's page is the
    /// only *image* with that label: a tile is a button, and the thumbnail inside it is merged into
    /// the button by `accessibilityElement(children: .combine)` and carries no label of its own.
    private func documentPage() -> XCUIElement {
        app.images.matching(NSPredicate(format: "label == 'Page 1'")).firstMatch
    }

    /// `"pages on selecting off selected 0 count 4 sizes 300x400,…"`, as the app itself has it.
    private func probe() -> String {
        let element = app.descendants(matching: .any)
            .matching(NSPredicate(format: "identifier == 'viewerPagesProbe'")).firstMatch
        guard element.waitForExistence(timeout: 10) else {
            XCTFail(dump("the pages probe is missing — did -uiTestZoomProbes survive? (#174)"))
            return ""
        }
        return element.label
    }

    /// The page sizes the app has, in order.
    private func sizes() -> [String] {
        let label = probe()
        guard let range = label.range(of: "sizes ") else { return [] }
        return label[range.upperBound...].split(separator: ",").map(String.init)
    }

    /// Polls until the probe says `expected`, so a passing assertion never depends on how long a
    /// SwiftUI transition or an engine call took.
    @discardableResult
    private func waitFor(_ expected: String, timeout: TimeInterval = 10) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        var last = ""
        repeat {
            last = probe()
            if last.contains(expected) { return true }
            Thread.sleep(forTimeInterval: 0.2)
        } while Date() < deadline
        XCTFail(dump("the app never reported '\(expected)' — it says '\(last)'"))
        return false
    }

    /// Which width class the app thinks it is in.
    ///
    /// The iPad's tool strip is built for regular width only (#172), so its presence *is* the
    /// width class as the app sees it — a more honest answer than the device idiom, which would
    /// still say "iPad" in Slide Over. It is queried as a **button**, not as an `otherElement`:
    /// `viewerToolStrip` is an identifier on the row, and a container's identifier in SwiftUI
    /// replaces its descendants', so what is actually in the tree is a handful of buttons each
    /// carrying that identifier and its own label.
    private var isRegularWidth: Bool {
        stripButton("Sign").exists
    }

    /// One of the iPad strip's buttons, found by its label, for the reason above.
    private func stripButton(_ label: String) -> XCUIElement {
        app.descendants(matching: .button)
            .matching(NSPredicate(format: "identifier == 'viewerToolStrip' AND label == %@", label))
            .firstMatch
    }

    private func tile(_ index: Int) -> XCUIElement {
        app.descendants(matching: .any)
            .matching(NSPredicate(format: "identifier == %@", "pageTile-\(index)")).firstMatch
    }

    private func openPages() {
        if isRegularWidth {
            let button = stripButton("Pages")
            XCTAssertTrue(button.waitForExistence(timeout: 10), dump("no Pages button on the strip"))
            button.tap()
        } else {
            let more = app.buttons["viewerMore"].firstMatch
            XCTAssertTrue(more.waitForExistence(timeout: 10), dump("no More menu"))
            more.tap()
            let row = app.buttons["viewerPages"].firstMatch
            XCTAssertTrue(row.waitForExistence(timeout: 5), dump("the ⋯ menu has no Pages row"))
            row.tap()
        }
        waitFor("pages on")
        XCTAssertTrue(tile(0).waitForExistence(timeout: 10), dump("the grid drew no tiles"))
    }

    private func startSelecting(_ pages: [Int]) {
        let select = app.buttons["pagesSelect"].firstMatch
        XCTAssertTrue(select.waitForExistence(timeout: 5), dump("no Select button"))
        select.tap()
        waitFor("selecting on")
        for page in pages {
            let element = tile(page)
            XCTAssertTrue(element.waitForExistence(timeout: 5), dump("no tile \(page)"))
            element.tap()
        }
        waitFor("selected \(pages.count)")
    }

    // MARK: - where the grid comes from, and what it leaves on screen

    /// The phone's answer: a sheet at the medium detent, with the page still above it. If this
    /// ever became a full-screen destination, the document would be gone and so would the reason
    /// this leg is a sheet at all.
    func testThePhonesGridIsASheetThatLeavesThePageOnScreen() throws {
        let page = launch(with: fourPages)
        try XCTSkipIf(isRegularWidth, "compact width only: the iPad's answer is the sidebar below")

        openPages()
        XCTAssertTrue(page.exists,
                      dump("the sheet took the document away — the medium detent is the point"))
        // Where the grid is, not merely that it is somewhere: a sheet comes up from the bottom,
        // so its tiles sit **below** the top of the page it left on screen. That is the claim.
        XCTAssertTrue(tile(0).frame.minY > page.frame.minY,
                      dump("the tiles are not below the page — this is not a sheet over it"))
        XCTAssertTrue(app.buttons["pagesClose"].exists,
                      dump("a sheet needs its own way out; the sidebar has the strip's toggle"))

        // And it goes away again from its own close, leaving the document where it was.
        app.buttons["pagesClose"].firstMatch.tap()
        waitFor("pages off")
        XCTAssertTrue(page.exists)
        XCTAssertFalse(tile(0).exists)
    }

    /// The iPad's answer: a sidebar inside the viewer, reached from the strip #172 gave it, with
    /// the document beside it rather than behind it.
    func testTheIPadsGridIsASidebarBesideTheDocument() throws {
        let page = launch(with: fourPages)
        try XCTSkipIf(!isRegularWidth, "regular width only: the phone's answer is the sheet above")

        XCTAssertTrue(stripButton("Pages").exists, dump("the iPad's strip has no Pages button"))
        openPages()
        XCTAssertTrue(page.exists, "the document stays beside it")
        // Beside, not over: the tiles end where the page begins.
        XCTAssertTrue(tile(0).frame.maxX <= page.frame.minX,
                      dump("the tiles are not to the left of the page — this is not a sidebar"))
        XCTAssertFalse(app.buttons["pagesClose"].exists,
                       "a sidebar is not modal, so it has no close of its own")

        stripButton("Pages").tap()
        waitFor("pages off")
        XCTAssertFalse(tile(0).exists, "the strip's button toggles it")
    }

    /// The ⋯ row is on every layout, because compact width has no strip to put a button on — the
    /// same arrangement reading mode settled on (#506).
    func testThePagesRowIsInTheMoreMenuOnEveryLayout() {
        launch(with: fourPages)
        let more = app.buttons["viewerMore"].firstMatch
        XCTAssertTrue(more.waitForExistence(timeout: 10), dump("no More menu"))
        more.tap()
        XCTAssertTrue(app.buttons["viewerPages"].waitForExistence(timeout: 5),
                      dump("the ⋯ menu has no Pages row"))
    }

    /// The grid is chrome, and reading mode takes the chrome out of the accessibility tree rather
    /// than merely off the screen (#506). An `XCUIElement` query *is* that tree.
    /// Regular width only, and not because the phone is exempt: on the phone reading mode is only
    /// reachable from the ⋯ menu, which the pages sheet is sitting over, so a grid and reading mode
    /// cannot be on screen together at all. The iPad's sidebar *can* be, which is exactly why it
    /// has to leave — a persistent panel of thumbnails is the kind of chrome reading mode exists to
    /// take away, and it has to leave the accessibility tree and not merely the screen (#506).
    /// `ReadingModeTests` covers the model's half on both.
    func testTheIPadsSidebarLeavesTheAccessibilityTreeInReadingMode() throws {
        launch(with: fourPages)
        try XCTSkipIf(!isRegularWidth,
                      "compact width cannot show both: reading mode's entry point is under the sheet")
        openPages()
        XCTAssertTrue(tile(0).exists)

        stripButton("Reading mode").tap()
        waitFor("pages off")
        XCTAssertFalse(tile(0).exists, dump("a page tile is still reachable in reading mode"))
        XCTAssertFalse(app.buttons["pagesSelect"].exists)
    }

    // MARK: - what a tap means

    /// Outside Select mode a tap on a thumbnail **goes to that page**. That is the reason Select is
    /// a mode here at all, where Android's grid selects on a plain tap, so it is worth pinning:
    /// nothing is selected by it.
    func testTappingAPageGoesToItRatherThanSelectingIt() {
        launch(with: fourPages)
        openPages()
        XCTAssertTrue(probe().contains("selecting off"))

        tile(3).tap()
        XCTAssertTrue(probe().contains("selected 0"),
                      dump("a plain tap must not select — that is what Select mode is for"))
        if !isRegularWidth {
            waitFor("pages off")   // the sheet puts itself away over the page it scrolled to
        }
    }

    // MARK: - the seven operations

    /// Turning three pages is **one** change and **one** press of Undo — the whole argument for a
    /// selection.
    func testRotatingASelectionIsOneChangeAndOneUndo() {
        launch(with: fourPages)
        let before = sizes()
        XCTAssertEqual(before, ["300x400", "400x420", "500x440", "600x460"],
                       dump("the fixture did not open as four pages of four sizes"))

        openPages()
        startSelecting([0, 1, 2])
        app.buttons["pagesRotateRight"].firstMatch.tap()
        waitFor("sizes 400x300,420x400,440x500,600x460")

        undo()
        waitFor("sizes 300x400,400x420,500x440,600x460")
        XCTAssertFalse(undoButton().isEnabled,
                       dump("three turned pages must be one undo step, not three"))
    }

    func testDeletingAPageAndUndoingItPutsThePageBack() {
        launch(with: fourPages)
        openPages()
        startSelecting([1])
        app.buttons["pagesDelete"].firstMatch.tap()
        waitFor("count 3")
        XCTAssertEqual(sizes(), ["300x400", "500x440", "600x460"])

        undo()
        waitFor("count 4")
        XCTAssertEqual(sizes(), ["300x400", "400x420", "500x440", "600x460"],
                       dump("the page came back somewhere other than where it was"))
    }

    /// Two pages, one undo step — and the selection follows the renumbering rather than being
    /// thrown away, which is the bug the Windows leg caught in its own grid.
    func testDeletingTwoPagesIsOneUndoStep() {
        launch(with: fourPages)
        openPages()
        startSelecting([0, 2])
        app.buttons["pagesDelete"].firstMatch.tap()
        waitFor("count 2")
        XCTAssertEqual(sizes(), ["400x420", "600x460"])

        undo()
        waitFor("count 4")
        XCTAssertEqual(sizes(), ["300x400", "400x420", "500x440", "600x460"])
        XCTAssertFalse(undoButton().isEnabled, "one step")
    }

    /// The one-page rule, said when Delete is pressed rather than left as a command that quietly
    /// does nothing.
    func testTheLastPageCannotBeDeletedAndSaysWhy() {
        launch(with: onePage)
        openPages()
        startSelecting([0])
        app.buttons["pagesDelete"].firstMatch.tap()

        let alert = app.alerts["Nothing was changed"]
        XCTAssertTrue(alert.waitForExistence(timeout: 5),
                      dump("deleting the last page said nothing at all"))
        XCTAssertTrue(alert.staticTexts["A PDF has to keep at least one page."].exists,
                      dump("the sentence is not the rule"))
        alert.buttons["OK"].tap()
        XCTAssertTrue(probe().contains("count 1"), "and nothing was changed")
    }

    /// The reorder every tile carries without a drag: unusable with a screen reader is exactly
    /// what a drag is, so these are the ones that have to exist.
    func testMoveEarlierAndMoveLaterAreOnEveryTilesMenu() {
        launch(with: fourPages)
        openPages()
        tile(2).press(forDuration: 1.2)
        let earlier = app.buttons["Move Earlier"].firstMatch
        XCTAssertTrue(earlier.waitForExistence(timeout: 5),
                      dump("a tile's menu has no Move Earlier"))
        XCTAssertTrue(app.buttons["Move Later"].firstMatch.exists)
        earlier.tap()
        waitFor("sizes 300x400,500x440,400x420,600x460")

        undo()
        waitFor("sizes 300x400,400x420,500x440,600x460")
    }

    /// A typed position — the phone's answer to the desktops' cut and paste (#2).
    func testMoveToPutsThePageAtThePositionTyped() {
        launch(with: fourPages)
        openPages()
        tile(3).press(forDuration: 1.2)
        let moveTo = app.buttons["Move to…"].firstMatch
        XCTAssertTrue(moveTo.waitForExistence(timeout: 5), dump("a tile's menu has no Move to…"))
        moveTo.tap()

        // Through the alert rather than by identifier: a `TextField` inside an `alert` is built by
        // UIKit from the SwiftUI description and does not carry the identifier across.
        let alert = app.alerts.firstMatch
        XCTAssertTrue(alert.waitForExistence(timeout: 5), dump("no Move to… box"))
        let field = alert.textFields.firstMatch
        XCTAssertTrue(field.waitForExistence(timeout: 5), dump("the Move to… box has no field"))
        field.tap()
        field.typeText("1")
        alert.buttons["Move"].tap()
        waitFor("sizes 600x460,300x400,400x420,500x440")
    }

    func testInsertingABlankPageAddsOneAndUndoRemovesIt() {
        launch(with: fourPages)
        openPages()
        tile(0).press(forDuration: 1.2)
        let insert = app.buttons["Insert Blank Page"].firstMatch
        XCTAssertTrue(insert.waitForExistence(timeout: 5),
                      dump("a tile's menu has no Insert Blank Page"))
        insert.tap()

        waitFor("count 5")
        XCTAssertEqual(sizes(), ["300x400", "300x400", "400x420", "500x440", "600x460"],
                       dump("a blank page should be the size of the page in front of it"))
        undo()
        waitFor("count 4")
    }

    /// The grid offers the two file commands the other legs offer, and they are where a hand can
    /// find them. The pickers themselves are the OS's and are driven by
    /// `tools/ios-files-e2e.sh`, not from here.
    func testTheGridOffersCombineAndExtract() {
        launch(with: fourPages)
        openPages()
        let add = app.buttons["pagesAdd"].firstMatch
        XCTAssertTrue(add.waitForExistence(timeout: 5), dump("no + menu"))
        add.tap()
        XCTAssertTrue(app.buttons["pagesInsertFromFile"].waitForExistence(timeout: 5),
                      dump("the + menu has no Insert Pages from File…"))
        XCTAssertTrue(app.buttons["Save Pages As…"].firstMatch.exists,
                      dump("the + menu has no Save Pages As…"))
    }

    // MARK: - undo, wherever it lives on this layout

    /// On the sheet, Undo is in the grid's own header: the app's own Undo is on the bottom toolbar,
    /// which the sheet is sitting over. On the iPad the strip above the sidebar already carries it.
    private func undoButton() -> XCUIElement {
        let inPanel = app.buttons["pagesUndo"].firstMatch
        if inPanel.exists { return inPanel }
        let onStrip = stripButton("Undo")
        if onStrip.exists { return onStrip }
        return app.buttons["Undo"].firstMatch
    }

    private func undo() {
        let button = undoButton()
        XCTAssertTrue(button.waitForExistence(timeout: 5), dump("no Undo anywhere on this layout"))
        XCTAssertTrue(button.isEnabled, dump("Undo is disabled after a page change"))
        button.tap()
    }

    func testTheSheetCarriesItsOwnUndoAndTheIPadDoesNot() {
        launch(with: fourPages)
        openPages()
        if isRegularWidth {
            XCTAssertFalse(app.buttons["pagesUndo"].exists,
                           "the strip above the sidebar already has Undo")
            XCTAssertTrue(stripButton("Undo").exists)
        } else {
            XCTAssertTrue(app.buttons["pagesUndo"].exists,
                          dump("the sheet covers the app's own Undo, so it needs one"))
        }
    }
}
