import XCTest

/// Touching the page, and saving on the system export sheet, by conditions the app and the
/// sheet report rather than by how long a machine is assumed to take (#681, #706).
///
/// **Why.** On GitHub's hosted macOS runners the UI suites failed on most `main` runs in
/// October 2026 while passing on the Mac mini, and every failure was an input or a sheet, never
/// an assertion about a document:
///
/// - A synthesised tap was measured taking **25 s** to deliver (run 39376112304), and the next
///   line said "tapping the line did not open the text editor". From outside, four different
///   things look exactly like that, and `-uiTestInputProbe` (`UITestInputProbe` in the app) is
///   how the app tells them apart; see `tap(_:in:until:)`.
/// - The export sheet came up showing its **Browse** root, with Cancel and no Save, seven times
///   in nine Markdown-export failures. Its default folder had not been resolved yet on a
///   simulator that had never shown one. See `ExportSheet`.
///
/// **What this is not.** It never re-runs an assertion, and it never re-sends a touch the app
/// says it acted on: a tap that was dispatched and did nothing fails at once, which is the bug
/// these tests exist to catch. A touch is sent again only when the app's own probe says the last
/// one was not dispatched — it never arrived, it arrived while an open was still finishing (#145
/// ignores page taps then, by design), or it arrived held long enough to be a long press — and
/// every re-send is printed as `PAGE INPUT:` so the job log shows how often the runner needed it.
enum PageInput {

    /// The launch argument that puts the probe in the accessibility tree. Suites that tap the
    /// page add it in `setUp`.
    static let launchArgument = "-uiTestInputProbe"

    /// What the app says became of the page's touches: `taps 2 dispatched 1 ignored 1
    /// presses 0 busy off selection off`.
    struct Probe: Equatable, CustomStringConvertible {
        var taps = 0, dispatched = 0, ignored = 0, presses = 0
        var busy = false, selection = false

        init?(_ label: String) {
            let words = label.split(separator: " ").map(String.init)
            guard words.count == 12 else { return nil }
            func number(_ key: String) -> Int? {
                guard let i = words.firstIndex(of: key), i + 1 < words.count else { return nil }
                return Int(words[i + 1])
            }
            guard let taps = number("taps"), let dispatched = number("dispatched"),
                  let ignored = number("ignored"), let presses = number("presses") else { return nil }
            self.taps = taps
            self.dispatched = dispatched
            self.ignored = ignored
            self.presses = presses
            busy = words[9] == "on"
            selection = words[11] == "on"
        }

        var description: String {
            "taps \(taps) dispatched \(dispatched) ignored \(ignored) presses \(presses)"
                + " busy \(busy ? "on" : "off") selection \(selection ? "on" : "off")"
        }
    }

    /// One snapshot of the probe, or nil while something (a sheet, the home screen) has taken it
    /// out of the tree.
    static func probe(_ app: XCUIApplication) -> Probe? {
        let element = app.staticTexts.matching(identifier: "viewerInputProbe").firstMatch
        guard let label = (try? element.snapshot())?.label else { return nil }
        return Probe(label)
    }

    /// Waits until page taps are being accepted: the probe is there and not busy. A document is
    /// on screen before its open has finished (`ViewerModel.open` draws the page, then reads the
    /// redaction marks, then ends the work), and a tap in that window is ignored on purpose.
    @discardableResult
    static func waitUntilIdle(_ app: XCUIApplication, timeout: TimeInterval = 180) -> Probe? {
        let deadline = Date().addingTimeInterval(timeout)
        repeat {
            if let probe = probe(app), !probe.busy { return probe }
            Thread.sleep(forTimeInterval: 0.3)
        } while Date() < deadline
        return nil
    }

