package com.megapdf.android

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * The renumbering a page operation leaves behind (#174), on the JVM.
 *
 * Each case is written against `megapdf_core.cpp`'s own `RenumberPages` remaps, which run inside
 * the core at the same moment for the core's own state: the same delete, the same insert, the same
 * move arithmetic. If these two ever disagree, the app's thumbnails and selection would be one
 * page out from the document, which is the class of bug #174's triage note warned about.
 */
class PageShiftTest {

    @Test
    fun `a delete drops its own page and pulls the rest back`() {
        val shift = PageShift.Deleted(2)
        assertEquals(0, shift.map(0))
        assertEquals(1, shift.map(1))
        assertEquals(-1, shift.map(2))
        assertEquals(2, shift.map(3))
        assertEquals(-1, shift.countDelta)
    }

    @Test
    fun `an insert pushes everything from its place on`() {
        val shift = PageShift.Inserted(1, count = 3)
        assertEquals(0, shift.map(0))
        assertEquals(4, shift.map(1))
        assertEquals(5, shift.map(2))
        assertEquals(3, shift.countDelta)
    }

    @Test
    fun `moving a page forward pulls the pages it passed back one`() {
        val shift = PageShift.Moved(from = 1, to = 3)
        assertEquals(0, shift.map(0))
        assertEquals(3, shift.map(1))
        assertEquals(1, shift.map(2))
        assertEquals(2, shift.map(3))
        assertEquals(4, shift.map(4))
    }

    @Test
    fun `moving a page back pushes the pages it passed on one`() {
        val shift = PageShift.Moved(from = 3, to = 1)
        assertEquals(0, shift.map(0))
        assertEquals(2, shift.map(1))
        assertEquals(3, shift.map(2))
        assertEquals(1, shift.map(3))
        assertEquals(4, shift.map(4))
    }

    @Test
    fun `a map of bitmaps keeps every page that did not go`() {
        val held = mapOf(0 to "a", 1 to "b", 2 to "c", 3 to "d")
        assertEquals(
            mapOf(0 to "a", 1 to "b", 2 to "d"),
            held.shiftedBy(listOf(PageShift.Deleted(2))),
        )
    }

    @Test
    fun `a selection follows its pages and loses the ones deleted`() {
        // Deleting a selection of two goes from the back, which is what the operation does.
        val shifts = listOf(PageShift.Deleted(3), PageShift.Deleted(1))
        assertEquals(setOf(0, 1, 2), setOf(0, 2, 4).shiftedBy(shifts))
        assertEquals(emptySet<Int>(), setOf(1, 3).shiftedBy(shifts))
    }

    @Test
    fun `a delete and its undo leave the page sizes as they were`() {
        val sizes = (0 until 4).map { PageSize(600.0 + it, 800.0) }
        val deleted = sizes.shiftedBy(PageShift.Deleted(1))
        assertEquals(3, deleted.size)
        val back = deleted.shiftedBy(PageShift.Inserted(1), inserted = listOf(sizes[1]))
        assertEquals(sizes, back)
    }

    @Test
    fun `an import inserts every page it brought`() {
        val sizes = listOf(PageSize(612.0, 792.0), PageSize(612.0, 792.0))
        val arrived = listOf(PageSize(595.0, 842.0), PageSize(595.0, 842.0))
        val after = sizes.shiftedBy(PageShift.Inserted(1, arrived.size), inserted = arrived)
        assertEquals(4, after.size)
        assertEquals(arrived, after.subList(1, 3))
    }

    @Test
    fun `a move takes the page's size with it`() {
        val sizes = listOf(PageSize(1.0, 1.0), PageSize(2.0, 2.0), PageSize(3.0, 3.0))
        assertEquals(
            listOf(PageSize(2.0, 2.0), PageSize(3.0, 3.0), PageSize(1.0, 1.0)),
            sizes.shiftedBy(PageShift.Moved(from = 0, to = 2)),
        )
    }

    @Test
    fun `several shifts in a row compose in the order they happened`() {
        val shifts = listOf(PageShift.Deleted(0), PageShift.Moved(from = 0, to = 2))
        // Page 1 becomes 0 after the delete, then goes to 2.
        assertEquals(2, shifts.mapIndex(1))
        assertEquals(-1, shifts.mapIndex(0))
        assertEquals(4, shifts.mapCount(5))
    }

    // --- The grid's drag arithmetic -----------------------------------------------------

    private fun cells(columns: Int, count: Int) = (0 until count).map {
        val column = it % columns
        val row = it / columns
        PageCellBounds(
            index = it,
            left = column * 100f, top = row * 140f,
            right = column * 100f + 90f, bottom = row * 140f + 130f,
        )
    }

    @Test
    fun `a drag over a cell finds that page`() {
        val grid = cells(columns = 3, count = 9)
        assertEquals(0, pageUnderPoint(grid, 10f, 10f))
        assertEquals(4, pageUnderPoint(grid, 150f, 200f))
        assertEquals(8, pageUnderPoint(grid, 250f, 350f))
    }

    @Test
    fun `a drag in the gaps and past the last page lands on nothing`() {
        val grid = cells(columns = 3, count = 5)
        assertEquals(-1, pageUnderPoint(grid, 95f, 10f))     // between two columns
        assertEquals(-1, pageUnderPoint(grid, 10f, 135f))    // between two rows
        assertEquals(-1, pageUnderPoint(grid, 250f, 200f))   // past the last cell in its row
    }
}
