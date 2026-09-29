package com.megapdf.android

import com.megapdf.engine.PdfPage
import com.megapdf.engine.PdfRect
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The page tools through the undo history on the JVM (#174), against a stand-in for the engine
 * that keeps a list of pages and nothing else.
 *
 * `PageToolsTest` drives the same sequences through the real screen and the real core on an
 * emulator; this one is where the *ordering* is pinned down, because ordering is where a page
 * history goes wrong: which index a restore goes back at, which end a multi-page delete starts
 * from, and — the shape #429 and #441 both had — whether an operation recorded *before* another
 * one still names the right thing when its turn to be undone comes.
 *
 * [FakePages] deliberately behaves like the core in the two ways that matter: a restored page is
 * the page that was deleted (not a copy of whatever is at that index now), and a handle that has
 * already been restored cannot be restored again.
 */
class PageHistoryTest {

    /** A document as a list of page names, with the core's own renumbering and nothing else. */
    private class FakePages : EditTarget {
        val pages = mutableListOf("A", "B", "C", "D")
        val turns = mutableMapOf<String, Int>()
        var imports = 0
            private set

        /** A deleted page, held exactly as `megapdf_removed_page` is. */
        inner class Held(val name: String) : RestorablePage {
            var spent = false
            var discarded = false
            override fun discard() {
                discarded = true
                spent = true
            }
        }

        val held = mutableListOf<Held>()

        override suspend fun <T> onPage(index: Int, body: suspend (PdfPage) -> T): T =
            throw UnsupportedOperationException("a page operation never opens a page")

        override suspend fun markForRedaction(pageIndex: Int, rects: List<PdfRect>): List<Int> =
            throw UnsupportedOperationException()

        override suspend fun moveRedactionMark(pageIndex: Int, markId: Int, rect: PdfRect): Boolean =
            throw UnsupportedOperationException()

        override suspend fun removeRedactionMarks(pageIndex: Int, markIds: List<Int>) =
            throw UnsupportedOperationException()

        override suspend fun clearRedactionMarks() = throw UnsupportedOperationException()

        override suspend fun rotatePage(pageIndex: Int, quarterTurns: Int) {
            val name = pages[pageIndex]
            turns[name] = (((turns[name] ?: 0) + quarterTurns) % 4 + 4) % 4
        }

        override suspend fun deletePage(pageIndex: Int): RestorablePage {
            check(pages.size > 1) { "a document must keep a page" }
            val page = Held(pages.removeAt(pageIndex))
            held += page
            return page
        }

        override suspend fun deletePageWithoutUndo(pageIndex: Int) {
            pages.removeAt(pageIndex)
        }

        override suspend fun restorePage(page: RestorablePage, at: Int) {
            val handle = page as Held
            check(!handle.spent) { "the removed page was already restored or discarded" }
            handle.spent = true
            pages.add(at, handle.name)
        }

        override suspend fun movePage(from: Int, to: Int) {
            pages.add(to, pages.removeAt(from))
        }

        override suspend fun insertBlankPage(at: Int, widthPoints: Double, heightPoints: Double) {
            pages.add(at, "blank")
        }

        override suspend fun importPages(path: String, insertAt: Int): Int {
            imports++
            pages.addAll(insertAt, listOf("X", "Y"))
            return 2
        }
    }

    @Test
    fun `deleting a selection is one undo step and every page comes back where it was`() = runTest {
        val core = FakePages()
        val history = EditHistory()

        history.perform(DeletePagesOperation(listOf(0, 2)), core)
        assertEquals(listOf("B", "D"), core.pages)

        history.undo(core)
        assertEquals("one press of Undo puts the whole selection back", listOf("A", "B", "C", "D"), core.pages)
        assertFalse(history.canUndo)
    }

    @Test
    fun `redoing a delete keeps the pages it deleted the second time`() = runTest {
        val core = FakePages()
        val history = EditHistory()

        history.perform(DeletePagesOperation(listOf(1)), core)
        history.undo(core)
        // The first apply's handle is spent by the restore above; a redo has to take a new one,
        // and a history that reused the old one would throw here instead.
        history.redo(core)
        assertEquals(listOf("A", "C", "D"), core.pages)
        history.undo(core)
        assertEquals(listOf("A", "B", "C", "D"), core.pages)
    }

    @Test
    fun `undo after taking back a delete still takes back the rotation before it`() = runTest {
        // The shape of #429 and #441, in the page tools: an operation recorded before another one
        // must still act on the right page when its own turn to be undone comes. Contract 10's
        // claim is that no index rewriting is needed for that, because a history undone backwards
        // puts the numbering back as it goes. This is that claim, driven.
        val core = FakePages()
        val history = EditHistory()

        history.perform(RotatePagesOperation(listOf(2), quarterTurns = 1), core)   // C
        assertEquals(1, core.turns["C"])

        history.perform(DeletePagesOperation(listOf(0)), core)                     // A goes
        assertEquals(listOf("B", "C", "D"), core.pages)

        history.undo(core)                                                        // A comes back
        assertEquals(listOf("A", "B", "C", "D"), core.pages)

        history.undo(core)                                                        // the rotation
        assertEquals("the rotation came off the page it went on", 0, core.turns["C"])
        assertEquals("and nothing else was turned", 0, core.turns.values.count { it != 0 })
    }

    @Test
    fun `moving a page and taking it back`() = runTest {
        val core = FakePages()
        val history = EditHistory()

        val operation = MovePageOperation(from = 0, to = 2)
        history.perform(operation, core)
        assertEquals(listOf("B", "C", "A", "D"), core.pages)
        // What the view model renumbers its own state by: the move it just made, not the move it
        // was asked for — an undo reports the opposite one.
        assertEquals(listOf(PageShift.Moved(0, 2)), operation.lastPageShifts)

        history.undo(core)
        assertEquals(listOf("A", "B", "C", "D"), core.pages)
        assertEquals(listOf(PageShift.Moved(2, 0)), operation.lastPageShifts)
    }

    @Test
    fun `a blank page's undo takes it off again`() = runTest {
        val core = FakePages()
        val history = EditHistory()

        history.perform(InsertBlankPageOperation(at = 2, widthPoints = 612.0, heightPoints = 792.0), core)
        assertEquals(listOf("A", "B", "blank", "C", "D"), core.pages)

        history.undo(core)
        assertEquals(listOf("A", "B", "C", "D"), core.pages)

        history.redo(core)
        assertEquals(listOf("A", "B", "blank", "C", "D"), core.pages)
    }

    @Test
    fun `redoing an import puts back the pages that arrived rather than reading the file again`() = runTest {
        val core = FakePages()
        val history = EditHistory()

        val operation = ImportPagesOperation(path = "/cache/other.pdf", insertAt = 1)
        history.perform(operation, core)
        assertEquals(listOf("A", "X", "Y", "B", "C", "D"), core.pages)
        assertEquals(2, operation.imported)
        assertEquals(1, core.imports)

        history.undo(core)
        assertEquals(listOf("A", "B", "C", "D"), core.pages)

        history.redo(core)
        assertEquals(listOf("A", "X", "Y", "B", "C", "D"), core.pages)
        assertEquals("the file is read once, however many times the import is redone", 1, core.imports)
    }

    @Test
    fun `an operation dropped from the history lets go of the pages it was holding`() = runTest {
        // A page copy is large and the core holds it until the document closes, so the history
        // says so when an operation can never be reverted again (#174).
        val core = FakePages()
        val history = EditHistory(capacity = 1)

        history.perform(DeletePagesOperation(listOf(0)), core)
        history.perform(DeletePagesOperation(listOf(0)), core)   // pushes the first one off

        assertEquals(2, core.held.size)
        assertTrue("the page the dropped operation held is still held", core.held[0].discarded)
        assertFalse("the operation still in the history keeps its page", core.held[1].discarded)
    }

    @Test
    fun `clearing the history lets go of every page it held`() = runTest {
        // What closing a document does, and what applying a redaction does: the history goes, and
        // with it every deleted page the engine was keeping for an undo that will not be made.
        val core = FakePages()
        val history = EditHistory()

        history.perform(DeletePagesOperation(listOf(0)), core)
        history.clear()

        assertTrue(core.held.single().discarded)
        assertFalse(history.canUndo)
    }
}
