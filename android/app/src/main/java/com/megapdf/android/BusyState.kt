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

    internal fun begin(label: BusyLabel, spot: BusySpot?, onEnd: () -> Unit): BusyToken {
        val token = BusyToken(this, label, spot, onEnd)
        active += token
        update()
        return token
    }

    internal fun changed() = update()

    internal fun ended(token: BusyToken) {
        active.remove(token)
        update()
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
    }

    private fun update() {
        val latest = active.lastOrNull()
        if (latest != null) {
            isActive = true
            label = latest.label
            spot = latest.spot
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
        showJob?.cancel()
        showJob = null
        if (!isVisible) {
            label = null
            spot = null
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
    }
}

/** One piece of busy work; [end] it in a `finally`. Ending twice, or after a reset, does nothing. */
class BusyToken internal constructor(
    private var owner: BusyIndicator?,
    label: BusyLabel,
    spot: BusySpot?,
    private var onEnd: (() -> Unit)?,
) {
    var label: BusyLabel = label
        private set
    var spot: BusySpot? = spot
        private set

    /** The same work moving on to its next step, e.g. Saving… to Checking the saved file…. */
    fun relabel(label: BusyLabel, spot: BusySpot? = this.spot) {
        this.label = label
        this.spot = spot
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

    /** Document-level work; [locks] for a save or a password change. */
    fun beginDocument(label: BusyLabel, locks: Boolean = false): BusyToken {
        if (locks) {
            this.locks++
            locksDocument = true
        }
        return document.begin(label, null) {
            if (locks) {
                this.locks--
                locksDocument = this.locks > 0
            }
        }
    }

    /** Page-level work, shown at [spot]. */
    fun beginPage(label: BusyLabel, spot: BusySpot?): BusyToken = page.begin(label, spot) {}

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
