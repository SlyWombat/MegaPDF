package com.megapdf.android

import com.megapdf.engine.LayoutCause
import com.megapdf.engine.LayoutVerdict
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** One shared page check per page, started early, with a 1.5 s budget (#139 follow-up, #145). */
@OptIn(ExperimentalCoroutinesApi::class)
class PageCheckGateTest {

    private fun verdict(keepsLook: Boolean) = LayoutVerdict(
        editable = keepsLook,
        cause = if (keepsLook) LayoutCause.OK else LayoutCause.RENDER,
        where = if (keepsLook) 0 else LayoutVerdict.WHERE_OTHER,
        changedPixels = if (keepsLook) 0 else 40,
        totalPixels = 10_000,
        maxShiftPoints = 0.0,
    )

    /** A fake engine: each page answers after its delay, or never; counts runs and cancellations. */
    private class FakeChecks(private val scope: TestScope) {
        val runs = mutableMapOf<Int, Int>()
        val cancelled = mutableSetOf<Int>()
        val answers = mutableMapOf<Int, Pair<Long, LayoutVerdict?>>()

        suspend fun check(page: Int): LayoutVerdict? {
            runs[page] = (runs[page] ?: 0) + 1
            val (delayMs, verdict) = answers[page] ?: (Long.MAX_VALUE to null)
            try {
                if (delayMs == Long.MAX_VALUE) awaitCancellation()
                kotlinx.coroutines.delay(delayMs)
                return verdict
            } catch (e: kotlinx.coroutines.CancellationException) {
                cancelled += page
                throw e
            }
        }
    }

    private fun TestScope.gate(fake: FakeChecks) =
        PageCheckGate(backgroundScope).apply { open { fake.check(it) } }

    private fun TestScope.busy() = BusyState(backgroundScope, now = { testScheduler.currentTime })

    @Test
    fun `a page that keeps its look is applied unasked and never checked again`() = runTest {
        val fake = FakeChecks(this).apply { answers[0] = 100L to verdict(true) }
        val gate = gate(fake)
        gate.prepare(0)
        advanceTimeBy(200)
        runCurrent()
        assertTrue("an early answer that keeps the look settles the page", gate.isSettled(0))
        assertTrue(gate.confirm(0))
        assertTrue(gate.confirm(0))
        assertNull(gate.question)
        assertEquals(1, fake.runs[0])
    }

    @Test
    fun `the change reuses the check started early`() = runTest {
        val fake = FakeChecks(this).apply { answers[0] = 1_000L to verdict(true) }
        val gate = gate(fake)
        val busy = busy()
        gate.prepare(0)
        advanceTimeBy(600)
        val change = async { gate.confirm(0, busy) }
        runCurrent()
        assertTrue("the change waits, busy", gate.isDeciding)
        assertEquals(BusyLabel.CHECKING_PAGE, busy.page.label)
        advanceTimeBy(500)
        runCurrent()
        assertTrue(change.await())
        assertEquals("one check, shared", 1, fake.runs[0])
        assertFalse(gate.isDeciding)
        assertFalse(busy.page.isActive)
    }

    @Test
    fun `a page that would change asks, and Continue settles and applies`() = runTest {
        val fake = FakeChecks(this).apply { answers[1] = 50L to verdict(false) }
        val gate = gate(fake)
        val change = async { gate.confirm(1) }
        advanceTimeBy(100)
        runCurrent()
        assertNotNull("the warning is up", gate.question)
        gate.answer(true)
        runCurrent()
        assertTrue(change.await())
        assertTrue(gate.isSettled(1))
        assertTrue("not asked again", gate.confirm(1))
        assertNull(gate.question)
    }

    @Test
    fun `Cancel applies nothing and asks again next time without checking again`() = runTest {
        val fake = FakeChecks(this).apply { answers[1] = 50L to verdict(false) }
        val gate = gate(fake)
        val first = async { gate.confirm(1) }
        advanceTimeBy(100)
        runCurrent()
        gate.answer(false)
        runCurrent()
        assertFalse("Cancel: the change is not applied", first.await())
        assertFalse(gate.isSettled(1))
        assertFalse(gate.isDeciding)

        val second = async { gate.confirm(1) }
        runCurrent()
        assertNotNull("asked again, at once", gate.question)
        gate.answer(false)
        runCurrent()
        assertFalse(second.await())
        assertEquals(1, fake.runs[1])
    }

    @Test
    fun `a check over budget stops and the change applies without a warning`() = runTest {
        val fake = FakeChecks(this)   // page 0 never answers
        val gate = gate(fake)
        val busy = busy()
        val change = async { gate.confirm(0, busy) }
        advanceTimeBy(1_499)
        runCurrent()
        assertTrue("still waiting at 1.499 s", gate.isDeciding)
        assertTrue("busy shows after 0.5 s", busy.page.isVisible)
        advanceTimeBy(1)
        runCurrent()
        assertTrue("applied", change.await())
        assertNull("no warning", gate.question)
        assertTrue("settled", gate.isSettled(0))
        assertTrue("the check was stopped", 0 in fake.cancelled)
        assertTrue(gate.confirm(0))
        assertEquals(1, fake.runs[0])
    }

    @Test
    fun `never two questions at once`() = runTest {
        val fake = FakeChecks(this).apply {
            answers[0] = 10L to verdict(false)
            answers[1] = 10L to verdict(false)
        }
        val gate = gate(fake)
        val first = async { gate.confirm(0) }
        advanceTimeBy(50)
        runCurrent()
        val question = gate.question
        assertNotNull(question)

        assertFalse("a second change is blocked, not queued", gate.confirm(1))
        assertTrue("and does not replace the first question", gate.question === question)

        gate.answer(true)
        runCurrent()
        assertTrue("the first change still goes in", first.await())
    }

    @Test
    fun `starting a check stops unfinished checks of other pages`() = runTest {
        val fake = FakeChecks(this).apply { answers[2] = 10L to verdict(false) }
        val gate = gate(fake)
        gate.prepare(2)
        advanceTimeBy(50)
        runCurrent()
        gate.prepare(0)   // never answers
        runCurrent()
        gate.prepare(1)
        runCurrent()
        assertTrue(0 in fake.cancelled)
        assertFalse("a finished answer is kept", 2 in fake.cancelled)
        assertTrue(gate.hasCheck(2))
        assertTrue(gate.hasCheck(1))
        assertFalse(gate.hasCheck(0))
    }

    @Test
    fun `a settled page is never checked`() = runTest {
        val fake = FakeChecks(this)
        val gate = gate(fake)
        gate.settle(3)
        gate.prepare(3)
        runCurrent()
        assertTrue(gate.confirm(3))
        assertNull(fake.runs[3])
    }

    @Test
    fun `closing the document answers Cancel and stops every check`() = runTest {
        val fake = FakeChecks(this).apply { answers[0] = 10L to verdict(false) }
        val gate = gate(fake)
        val change = async { gate.confirm(0) }
        advanceTimeBy(50)
        runCurrent()
        gate.prepare(0)
        gate.reset()
        runCurrent()
        assertFalse(change.await())
        assertNull(gate.question)
        assertFalse(gate.isDeciding)
        assertFalse(gate.isSettled(0))
    }

    @Test
    fun `a page that cannot be judged applies unasked`() = runTest {
        val fake = FakeChecks(this).apply { answers[0] = 10L to null }
        val gate = gate(fake)
        val change = async { gate.confirm(0) }
        advanceTimeBy(20)
        runCurrent()
        assertTrue(change.await())
        assertTrue(gate.isSettled(0))
    }
}
