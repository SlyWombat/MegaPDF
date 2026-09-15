package com.megapdf.android

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The busy indicator's timing (#145): nothing for 0.5 s, then at least 0.3 s once shown. */
@OptIn(ExperimentalCoroutinesApi::class)
class BusyStateTest {

    private fun TestScope.busy() = BusyState(backgroundScope, now = { testScheduler.currentTime })

    private fun TestScope.advance(ms: Long) {
        advanceTimeBy(ms)
        runCurrent()
    }

    @Test
    fun `work quicker than half a second disables at once and shows nothing`() = runTest {
        val busy = busy()
        val save = busy.beginDocument(BusyLabel.SAVING, locks = true)
        assertTrue("the control is disabled at once", busy.document.isActive)
        assertTrue("a save locks the document", busy.locksDocument)
        assertFalse(busy.document.isVisible)
        advance(400)
        save.end()
        assertFalse(busy.document.isActive)
        assertFalse(busy.locksDocument)
        advance(2_000)
        assertFalse("never flickers", busy.document.isVisible)
        assertNull(busy.document.label)
    }

    @Test
    fun `the indicator appears at half a second and stays at least 0_3 s`() = runTest {
        val busy = busy()
        val save = busy.beginDocument(BusyLabel.SAVING, locks = true)
        advance(499)
        assertFalse(busy.document.isVisible)
        advance(1)
        assertTrue("shown at 500 ms", busy.document.isVisible)
        assertEquals(BusyLabel.SAVING, busy.document.label)

        advance(50)
        save.end()
        assertFalse("editing comes back as soon as the work ends", busy.locksDocument)
        assertTrue("but the indicator stays", busy.document.isVisible)
        advance(249)
        assertTrue("799 ms: still within its 0.3 s", busy.document.isVisible)
        advance(1)
        assertFalse("800 ms: gone", busy.document.isVisible)
    }

    @Test
    fun `long work hides as soon as it ends`() = runTest {
        val busy = busy()
        val open = busy.beginDocument(BusyLabel.OPENING)
        advance(3_000)
        assertTrue(busy.document.isVisible)
        open.end()
        assertFalse("shown for 2.5 s already", busy.document.isVisible)
    }

    @Test
    fun `a relabel moves the label on without restarting the clock`() = runTest {
        val busy = busy()
        val save = busy.beginDocument(BusyLabel.SAVING, locks = true)
        advance(300)
        save.relabel(BusyLabel.VERIFYING_SAVE)
        assertEquals(BusyLabel.VERIFYING_SAVE, busy.document.label)
        advance(200)
        assertTrue("still 500 ms from the start", busy.document.isVisible)
        save.end()
    }

    @Test
    fun `work starting during the minimum keeps the indicator up`() = runTest {
        val busy = busy()
        val first = busy.beginPage(BusyLabel.CHECKING_PAGE, BusySpot(2))
        advance(600)
        first.end()
        advance(100)
        val second = busy.beginPage(BusyLabel.APPLYING, BusySpot(2))
        assertTrue(busy.page.isVisible)
        assertEquals(BusyLabel.APPLYING, busy.page.label)
        advance(1_000)
        assertTrue("stays while the work runs", busy.page.isVisible)
        second.end()
        assertFalse(busy.page.isVisible)
    }

    @Test
    fun `page work leaves the document strip and its lock alone`() = runTest {
        val busy = busy()
        val apply = busy.beginPage(BusyLabel.APPLYING, BusySpot(0))
        assertTrue(busy.page.isActive)
        assertFalse(busy.document.isActive)
        assertFalse(busy.locksDocument)
        apply.end()
    }

    @Test
    fun `a reset drops everything, and a late end cannot unlock the next document`() = runTest {
        val busy = busy()
        val oldSave = busy.beginDocument(BusyLabel.SAVING, locks = true)
        advance(700)
        busy.reset()
        assertFalse(busy.document.isVisible)
        assertFalse(busy.locksDocument)

        val newSave = busy.beginDocument(BusyLabel.SAVING, locks = true)
        oldSave.end()
        assertTrue("the old save's end belongs to the old document", busy.locksDocument)
        newSave.end()
        newSave.end()
        assertFalse(busy.locksDocument)
    }
}
