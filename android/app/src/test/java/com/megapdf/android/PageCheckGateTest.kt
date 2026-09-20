package com.megapdf.android

import com.megapdf.engine.LayoutCause
import com.megapdf.engine.LayoutVerdict
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.delay
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The one shared page check, started early, with a 1.5 s budget (#139 follow-up, #145).
 *
 * The gate itself never asks anybody anything (#332): it says whether the page would change and
 * leaves the warning and its Continue/Cancel to the caller, which [PageRewriteQuestionTest] covers.
 */
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
        /** Completes when a page's check has started, so a test can act while it is running. */
        val started = mutableMapOf<Int, CompletableDeferred<Unit>>()

        suspend fun check(page: Int): LayoutVerdict? {
            runs[page] = (runs[page] ?: 0) + 1
            started[page]?.complete(Unit)
            val (delayMs, verdict) = answers[page] ?: (Long.MAX_VALUE to null)
            try {
                if (delayMs == Long.MAX_VALUE) awaitCancellation()
                delay(delayMs)
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
        assertEquals(PageCheckOutcome.APPLY, gate.confirm(0))
        assertEquals(PageCheckOutcome.APPLY, gate.confirm(0))
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
        assertEquals(BusyLabel.CHECKING_PAGE, busy.page.label)
        advanceTimeBy(500)
        runCurrent()
        assertEquals(PageCheckOutcome.APPLY, change.await())
        assertEquals("one check, shared", 1, fake.runs[0])
        assertFalse(busy.page.isActive)
    }

    @Test
    fun `a page that would change warns, and the caller decides`() = runTest {
        val fake = FakeChecks(this).apply { answers[1] = 50L to verdict(false) }
        val gate = gate(fake)
        val change = async { gate.confirm(1) }
        advanceTimeBy(100)
        runCurrent()
        assertEquals(PageCheckOutcome.WARN, change.await())
        // Warning is not settling: Cancel has to be able to ask again.
        assertFalse(gate.isSettled(1))
        assertEquals(PageCheckOutcome.WARN, gate.confirm(1))
        assertEquals("the page's check answered once", 1, fake.runs[1])

        gate.settle(1)   // Continue
        assertEquals(PageCheckOutcome.APPLY, gate.confirm(1))
    }

    @Test
    fun `a check over budget stops and the change applies without a warning`() = runTest {
        val fake = FakeChecks(this)   // page 0 never answers
        val gate = gate(fake)
        val busy = busy()
        val change = async { gate.confirm(0, busy) }
        advanceTimeBy(1_499)
        runCurrent()
        assertTrue("busy shows after 0.5 s", busy.page.isVisible)
        advanceTimeBy(1)
        runCurrent()
        assertEquals("applied", PageCheckOutcome.APPLY, change.await())
        assertTrue("settled", gate.isSettled(0))
        assertTrue("the check was stopped", 0 in fake.cancelled)
        assertEquals(PageCheckOutcome.APPLY, gate.confirm(0))
        assertEquals(1, fake.runs[0])
    }

    /**
     * The budget ends the wait; it does not throw away an answer that is already there (#332).
     * The rule is [verdictIfAnswered], which is what `confirm` reads once the wait is over — the
     * same rule C# and Swift use (`ACheckThatAnswersWithinItsBudget_IsTheAnswer` pins it there).
     */
    @Test
    fun `a check that has answered is read even after the budget has run out`() = runTest {
        val answered = backgroundScope.async { verdict(false) }
        runCurrent()
        assertTrue("the check answered", answered.isCompleted)
        assertEquals(false, verdictIfAnswered(answered)?.editable)

        val stopped = backgroundScope.async { awaitCancellation() }
        runCurrent()
        stopped.cancel()
        runCurrent()
        assertNull("a stopped check has nothing to say", verdictIfAnswered(stopped))

        val stillRunning = backgroundScope.async<LayoutVerdict?> { delay(1_000); null }
        runCurrent()
        assertNull("a check still running has nothing to say", verdictIfAnswered(stillRunning))
    }

    @Test
    fun `a second change is refused while one waits on its check`() = runTest {
        val fake = FakeChecks(this).apply { answers[0] = 1_000L to verdict(false) }
        val gate = gate(fake)
        val first = async { gate.confirm(0) }
        runCurrent()
        assertEquals("a second change is refused, not queued", PageCheckOutcome.ABANDONED, gate.confirm(1))
        advanceTimeBy(1_000)
        runCurrent()
        assertEquals(PageCheckOutcome.WARN, first.await())
    }

    @Test
    fun `starting another page stops an unfinished check, but a finished one has nothing left to stop`() = runTest {
        val fake = FakeChecks(this).apply { answers[2] = 10L to verdict(true) }
        val gate = gate(fake)
        gate.prepare(2)
        advanceTimeBy(50)
        runCurrent()
        assertTrue("the early check settles its page", gate.isSettled(2))

        gate.prepare(0)   // never answers
        runCurrent()
        gate.prepare(1)
        runCurrent()
        assertTrue(0 in fake.cancelled)
        assertFalse("a check that has answered is not cancelled", 2 in fake.cancelled)
        assertTrue("the one check is page 1's", gate.isChecking(1))
        assertFalse(gate.isChecking(0))
    }

    /**
     * #332: the page a change is waiting on keeps its check. Scrolling on, or arming a tool for
     * another page, does not throw away the answer the change is about to be given — which is what
     * happened while the gate kept one check per page.
     */
    @Test
    fun `a page a change is waiting on keeps its check when another page is prepared`() = runTest {
        val fake = FakeChecks(this).apply {
            started[0] = CompletableDeferred()
            answers[0] = 1_000L to verdict(false)
        }
        val gate = gate(fake)
        val change = async { gate.confirm(0) }
        fake.started.getValue(0).await()
        runCurrent()

        gate.prepare(1)   // the person scrolled on while the change waited

        assertTrue("the check being waited on is left alone", gate.isChecking(0))
        assertFalse("the other page waits its turn", gate.isChecking(1))
        advanceTimeBy(1_000)
        runCurrent()
        assertEquals(PageCheckOutcome.WARN, change.await())
    }

    @Test
    fun `a settled page is never checked`() = runTest {
        val fake = FakeChecks(this)
        val gate = gate(fake)
        gate.settle(3)
        gate.prepare(3)
        runCurrent()
        assertEquals(PageCheckOutcome.APPLY, gate.confirm(3))
        assertNull(fake.runs[3])
    }

    @Test
    fun `closing the document abandons the change and stops the check`() = runTest {
        // The check never answers, so the change is still inside the gate when the document closes.
        val fake = FakeChecks(this).apply { started[0] = CompletableDeferred() }
        val gate = gate(fake)
        val change = async { gate.confirm(0) }
        fake.started.getValue(0).await()
        runCurrent()
        gate.reset()
        runCurrent()
        assertEquals(PageCheckOutcome.ABANDONED, change.await())
        assertFalse("nothing settled for the document that comes next", gate.isSettled(0))
        assertTrue("the check was stopped", 0 in fake.cancelled)
    }

    /**
     * #332: opening another document while a check runs settles nothing in the one that came after
     * it — page 0 of the next document is a different page, and still needs asking about. (The
     * check's own generation guard, `generation == this@PageCheckGate.generation`, closes the
     * narrower window where the answer lands with the reset; a single-threaded test cannot put an
     * answer inside it, which is why this covers the cancel instead.)
     */
    @Test
    fun `opening another document while a check runs settles nothing in the new one`() = runTest {
        val fake = FakeChecks(this).apply {
            started[0] = CompletableDeferred()
            answers[0] = 500L to verdict(true)   // keeps its look: settles its own page
        }
        val gate = gate(fake)
        gate.prepare(0)
        fake.started.getValue(0).await()
        runCurrent()

        gate.open { fake.check(it) }   // another document opened, before the answer lands
        advanceTimeBy(500)
        runCurrent()

        assertFalse("the old document's answer settles nothing here", gate.isSettled(0))
        assertEquals(PageCheckOutcome.APPLY, gate.confirm(0))
    }

    @Test
    fun `a page that cannot be judged applies unasked`() = runTest {
        val fake = FakeChecks(this).apply { answers[0] = 10L to null }
        val gate = gate(fake)
        val change = async { gate.confirm(0) }
        advanceTimeBy(20)
        runCurrent()
        assertEquals(PageCheckOutcome.APPLY, change.await())
        assertTrue(gate.isSettled(0))
    }

    @Test
    fun `no document to judge with is no reason to refuse the change`() = runTest {
        val gate = PageCheckGate(backgroundScope)   // never opened
        assertEquals(PageCheckOutcome.APPLY, gate.confirm(0))
        assertTrue(gate.isSettled(0))
    }
}

/** The warning a change puts up, which the gate no longer owns (#332). */
@OptIn(ExperimentalCoroutinesApi::class)
class PageRewriteQuestionTest {

    @Test
    fun `Continue and Cancel are the two answers, and the warning goes away after either`() = runTest {
        val question = PageRewriteQuestion()
        assertFalse(question.isUp)

        val asked = async { question.ask() }
        runCurrent()
        assertTrue("the warning is up while the change waits", question.isUp)
        question.answer(true)
        runCurrent()
        assertTrue("Continue", asked.await())
        assertFalse("and it is off the screen", question.isUp)

        val second = async { question.ask() }
        runCurrent()
        question.answer(false)
        runCurrent()
        assertFalse("Cancel", second.await())
        assertFalse(question.isUp)
    }

    /** Answering when nothing is up is a stray tap, not a crash. */
    @Test
    fun `answering with no warning up does nothing`() = runTest {
        val question = PageRewriteQuestion()
        question.answer(true)
        question.abandon()
        assertFalse(question.isUp)
    }

    /** A closing document: the change it was asking about belonged to the document that went away. */
    @Test
    fun `the document closing answers Cancel`() = runTest {
        val question = PageRewriteQuestion()
        val asked = async { question.ask() }
        runCurrent()
        question.abandon()
        runCurrent()
        assertFalse(asked.await())
        assertFalse(question.isUp)
    }
}
