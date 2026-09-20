package com.megapdf.android

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import com.megapdf.engine.LayoutVerdict
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.async
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withTimeoutOrNull

/**
 * The once-per-page warning before a change the layout guard never judges (#139). Text boxes
 * make PDFium regenerate their page's content just as a body-text edit does, and on a page its
 * writer cannot write back faithfully that changes parts of the page the person never touched.
 * Those changes are never refused: the view model asks the engine before the first one on a
 * page and warns with Continue and Cancel. One instance per viewer; [reset] whenever another
 * document is opened, so a page is asked about at most once per open document.
 */
class PageRewriteWarnings {
    private val settled = mutableSetOf<Int>()

    /** True once the page passed the check or the person chose Continue for it. */
    @Synchronized
    fun isSettled(pageIndex: Int): Boolean = pageIndex in settled

    @Synchronized
    fun settle(pageIndex: Int) {
        settled += pageIndex
    }

    @Synchronized
    fun reset() {
        settled.clear()
    }

    companion object {
        /**
         * Whether [operation] regenerates its page's content without the text guard judging it.
         * Body-text edits and deletes are judged (and refused) on their own; signatures, check
         * marks and form values are annotations and leave the content alone.
         */
        fun regeneratesUnjudged(operation: PdfEditOperation): Boolean =
            operation is TextBoxOperation || operation is EditTextBoxOperation || operation is MoveTextBoxOperation
    }
}

/** What a change waiting on a page check should do (#332). */
enum class PageCheckOutcome {
    /** Apply: the page keeps its look, could not be judged, was settled already, or ran past its budget. */
    APPLY,

    /** Ask first: the page would change. */
    WARN,

    /** Apply nothing: the document closed, or another change is still deciding. */
    ABANDONED,
}

/**
 * One shared page check, started early, with a time budget (#139 follow-up, #145). One *running*
 * check, not one per page: "starting a check for a page stops an unfinished check for another"
 * is then true by construction, and closing the document drops the only record of one, so nothing
 * here keeps a closed document alive (#332).
 *
 * The check starts in the background when a page is first shown or a tool that regenerates the
 * page is armed ([prepare]), so its answer is usually in by the time the change comes. At the
 * first such change on a page, [confirm] reuses the running check and waits at most [budgetMs]:
 * - the page keeps its look: settle it and apply;
 * - it would change: [PageCheckOutcome.WARN] — the caller asks, Continue settles and applies;
 * - no answer in time: stop the check, settle the page and apply with no warning.
 * [confirm] itself never asks anybody anything; the warning and its Continue/Cancel belong to the
 * caller ([PageRewriteQuestion]), which is what "one question at a time" is really about (#332).
 * Nothing is ever refused. A settled page is never checked or asked about again.
 *
 * Call it from one thread (the main thread in the app); the checks themselves run wherever
 * [open]'s check function sends them.
 */
