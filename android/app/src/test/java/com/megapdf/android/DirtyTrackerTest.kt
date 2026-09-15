package com.megapdf.android

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** D3 of #145: a save marks the document saved only if nothing changed while it ran. */
class DirtyTrackerTest {
    @Test
    fun `a save with no change during it marks the document saved`() {
        val dirty = DirtyTracker()
        dirty.markEdited()
        val mark = dirty.beginSave()
        assertTrue(dirty.markSaved(mark))
        assertFalse(dirty.isDirty)
    }

    @Test
    fun `an edit made while the save ran keeps the document dirty`() {
        val dirty = DirtyTracker()
        dirty.markEdited()
        val mark = dirty.beginSave()
        dirty.markEdited()   // lands while the bytes are being written
        assertFalse(dirty.markSaved(mark))
        assertTrue("a later close still asks", dirty.isDirty)
        assertTrue("the next save, with nothing changing, clears it", dirty.markSaved(dirty.beginSave()))
    }

    @Test
    fun `a save still running for the last document cannot mark the next one saved`() {
        val dirty = DirtyTracker()
        val mark = dirty.beginSave()
        dirty.reset()
        dirty.markEdited()
        assertFalse(dirty.markSaved(mark))
        assertTrue(dirty.isDirty)
    }
}
