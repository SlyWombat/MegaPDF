import XCTest
@testable import MegaPDF

/// What was read of a scan, and how Find uses it (#676): the pure half. Vision itself is
/// `ScanSearchTests`; nothing here needs a recogniser, a document or a simulator.
final class OcrTextTests: XCTestCase {

    private func word(_ range: Range<Int>, _ left: Double, _ right: Double) -> OcrWord {
        OcrWord(range: range, rect: PdfRect(left: left, bottom: 100, right: right, top: 120))
    }

    /// "Total due 1,000" with a word each, 10 points apart.
    private var invoice: OcrPage {
        OcrPage(lines: [OcrLine(text: "Total due 1,000",
                                words: [word(0..<5, 10, 40), word(6..<9, 50, 80), word(10..<15, 90, 150)])])
    }

    func testAMatchIsCaseInsensitiveAndLandsOnItsWord() {
        let hits = invoice.matches(of: "DUE")
        XCTAssertEqual(hits.count, 1)
        XCTAssertEqual(hits.first?.rects, [PdfRect(left: 50, bottom: 100, right: 80, top: 120)])
    }

    func testAMatchInsideAWordHighlightsTheWholeWordNotAGuessedSlice() {
        let hits = invoice.matches(of: "otal")
        XCTAssertEqual(hits.first?.rects, [PdfRect(left: 10, bottom: 100, right: 40, top: 120)],
                       "the recogniser said where the word is, not where its third letter is")
    }

    func testAMatchAcrossWordsIsOneRectangleOverBoth() {
        let hits = invoice.matches(of: "due 1,0")
        XCTAssertEqual(hits.count, 1)
        XCTAssertEqual(hits.first?.rects, [PdfRect(left: 50, bottom: 100, right: 150, top: 120)])
    }

    func testRepeatedTermIsOneHitEachWithoutOverlap() {
        let page = OcrPage(lines: [OcrLine(text: "aaaa", words: [word(0..<4, 0, 40)])])
        XCTAssertEqual(page.matches(of: "aa").count, 2, "as the engine counts them")
    }

    func testNoMatchAndEmptyTermAreEmpty() {
        XCTAssertTrue(invoice.matches(of: "zebra").isEmpty)
        XCTAssertTrue(invoice.matches(of: "").isEmpty)
    }

    func testLinesAreSearchedInReadingOrder() {
        let page = OcrPage(lines: [
            OcrLine(text: "one", words: [word(0..<3, 0, 30)]),
            OcrLine(text: "one again", words: [word(0..<3, 0, 30), word(4..<9, 40, 90)])])
        XCTAssertEqual(page.matches(of: "one").count, 2)
    }

    func testAReadHitOnTopOfOneTheDocumentAlreadyFoundIsNotShownTwice() {
        // The engine matched "due" with a tight glyph box; the recogniser's word box is wider.
        let tight = PdfRect(left: 52, bottom: 102, right: 78, top: 118)
        XCTAssertTrue(invoice.matches(of: "due", besides: [tight]).isEmpty)
        let elsewhere = PdfRect(left: 500, bottom: 500, right: 520, top: 520)
        XCTAssertEqual(invoice.matches(of: "due", besides: [elsewhere]).count, 1,
                       "a hit that merely shares the page is kept")
    }

    // MARK: - the cache follows the pages

    private func cache() -> OcrCache {
        var cache = OcrCache()
        for index in 0..<4 {
            cache[index] = OcrPage(lines: [OcrLine(text: "page \(index)", words: [word(0..<6, 0, 1)])])
        }
        return cache
    }

    private func text(_ cache: OcrCache, _ page: Int) -> String? { cache[page]?.lines.first?.text }

    func testADeleteDropsThatPagesTextAndRenumbersTheRest() {
        let shifted = cache().shifted(by: [.removed(at: 1)])
        XCTAssertEqual((0..<4).map { text(shifted, $0) }, ["page 0", "page 2", "page 3", nil])
    }

    func testAMoveCarriesTheTextWithThePage() {
        let shifted = cache().shifted(by: [.moved(from: 0, to: 2)])
        XCTAssertEqual((0..<4).map { text(shifted, $0) }, ["page 1", "page 2", "page 0", "page 3"],
                       "text that stayed behind would sit on the wrong page: a silent wrong answer")
    }

    func testAnInsertLeavesTheNewPageUnreadNotEmpty() {
        let shifted = cache().shifted(by: [.inserted(at: 1, count: 1)])
        XCTAssertNil(shifted[1], "no entry means not read yet")
        XCTAssertEqual(text(shifted, 2), "page 1")
    }

    func testThePagesFoundToHaveTextFollowToo() {
        var base = OcrCache()
        base.noteText(2)
        XCTAssertTrue(base.shifted(by: [.removed(at: 0)]).hasText(1))
        XCTAssertFalse(base.shifted(by: [.removed(at: 2)]).hasText(2))
    }

    func testRemovingAPageForgetsBothWhatWasReadAndThatItHadText() {
        var base = cache()
        base.noteText(0)
        base.remove(page: 0)
        XCTAssertNil(base[0])
        XCTAssertFalse(base.hasText(0))
    }
}