    /// Taps `point` and waits for `effect`, re-sending the tap only on the app's word that the
    /// last one was not dispatched. Returns nil on success, or a sentence saying what happened.
    ///
    /// - `arrival`: how long to wait for the probe to show *something* became of a tap before
    ///   calling it lost. Generous: the slowest delivery measured on a hosted runner was 25 s.
    /// - `effect`: how long a dispatched tap may take to produce `effect`. A dispatched tap that
    ///   produces nothing in that time fails; it is not sent again.
    static func tap(_ point: XCUICoordinate, in app: XCUIApplication, until expected: XCUIElement,
                    attempts: Int = 4, arrival: TimeInterval = 45,
                    effect: TimeInterval = 60) -> String? {
        var history: [String] = []
        for attempt in 1...attempts {
            guard var before = waitUntilIdle(app) else {
                return "the app never stopped being busy, so no tap could do anything"
                    + " (\(history.joined(separator: "; ")))"
            }
            // A selection already up would take this tap (see `putAwaySelection`).
            if before.selection {
                putAwaySelection(near: point, in: app, settle: 0)
                before = waitUntilIdle(app) ?? before
            }
            let sent = Date()
            point.tap()
            let deadline = Date().addingTimeInterval(arrival)
            var outcome = "nothing became of it in \(Int(arrival)) s"
            while Date() < deadline {
                if present(expected) { return nil }
                guard let now = probe(app) else {
                    // A sheet coming up takes the probe out of the tree, and so, now and then,
                    // does a snapshot that times out on a slow runner. Neither is an answer.
                    Thread.sleep(forTimeInterval: 0.3)
                    continue
                }
                if now.dispatched > before.dispatched {
                    if appears(expected, timeout: effect) { return nil }
                    return "the tap was dispatched (\(now)) and nothing appeared in \(Int(effect)) s"
                }
                if now.ignored > before.ignored {
                    outcome = "it arrived while the app was busy and was ignored (#145)"
                    break
                }
                if now.presses > before.presses {
                    outcome = "it arrived held long enough to be a long press"
                    break
                }
                if now.taps > before.taps {
                    outcome = "it arrived as a tap that was not dispatched (a selection or reading mode took it)"
                    break
                }
                Thread.sleep(forTimeInterval: 0.3)
            }
            putAwaySelection(near: point, in: app)
            let line = "attempt \(attempt), sent \(String(format: "%.1f", Date().timeIntervalSince(sent))) s ago: \(outcome)"
            print("PAGE INPUT: \(line)")
            history.append(line)
        }
        return "no tap was dispatched in \(attempts) attempts (\(history.joined(separator: "; ")))"
    }

    /// A press leaves a text selection up (#710), and a tap on the selected word itself does
    /// not reach the page's tap at all (measured on the Mac mini: four taps there, none
    /// counted); a tap elsewhere on the page is what puts it away, as for a person.
    /// "Elsewhere" is the foot of the page the point is on, which every test document here
    /// leaves blank. Does nothing when no selection is up.
    private static func putAwaySelection(near point: XCUICoordinate, in app: XCUIApplication,
                                         settle: TimeInterval = 5) {
        // The selection is set a moment after the press is recognised (`beginTextSelection`
        // reads the page's text first), so a press is given a little while to produce one.
        let until = Date().addingTimeInterval(settle)
        while Date() < until, probe(app)?.selection == false { Thread.sleep(forTimeInterval: 0.3) }
        guard probe(app)?.selection == true else { return }
        print("PAGE INPUT: a selection is up; tapping the foot of the page to put it away")
        point.referencedElement.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.97)).tap()
        let gone = Date().addingTimeInterval(30)
        while Date() < gone, probe(app)?.selection == true { Thread.sleep(forTimeInterval: 0.3) }
    }
}

extension PageInput {
    /// Whether `element` is in the tree, by one snapshot that is allowed to fail.
    ///
    /// `exists` and `waitForExistence` record a test failure of their own when the app cannot
    /// answer an accessibility snapshot in time — "Failed to get matching snapshots: Timed out
    /// while evaluating UI query", five of the hosted failures in October, every one of them a
    /// wait that still had time left. A snapshot that throws is "not yet" here, and the caller's
    /// own deadline decides.
    static func present(_ element: XCUIElement) -> Bool {
        (try? element.snapshot()) != nil
    }

