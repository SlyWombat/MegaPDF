package com.megapdf.android

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * The strip's label for each page tool (#145): a pure mapping, so it is proved here rather than
 * on a device. [ViewerViewModel.performPageEdit] is what actually shows it, but which label goes
 * with which operation does not need an engine, a document or a coroutine to get right or wrong.
 *
 * Every page tool already reports *somewhere* on Android — the Pages screen's document strip,
 * never a page-pinned spinner the operation itself could invalidate (the defect the Mac and
 * Windows passes found and fixed). This is the one piece of that work Android still had to do:
 * a name for what is running, rather than the generic [BusyLabel.APPLYING] every one of them
 * used before.
 */
class PageOperationBusyLabelTest {

    @Test
    fun `rotating pages is named`() {
        assertEquals(BusyLabel.TURNING_PAGES, RotatePagesOperation(listOf(0, 1), 1).busyLabel)
    }

    @Test
    fun `deleting pages is named`() {
        assertEquals(BusyLabel.DELETING_PAGES, DeletePagesOperation(listOf(0)).busyLabel)
    }

    @Test
    fun `moving a page is named`() {
        assertEquals(BusyLabel.MOVING_PAGE, MovePageOperation(0, 2).busyLabel)
    }

    @Test
    fun `inserting a blank page is named`() {
        assertEquals(BusyLabel.INSERTING_PAGE, InsertBlankPageOperation(0, 612.0, 792.0).busyLabel)
    }

    @Test
    fun `combining pages in from another file is named`() {
        assertEquals(BusyLabel.ADDING_PAGES, ImportPagesOperation("/tmp/other.pdf", 0).busyLabel)
    }

    @Test
    fun `an ordinary field edit keeps the generic label`() {
        // Not a page tool: it already reports on the page it touches ([ViewerViewModel.perform]'s
        // own spot-based spinner), so it has no business with a page-tool-specific name.
        assertEquals(BusyLabel.APPLYING, FieldToggleOperation(0, 1.0, 2.0).busyLabel)
    }
}
