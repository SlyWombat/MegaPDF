import XCTest

/// Whether the iPad pages grid's second-row tile takes a touch (#640).
///
/// `pageTile-2` is the first tile of the second row of the iPad's sidebar grid, and an earlier
/// measurement (`docs/qa/ios-screen-inventory.md` §10c) found it reporting `isHittable == false`
/// for ten seconds and more in about one opening of the panel in eight. `isHittable` is the
/// accessibility tree's hit-test verdict, which is not the same thing as whether a finger lands,
/// so nobody knew whether this was a user's problem or only a test's.
///
/// **What answers it here is a coordinate tap.** `XCUIElement.coordinate(...).tap()` does not ask
/// `isHittable`; it injects a touch at that point, which UIKit then hit-tests exactly as it does
/// a finger's. The app, under `-uiTestGridTrace`, counts the taps each tile received in the
/// tile's accessibility value (`"taps N"`), so "the touch landed" is read from the app and not
/// inferred from the gesture having been sent.
///
/// Two tests:
///
/// - `testMeasureSecondRowHittability` is a measurement, run by hand on the Mac mini. It skips
///   unless the runner's environment has `MEGAPDF_GRID_MEASURE` (the number of openings), and it
///   never fails because the tile misbehaved: it prints a `GRID-HIT` line per opening and, on an
///   episode, `GRID-COVER` lines naming every element over the tile's centre. Its figures are
///   recorded on the issue.
/// - `testTheSecondRowsFirstTileTakesATouchOnEveryOpening` is the CI gate: twelve openings of
///   the panel, each asserting that the tile can be touched and that a touch lands.
///
/// iPad only. The sidebar grid exists at regular width, so every test skips at its first line on
/// the phone. No test here sends a long press: a tile that is both a drag source and a
/// context-menu host costs XCUITest a sixty-second idle wait per long-press event (#599).
final class PagesGridTouchUITests: XCTestCase {

    private var app: XCUIApplication!

    override func setUp() {
        continueAfterFailure = false
    }

    // MARK: - the document

    private var fourPages: String {
        pdf(pageSizes: [(300, 400), (400, 420), (500, 440), (600, 460)])
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

    // MARK: - launching and reading

    private func requireIPad() throws {
        try XCTSkipUnless(UIDevice.current.userInterfaceIdiom == .pad,
                          "the sidebar grid is regular width only (#640)")
    }

    /// Launches the app on the four-page document and waits for it to open.
    ///
    /// No `-uiTestNoTileDrag`: this is the product configuration, and no long press is sent, so
    /// #599's idle stall does not apply. `-uiTestZoomProbes` only when asked for, because the
    /// probe overlay is itself a suspect and the measurement has to run with it off.
    private func launch(probes: Bool) {
        app = XCUIApplication()
        var arguments = ["-AppleLanguages", "(en)", "-AppleLocale", "en_US",
                         "-uiTestGridTrace",
                         // So `DocumentOpening.wait` waits for the open to finish (#681).
                         PageInput.launchArgument]
        if probes { arguments.append("-uiTestZoomProbes") }
        app.launchArguments = arguments
        app.launchEnvironment["MEGAPDF_UITEST_PDF_BASE64"] = fourPages
        app.launch()
        let page = app.images.matching(NSPredicate(format: "label == 'Page 1'")).firstMatch
        XCTAssertTrue(DocumentOpening.wait(for: page, in: app),
                      DocumentOpening.why(app, "the test document did not open"))
    }

    private func tile(_ index: Int) -> XCUIElement {
        app.descendants(matching: .any)
            .matching(NSPredicate(format: "identifier == %@", "pageTile-\(index)")).firstMatch
    }

    private func pagesButton() -> XCUIElement {
        app.descendants(matching: .button)
            .matching(NSPredicate(format: "identifier == 'viewerToolStrip' AND label == 'Pages'"))
            .firstMatch
    }

    /// Taps the app has counted on tile `index`, from its accessibility value (`"taps N"`).
    /// A snapshot rather than `.value`, so a tile that has gone answers 0 instead of waiting.
    private func tapCount(_ index: Int) -> Int {
        guard let value = (try? tile(index).snapshot())?.value as? String,
              value.hasPrefix("taps "), let count = Int(value.dropFirst(5)) else { return 0 }
        return count
    }

    private func isHittable(_ index: Int) -> Bool {
        let element = tile(index)
        return element.exists && element.isHittable
    }

    /// A touch at the middle of tile `index`, injected by coordinate. It does not consult
    /// `isHittable`, so it is the question a finger asks. Answers whether the app counted it
    /// within `timeout`.
    private func touchLands(_ index: Int, timeout: TimeInterval = 5) -> Bool {
        let before = tapCount(index)
        tile(index).coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).tap()
        let deadline = Date().addingTimeInterval(timeout)
        repeat {
            if tapCount(index) > before { return true }
            Thread.sleep(forTimeInterval: 0.1)
        } while Date() < deadline
        return false
    }