    /// Polls `present` until `timeout`.
    static func appears(_ element: XCUIElement, timeout: TimeInterval) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        repeat {
            if present(element) { return true }
            Thread.sleep(forTimeInterval: 0.5)
        } while Date() < deadline
        return false
    }

    /// Whether `element` has the keyboard's focus within `seconds`, which is the thing
    /// `typeText` actually checks. A keyboard on screen is not the same thing: run 39048968581
    /// had one up, focus elsewhere, and failed with "Neither element nor any descendant has
    /// keyboard focus" after 528 s.
    static func hasKeyboardFocus(_ element: XCUIElement, within seconds: TimeInterval) -> Bool {
        let focused = XCTNSPredicateExpectation(predicate: NSPredicate(format: "hasKeyboardFocus == true"),
                                                object: element)
        return XCTWaiter().wait(for: [focused], timeout: seconds) == .completed
    }

    /// Puts the keyboard's focus in `field`: the editor sheets focus their field as they appear,
    /// and a tap on it is the person's own way in when that has not happened yet.
    static func focus(_ field: XCUIElement) -> Bool {
        if hasKeyboardFocus(field, within: 5) { return true }
        field.tap()
        return hasKeyboardFocus(field, within: 60)
    }
}

extension PageInput {
    /// Opens the viewer's ⋯ menu and returns nil once `row` is in it, or a sentence.
    ///
    /// Run 39291554032 tapped ⋯, the tap was synthesised, and five seconds later the menu was
    /// not on screen at all — the dump has no menu, not a menu without the row. A person whose
    /// tap did not open a menu taps again; so does this, and only then. A menu that is open and
    /// lacks `row` fails at once, since that is a real answer (Save a copy is in every ⋯ menu,
    /// so its presence is what "the menu is open" means here).
    static func openMoreMenu(in app: XCUIApplication, for row: XCUIElement, attempts: Int = 3) -> String? {
        let more = app.buttons["viewerMore"].firstMatch
        guard appears(more, timeout: 30) else { return "there is no ⋯ menu" }
        let open = app.buttons["viewerSaveCopy"].firstMatch
        for attempt in 1...attempts {
            more.tap()
            if appears(row, timeout: 20) { return nil }
            if present(open) { return "the ⋯ menu is open and has no \(row)" }
            print("PAGE INPUT: attempt \(attempt): the ⋯ menu was not open 20 s after its tap")
        }
        return "the ⋯ menu did not open in \(attempts) taps"
    }

    /// Waits for a control to stop being disabled. Every file command is disabled while an
    /// open, a save or an export runs (#145), and XCUITest taps a disabled control without
    /// complaint — which surfaces later as a sheet that never appeared.
    static func waitEnabled(_ element: XCUIElement, timeout: TimeInterval = 90) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if (try? element.snapshot())?.isEnabled == true { return true }
            Thread.sleep(forTimeInterval: 0.5)
        }
        return false
    }
}

/// The system export sheet (`.fileExporter`), saved into the device's own storage.
///
/// iOS 26's sheet has no name field: it opens on a folder with Save in its navigation bar, and a
/// clash is answered Keep Both. On a simulator that has never shown one, it can instead open on
/// its **Browse** root — Locations, Tags, Cancel, no Save — while it resolves that folder, and on
/// a hosted runner it was still there fifteen seconds later (#706: seven of nine
/// `MarkdownExportUITests` failures, every one with this tree in its dump; the Mac mini has never
/// shown it). A person there taps On My iPhone. So does this, once per sight of the root, and
/// otherwise waits for Save for as long as the sheet is visibly still loading.
enum ExportSheet {