class PageCheckGate(
    private val scope: CoroutineScope,
    private val budgetMs: Long = BUDGET_MS,
) {
    private val warnings = PageRewriteWarnings()
    private var running: Running? = null
    private var check: (suspend (Int) -> LayoutVerdict?)? = null
    private var generation = 0

    /**
     * The page a change is waiting on, if any. A check started for another page leaves it alone:
     * its answer is the one somebody is waiting for, and this page's turn comes later (#332).
     */
    private var awaitedPage: Int? = null

    /** True while a change is inside [confirm]. */
    private var deciding = false

    private class Running(val pageIndex: Int, val job: Deferred<LayoutVerdict?>)

    /**
     * A document opened: [check] judges a page, returning null when it cannot, and must stop
     * when its coroutine is cancelled.
     */
    fun open(check: suspend (pageIndex: Int) -> LayoutVerdict?) {
        reset()
        this.check = check
    }

    /** The document closed: stop the check and forget every page. */
    fun reset() {
        generation++
        running?.job?.cancel()
        running = null
        awaitedPage = null
        warnings.reset()
        check = null
        deciding = false
    }

    fun isSettled(pageIndex: Int): Boolean = warnings.isSettled(pageIndex)

    /**
     * No check for this page again: it passed, Continue was chosen, or a change already went in
     * unasked. A check still running for it is stopped, having nothing left to say.
     */
    fun settle(pageIndex: Int) {
        warnings.settle(pageIndex)
        running?.takeIf { it.pageIndex == pageIndex }?.let {
            running = null
            it.job.cancel()
        }
    }

    /**
     * Starts [pageIndex]'s check unless it is settled or already running; stops an unfinished
     * check of another page, unless a change is waiting on that page's answer, in which case this
     * page waits its turn ([confirm] on the page being waited for always finds its own check).
     */
    fun prepare(pageIndex: Int) {
        start(pageIndex)
    }

    /** Whether a check for [pageIndex] is running (for tests and diagnostics). */
    fun isChecking(pageIndex: Int): Boolean = running?.pageIndex == pageIndex && running?.job?.isCompleted == false

    private fun start(pageIndex: Int): Deferred<LayoutVerdict?>? {
        val run = check ?: return null
        if (warnings.isSettled(pageIndex)) return null
        running?.let { current ->
            if (current.pageIndex == pageIndex) return current.job
            // A change is waiting on that answer; this page's turn comes later (#332).
            if (current.pageIndex == awaitedPage && !current.job.isCompleted) return null
            if (!current.job.isCompleted) {
                running = null
                current.job.cancel()
            }
        }
        val generation = this.generation
        val job = scope.async {
            val verdict = try {
                run(pageIndex)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                null
            }
            // A check whose document has gone settles nothing in the one that came after it (#332).
            if (verdict?.editable == true && generation == this@PageCheckGate.generation) {
                warnings.settle(pageIndex)
            }
            verdict
        }
        running = Running(pageIndex, job)
        return job
    }

    /**
     * What a change that regenerates [pageIndex] should do (#332): [PageCheckOutcome.APPLY] to go
     * ahead, [PageCheckOutcome.WARN] to ask the person first, [PageCheckOutcome.ABANDONED] to apply
     * nothing — the document closed, or another change is still deciding. While the check is
     * awaited, [busy] shows Checking this page… at [spot].
     */
    suspend fun confirm(pageIndex: Int, busy: BusyState? = null, spot: BusySpot? = BusySpot(pageIndex)): PageCheckOutcome {
        if (warnings.isSettled(pageIndex)) return PageCheckOutcome.APPLY
        if (deciding) return PageCheckOutcome.ABANDONED
        val started = generation
        deciding = true
        awaitedPage = pageIndex
        try {
            val job = start(pageIndex)
            if (job == null) {
                // No document to judge with; nothing is refused for want of an answer.
                warnings.settle(pageIndex)
                return PageCheckOutcome.APPLY
            }
            val token = busy?.beginPage(BusyLabel.CHECKING_PAGE, spot)
            val waited: LayoutVerdict? = try {
                withTimeoutOrNull(budgetMs) { awaitVerdict(job) }
            } finally {
                token?.end()
            }
            if (generation != started) return PageCheckOutcome.ABANDONED
            // The budget's expiry ends the wait; the check's answer is read afterwards, so one that
            // landed in the same breath as the budget is used rather than thrown away (#332). C#
            // and Swift decide it the same way, and the desktop test pins the rule for all three.
            val verdict = waited ?: verdictIfAnswered(job)
            if (verdict == null || verdict.editable) {
                // Past the budget or stopped by someone else (either way the check stops here),
                // not judgeable, or keeps its look: apply unasked.
                settle(pageIndex)
                return PageCheckOutcome.APPLY
            }
            return PageCheckOutcome.WARN
        } finally {
            if (generation == started) {
                deciding = false
                awaitedPage = null
            }
        }
    }

    companion object {
        /** How long a change waits on its page check before it is applied without a warning. */
        const val BUDGET_MS = 1_500L
    }
}

/**
 * The verdict of a check that has finished, or null when it has nothing to give: still running, or
 * stopped by someone else. The budget's expiry only ends the *wait* — the answer is read from the
 * check afterwards, so one that landed in the same breath as the budget counts (#332).
 */
internal fun verdictIfAnswered(job: Deferred<LayoutVerdict?>): LayoutVerdict? =
    if (job.isCompleted && !job.isCancelled) job.getCompleted() else null

/** The check's verdict; null when it was stopped by someone else (another page's check, a close). */
private suspend fun awaitVerdict(job: Deferred<LayoutVerdict?>): LayoutVerdict? = try {
    job.await()
} catch (e: CancellationException) {
    // Our own cancellation (the budget running out) goes on up.
    currentCoroutineContext().ensureActive()
    null
}

/**
 * The warning a change puts up before it regenerates a page that would change, and the person's
 * answer to it (#139, #332). The gate never asks; the change asks this, which is why "never two
 * questions at once" is the view model's to keep.
 */
class PageRewriteQuestion {
    private var pending: CompletableDeferred<Boolean>? by mutableStateOf(null)

    /** True while the warning is on screen. */
    val isUp: Boolean get() = pending != null

    /** Puts the warning up and waits: true Continue, false Cancel. */
    suspend fun ask(): Boolean {
        val asked = CompletableDeferred<Boolean>()
        pending = asked
        return try {
            asked.await()
        } finally {
            if (pending === asked) pending = null
        }
    }

    /** Continue or Cancel, from the screen. Does nothing when no warning is up. */
    fun answer(proceed: Boolean) {
        pending?.complete(proceed)
    }

    /** The document closed: a warning still up is answered Cancel. */
    fun abandon() {
        pending?.complete(false)
    }
}
