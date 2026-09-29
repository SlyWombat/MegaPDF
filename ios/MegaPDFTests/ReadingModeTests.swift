import CoreGraphics
import XCTest
@testable import MegaPDF

/// Reading mode on iPhone and iPad (#506 tier 1, #512 tier 2).
///
/// What is worth testing without a screen: the decisions that fail *quietly*. A stored
/// page colour that a later version wrote and this one cannot read; a fit-page scale that
/// is subtly wrong on a landscape page; a fade timer armed while VoiceOver is running; a
/// tap that still edits the document with the chrome away; a cached page image that keeps
/// its daylight colours after the tint changed. Each of those would look like nothing at
/// all in a screenshot and like a bug report a fortnight later.
///
/// The parts that need a window — the chrome leaving the accessibility tree, the tap
/// toggling the bar, the two ways out — are `MegaPDFUITests/ReadingModeUITests.swift`,
/// because only a real gesture in a real window can say anything honest about them.
@MainActor
final class ReadingModeTests: XCTestCase {

    // MARK: - page colours as a stored value

    func testTheStoredPageColoursAreTheSameThreeStringsTheDesktopsStore() {
        // Not a detail: `settings.json` on Windows and the Mac holds "", "Sepia" or
        // "Night" (`AppSettings.NameOf`), and one concept with two spellings is how the
        // apps drift apart. The raw values are the contract.
        XCTAssertEqual(PageTint.normal.rawValue, "")
        XCTAssertEqual(PageTint.sepia.rawValue, "Sepia")
        XCTAssertEqual(PageTint.night.rawValue, "Night")
    }

    func testEachTintCarriesItsOwnEngineFlagAndNormalCarriesNone() {
        XCTAssertEqual(PageTint.normal.renderFlag, 0)
        XCTAssertEqual(PageTint.sepia.renderFlag, UInt32(MEGAPDF_RENDER_SEPIA))
        XCTAssertEqual(PageTint.night.renderFlag, UInt32(MEGAPDF_RENDER_NIGHT))
        XCTAssertNotEqual(PageTint.sepia.renderFlag, PageTint.night.renderFlag)
    }

    /// A defaults database is user data. A value this version does not know — a fourth
    /// tint from a later release, or something a person put there by hand — must read
    /// back as Normal, not crash and not stop a document opening.
    func testAnUnknownStoredPageColourReadsBackAsNormal() throws {
        let defaults = try scratchDefaults()
        defaults.set("Twilight", forKey: ReadingDefaults.pageColoursKey)
        XCTAssertEqual(ReadingDefaults.pageTint(defaults), .normal)

        defaults.set("Night", forKey: ReadingDefaults.pageColoursKey)
        XCTAssertEqual(ReadingDefaults.pageTint(defaults), .night)
    }

    func testWithNothingStoredThePageIsNormalAndDocumentsDoNotOpenInReadingMode() throws {
        let defaults = try scratchDefaults()
        XCTAssertEqual(ReadingDefaults.pageTint(defaults), .normal)
        // Off by default (#168 decision 2): the app opens the way it always has unless
        // someone asks otherwise.
        XCTAssertFalse(ReadingDefaults.openInReadingMode(defaults))
    }

    func testTheTwoPreferencesRoundTripThroughUserDefaults() throws {
        let defaults = try scratchDefaults()
        defaults.set(PageTint.sepia.rawValue, forKey: ReadingDefaults.pageColoursKey)
        defaults.set(true, forKey: ReadingDefaults.openInReadingModeKey)
        XCTAssertEqual(ReadingDefaults.pageTint(defaults), .sepia)
        XCTAssertTrue(ReadingDefaults.openInReadingMode(defaults))
    }

    // MARK: - the zoom presets

    /// Fit width is 1 by construction on the phones: the page is laid out at
    /// `containerWidth * zoom`, so zoom 1 *is* the page as wide as the viewport.
    func testFitWidthIsOne() {
        XCTAssertEqual(ReadingZoom.fitWidth, 1)
    }