    /// Returns nil once Save has been tapped, or a sentence saying what the sheet was showing.
    static func save(in app: XCUIApplication, appearTimeout: TimeInterval = 60,
                     saveTimeout: TimeInterval = 90) -> String? {
        let bar = app.navigationBars["FullDocumentManagerViewControllerNavigationBar"]
        guard PageInput.appears(bar, timeout: appearTimeout) else { return "the export sheet did not appear" }
        let save = bar.buttons["Save"]
        // The device's own storage in the Browse root's Locations: "On My iPhone" or "On My iPad".
        let local = app.cells.matching(NSPredicate(format: "identifier BEGINSWITH 'DOC.sidebar.item.On My'")).firstMatch
        let deadline = Date().addingTimeInterval(saveTimeout)
        var lastRootTap = Date.distantPast
        while Date() < deadline {
            if (try? save.snapshot())?.isEnabled == true {
                save.tap()
                // A name clash is asked after this; `completionAlert` answers it.
                return nil
            }
            // On the root, and not already on the way into a folder (a Back button means a
            // folder has been pushed and is loading).
            if !PageInput.present(bar.buttons["BackButton"]), PageInput.present(local),
               Date().timeIntervalSince(lastRootTap) > 10 {
                print("PAGE INPUT: the export sheet opened on its Browse root; opening \(local.label)")
                local.tap()
                lastRootTap = Date()
            }
            Thread.sleep(forTimeInterval: 0.5)
        }
        return "no Save on the export sheet in \(Int(saveTimeout)) s"
    }

    /// The app's own completion alert after a save from the sheet, answering the sheet's
    /// "Replace Existing Items?" with Keep Both on the way if it comes. It can come late: run
    /// 39244041029 found it where the completion alert was expected, after the three seconds
    /// the test had given it.
    static func completionAlert(in app: XCUIApplication, timeout: TimeInterval = 60) -> XCUIElement? {
        let deadline = Date().addingTimeInterval(timeout)
        let alert = app.alerts.firstMatch
        while Date() < deadline {
            if PageInput.present(alert) {
                let keepBoth = alert.buttons["Keep Both"]
                if PageInput.present(keepBoth) {
                    print("PAGE INPUT: the export sheet asked to replace; Keep Both")
                    keepBoth.tap()
                    continue
                }
                return alert
            }
            let keepBoth = app.buttons["Keep Both"].firstMatch
            if PageInput.present(keepBoth) { keepBoth.tap() }
            Thread.sleep(forTimeInterval: 0.5)
        }
        return nil
    }

    /// The app's own completion **notice** after a save from the sheet (#631): "Saved" and
    /// "Exported" are the self-clearing banner now, not an alert to tap OK on. Returns a failure
    /// message, or nil once the banner says `expected`.
    ///
    /// The tests pin notices (`-uiTestPinNotices`), so the banner outlives the poll, and the
    /// label is what tells one export from the next. An alert is a failure here, not something
    /// to wait through: "Save failed ..." and "Couldn't ..." are still alerts, and so is "Saved"
    /// with a redaction summary, which these documents never carry. Answers the sheet's own
    /// "Keep Both" on the way, as `completionAlert` does.
    static func completionNotice(in app: XCUIApplication, expected: String,
                                 timeout: TimeInterval = 60) -> String? {
        let deadline = Date().addingTimeInterval(timeout)
        let banner = app.staticTexts["noticeBanner"]
        let alert = app.alerts.firstMatch
        var lastBanner: String?
        while Date() < deadline {
            if PageInput.present(alert) {
                let keepBoth = alert.buttons["Keep Both"]
                if PageInput.present(keepBoth) {
                    print("PAGE INPUT: the export sheet asked to replace; Keep Both")
                    keepBoth.tap()
                    continue
                }
                let texts = alert.staticTexts.allElementsBoundByIndex.map(\.label)
                return "an alert came where the \"\(expected)\" notice was expected: \(texts)"
            }
            if let snapshot = try? banner.snapshot() {
                lastBanner = snapshot.label
                if snapshot.label == expected { return nil }
            }
            let keepBoth = app.buttons["Keep Both"].firstMatch
            if PageInput.present(keepBoth) { keepBoth.tap() }
            Thread.sleep(forTimeInterval: 0.25)
        }
        if let lastBanner {
            return "the notice said \"\(lastBanner)\", not \"\(expected)\", in \(Int(timeout)) s"
        }
        return "no \"\(expected)\" notice in \(Int(timeout)) s"
    }
}
