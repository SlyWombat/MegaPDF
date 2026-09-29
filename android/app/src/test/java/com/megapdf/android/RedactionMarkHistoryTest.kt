package com.megapdf.android

import com.megapdf.engine.PdfPage
import com.megapdf.engine.PdfRect
import com.megapdf.engine.RedactionMark
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The mark lifecycle through the history on the JVM (#429): place, move, remove, clear, and
 * Undo all the way back through each of them.
 *
 * `RedactionMarkLifecycleTest` drives the same sequences through the real screen and the real
 * core, and needs an emulator for both. This one needs neither: the marks reach the core
 * through [EditTarget], so [FakeCore] can be the core — including the one thing about it that
 * #429 turned on, that a mark comes back from an undo under an id nothing recorded earlier is
 * holding.
 */
class RedactionMarkHistoryTest {

    /** Where a drag along a line of text left a mark, and where a drag down four lines took it. */
    private val drawn = PdfRect(72.0, 700.0, 300.0, 712.0)
    private val moved = PdfRect(72.0, 652.0, 300.0, 664.0)

    /**
     * The core's redaction marks, as the history sees them: an area and an id per mark, and a
     * *fresh* id for every marking — the core never reuses one for the life of a document.
     */
    private class FakeCore : EditTarget {
        private val pages = mutableMapOf<Int, MutableList<RedactionMark>>()
        private var nextId = 101

        /** How many moves the core refused because the id was not on the page. */
        var refusedMoves = 0
            private set

        fun marks(pageIndex: Int): List<RedactionMark> = pages[pageIndex].orEmpty()
        fun rects(pageIndex: Int): List<PdfRect> = marks(pageIndex).map { it.rect }

        override suspend fun <T> onPage(index: Int, body: suspend (PdfPage) -> T): T =
            throw UnsupportedOperationException("a mark operation never opens a page")

        override suspend fun markForRedaction(pageIndex: Int, rects: List<PdfRect>): List<Int> =
            rects.map { rect ->
                val id = nextId++
                pages.getOrPut(pageIndex) { mutableListOf() } += RedactionMark(id, rect)
                id
            }

        override suspend fun moveRedactionMark(pageIndex: Int, markId: Int, rect: PdfRect): Boolean {
            val page = pages[pageIndex] ?: return refuse()
            val at = page.indexOfFirst { it.markId == markId }
            if (at < 0) return refuse()
            page[at] = RedactionMark(markId, rect)
            return true
        }

        override suspend fun removeRedactionMarks(pageIndex: Int, markIds: List<Int>) {
            pages[pageIndex]?.removeAll { it.markId in markIds }
        }

        override suspend fun clearRedactionMarks() = pages.clear()

        // The page tools (#174) are not what this test drives; `FakePages` in PageHistoryTest is.
        private fun notHere(): Nothing =
            throw UnsupportedOperationException("a mark operation never touches the page order")

        override suspend fun rotatePage(pageIndex: Int, quarterTurns: Int) = notHere()
        override suspend fun deletePage(pageIndex: Int): RestorablePage = notHere()
        override suspend fun deletePageWithoutUndo(pageIndex: Int) = notHere()
        override suspend fun restorePage(page: RestorablePage, at: Int) = notHere()
        override suspend fun movePage(from: Int, to: Int) = notHere()
        override suspend fun insertBlankPage(at: Int, widthPoints: Double, heightPoints: Double) = notHere()
        override suspend fun importPages(path: String, insertAt: Int): Int = notHere()

        private fun refuse(): Boolean {
            refusedMoves++
            return false
        }
    }

    /** Places one mark the way the view model does: the core made it, the history records it. */
    private suspend fun EditHistory.placeMark(core: FakeCore, rect: PdfRect): Int {
        val id = core.markForRedaction(0, listOf(rect)).single()
        record(RedactMarkOperation(0, listOf(rect), listOf(id), adding = true))
        return id
    }

    @Test
    fun `undo after a removal takes back the move before it`() = runTest {
        val core = FakeCore()
        val history = EditHistory()

        val placed = history.placeMark(core, drawn)
        history.perform(MoveRedactionMarkOperation(0, placed, drawn, moved), core)
        assertEquals(listOf(moved), core.rects(0))

        // ✕: the mark goes, and the rectangle it had is what its undo will put back.
        history.perform(RedactMarkOperation(0, listOf(moved), listOf(placed), adding = false), core)
        assertTrue(core.marks(0).isEmpty())

        // Undo the removal: the mark is back where it was dropped, under a new id (#429).
        history.undo(core)
        assertEquals(listOf(moved), core.rects(0))
        assertNotEquals(placed, core.marks(0).single().markId)

        // Undo the move: back where the drag drew it. This is the step the stale id lost.
        history.undo(core)
        assertEquals(listOf(drawn), core.rects(0))

        // Undo the placement: nothing marked, nothing left to undo — the history came out even.
        history.undo(core)
        assertTrue(core.marks(0).isEmpty())
        assertFalse(history.canUndo)
        assertEquals("no move was ever asked of a mark that was not there", 0, core.refusedMoves)
    }

    @Test
    fun `redo forward through the removal follows the same mark`() = runTest {
        val core = FakeCore()
        val history = EditHistory()

        val placed = history.placeMark(core, drawn)
        history.perform(MoveRedactionMarkOperation(0, placed, drawn, moved), core)
        history.perform(RedactMarkOperation(0, listOf(moved), listOf(placed), adding = false), core)
        repeat(3) { history.undo(core) }

        // Every redo takes fresh ids of its own, so the way forward needs the rebinding too.
        history.redo(core)
        assertEquals(listOf(drawn), core.rects(0))
        history.redo(core)
        assertEquals(listOf(moved), core.rects(0))
        history.redo(core)
        assertTrue(core.marks(0).isEmpty())
        assertFalse(history.canRedo)
        assertEquals(0, core.refusedMoves)
    }

    @Test
    fun `undo after clearing every mark takes back the move before it`() = runTest {
        val core = FakeCore()
        val history = EditHistory()

        val placed = history.placeMark(core, drawn)
        history.perform(MoveRedactionMarkOperation(0, placed, drawn, moved), core)

        // Clear all marks re-marks the same way a removal's undo does, so it had the same bug.
        history.perform(ClearRedactionMarksOperation(0, mapOf(0 to core.marks(0))), core)
        assertTrue(core.marks(0).isEmpty())

        history.undo(core)
        assertEquals(listOf(moved), core.rects(0))
        history.undo(core)
        assertEquals(listOf(drawn), core.rects(0))
        assertEquals(0, core.refusedMoves)
    }

    @Test
    fun `a move that cannot find its mark says so instead of doing nothing`() = runTest {
        val core = FakeCore()
        val history = EditHistory()
        history.record(MoveRedactionMarkOperation(0, markId = 404, from = drawn, to = moved))

        val failure = runCatching { history.undo(core) }.exceptionOrNull()
        assertTrue("a move that found nothing must not pass for done: $failure", failure is IllegalStateException)
        // And the history keeps the step, because it did not happen.
        assertTrue(history.canUndo)
        assertEquals(1, core.refusedMoves)
    }
}