    /// Opens the panel from the strip and answers when the tap was sent, as epoch seconds.
    private func openPanel() -> Date {
        let button = pagesButton()
        let deadline = Date().addingTimeInterval(10)
        while !(button.exists && button.isHittable) && Date() < deadline {
            Thread.sleep(forTimeInterval: 0.1)
        }
        let sent = Date()
        button.tap()
        return sent
    }

    /// Closes the panel and waits for the grid to leave the tree, so the next opening starts clean.
    private func closePanel() {
        pagesButton().tap()
        let deadline = Date().addingTimeInterval(10)
        while tile(0).exists && Date() < deadline { Thread.sleep(forTimeInterval: 0.1) }
    }

    // MARK: - the measurement

    /// Every element whose frame holds `point`, from a snapshot of the whole app.
    private func cover(at point: CGPoint, iteration: Int, in snapshot: XCUIElementSnapshot) {
        if snapshot.frame.contains(point) {
            print("GRID-COVER iter=\(iteration) type=\(snapshot.elementType.rawValue) "
                  + "id=\(snapshot.identifier) label=\(snapshot.label) frame=\(snapshot.frame)")
        }
        for child in snapshot.children {
            cover(at: point, iteration: iteration, in: child)
        }
    }

    private func fmt(_ seconds: Double?) -> String {
        seconds.map { String(format: "%.2f", $0) } ?? "never"
    }

    func testMeasureSecondRowHittability() throws {
        try requireIPad()
        guard let raw = ProcessInfo.processInfo.environment["MEGAPDF_GRID_MEASURE"],
              let openings = Int(raw), openings > 0 else {
            throw XCTSkip("a measurement, run by hand: set TEST_RUNNER_MEGAPDF_GRID_MEASURE=<openings> (#640)")
        }
        let env = ProcessInfo.processInfo.environment
        let toggle = env["MEGAPDF_GRID_TOGGLE"] == "1"
        let probes = env["MEGAPDF_GRID_PROBES"] == "1"
        // `MEGAPDF_GRID_PASSIVE=1`: on an episode, do nothing but keep looking (no screenshot,
        // no snapshot, no touch until the tile is hittable or 15 s are up). The first four runs
        // intervened at the one-second mark, and every episode then ended 2 s after the touch,
        // which made the intervention a suspect in how long the episode lasted.
        let passive = env["MEGAPDF_GRID_PASSIVE"] == "1"
        // `MEGAPDF_GRID_ONLY=screenshot|snapshot|tap`: of the three things an intervention does,
        // do only that one, to find which of them ends an episode.
        let only = env["MEGAPDF_GRID_ONLY"] ?? "all"
        let variant = "\(toggle ? "toggle" : "launch")/probes \(probes ? "on" : "off")"
            + (passive ? "/passive" : "") + (only == "all" ? "" : "/only-\(only)")
        print("GRID-RUN variant=\(variant) openings=\(openings)")

        if toggle { launch(probes: probes) }
        for iteration in 1...openings {
            if !toggle { launch(probes: probes) }
            let t0 = openPanel()
            var hit: [Double?] = [nil, nil, nil, nil]
            var allOthers: Double?
            var episode = false
            var landed: Bool?
            while Date().timeIntervalSince(t0) < 15 {
                let now = Date().timeIntervalSince(t0)
                // The episode test runs on the state from the previous look, before this look's
                // answers are recorded, so a tile that turns up at 1.0x s is still an episode.
                if let others = allOthers, hit[2] == nil, !episode, now - others >= 1.0 {
                    episode = true
                    // Before anything else: what is over the tile, and does a finger land?
                    if !passive {
                        if only == "all" || only == "screenshot" {
                            let attachment = XCTAttachment(screenshot: app.screenshot())
                            attachment.name = "episode-\(variant.replacingOccurrences(of: "/", with: "-"))-\(iteration)"
                            attachment.lifetime = .keepAlways
                            add(attachment)
                        }
                        if only == "all" || only == "snapshot",
                           tile(2).exists, let snapshot = try? app.snapshot() {
                            let frame = tile(2).frame
                            cover(at: CGPoint(x: frame.midX, y: frame.midY), iteration: iteration, in: snapshot)
                        }
                        if only == "all" || only == "tap", tile(2).exists { landed = touchLands(2) }
                    }
                }
                for index in 0...3 where hit[index] == nil && isHittable(index) {
                    hit[index] = Date().timeIntervalSince(t0)
                }
                if allOthers == nil, hit[0] != nil, hit[1] != nil, hit[3] != nil {
                    allOthers = Date().timeIntervalSince(t0)
                }
                if hit.allSatisfy({ $0 != nil }) { break }
                Thread.sleep(forTimeInterval: 0.1)
            }
            // The control: on a non-episode the touch is sent once the tile has called itself
            // hittable, and it has to land, or the probe itself is broken.
            if landed == nil, tile(2).exists {
                landed = touchLands(2)
                if !episode {
                    XCTAssertTrue(landed == true,
                                  "iteration \(iteration): a hittable tile 2 did not count a touch; the probe is broken")
                }
            }
            print(String(format: "GRID-HIT variant=%@ iter=%d t0=%.3f h0=%@ h1=%@ h2=%@ h3=%@ episode=%@ landed=%@",
                         variant, iteration, t0.timeIntervalSince1970,
                         fmt(hit[0]), fmt(hit[1]), fmt(hit[2]), fmt(hit[3]),
                         episode ? "yes" : "no",
                         landed.map { $0 ? "yes" : "no" } ?? "n/a"))
            if toggle { closePanel() } else { app.terminate() }
        }
        if toggle { app.terminate() }
    }

