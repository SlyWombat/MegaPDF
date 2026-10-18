package com.megapdf.android

import com.megapdf.engine.PdfRect
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Find on scanned pages (#676), the JVM half: what the recogniser read and how Find uses it, with
 * the recogniser scripted. The real recogniser is `ScanRecognitionTest` in androidTest.
 */
class ScanSearchTest {

    private fun word(start: Int, end: Int, left: Double, right: Double) =
        OcrWord(start, end, PdfRect(left, 100.0, right, 120.0))

    /** "Total due 1,000", a word each. */
    private val invoice = OcrPage(
        listOf(OcrLine("Total due 1,000", listOf(word(0, 5, 10.0, 40.0), word(6, 9, 50.0, 80.0), word(10, 15, 90.0, 150.0)))),
    )

    // --- the matcher ---

    @Test fun aMatchIsCaseInsensitiveAndLandsOnItsWord() {
        val hits = invoice.matches("DUE")
        assertEquals(1, hits.size)
        assertEquals(listOf(PdfRect(50.0, 100.0, 80.0, 120.0)), hits[0].rects)
    }

    @Test fun aMatchInsideAWordHighlightsTheWholeWordNotAGuessedSlice() {
        assertEquals(listOf(PdfRect(10.0, 100.0, 40.0, 120.0)), invoice.matches("otal")[0].rects)
    }

    @Test fun aMatchAcrossWordsIsOneRectangleOverBoth() {
        val hits = invoice.matches("due 1,0")
        assertEquals(1, hits.size)
        assertEquals(listOf(PdfRect(50.0, 100.0, 150.0, 120.0)), hits[0].rects)
    }

    @Test fun repeatedTermIsOneHitEachWithoutOverlap() {
        val page = OcrPage(listOf(OcrLine("aaaa", listOf(word(0, 4, 0.0, 40.0)))))
        assertEquals(2, page.matches("aa").size)
    }

    @Test fun noMatchAndEmptyQueryAreEmpty() {
        assertTrue(invoice.matches("zebra").isEmpty())
        assertTrue(invoice.matches("").isEmpty())
    }

    @Test fun aReadHitOnTopOfOneTheDocumentAlreadyFoundIsNotShownTwice() {
        val tight = PdfRect(52.0, 102.0, 78.0, 118.0)
        assertTrue(invoice.matches("due", besides = listOf(tight)).isEmpty())
        val elsewhere = PdfRect(500.0, 500.0, 520.0, 520.0)
        assertEquals(1, invoice.matches("due", besides = listOf(elsewhere)).size)
    }

    // --- the cache follows the pages ---

    private fun cache(): OcrCache = OcrCache().also { c ->
        for (i in 0 until 4) c[i] = OcrPage(listOf(OcrLine("page $i", listOf(word(0, 6, 0.0, 1.0)))))
    }

    private fun text(c: OcrCache, page: Int) = c[page]?.lines?.firstOrNull()?.text

    @Test fun aDeleteDropsThatPagesTextAndRenumbersTheRest() {
        val shifted = cache().shifted(listOf(PageShift.Deleted(1)))
        assertEquals(listOf("page 0", "page 2", "page 3", null), (0 until 4).map { text(shifted, it) })
    }

    @Test fun aMoveCarriesTheTextWithThePage() {
        val shifted = cache().shifted(listOf(PageShift.Moved(0, 2)))
        assertEquals(listOf("page 1", "page 2", "page 0", "page 3"), (0 until 4).map { text(shifted, it) })
    }

    @Test fun anInsertLeavesTheNewPageUnreadNotEmpty() {
        val shifted = cache().shifted(listOf(PageShift.Inserted(1)))
        assertNull(shifted[1])
        assertEquals("page 1", text(shifted, 2))
    }

    @Test fun pagesFoundToHaveTextFollowToo() {
        val base = OcrCache().also { it.noteText(2) }
        assertTrue(base.shifted(listOf(PageShift.Deleted(0))).hasText(1))
        assertFalse(base.shifted(listOf(PageShift.Deleted(2))).hasText(2))
    }

    // --- blank sheets ---

    @Test fun aUniformSheetIsBlankAndASheetWithTextIsNot() {
        val w = 200
        val h = 200
        val white = IntArray(w * h) { 0xFFFFFFFF.toInt() }
        assertTrue(OcrBlank.isBlank(white, w, h))
        val text = white.copyOf()
        for (y in 80 until 120) for (x in 20 until 180) text[y * w + x] = 0xFF000000.toInt()
        assertFalse(OcrBlank.isBlank(text, w, h))
    }

    // --- the searcher, with the recogniser scripted ---

    private class Script(val ownText: Set<Int> = emptySet()) {
        val reads = ArrayList<Int>()
        suspend fun hasText(page: Int) = page in ownText
        suspend fun read(page: Int): OcrPage? {
            reads += page
            return OcrPage(listOf(OcrLine("invoice ${page + 1}", listOf(OcrWord(0, 7, PdfRect(10.0, 100.0, 70.0, 120.0)), OcrWord(8, 9, PdfRect(80.0, 100.0, 90.0, 120.0))))))
        }
    }

    private fun searcher(script: Script) = ScanSearcher(script::hasText, script::read)

    @Test fun aScanIsReadOnceAndFoundFromWhatWasReadAfterwards() = runTest {
        val script = Script()
        val s = searcher(script)
        for (p in 0 until 4) assertEquals(1, s.hits(p, "invoice", emptyList()).size)
        assertEquals(listOf(0, 1, 2, 3), script.reads)
        assertTrue(s.hits(2, "invoice 3", emptyList()).single().fromScan)
        assertEquals("the second search read nothing", 4, script.reads.size)
    }

    @Test fun aPageWithTextOfItsOwnIsNeverRead() = runTest {
        val script = Script(ownText = setOf(0, 1))
        val s = searcher(script)
        assertTrue(s.hits(0, "invoice", emptyList()).isEmpty())
        assertTrue(s.hits(0, "invoice", emptyList()).isEmpty())
        assertTrue(script.reads.isEmpty())
    }

    @Test fun whatWasReadFollowsAMoveAndIsNotReadAgain() = runTest {
        val script = Script()
        val s = searcher(script)
        for (p in 0 until 4) s.hits(p, "invoice", emptyList())
        s.renumbered(listOf(PageShift.Moved(0, 2)))
        // invoice 1 was on page 0 and now stands on page 2.
        assertEquals(1, s.hits(2, "invoice 1", emptyList()).size)
        assertTrue(s.hits(0, "invoice 1", emptyList()).isEmpty())
        assertEquals(4, script.reads.size)
    }

    @Test fun aDeletedPageIsGoneAndTheRestAreNotReadAgain() = runTest {
        val script = Script()
        val s = searcher(script)
        for (p in 0 until 4) s.hits(p, "invoice", emptyList())
        s.renumbered(listOf(PageShift.Deleted(1)))
        assertTrue(s.hits(1, "invoice 2", emptyList()).isEmpty())
        assertEquals(1, s.hits(1, "invoice 3", emptyList()).size)
        assertEquals(4, script.reads.size)
    }

    @Test fun aChangedPageIsReadAgainAndRedactionAndCloseForgetEverything() = runTest {
        val script = Script()
        val s = searcher(script)
        for (p in 0 until 3) s.hits(p, "invoice", emptyList())
        s.pageChanged(1)
        s.hits(1, "invoice", emptyList())
        assertEquals(listOf(0, 1, 2, 1), script.reads)
        s.clear()
        assertTrue(s.cache.isEmpty)
        s.hits(0, "invoice", emptyList())
        assertEquals(5, script.reads.size)
    }

    @Test fun aReadThatAnEditOvertookIsUsedOnceAndNotKept() = runTest {
        lateinit var s: ScanSearcher
        val reads = ArrayList<Int>()
        s = ScanSearcher({ false }) { page ->
            reads += page
            s.pageChanged(page)   // the edit lands while the page is being read
            OcrPage(listOf(OcrLine("invoice", listOf(word(0, 7, 10.0, 70.0)))))
        }
        assertTrue("a read an edit overtook is not trusted", s.hits(0, "invoice", emptyList()).isEmpty())
        assertNull("and is not kept", s.cache[0])
    }

    @Test fun aReadHitOverAnOwnHitIsDroppedSoTheBoxIsNotShownTwice() = runTest {
        val s = searcher(Script())
        val box = SearchHit(0, listOf(PdfRect(15.0, 102.0, 65.0, 118.0)))
        assertTrue("the box's own hit is the one shown", s.hits(0, "invoice", listOf(box)).isEmpty())
        val order = ScanSearcher.inReadingOrder(
            listOf(SearchHit(0, listOf(PdfRect(0.0, 10.0, 5.0, 20.0)))),
            listOf(SearchHit(0, listOf(PdfRect(0.0, 90.0, 5.0, 100.0)), fromScan = true)),
        )
        assertTrue("reading order puts the higher one first", order.first().fromScan)
    }
}
