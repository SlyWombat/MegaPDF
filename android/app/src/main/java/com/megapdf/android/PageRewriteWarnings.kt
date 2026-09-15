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

/**
 * One shared page check per page, started early, with a time budget (#139 follow-up, #145).
 *
 * The check starts in the background when a page is first shown or a tool that regenerates the
 * page is armed ([prepare]), so its answer is usually in by the time the change comes. At the
 * first such change on a page, [confirm] reuses the running check and waits at most [budgetMs]:
 * - the page keeps its look: settle it and apply;
 * - it would change: ask ([question]) — Continue settles and applies, Cancel applies nothing;
 * - no answer in time: stop the check, settle the page and apply with no warning.
 * Nothing is ever refused. A settled page is never checked or asked about again.
 *
 * Never two questions at once: while one change waits on its check or its question, [isDeciding]
 * is true and the view model blocks further edits; [confirm] refuses a second one outright rather
 * than replacing the first.
 *
 * Call it from one thread (the main thread in the app); the checks themselves run wherever
 * [open]'s check function sends them.
 */
class PageCheckGate(
    private val scope: CoroutineScope,
    private val budgetMs: Long = BUDGET_MS,
) {
    private val warnings = PageRewriteWarnings()
    private val checks = HashMap<Int, Deferred<LayoutVerdict?>>()
    private var check: (suspend (Int) -> LayoutVerdict?)? = null
    private var generation = 0

    /** The warning waiting for Continue (true) or Cancel (false); answered with [answer]. */
    var question: CompletableDeferred<Boolean>? by mutableStateOf(null)
        private set

    /** True while a change waits on its page check or on the person's answer. */
    var isDeciding: Boolean by mutableStateOf(false)
        private set

    /**
     * A document opened: [check] judges a page, returning null when it cannot, and must stop
     * when its coroutine is cancelled.
     */
    fun open(check: suspend (pageIndex: Int) -> LayoutVerdict?) {
        reset()
        this.check = check
    }

    /** The document closed: stop every check and answer any question with Cancel. */
    fun reset() {
        generation++
        checks.values.forEach { it.cancel() }
        checks.clear()
        warnings.reset()
        check = null
        question?.complete(false)
        question = null
        isDeciding = false
    }

    fun isSettled(pageIndex: Int): Boolean = warnings.isSettled(pageIndex)

    /** No check or question for this page again: it passed, Continue was chosen, or a change already went in unasked. */
    fun settle(pageIndex: Int) {
        warnings.settle(pageIndex)
        checks.remove(pageIndex)?.cancel()
    }

    /** Starts [pageIndex]'s check unless it is settled or already started; stops unfinished checks of other pages. */
    fun prepare(pageIndex: Int) {
        startCheck(pageIndex)
    }

    /** Whether a check for [pageIndex] is running or has answered (for tests and diagnostics). */
    fun hasCheck(pageIndex: Int): Boolean = checks.containsKey(pageIndex)

    private fun startCheck(pageIndex: Int): Deferred<LayoutVerdict?>? {
        val run = check ?: return null
        if (warnings.isSettled(pageIndex)) return null
        val others = checks.entries.filter { it.key != pageIndex && !it.value.isCompleted }
        for (entry in others) {
            entry.value.cancel()
            checks.remove(entry.key)
        }
        checks[pageIndex]?.takeIf { !it.isCancelled }?.let { return it }
        val job = scope.async {
            val verdict = try {
                run(pageIndex)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                null
            }
            if (verdict?.editable == true) warnings.settle(pageIndex)
            verdict
        }
        checks[pageIndex] = job
        return job
    }

    /**
     * True when a change that regenerates [pageIndex] may go ahead now, false when it must not be
     * applied: Cancel, the document closed, or another change is still deciding. While the check
     * is awaited, [busy] shows Checking this page… at [spot].
     */
    suspend fun confirm(pageIndex: Int, busy: BusyState? = null, spot: BusySpot? = BusySpot(pageIndex)): Boolean {
        if (warnings.isSettled(pageIndex)) return true
        if (isDeciding) return false
        val started = generation
        isDeciding = true
        try {
            val job = startCheck(pageIndex)
            if (job == null) {
                // No document to judge with; nothing is refused for want of an answer.
                warnings.settle(pageIndex)
                return true
            }
            val token = busy?.beginPage(BusyLabel.CHECKING_PAGE, spot)
            val answer = try {
                withTimeoutOrNull(budgetMs) { Answer(awaitVerdict(job)) }
            } finally {
                token?.end()
            }
            if (generation != started) return false
            val verdict = answer?.verdict
            if (answer == null || verdict == null || verdict.editable) {
                // Over budget (the check stops), not judgeable, or keeps its look: apply unasked.
                settle(pageIndex)
                return true
            }
            val asked = CompletableDeferred<Boolean>()
            question = asked
            val proceed = try {
                asked.await()
            } finally {
                if (question === asked) question = null
            }
            if (generation != started) return false
            if (proceed) settle(pageIndex)
            return proceed
        } finally {
            if (generation == started) isDeciding = false
        }
    }

    fun answer(proceed: Boolean) {
        question?.complete(proceed)
    }

    private class Answer(val verdict: LayoutVerdict?)

    /** The check's verdict; null when it was stopped by someone else (another page's check, a close). */
    private suspend fun awaitVerdict(job: Deferred<LayoutVerdict?>): LayoutVerdict? = try {
        job.await()
    } catch (e: CancellationException) {
        // Our own cancellation (the budget running out) goes on up.
        currentCoroutineContext().ensureActive()
        null
    }

    companion object {
        /** How long a change waits on its page check before it is applied without a warning. */
        const val BUDGET_MS = 1_500L
    }
}
