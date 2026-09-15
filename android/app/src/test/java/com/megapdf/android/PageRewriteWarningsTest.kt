package com.megapdf.android

import com.megapdf.engine.PdfRect
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** The once-per-page memory behind the #139 warning, and which changes it applies to. */
class PageRewriteWarningsTest {
    @Test
    fun `a page is settled once and stays settled until the next document`() {
        val warnings = PageRewriteWarnings()
        assertFalse(warnings.isSettled(2))
        warnings.settle(2)
        assertTrue(warnings.isSettled(2))
        assertFalse("other pages are asked about on their own", warnings.isSettled(3))
        warnings.reset()
        assertFalse("another document starts again", warnings.isSettled(2))
    }

    @Test
    fun `text box changes regenerate the page, annotations do not`() {
        val add = TextBoxOperation(0, "text:a", "Note", 12.0, 72.0, 700.0, adding = true)
        val remove = TextBoxOperation(0, "text:a", "Note", 12.0, 72.0, 700.0, adding = false, boundsAnchored = true)
        val move = MoveTextBoxOperation(0, "text:a", fromX = 72.0, fromY = 700.0, toX = 100.0, toY = 650.0)
        assertTrue(PageRewriteWarnings.regeneratesUnjudged(add))
        assertTrue(PageRewriteWarnings.regeneratesUnjudged(remove))
        assertTrue(PageRewriteWarnings.regeneratesUnjudged(move))
        val mark = MarkOperation(0, PdfRect(100.0, 100.0, 112.0, 112.0), "mark:a", adding = true)
        assertFalse(PageRewriteWarnings.regeneratesUnjudged(mark))
    }
}
