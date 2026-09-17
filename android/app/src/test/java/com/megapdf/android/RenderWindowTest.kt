package com.megapdf.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Test

/**
 * The window carried across a document replaced in place (#146).
 *
 * Setting, changing or removing a password reopens the file, and so does the
 * owner-password unlock; the page list has nothing new to report afterwards, so
 * the view model re-renders what the list last asked for. Every case here is one
 * the reopen can actually produce.
 */
class RenderWindowTest {

    private val window = RenderWindow(firstVisible = 4, lastVisible = 7, targetWidthPx = 1080)

    @Test
    fun `a window inside the document is carried over unchanged`() {
        assertSame(window, window.clampedTo(20))
    }

    @Test
    fun `the last page is inside the document`() {
        assertSame(window, window.clampedTo(8))
    }

    @Test
    fun `a shorter document pulls the window back to its last page`() {
        // Removing a password rewrites the file; the replacement need not be as long.
        assertEquals(RenderWindow(4, 4, 1080), window.clampedTo(5))
        assertEquals(RenderWindow(0, 0, 1080), window.clampedTo(1))
    }

    @Test
    fun `first never ends up past last`() {
        val clamped = window.clampedTo(2)!!
        assertEquals(1, clamped.firstVisible)
        assertEquals(1, clamped.lastVisible)
    }

    @Test
    fun `a document with no pages has nothing to render`() {
        assertNull(window.clampedTo(0))
        assertNull(window.clampedTo(-1))
    }

    @Test
    fun `a width the list has not reported yet renders nothing`() {
        // The page list reports a width only once it has been measured, so a reopen
        // before the first layout has nothing to render at.
        assertNull(RenderWindow(0, 0, 0).clampedTo(10))
    }

    @Test
    fun `a window that starts below zero is pulled to the first page`() {
        assertEquals(RenderWindow(0, 3, 900), RenderWindow(-2, 3, 900).clampedTo(10))
    }
}
