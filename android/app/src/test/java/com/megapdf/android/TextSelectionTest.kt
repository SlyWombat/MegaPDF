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
}