    func testFitPageMakesThePageExactlyAsTallAsTheViewport() {
        // A US Letter page in a 400x800 viewport. At zoom 1 the page is 400 wide and
        // 400 * 792/612 = 517.6 tall, which already fits, so fit page zooms *in* to
        // 800/517.6 = 1.545.
        let viewport = CGSize(width: 400, height: 800)
        let letter = CGSize(width: 612, height: 792)
        let fit = ReadingZoom.fitPage(viewport: viewport, page: letter)
        XCTAssertEqual(fit, 800 * 612 / (400 * 792), accuracy: 0.0001)
        // And the property that matters, stated as the layout states it: the page's
        // laid-out height is the viewport's.
        XCTAssertEqual(viewport.width * fit * letter.height / letter.width,
                       viewport.height, accuracy: 0.01)
    }

    /// The case the phones could not reach before #512, and the reason the zoom floor had
    /// to come down with the preset: a tall page in a short viewport fits only below 1.
    func testFitPageGoesBelowFitWidthForAPageTallerThanTheViewport() {
        let fit = ReadingZoom.fitPage(viewport: CGSize(width: 400, height: 300),
                                      page: CGSize(width: 612, height: 792))
        XCTAssertLessThan(fit, ReadingZoom.fitWidth)
        XCTAssertGreaterThan(fit, 0)
    }

    func testFitPageIsClampedAtBothEndsAndSurvivesADegenerateViewport() {
        // A very wide, very short page would ask for far more than the app's ceiling.
        let huge = ReadingZoom.fitPage(viewport: CGSize(width: 100, height: 4000),
                                       page: CGSize(width: 2000, height: 10))
        XCTAssertEqual(huge, ReadingZoom.maximum)
        // A GeometryReader reports zero before the first layout pass; that must answer
        // fit width rather than a NaN that would propagate into every frame after it.
        let none = ReadingZoom.fitPage(viewport: .zero, page: CGSize(width: 612, height: 792))
        XCTAssertEqual(none, ReadingZoom.fitWidth)
        XCTAssertFalse(none.isNaN)
    }

    // MARK: - the fade rule

    /// The rule reading mode exists for. A bar that fades is a bar a VoiceOver user cannot
    /// get back: there is no pointer to move, and the gesture that would ask for it is the
    /// gesture they are using to read the page.
    func testTheFadeTimerIsNeverArmedWhileAScreenReaderIsRunning() {
        XCTAssertTrue(ReadingBarFade.armsTimer(voiceOverRunning: false))
        XCTAssertFalse(ReadingBarFade.armsTimer(voiceOverRunning: true))
    }

    // MARK: - the model

