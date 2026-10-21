import XCTest

/// Waiting for a document to open, by the app's own account rather than by a stopwatch (#599).
///
/// **Why this exists at all.** Every suite here opens a document and then waits for its first
/// page, and every one of them wrote down a number: 20 s, 30 s, 60 s. A number assumes a
/// machine, and the runners are not reliably that machine. The same test measured **14 s** on
/// our own Mac, **98 s** and **105 s** on two hosted runners, and **530 s — passing** on a
/// third. Nothing is the right number for all four, and raising them to cover the worst would
/// only move the threshold to the next slow runner (#515).
///
/// **What went wrong when they were just numbers.** On run 36929065249 `app.launch()` alone
/// took a minute — forty seconds of it inside XCTest's own "Setting up automation session" —
/// and the thirty-second wait that followed got **two looks** before giving up. The screen
/// recording attached to that failure shows what the second look saw: the app sitting on
/// **"Opening…"** with its progress bar running. It had not failed to open the document; it
/// had not finished. The sentence "the test document did not open" was true and misleading,
/// and the same sentence has now been collected from three different layers — this suite, the
/// launch itself (`BodyTextEditUITests`, "Timed out while launching application via Xcode"),
/// and a *unit* test with no XCUITest in it at all (`DynamicXfaViewerModelTests`, "timed out
/// waiting for: the document opens").
///
/// **What this does instead.** While the app is *actively reporting* that it is opening — the
/// `busyOpening` strip, which was in the accessibility tree the whole time and which nothing
/// read — the deadline is renewed, up to a cap. When the app is not saying that, the wait
/// fails in `grace` exactly as the old number did. "Still working" and "never did it" stop
/// being the same answer, and a wedged app still fails fast, because a wedged app stops saying
/// "Opening…".
///
/// **What this is not for.** A wait on a sheet appearing, a count changing or a menu row
/// taking a tap is a different condition, and some of those are genuinely lost events (#634)
/// where a renewing deadline would make a fast, honest failure slow. Only the document opening
/// belongs here.
///
/// Warming the simulator was tried first and measured not to work: `simctl install` plus one
/// launch on both devices cost **10 m 22 s** on a hosted runner and the first test still took
/// **76.6 s**, because the forty seconds is XCTest's own automation session and `simctl` cannot
/// reach it. The simulator boot stays in the workflow, for both devices; the rest is here.
enum DocumentOpening {

    /// What the app says it is doing while a document opens, if it says anything.
    static func label(_ app: XCUIApplication) -> String? {
        let strip = app.descendants(matching: .any)
            .matching(NSPredicate(format: "identifier == 'busyOpening'")).firstMatch
        return (try? strip.snapshot())?.label
    }

    /// Waits for `page`, renewing the deadline for as long as the app reports itself opening.
    ///
    /// **And then, for a suite that launched with `-uiTestInputProbe`, for the open to have
    /// finished (#681).** The page is drawn before the open's own work ends —
    /// `ViewerModel.open` sets the viewing state, then reads the document's redaction marks,
    /// then lets go — and in that window page taps are ignored and the file commands in the ⋯
    /// menu are disabled, both on purpose (#145). On the Mac mini the window is too short to
    /// hit. On a hosted runner it is not: a tap there was ignored, a disabled Export as Markdown
    /// row swallowed its tap, and the next line failed as "did not open the text editor" or "the
    /// export sheet did not appear". The probe's `busy` is that window, by the app's account.
    static func wait(for page: XCUIElement, in app: XCUIApplication,
                     grace: TimeInterval = 30, cap: TimeInterval = 240) -> Bool {
        let start = Date()
        var deadline = start.addingTimeInterval(grace)
        var shown = false
        while Date() < deadline && Date().timeIntervalSince(start) < cap {
            if page.exists { shown = true; break }
            if label(app) != nil { deadline = Date().addingTimeInterval(grace) }
            Thread.sleep(forTimeInterval: 0.2)
        }
        guard shown || page.exists else { return false }
        if app.launchArguments.contains(PageInput.launchArgument) {
            return PageInput.waitUntilIdle(app, timeout: max(cap - Date().timeIntervalSince(start), grace)) != nil
        }
        return true
    }

    /// `sentence`, plus what the app said it was doing — the thing that would have turned a
    /// fifty-eight-minute round into a one-line read.
    static func why(_ app: XCUIApplication, _ sentence: String) -> String {
        if let label = label(app) {
            return "\(sentence) — the app is still busy: '\(label)'"
        }
        if let probe = PageInput.probe(app), probe.busy {
            return "\(sentence) — the page is up but the open never finished: '\(probe)'"
        }
        return "\(sentence), and the app was not reporting itself busy"
    }
}
