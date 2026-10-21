package com.megapdf.engine

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

/**
 * The JNI binding to contract 11's selection snapshot (#710), on `selection.pdf` (the core's own
 * fixture, `tools/gen_selection_fixture.py`): page 1 is a title over two columns whose lines
 * share baselines, the left column's first line "Left one alpha" at baseline y=680 and the right
 * column's "Right one golf" beside it. The core's own tests (`test_selection_*`) cover what is
 * selected; these cover that the binding hands it across intact.
 */
@RunWith(AndroidJUnit4::class)
class TextSelectionEngineTest {

    private val engine = PdfEngine()

    private fun asset(name: String): ByteArray =
        InstrumentationRegistry.getInstrumentation().context.assets.open(name).use { it.readBytes() }

    private fun <T> onSnapshot(pageIndex: Int = 0, block: (SelectableText) -> T): T = runBlocking {
        val doc = engine.open(asset("selection.pdf"))
        try {
            val page = doc.openPage(pageIndex)
            val snapshot = try { page.loadSelectable() } finally { page.close() }
            assertNotNull("the page's text loads", snapshot)
            // The snapshot outlives its page: it is used after the page closed above.
            snapshot!!.use(block)
        } finally {
            doc.close()
        }
    }

    @Test
    fun twoColumnsReadDownTheFirstAndThenTheSecond() = onSnapshot { s ->
        assertEquals(
            "Selection Columns\n" +
                "Left one alpha\nLeft two bravo and more\nLeft three charlie\nLeft four delta\nLeft five echo\n" +
                "Right one golf\nRight two hotel\nRight three india\nRight four juliet\nRight five kilo",
            s.text(0, s.length),
        )
        assertEquals("carets may come in either order", s.text(0, s.length), s.text(s.length, 0))
    }

    @Test
    fun aLongPressPointSelectsTheWordAndItsRectCoversIt() = onSnapshot { s ->
        // "alpha" in the left column's first line, on baseline 680.
        val word = s.wordAt(140.0, 684.0, 8.0)
        assertNotNull(word)
        assertEquals("alpha", s.text(word!!.first, word.last))
        val rect = s.rects(word.first, word.last).single()
        assertTrue("the rect is over the word, on its line", rect.left in 120.0..130.0 && rect.right in 155.0..165.0)
        assertTrue("and the line's full height", rect.bottom < 680.0 && rect.top > 688.0)
    }

    @Test
    fun noTextWithinToleranceAnswersNothing() = onSnapshot { s ->
        assertNull(s.wordAt(560.0, 40.0, 8.0))
        assertEquals(-1, s.caretAt(560.0, 40.0, 8.0))
        assertTrue("with no tolerance the nearest caret always answers", s.caretAt(560.0, 40.0) >= 0)
    }

    @Test
    fun aCaretAcrossTheColumnsReachesTheOtherColumn() = onSnapshot { s ->
        val start = s.wordAt(140.0, 684.0, 8.0)!!.first
        val golfEnd = s.caretAt(414.0, 684.0)
        assertEquals(
            "alpha\nLeft two bravo and more\nLeft three charlie\nLeft four delta\nLeft five echo\nRight one golf",
            s.text(start, golfEnd),
        )
    }

    @Test
    fun aClosedSnapshotAnswersAsAnEmptyPage() = onSnapshot { s ->
        val length = s.length
        assertTrue(length > 0)
        s.close()
        assertEquals(0, s.length)
        assertEquals("", s.text(0, length))
        assertEquals(-1, s.caretAt(140.0, 684.0))
        assertTrue(s.rects(0, length).isEmpty())
        s.close() // twice is fine
    }
}