    func testEnteringReadingModePutsEveryArmedToolAway() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }

        model.startTextPlacement()
        model.toggleRedactMode()
        XCTAssertTrue(model.isPlacingText)
        XCTAssertTrue(model.redactMode)

        model.setReadingMode(true)

        XCTAssertTrue(model.readingMode)
        XCTAssertFalse(model.isPlacingText, "Add text was left armed inside reading mode")
        XCTAssertFalse(model.redactMode, "Redact was left armed inside reading mode")
        XCTAssertNil(model.statusMessage,
                     "the instruction for a tool that has just been put away is still up")

        // And they do not come back on the way out: the tap that entered reading mode is
        // the last thing anyone did before forgetting the tool was armed at all.
        model.setReadingMode(false)
        XCTAssertFalse(model.readingMode)
        XCTAssertFalse(model.isPlacingText)
        XCTAssertFalse(model.redactMode)
    }

    /// The silent-failure risk the plan names (§7): a tap that still edits with the chrome
    /// away, or — the other way round — a tap suppressed once and never restored.
    func testAPageTapIsSwallowedInsideReadingModeAndWorksAgainAfterIt() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }
        let doc = try XCTUnwrap(model.document)

        // Something on the page for a tap to find: a mark, which a tap selects.
        let lines = try await PdfEngine.shared.textLines(doc, pageIndex: 0)
        let line = try XCTUnwrap(lines.max { $0.text.count < $1.text.count })
        model.markForRedaction(pageIndex: 0, rect: line.rect)
        try await waitUntil("the mark lands") { model.redactionMarkCount > 0 }
        let mark = try XCTUnwrap(model.redactionMarks[0]?.first)

        // Where that mark is, as the view would report a tap on it.
        guard case let .viewing(_, sizes) = model.state else { return XCTFail("not viewing") }
        let size = sizes[0]
        let x = (mark.rect.left + mark.rect.right) / 2 / size.width
        let y = 1 - ((mark.rect.bottom + mark.rect.top) / 2 / size.height)

        model.setReadingMode(true)
        model.onPageTapped(index: 0, xFraction: x, yFraction: y)
        try await Task.sleep(nanoseconds: 200_000_000)
        XCTAssertNil(model.selectedRedactionMark,
                     "a tap reached the page's dispatch inside reading mode (#506)")

        model.setReadingMode(false)
        model.onPageTapped(index: 0, xFraction: x, yFraction: y)
        try await waitUntil("the mark is selected once reading mode is off") {
            model.selectedRedactionMark != nil
        }
    }

    func testLeavingReadingModeLeavesTheDocumentExactlyAsItWas() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }

        let editsBefore = model.editCount
        let dirtyBefore = model.isDirty
        let marksBefore = model.redactionMarkCount

        model.setReadingMode(true)
        model.setReadingMode(false)

        // Entering and leaving changes nothing on disk and nothing in the document —
        // which is why the way out asks no question (#506).
        XCTAssertEqual(model.editCount, editsBefore)
        XCTAssertEqual(model.isDirty, dirtyBefore)
        XCTAssertEqual(model.redactionMarkCount, marksBefore)
    }

    func testClosingADocumentTakesReadingModeWithIt() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }
        model.setReadingMode(true)
        model.close()
        try await waitUntil("the document closes") {
            if case .home = model.state { return true }
            return false
        }
        XCTAssertFalse(model.readingMode,
                       "the next document would have opened into the last one's mode")
    }

    /// #512's *Open documents in reading mode*, end to end through the real defaults the
    /// Settings sheet writes — the one place the switch is read.
    func testOpenInReadingModeOpensTheNextDocumentStraightIntoIt() async throws {
        UserDefaults.standard.set(true, forKey: ReadingDefaults.openInReadingModeKey)
        defer { UserDefaults.standard.removeObject(forKey: ReadingDefaults.openInReadingModeKey) }

        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }
        XCTAssertTrue(model.readingMode)
    }

    func testWithTheSwitchOffADocumentOpensWithItsChromeAsItAlwaysHas() async throws {
        UserDefaults.standard.removeObject(forKey: ReadingDefaults.openInReadingModeKey)
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }
        XCTAssertFalse(model.readingMode)
    }

    /// The cache key is what makes "switching tint re-renders the visible pages, and only
    /// those" true: the pages on screen have a stale key and are drawn again, and there is
    /// nothing else in the dictionary to draw.
    func testChangingTheTintRedrawsThePagesOnScreen() async throws {
        let (model, url) = try await openModel()
        defer { try? FileManager.default.removeItem(at: url) }

        model.updateRenderWindow(first: 0, last: 0, widthPx: 320)
        try await waitUntil("the first page renders") { model.pageImages[0] != nil }
        let daylight = try XCTUnwrap(model.pageImages[0]).averageColour()

        model.setPageTint(.night)
        XCTAssertEqual(model.pageTint, .night)
        try await waitUntil("the page is drawn again, in the dark") {
            guard let image = model.pageImages[0] else { return false }
            return image.averageColour().distance(to: daylight) > 0.1
        }
    }

    // MARK: - the engine's two tints, through this platform's binding (#509)

    /// #509 proved the post-pass in the core, against the core's own pixels. What had no
    /// test is the half a line in `PdfEngine.render` that decides whether the flag ever
    /// reaches it — the kind of mistake that ships as "night mode does nothing on iOS".
    func testSepiaAndNightReachTheEngineFromThisBinding() async throws {
        let engine = PdfEngine.shared
        let doc = try await engine.open(try fixture("fixture"))
        defer { Task { await engine.close(doc) } }

        let normal = try await engine.render(doc, index: 0, pixelWidth: 200, pixelHeight: 260)
            .averageColour()
        let sepia = try await engine.render(doc, index: 0, pixelWidth: 200, pixelHeight: 260,
                                            tint: .sepia).averageColour()
        let night = try await engine.render(doc, index: 0, pixelWidth: 200, pixelHeight: 260,
                                            tint: .night).averageColour()

        // A mostly-white page: sepia warms it (red above blue, and darker than white),
        // night turns it dark. Directions, not values — the exact curve is #509's test.
        XCTAssertGreaterThan(sepia.red, sepia.blue + 0.02, "sepia did not warm the page")
        XCTAssertLessThan(sepia.blue, normal.blue, "sepia left the page as white as it was")
        XCTAssertLessThan(night.luminance, 0.3, "night left the page in daylight")
        XCTAssertGreaterThan(normal.luminance, 0.7, "the fixture is not a mostly-white page")
    }

    // MARK: - helpers

    /// A `UserDefaults` of this test's own, so nothing here can leave a preference behind
    /// on the simulator for the next test — or the next run — to trip over.
    private func scratchDefaults(function: String = #function) throws -> UserDefaults {
        let name = "megapdf.tests.\(function)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defaults.removePersistentDomain(forName: name)
        addTeardownBlock { defaults.removePersistentDomain(forName: name) }
        return defaults
    }

    private func fixture(_ name: String) throws -> Data {
        let bundle = Bundle(for: Self.self)
        guard let url = bundle.url(forResource: name, withExtension: "pdf") else {
            throw XCTSkip("fixture \(name).pdf missing from test bundle")
        }
        return try Data(contentsOf: url)
    }

    /// A model with the demo agreement open from a writable temporary file, the same shape
    /// `PageCheckTests` uses.
    private func openModel() async throws -> (ViewerModel, URL) {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("reading-\(UUID().uuidString).pdf")
        try fixture("demo").write(to: url)
        let model = ViewerModel()
        model.openPicked(url: url)
        try await waitUntil("the document opens") {
            if case .viewing = model.state { return model.document != nil && !model.busy.isBlocked }
            return false
        }
        return (model, url)
    }

    private func waitUntil(_ what: String, timeout: TimeInterval = 15,
                           _ condition: @MainActor () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition() {
            if Date() > deadline {
                XCTFail("timed out waiting for: \(what)")
                throw CancellationError()
            }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
    }
}

