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

    // --- Progress and Stop (#145) ---

    @Test
    fun `report publishes the count, after the item it describes`() = runTest {
        val busy = busy()
        val search = busy.beginDocument(BusyLabel.SEARCHING)
        assertEquals(null, busy.document.progressDone)
        assertEquals(null, busy.document.progressTotal)
        search.report(1, 10)
        assertEquals(1, busy.document.progressDone)
        assertEquals(10, busy.document.progressTotal)
        search.report(2, 10)
        assertEquals(2, busy.document.progressDone)
        search.end()
    }

    @Test
    fun `cancel is offered only while a cancellable operation is running`() = runTest {
        val busy = busy()
        var cancelled = false
        val save = busy.beginDocument(BusyLabel.SAVING, locks = true)
        assertFalse("a save offers no Stop", busy.document.canCancel)
        save.end()

        val search = busy.beginDocument(BusyLabel.SEARCHING, cancel = { cancelled = true })
        assertTrue("a cancellable operation offers Stop", busy.document.canCancel)
        assertFalse(busy.document.isCancelling)
        search.end()
        assertFalse("nothing is running any more, so there is nothing left to stop", busy.document.canCancel)
        assertFalse(cancelled)
    }

    @Test
    fun `requestCancel invokes the newest running operation's callback once`() = runTest {
        val busy = busy()
        var calls = 0
        busy.beginDocument(BusyLabel.SEARCHING, cancel = { calls++ })
        busy.document.requestCancel()
        assertEquals(1, calls)
        assertTrue("pressed, not offered again", busy.document.isCancelling)
        assertFalse("already cancelling: a second ask does not call it again", busy.document.canCancel)

        busy.document.requestCancel()
        assertEquals("a second Stop while the first is still being honoured does nothing", 1, calls)
    }

    @Test
    fun `requestCancel does nothing when nothing offers it`() = runTest {
        val busy = busy()
        val save = busy.beginDocument(BusyLabel.SAVING, locks = true)
        busy.document.requestCancel()   // must not throw, and must not touch the save
        assertTrue(busy.locksDocument)
        save.end()
    }

    @Test
    fun `progress and the count hold their last value while the indicator lingers, but cancel does not`() = runTest {
        val busy = busy()
        val search = busy.beginDocument(BusyLabel.SEARCHING, cancel = {})
        advance(600)   // past showAfterMs: the indicator is up
        search.report(5, 10)
        search.end()

        // The work is over, so there is nothing left to stop — at once, not after the minimum.
        assertFalse(busy.document.canCancel)
        // But the indicator itself, and what it last said, lingers out its minimum (#145's own
        // reason to exist): a count dropping out for that last stretch would be the same flicker.
        assertTrue(busy.document.isVisible)
        assertEquals(5, busy.document.progressDone)
        assertEquals(10, busy.document.progressTotal)

        advance(300)
        assertFalse(busy.document.isVisible)
        assertEquals(null, busy.document.progressDone)
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
