package com.megapdf.android

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import com.megapdf.engine.PdfRect
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** What the app is busy with (#145); each has its label in the string catalogue. */
enum class BusyLabel(val stringId: Int) {
    OPENING(R.string.busy_opening),
    SAVING(R.string.saving),
    VERIFYING_SAVE(R.string.busy_verifying_save),
    SEARCHING(R.string.busy_searching),
    CHECKING_PAGE(R.string.busy_checking_page),
    APPLYING(R.string.busy_applying),
    /** Exporting the document's text as Markdown (#386) — its own label, not [SAVING]: a
     * Markdown export is not a save of the document (see [ViewerViewModel.exportMarkdown]). */
    EXPORTING(R.string.busy_exporting),
    // The page tools (#174), named for what each one does rather than the generic APPLYING
    // (#145): the Mac and Windows passes found that reporting a structure operation nowhere
    // at all — a page spinner pinned to a page the operation itself removes — was the real
    // defect, and gave each one its own strip label while they were at it. Android's page
    // tools already report in the Pages screen's document strip (they run through
    // [ViewerViewModel.performPageEdit], never through a page-pinned spinner), so the defect
    // itself is not present here — but a phone's storage is slower than a workstation's, and
    // a generic "Applying…" on a device slow enough to show it at all is the same vagueness
    // the desktops fixed, so these exist for the same reason.
    TURNING_PAGES(R.string.busy_turning_pages),
    DELETING_PAGES(R.string.busy_deleting_pages),
    MOVING_PAGE(R.string.busy_moving_page),
    INSERTING_PAGE(R.string.busy_inserting_page),
    ADDING_PAGES(R.string.busy_adding_pages),
}

/** Where page-level work shows its spinner: a page, and the line on it when there is one. */
data class BusySpot(val pageIndex: Int, val rect: PdfRect? = null)

/**
 * One busy indicator (#145): the strip under the top bar, or the spinner on a page.
 *
 * [isActive] turns on the moment work begins, so the control used can be disabled and repeat
 * taps ignored at once. [isVisible] turns on only once work has run for [showAfterMs], so quick
 * work never flickers an indicator, and once on it stays at least [minShownMs], so it never
 * blinks. While several pieces of work overlap, the latest one's [label] and [spot] show.
 *
 * [progressDone]/[progressTotal] and [canCancel]/[isCancelling] are the P2 half (#145): a count
 * for the operations that have an honest one (search), and a way to ask the newest running piece
 * of work to stop (search, extract) — both plain numbers and a callback, with no wording of their
 * own, because the count's noun ("page" vs "picture") and the words around Stop belong to the
 * app, not to this shared layer. [requestCancel] is offered only while something is genuinely
 * running: once the last piece of work ends there is nothing left to stop, so [canCancel] goes
 * false at once while [progressDone]/[progressTotal] hold their last value until the indicator
 * actually hides — a bar or a count dropping out for the final [minShownMs] would be exactly the
 * flicker this class exists to prevent.
 */
class BusyIndicator internal constructor(
    private val scope: CoroutineScope,
    private val now: () -> Long,
    private val showAfterMs: Long,
    private val minShownMs: Long,
) {
    private val active = ArrayList<BusyToken>()
    private var showJob: Job? = null
    private var hideJob: Job? = null
    private var shownAt = 0L

    var isActive: Boolean by mutableStateOf(false)
        private set
    var isVisible: Boolean by mutableStateOf(false)
        private set
    var label: BusyLabel? by mutableStateOf(null)
        private set
    var spot: BusySpot? by mutableStateOf(null)
        private set
    var progressDone: Int? by mutableStateOf(null)
        private set
    var progressTotal: Int? by mutableStateOf(null)
        private set
    var canCancel: Boolean by mutableStateOf(false)
        private set
    var isCancelling: Boolean by mutableStateOf(false)
        private set

    internal fun begin(label: BusyLabel, spot: BusySpot?, cancel: (() -> Unit)?, onEnd: () -> Unit): BusyToken {
        val token = BusyToken(this, label, spot, cancel, onEnd)
        active += token
        update()
        return token
    }

    internal fun changed() = update()

    internal fun ended(token: BusyToken) {
        active.remove(token)
        update()
    }

    /** Asks the newest running piece of work to stop; does nothing if it cannot, or already was. */
    fun requestCancel() {
        val latest = active.lastOrNull() ?: return
        val cancel = latest.cancel ?: return
        if (latest.isCancelling) return
        latest.isCancelling = true
        update()
        cancel.invoke()
    }

    /** Drops every piece of work at once, without waiting out the minimum: its document is gone. */
    internal fun reset() {
        active.forEach { it.detach() }
        active.clear()
        showJob?.cancel()
        showJob = null
        hideJob?.cancel()
        hideJob = null
        isActive = false
        isVisible = false
        label = null
        spot = null
        progressDone = null
        progressTotal = null
        canCancel = false
        isCancelling = false
    }

    private fun update() {
        val latest = active.lastOrNull()
        if (latest != null) {
            isActive = true
            label = latest.label
            spot = latest.spot
            progressDone = latest.progressDone
            progressTotal = latest.progressTotal
            canCancel = latest.cancel != null && !latest.isCancelling
            isCancelling = latest.isCancelling
            hideJob?.cancel()
            hideJob = null
            if (!isVisible && showJob == null) {
                showJob = scope.launch {
                    delay(showAfterMs)
                    showJob = null
                    isVisible = true
                    shownAt = now()
                }
            }
            return
        }
        isActive = false
        // Nothing is running any more, so there is nothing left to stop — unlike the label and
        // the count, which stay up with their last value for as long as the indicator itself does.
        canCancel = false
        isCancelling = false
        showJob?.cancel()
        showJob = null
        if (!isVisible) {
            label = null
            spot = null
            progressDone = null
            progressTotal = null
        } else if (hideJob == null) {
            val remaining = minShownMs - (now() - shownAt)
            if (remaining <= 0) {
                hide()
            } else {
                hideJob = scope.launch {
                    delay(remaining)
                    hideJob = null
                    hide()
                }
            }
        }
    }

    private fun hide() {
        if (active.isNotEmpty()) return
        isVisible = false
        label = null
        spot = null
        progressDone = null
        progressTotal = null
    }
}

/** One piece of busy work; [end] it in a `finally`. Ending twice, or after a reset, does nothing. */
class BusyToken internal constructor(
    private var owner: BusyIndicator?,
    label: BusyLabel,
    spot: BusySpot?,
    internal val cancel: (() -> Unit)?,
    private var onEnd: (() -> Unit)?,
) {
    var label: BusyLabel = label
        private set
    var spot: BusySpot? = spot
        private set
    var progressDone: Int? = null
        private set
    var progressTotal: Int? = null
        private set

    /** Set by the indicator once [BusyIndicator.requestCancel] has asked this token to stop. */
    internal var isCancelling: Boolean = false

    /** The same work moving on to its next step, e.g. Saving… to Checking the saved file…. */
    fun relabel(label: BusyLabel, spot: BusySpot? = this.spot) {
        this.label = label
        this.spot = spot
        owner?.changed()
    }

    /**
     * How much of a cancellable, per-item loop is done (#145): a search counts pages. Call it
     * after each item, not before — "page 1 of N" while page 1 is still being read would be a
     * count that finishes before the work does.
     */
    fun report(done: Int, total: Int) {
        progressDone = done
        progressTotal = total
        owner?.changed()
    }

    fun end() {
        val indicator = owner ?: return
        owner = null
        onEnd?.invoke()
        onEnd = null
        indicator.ended(this)
    }

    internal fun detach() {
        owner = null
        onEnd = null
    }
}

/**
 * The busy state of the open document (#145): one strip for document-level work (open, save,
 * search) and one spinner for page-level work (the page check, the text-edit check, applying a
 * change). The view model resets it whenever a document opens or closes.
 *
 * Call it from one thread (the main thread in the app). [now] and the delays are injectable so
 * the timing is tested on a virtual clock.
 */
class BusyState(
    scope: CoroutineScope,
    now: () -> Long = { System.nanoTime() / 1_000_000 },
    showAfterMs: Long = SHOW_AFTER_MS,
    minShownMs: Long = MIN_SHOWN_MS,
) {
    /** The strip under the top app bar. */
    val document = BusyIndicator(scope, now, showAfterMs, minShownMs)

    /** The small spinner on a page or a line. */
    val page = BusyIndicator(scope, now, showAfterMs, minShownMs)

    private var locks = 0

    /**
     * True while a save or a password change runs: editing, Close and Back, and the file
     * commands are disabled until it ends.
     */
    var locksDocument: Boolean by mutableStateOf(false)
        private set

    /**
     * Document-level work; [locks] for a save or a password change, [cancel] when the newest
     * running operation can be asked to stop (search, extract) — omitted, as for a save or a
     * combine, [BusyIndicator.canCancel] simply never turns on for this piece of work.
     */
    fun beginDocument(label: BusyLabel, locks: Boolean = false, cancel: (() -> Unit)? = null): BusyToken {
        if (locks) {
            this.locks++
            locksDocument = true
        }
        return document.begin(label, null, cancel) {
            if (locks) {
                this.locks--
                locksDocument = this.locks > 0
            }
        }
    }

    /** Page-level work, shown at [spot]. */
    fun beginPage(label: BusyLabel, spot: BusySpot?): BusyToken = page.begin(label, spot, cancel = null) {}

    /** Page-level [block], with its spinner. */
    suspend fun <T> pageWork(label: BusyLabel, spot: BusySpot?, block: suspend () -> T): T {
        val token = beginPage(label, spot)
        try {
            return block()
        } finally {
            token.end()
        }
    }

    /** Forgets all work: another document is opening, or this one closed. */
    fun reset() {
        document.reset()
        page.reset()
        locks = 0
        locksDocument = false
    }

    companion object {
        /** Work quicker than this shows no indicator at all. */
        const val SHOW_AFTER_MS = 500L

        /** An indicator that appeared stays at least this long. */
        const val MIN_SHOWN_MS = 300L
    }
}