/// What a rendered page averages out to. Enough to tell daylight from night and warm from
/// neutral, which is all these tests claim; #509's own tests own the exact curve.
struct AverageColour {
    let red: Double
    let green: Double
    let blue: Double

    var luminance: Double { 0.2126 * red + 0.7152 * green + 0.0722 * blue }

    func distance(to other: AverageColour) -> Double {
        abs(red - other.red) + abs(green - other.green) + abs(blue - other.blue)
    }
}

extension CGImage {
    func averageColour() -> AverageColour {
        let w = width, h = height
        var bytes = [UInt8](repeating: 0, count: w * h * 4)
        guard let ctx = CGContext(data: &bytes, width: w, height: h, bitsPerComponent: 8,
                                  bytesPerRow: w * 4, space: CGColorSpaceCreateDeviceRGB(),
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            return AverageColour(red: 0, green: 0, blue: 0)
        }
        ctx.draw(self, in: CGRect(x: 0, y: 0, width: w, height: h))
        var r = 0.0, g = 0.0, b = 0.0
        for i in stride(from: 0, to: bytes.count, by: 4) {
            r += Double(bytes[i]); g += Double(bytes[i + 1]); b += Double(bytes[i + 2])
        }
        let n = Double(w * h) * 255
        return AverageColour(red: r / n, green: g / n, blue: b / n)
    }
}
