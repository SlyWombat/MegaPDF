import XCTest

/// Drag-to-reorder in the pages grid (#174), on its own and **out of CI on purpose**.
///
/// ## Why it is not in `PageToolsUITests`
///
/// A long press on a page tile means two things, and only movement tells them apart. Hold still
/// and the tile's context menu opens — the surface iOS's own PDF markup keeps Rotate, Insert and
/// Delete on, and the one a pointer reaches by right-click. Hold and *move* and the tile is picked
/// up. That is how a tile behaves in Photos, in Files and on the Home screen, and it is what the
/// grid is built to do.
///
/// `XCUICoordinate.press(forDuration:thenDragTo:)` cannot express the difference. It presses for a
/// duration and then moves, which is the same opening that raises the menu; the menu wins, the
/// drag session never begins, and the probe truthfully reports the original order. Run beside
/// `testMoveEarlierAndMoveLaterAreOnEveryTilesMenu`, which presses the *same tile for the same
/// 1.2 seconds* and asserts the menu appeared, the pair reads as a contradiction — because it is
/// one. Both tests are right about what they ask for; the synthesised gesture is what cannot be
/// two things.
///
/// That is the same shape as #465/#531, where a synthesised pinch put a finger on the toolbar and a
/// working app measured as broken. It is also the conclusion the other two legs reached from the
/// other direction: Avalonia proved the drop through the view-model command because a headless
/// platform has no drag session, and the Windows pass drove "the same gap-to-index step a real drop
/// runs" and recorded the pointer half as a by-hand gate. This leg does the same, and this file is
/// the Mac-mini and by-hand half of it.
///
/// ## What covers the drag without it
///
/// Everything except whether iOS starts a drag session, and all of it in CI:
///
/// - `PageShiftTests.testWhichHalfOfATileADropLandedOn` — the midpoint rule, against the widths a
///   tile actually comes out at, because the grid's columns are adaptive and the width is measured
///   rather than assumed.
/// - `PageShiftTests.testADroppedPageLandsWhereTheGapCloses`, `…testEveryDropAgreesWithWhatTheMoveDoes`
///   — the index `movePage` is given, in all four directions and for every tile, held against what
///   the move actually produces rather than against itself.
/// - `PageShiftTests.testAPointInsideATileBecomesTheIndexTheMoveIsGiven` — the two composed.
/// - `PageToolsUITests.testMoveEarlierAndMoveLaterAreOnEveryTilesMenu` and
///   `…testMoveToPutsThePageAtThePositionTyped` — the reorder end to end through the real UI, and
///   the paths a screen-reader user depends on, since a drag is the one way they cannot reorder.
/// - `PageToolsModelTests.testTheSelectionAndTheSearchHitsFollowAMove` — what the app does after a
///   move, which is the half that is silent when it goes wrong.
///
/// So what is left here is one platform question — *does a long press on a tile begin a drag* — and
/// it is answered on the Mac mini and by hand (`docs/qa/ios-screen-inventory.md` §9).
///
/// ## Running it
///
/// `xcodebuild test-without-building -scheme MegaPDFDemo -only-testing:MegaPDFUITests/PageDragUITests`
/// It is expected to fail on a simulator for the reason above. It is kept because it is the exact
/// thing to run by hand on a device, and because a future iOS or XCTest that *can* express the
/// difference should find it waiting rather than need it written again.
final class PageDragUITests: XCTestCase {

    private var app: XCUIApplication!

    override func setUp() {
        continueAfterFailure = false
        app = XCUIApplication()
        app.launchArguments = ["-AppleLanguages", "(en)", "-AppleLocale", "en_US",
                               "-uiTestZoomProbes"]
    }

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

    private func probe() -> String {
        app.descendants(matching: .any)
            .matching(NSPredicate(format: "identifier == 'viewerPagesProbe'")).firstMatch.label
    }

    private func tile(_ index: Int) -> XCUIElement {
        app.descendants(matching: .any)
            .matching(NSPredicate(format: "identifier == %@", "pageTile-\(index)")).firstMatch
    }

    /// Page 3 dragged onto the leading half of page 1, which should leave it first.
    ///
    /// Aimed at *coordinates inside* the two tiles rather than at the tiles as elements: given an
    /// element, `press(forDuration:thenDragTo:)` drops on that element's centre, and the centre of
    /// a tile is exactly the midpoint between "in front of this page" and "behind it" — so a test
    /// that dropped there would be measuring its own aim.
    func testDraggingAPageOntoAnotherReordersIt() {
        app.launchEnvironment["MEGAPDF_UITEST_PDF_BASE64"] =
            pdf(pageSizes: [(300, 400), (400, 420), (500, 440), (600, 460)])
        app.launch()
        let page = app.images.matching(NSPredicate(format: "label == 'Page 1'")).firstMatch
        XCTAssertTrue(DocumentOpening.wait(for: page, in: app), DocumentOpening.why(app, "the test document did not open"))

        // Open the pages: the strip on an iPad, the ⋯ menu on a phone.
        let strip = app.descendants(matching: .button)
            .matching(NSPredicate(format: "identifier == 'viewerToolStrip' AND label == 'Pages'"))
            .firstMatch
        if strip.exists {
            strip.tap()
        } else {
            app.buttons["viewerMore"].firstMatch.tap()
            app.buttons["viewerPages"].firstMatch.tap()
        }
        XCTAssertTrue(tile(2).waitForExistence(timeout: 15), "the grid drew no tiles")

        let before = probe()
        tile(2).coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5))
            .press(forDuration: 1.2,
                   thenDragTo: tile(0).coordinate(withNormalizedOffset: CGVector(dx: 0.15, dy: 0.5)),
                   withVelocity: .slow,
                   thenHoldForDuration: 0.4)

        let deadline = Date().addingTimeInterval(10)
        var now = before
        repeat {
            now = probe()
            if now.contains("sizes 500x440,300x400,400x420,600x460") { return }
            Thread.sleep(forTimeInterval: 0.2)
        } while Date() < deadline
        XCTFail("""
            the drag did not reorder the pages — it says '\(now)'.
            On a simulator this is expected: a synthesised press-then-drag raises the tile's \
            context menu instead of beginning a drag session, which is what this file's own \
            comment is about. Run it on a device, by hand, before concluding the app is wrong.
            """)
    }
}
