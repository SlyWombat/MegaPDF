package com.megapdf.android

import com.megapdf.engine.PdfPermissions
import com.megapdf.engine.PdfRect
import com.megapdf.engine.PdfSecurity
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The pure parts of selecting and copying a document's text (#710). The gesture and the clipboard are `TextSelectCopyTest`. */
class TextSelectionTest {

    private val sel = TextSelection(pageIndex = 2, anchor = 10, focus = 20)

    @Test
    fun `carets may come in either order`() {
        val backwards = TextSelection(0, anchor = 20, focus = 10)
        assertEquals(10, backwards.start)
        assertEquals(20, backwards.end)
        assertFalse(backwards.isEmpty)
        assertTrue(TextSelection(0, 5, 5).isEmpty)
    }

    @Test
    fun `moving a handle keeps the other end where it was`() {
        assertEquals(TextSelection(2, anchor = 20, focus = 4), sel.withHandleAt(movedIsStart = true, caret = 4))
        assertEquals(TextSelection(2, anchor = 10, focus = 30), sel.withHandleAt(movedIsStart = false, caret = 30))
    }

    @Test
    fun `the handles never cross`() {
        // The start handle stops one short of the end, the end handle one past the start.
        assertEquals(19, clampedHandleCaret(50, movedIsStart = true, selection = sel, length = 100))
        assertEquals(11, clampedHandleCaret(2, movedIsStart = false, selection = sel, length = 100))
        assertEquals(0, clampedHandleCaret(-5 + 5, movedIsStart = true, selection = sel, length = 100))
        assertEquals(100, clampedHandleCaret(400, movedIsStart = false, selection = sel, length = 100))
    }

    @Test
    fun `a drag that finds no caret leaves the handle where it was`() {
        assertEquals(10, clampedHandleCaret(-1, movedIsStart = true, selection = sel, length = 100))
        assertEquals(20, clampedHandleCaret(-1, movedIsStart = false, selection = sel, length = 100))
    }

    @Test
    fun `a gesture's fractions become crop-space points with a bottom-left origin`() {
        val (x, y) = pagePoint(PageSize(612.0, 792.0), 0.5f, 0.25f)
        assertEquals(306.0, x, 1e-6)
        assertEquals(594.0, y, 1e-6)
    }

    @Test
    fun `handles hang from the first line's start and the last line's end`() {
        val anchors = handleAnchors(listOf(PdfRect(72.0, 600.0, 200.0, 612.0), PdfRect(72.0, 588.0, 150.0, 600.0)))!!
        assertEquals(Pair(72.0, 600.0), anchors.first)
        assertEquals(Pair(150.0, 588.0), anchors.second)
        assertNull(handleAnchors(emptyList()))
    }

    @Test
    fun `copying follows the copy permission and selecting does not`() {
        assertTrue(DocumentCapabilities.FULL.canCopyText)
        val noCopy = PdfSecurity(
            isEncrypted = true, revision = 3,
            permissions = PdfPermissions.PRINT, hasFullAccess = false,
        )
        assertFalse(DocumentCapabilities.fromSecurity(noCopy).canCopyText)
        val mayCopy = noCopy.copy(permissions = PdfPermissions.COPY)
        assertTrue(DocumentCapabilities.fromSecurity(mayCopy).canCopyText)
        // The owner's open has every permission whatever the bits say.
        assertTrue(DocumentCapabilities.fromSecurity(noCopy.copy(hasFullAccess = true)).canCopyText)
    }

    // "alpha beta  gamma delta": the words as caret ranges, with a space between the first two and
    // two between the last, the way the core answers them.
    private val words = listOf(0..5, 6..10, 12..17, 18..23)
    private fun wordAtChar(i: Int): IntRange? = words.firstOrNull { i >= it.first && i < it.last }
    private fun step(sel: TextSelection, start: Boolean, later: Boolean) =
        steppedHandleCaret(sel, start, later, length = 23, wordAtChar = ::wordAtChar)

    @Test
    fun `the end handle moves to the end of the next word and the previous one`() {
        val alpha = TextSelection(0, anchor = 0, focus = 5)
        assertEquals(10, step(alpha, start = false, later = true))
        assertEquals(17, step(TextSelection(0, 0, 10), start = false, later = true))
        assertEquals(23, step(TextSelection(0, 0, 17), start = false, later = true))
        assertEquals(5, step(TextSelection(0, 0, 10), start = false, later = false))
        // Part-way through a word, later finishes it and earlier abandons it.
        assertEquals(10, step(TextSelection(0, 0, 8), start = false, later = true))
        assertEquals(5, step(TextSelection(0, 0, 8), start = false, later = false))
    }

    @Test
    fun `the start handle moves to the start of the next word and the previous one`() {
        assertEquals(6, step(TextSelection(0, 0, 17), start = true, later = true))
        assertEquals(12, step(TextSelection(0, 6, 23), start = true, later = true))
        assertEquals(0, step(TextSelection(0, 6, 17), start = true, later = false))
        assertEquals(6, step(TextSelection(0, 8, 17), start = true, later = false))
        assertEquals(12, step(TextSelection(0, 10, 23), start = true, later = true))
    }

    @Test
    fun `a handle has nowhere to go at the page's edge or against the other end`() {
        assertNull(step(TextSelection(0, 18, 23), start = false, later = true))
        assertNull(step(TextSelection(0, 0, 5), start = true, later = false))
        // The ends never meet: the only word before the end handle is the one already selected.
        assertNull(step(TextSelection(0, 0, 5), start = false, later = false))
        assertNull(step(TextSelection(0, 6, 10), start = true, later = true))
        // A selection read backwards steps the same ends.
        assertEquals(10, step(TextSelection(0, anchor = 5, focus = 0), start = false, later = true))
    }
}
