package com.megapdf.android

import androidx.compose.ui.geometry.Rect
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Where the Copy pill is anchored as the page scrolls (#803). On the Pixel 4 XL the pill rode up with
 * the page and ended drawn over the title bar, because the floating toolbar was told the selection's
 * box wherever it had scrolled to, and the system only knows the window, not our content area.
 */
class ToolbarAnchorTest {

    // A phone: title bar to y=200, content from 200 to 2000, a 100 px strip reserved for the pill.
    private val viewport = Rect(0f, 200f, 1000f, 2000f)
    private val reserve = 100f

    @Test
    fun `a selection well inside the content area is left where it is`() {
        val box = Rect(100f, 800f, 400f, 860f)
        assertEquals(box, toolbarAnchor(box, viewport, reserve))
    }

    @Test
    fun `no viewport known means no limit`() {
        val box = Rect(100f, -50f, 400f, 10f)
        assertEquals(box, toolbarAnchor(box, null, reserve))
    }

    @Test
    fun `a selection that has scrolled up under the title bar never puts the pill above the content area`() {
        // Top edge at 150, 50 px inside the title bar, bottom still visible at 400.
        val anchor = toolbarAnchor(Rect(100f, 150f, 400f, 400f), viewport, reserve)!!
        // The pill sits above the anchor's top, so the top must leave room for it below the title bar.
        assertTrue("pill room above ${anchor.top}", anchor.top - reserve >= viewport.top)
        assertTrue(anchor.bottom >= anchor.top)
    }

    @Test
    fun `a selection scrolled wholly out of sight has no pill`() {
        assertNull(toolbarAnchor(Rect(100f, 20f, 400f, 120f), viewport, reserve))
        assertNull(toolbarAnchor(Rect(100f, 2100f, 400f, 2200f), viewport, reserve))
    }

    @Test
    fun `a selection that is scrolled sideways out of the content area has no pill`() {
        assertNull(toolbarAnchor(Rect(1100f, 800f, 1400f, 860f), viewport, reserve))
    }

    @Test
    fun `a selection only in the strip at the top is still shown, inside the content area`() {
        val anchor = toolbarAnchor(Rect(100f, 210f, 400f, 250f), viewport, reserve)!!
        assertTrue(anchor.top - reserve >= viewport.top)
        assertTrue(anchor.bottom >= anchor.top)
    }

    @Test
    fun `a selection running off the bottom is cut at the content area`() {
        val anchor = toolbarAnchor(Rect(100f, 1900f, 400f, 2400f), viewport, reserve)!!
        assertEquals(1900f, anchor.top, 0f)
        assertEquals(2000f, anchor.bottom, 0f)
    }
}
