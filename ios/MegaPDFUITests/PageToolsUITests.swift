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
                               "-uiTestZoomProbes", "-uiTestPinNotices",
                               // The tiles are not drag sources here, and `PageTileDrag` says
                               // why at length (#599): a tile that is both a drag source and a
                               // context-menu host leaves a *synthesised* long press with a
                               // drag in flight, the app never reports quiescence while the
                               // menu is up, and XCUITest waits out a sixty-second idle
                               // timeout on every event in that window — twice per menu test,
                               // which was 122 of the 135 seconds three of these cost, and two
                               // taps dispatched into an app it had stopped waiting for.
                               // Dragging a tile is a by-hand gate either way (#570), and this
                               // suite's first paragraph already says it is not its business.
                               "-uiTestNoTileDrag"]
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
        XCTAssertTrue(waitForTheDocument(page), dump(openingState()))
        // Asked once, here, rather than on every read of it — see `pagesProbe`.
        XCTAssertTrue(appears(pagesProbe, timeout: 30),
                      dump("the pages probe is missing — did -uiTestZoomProbes survive? (#174)"))
        return page
    }

    /// What the app says it is doing, if it says anything: the label on `busyOpening`.
    ///
    /// On the hosted run that went red (36929065249) the screen recording shows the app
    /// sitting on **"Opening…"** with its progress bar running — the app saying "I am still
    /// working", not "I failed". `busyOpening` was in the tree the whole time, and nothing
    /// read it (#145, #599).
    private func opening() -> String? {
        let strip = app.descendants(matching: .any)
            .matching(NSPredicate(format: "identifier == 'busyOpening'")).firstMatch
        return (try? strip.snapshot())?.label
    }

    private func openingState() -> String {
        if let label = opening() {
            return "the test document did not open — the app is still busy: '\(label)'"
        }
        return "the test document did not open, and the app was not reporting itself busy"
    }

    /// Waits for the document, by the app's own account rather than by a stopwatch.
    ///
    /// A fixed budget here assumes a machine, and hosted runners are not reliably that
    /// machine (#599): the same first test measured 98 s, 105 s and **530 s** on three of
    /// them against 14 s on our own Mac, and on run 36929065249 `app.launch()` alone took a
    /// minute, after which a thirty-second wait got **two looks** and gave up while the app
    /// was still saying "Opening…". Guessing a bigger number would only move that threshold
    /// to the next slow runner (#515).
    ///
    /// So the deadline is renewed for as long as the app is **actively reporting that it is
    /// opening**, up to a cap, and the test fails promptly when it is not. That makes "still
    /// working" and "never did it" different answers, which is the whole of it: a wedged app
    /// still fails in `grace`, because a wedged app stops saying "Opening…".
    ///
    /// Warming the simulator was tried first and did not work: `simctl install` plus one
    /// launch cost ten minutes on a hosted runner and the first test still took 76 s, because
    /// forty of those seconds are XCTest's own "Setting up automation session", which no
    /// amount of `simctl` touches. The boot is still done in the workflow, for both devices.
    private func waitForTheDocument(_ page: XCUIElement, grace: TimeInterval = 30,
                                    cap: TimeInterval = 240) -> Bool {
        let start = Date()
        var deadline = start.addingTimeInterval(grace)
        while Date() < deadline && Date().timeIntervalSince(start) < cap {
            if page.exists { return true }
            // Still opening? Then it has not failed, and the clock starts again.
            if opening() != nil { deadline = Date().addingTimeInterval(grace) }
            Thread.sleep(forTimeInterval: 0.2)
        }
        return page.exists
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

    /// The pages probe, from a query built once per test.
    ///
    /// Built once, and read below without an existence wait, for a measured reason (#599).
    /// `XCUIElement.waitForExistence` costs **a flat second of XCTest waiter overhead per call
    /// even when the element is already on screen**: against this very element on the Mac mini,
    /// 1.049 s asked for a one-second timeout and 1.067 s asked for thirty, against 0.021 s to
    /// read the same element through `snapshot()`. `probe()` is read from the poll loop below,
    /// so that second used to be paid on every poll, and a ten-second budget bought about
    /// **eight** looks at the app where it should buy about fifty.
    ///
    /// The query was never the expense, though it reads like one: `descendants(matching: .any)`
    /// with this predicate measures 0.015 s over the viewer's 61-element tree (84 with the grid
    /// open), and the narrower `app.staticTexts["viewerPagesProbe"]` measured no faster. This
    /// app is cheap to query. What is expensive is asking XCTest to wait.
    private lazy var pagesProbe: XCUIElement = app.descendants(matching: .any)
        .matching(NSPredicate(format: "identifier == 'viewerPagesProbe'")).firstMatch

    /// `"pages on selecting off selected 0 count 4 sizes 300x400,…"`, as the app itself has it.
    ///
    /// One element snapshot and nothing else. `snapshot()` rather than `label` so that a probe
    /// which has momentarily gone answers `""` from inside a poll loop instead of failing the
    /// test on a missing element; that it is there at all is asserted once, in `launch(with:)`.
    private func probe() -> String {
        (try? pagesProbe.snapshot())?.label ?? ""
    }

    /// Whether an element turns up — polled, where this suite used to say `waitForExistence`,
    /// and for the reason given on `pagesProbe`. A test here waits on a dozen elements, and the
    /// waiter's own second apiece was most of what these tests cost when they passed.
    private func appears(_ element: XCUIElement, timeout: TimeInterval = 10) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        repeat {
            if element.exists { return true }
            Thread.sleep(forTimeInterval: 0.1)
        } while Date() < deadline
        return false
    }

    /// Waits for an element to say it can be touched, and answers whether it ever did.
    ///
    /// This is the race the old waits were covering by accident (#599). The iPad's pages
    /// sidebar puts its tiles into the accessibility tree as it *begins* sliding in, and a
    /// gesture synthesised in that window is refused outright — `Failed to synthesize event:
    /// Not hittable: Button, …, identifier: 'pageTile-2'`, which is what the first run of this
    /// suite did once the waits had stopped costing a second of XCTest waiter overhead each.
    /// So every gesture below goes through `settle` first.
    ///
    /// **It is a settle and not a verdict**, which is the part worth reading. Measured on the
    /// iPad over twenty-four panel openings, `pageTile-2` — the first tile of the grid's second
    /// row — reports `isHittable == false` for ten seconds and more in about one opening in
    /// eight, with the right frame, an unchanged subtree and nothing over it, and at the same
    /// rate with and without `-uiTestNoTileDrag`. It is neither this suite's doing nor a layer
    /// in the way, and a gesture sent in that state usually lands: the suite pressed that tile
    /// twelve times out of twelve before any of this. So the wait buys the settle and XCUITest
    /// keeps the last word — if a gesture really cannot be synthesised it says so itself, and
    /// better than an assertion here could. Noted in `docs/qa/ios-screen-inventory.md` §10c as
    /// worth a look of its own: a tile that cannot take a touch is a user's problem too.
    @discardableResult
    private func settle(_ element: XCUIElement, _ name: String,
                        timeout: TimeInterval = 5) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        repeat {
            if element.exists && element.isHittable { return true }
            Thread.sleep(forTimeInterval: 0.1)
        } while Date() < deadline
        print("PAGE TOOLS: \(name) never called itself hittable in "
              + "\(String(format: "%.0f", timeout)) s; gesturing at it anyway (#599)")
        return false
    }

    /// Taps `element` once it has settled. `name` is for the log, not for the query.
    private func tap(_ element: XCUIElement, _ name: String) {
        settle(element, name)
        element.tap()
    }

    /// The long press that opens a tile's menu, once the tile has settled.
    private func longPress(_ element: XCUIElement, _ name: String) {
        settle(element, name)
        element.press(forDuration: 1.2)
    }

    /// The page sizes the app has, in order.
    private func sizes() -> [String] {
        let label = probe()
        guard let range = label.range(of: "sizes ") else { return [] }
        return label[range.upperBound...].split(separator: ",").map(String.init)
    }

    /// Polls until the probe says `expected`, so a passing assertion never depends on how long a
    /// SwiftUI transition or an engine call took.
    ///
    /// The failure says **how many times it looked**, which is the one thing #599's reds could
    /// not be read without: "it never said this" means something quite different after fifty
    /// looks than after eight, and eight was what was really wrong. Ten seconds is left alone
    /// on purpose — measured, the app answers a page move in 0.026-0.071 s, so the budget was
    /// never the binding constraint and widening it would only have moved a threshold (#515).
    @discardableResult
    private func waitFor(_ expected: String, timeout: TimeInterval = 10) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        var last = ""
        var looks = 0
        repeat {
            looks += 1
            last = probe()
            if last.contains(expected) { return true }
            Thread.sleep(forTimeInterval: 0.1)
        } while Date() < deadline
        XCTFail(dump("the app never reported '\(expected)' in \(looks) looks over "
                     + "\(String(format: "%.0f", timeout)) s — it says '\(last)'"))
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
            XCTAssertTrue(appears(button), dump("no Pages button on the strip"))
            tap(button, "the strip's Pages button")
        } else {
            let more = app.buttons["viewerMore"].firstMatch
            XCTAssertTrue(appears(more), dump("no More menu"))
            tap(more, "the ⋯ menu")
            let row = app.buttons["viewerPages"].firstMatch
            XCTAssertTrue(appears(row, timeout: 5), dump("the ⋯ menu has no Pages row"))
            tap(row, "the ⋯ menu's Pages row")
        }
        waitFor("pages on")
        XCTAssertTrue(appears(tile(0)), dump("the grid drew no tiles"))
        // And give it a moment to finish arriving: on the iPad the sidebar is in the tree
        // before it has slid in, and the next thing any of these tests does is aim a gesture
        // at a tile. A settle, not an assertion — see `settle`.
        settle(tile(0), "tile 0")
    }

    private func startSelecting(_ pages: [Int]) {
        let select = app.buttons["pagesSelect"].firstMatch
        XCTAssertTrue(appears(select, timeout: 5), dump("no Select button"))
        tap(select, "the Select button")
        waitFor("selecting on")
        for page in pages {
            let element = tile(page)
            XCTAssertTrue(appears(element, timeout: 5), dump("no tile \(page)"))
            tap(element, "tile \(page)")
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
        tap(app.buttons["pagesClose"].firstMatch, "the sheet's Close")
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

        tap(stripButton("Pages"), "the strip's Pages button")
        waitFor("pages off")
        XCTAssertFalse(tile(0).exists, "the strip's button toggles it")
    }

    /// The ⋯ row is on every layout, because compact width has no strip to put a button on — the
    /// same arrangement reading mode settled on (#506).
    func testThePagesRowIsInTheMoreMenuOnEveryLayout() {
        launch(with: fourPages)
        let more = app.buttons["viewerMore"].firstMatch
        XCTAssertTrue(appears(more), dump("no More menu"))
        tap(more, "the ⋯ menu")
        XCTAssertTrue(appears(app.buttons["viewerPages"].firstMatch, timeout: 5),
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

        tap(stripButton("Reading mode"), "the strip's Reading mode button")
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

        tap(tile(3), "tile 3")
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
        tap(app.buttons["pagesRotateRight"].firstMatch, "Rotate Right")
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
        tap(app.buttons["pagesDelete"].firstMatch, "Delete")
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
        tap(app.buttons["pagesDelete"].firstMatch, "Delete")
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
        tap(app.buttons["pagesDelete"].firstMatch, "Delete")

        let alert = app.alerts["Nothing was changed"]
        XCTAssertTrue(appears(alert, timeout: 5),
                      dump("deleting the last page said nothing at all"))
        XCTAssertTrue(alert.staticTexts["A PDF has to keep at least one page."].exists,
                      dump("the sentence is not the rule"))
        tap(alert.buttons["OK"], "the alert's OK")
        XCTAssertTrue(probe().contains("count 1"), "and nothing was changed")
    }

    /// The reorder every tile carries without a drag: unusable with a screen reader is exactly
    /// what a drag is, so these are the ones that have to exist.
    ///
    /// Driven on **tile 1** rather than tile 2, and that is not arbitrary (#599). `pageTile-2`
    /// — the first tile of the iPad grid's second row — intermittently refuses to be touched
    /// at all: `isHittable` stays false for ten seconds and more in about one opening of the
    /// panel in eight, and XCUITest then declines the gesture outright, `Failed to synthesize
    /// event: Not hittable`. It is that one tile, with the right frame, an unchanged subtree,
    /// nothing over it, and at the same rate however this suite is launched, so it is a
    /// property of the screen and **not** of this test; `docs/qa/ios-screen-inventory.md` §10c
    /// has the measurements and says plainly that it wants a look of its own, because a tile a
    /// test cannot touch may be a tile a finger cannot touch. Any middle tile proves the same
    /// claim, so this one asks a tile that the platform will actually hand over, and the
    /// grid's second row is still pressed by `testMoveToPutsThePageAtThePositionTyped`
    /// (tile 3). If that tile ever starts doing it too, `settle` says so in the log.
    func testMoveEarlierAndMoveLaterAreOnEveryTilesMenu() {
        launch(with: fourPages)
        openPages()
        longPress(tile(1), "tile 1")
        let earlier = app.buttons["Move Earlier"].firstMatch
        XCTAssertTrue(appears(earlier, timeout: 5),
                      dump("a tile's menu has no Move Earlier"))
        XCTAssertTrue(app.buttons["Move Later"].firstMatch.exists)
        tap(earlier, "Move Earlier")
        waitFor("sizes 400x420,300x400,500x440,600x460")

        undo()
        waitFor("sizes 300x400,400x420,500x440,600x460")
    }

    /// A typed position — the phone's answer to the desktops' cut and paste (#2).
    func testMoveToPutsThePageAtThePositionTyped() {
        launch(with: fourPages)
        openPages()
        longPress(tile(3), "tile 3")
        let moveTo = app.buttons["Move to…"].firstMatch
        XCTAssertTrue(appears(moveTo, timeout: 5), dump("a tile's menu has no Move to…"))
        tap(moveTo, "Move to…")

        // Through the alert rather than by identifier: a `TextField` inside an `alert` is built by
        // UIKit from the SwiftUI description and does not carry the identifier across.
        let alert = app.alerts.firstMatch
        XCTAssertTrue(appears(alert, timeout: 5), dump("no Move to… box"))
        let field = alert.textFields.firstMatch
        XCTAssertTrue(appears(field, timeout: 5), dump("the Move to… box has no field"))
        tap(field, "the Move to… field")
        field.typeText("1")
        tap(alert.buttons["Move"], "the Move to… alert's Move")
        waitFor("sizes 600x460,300x400,400x420,500x440")
    }

    func testInsertingABlankPageAddsOneAndUndoRemovesIt() {
        launch(with: fourPages)
        openPages()
        longPress(tile(0), "tile 0")
        let insert = app.buttons["Insert Blank Page"].firstMatch
        XCTAssertTrue(appears(insert, timeout: 5),
                      dump("a tile's menu has no Insert Blank Page"))
        tap(insert, "Insert Blank Page")

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
        XCTAssertTrue(appears(add, timeout: 5), dump("no + menu"))
        tap(add, "the + menu")
        XCTAssertTrue(appears(app.buttons["pagesInsertFromFile"].firstMatch, timeout: 5),
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
        XCTAssertTrue(appears(button, timeout: 5), dump("no Undo anywhere on this layout"))
        XCTAssertTrue(button.isEnabled, dump("Undo is disabled after a page change"))
        tap(button, "Undo")
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