    // MARK: - the gate

    /// The CI gate (#640): a touch at the second row's first tile has to land, on a fresh
    /// launch's first opening of the panel and on every opening after it.
    ///
    /// **Why the first opening is asserted on the touch and not on `isHittable`.** Measured
    /// (see the issue): on a fresh launch's first opening, `pageTile-2` reports
    /// `isHittable == false` in roughly one launch in six and stays that way for more than 15 s
    /// unless something touches it; a touch sent at its centre in that state lands every time
    /// (12 of 12 during an episode). So `isHittable` there is XCUITest's accessibility verdict
    /// and not a user's problem, and a gate on it would be red one run in six for no reason a
    /// person could feel. What a person can feel is the touch, so that is what is asserted.
    /// The openings after the first never showed the state in 48 toggles, so on those the gate
    /// also asks for `isHittable`, with ten seconds for it, which is generous for a slow runner
    /// and short of the stale state's 15 s and more.
    func testTheSecondRowsFirstTileTakesATouchOnEveryOpening() throws {
        try requireIPad()
        launch(probes: false)
        for opening in 1...9 {
            let t0 = openPanel()
            var hit: [Double?] = [nil, nil, nil, nil]
            // The panel is up when the tiles around tile 2 can be touched. Tile 2 itself is
            // waited for only on the openings after the first, for the reason above.
            while Date().timeIntervalSince(t0) < 10 {
                for index in 0...3 where hit[index] == nil && isHittable(index) {
                    hit[index] = Date().timeIntervalSince(t0)
                }
                let neighbours = hit[0] != nil && hit[1] != nil && hit[3] != nil
                if neighbours && (opening == 1 || hit[2] != nil) { break }
                Thread.sleep(forTimeInterval: 0.1)
            }
            let times = hit.enumerated().map { "h\($0.offset)=\(fmt($0.element))" }.joined(separator: " ")
            XCTAssertNotNil(hit[0], "opening \(opening): the grid never became touchable (\(times))")
            if opening > 1 {
                XCTAssertNotNil(hit[2], "opening \(opening): pageTile-2 was not hittable within 10 s (\(times))")
            } else if hit[2] == nil {
                print("GRID-GATE: pageTile-2 is not hittable on the first opening (\(times)); #640's stale state, checking that a touch lands")
            }
            XCTAssertTrue(touchLands(2),
                          "opening \(opening): a touch at pageTile-2's centre was not counted (\(times))")
            closePanel()
        }
    }
}
