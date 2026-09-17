package com.megapdf.android

import com.megapdf.engine.TextEditOutcome
import android.app.Application
import android.graphics.Bitmap
import android.net.Uri
import android.provider.OpenableColumns
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.megapdf.engine.LayoutCause
import com.megapdf.engine.PageCheck
import com.megapdf.engine.PdfDocument
import com.megapdf.engine.PdfEngine
import com.megapdf.engine.PdfLoadException
import com.megapdf.engine.PdfPasswordException
import com.megapdf.engine.PdfPermissions
import com.megapdf.engine.PdfSecurity
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

/** Width/height of a page in PDF points (1/72 inch). */
data class PageSize(val widthPoints: Double, val heightPoints: Double)

/** A stamp currently selected for move/resize/remove. */
data class SelectedStamp(
    val pageIndex: Int,
    val annotIndex: Int,
    val id: String,
    val rect: com.megapdf.engine.PdfRect,
)

/** One document-wide search hit: page plus highlight rects in page points. */
data class SearchHit(
    val pageIndex: Int,
    val rects: List<com.megapdf.engine.PdfRect>,
)

/** A text box currently selected for drag/correct/remove (#36). */
data class SelectedTextBox(
    val pageIndex: Int,
    val id: String,
    val text: String,
    val fontSize: Double,
    val fontName: String,
    val rect: com.megapdf.engine.PdfRect,
)

/**
 * A tap that is waiting for the text the user is about to type (#34). When
 * [editingId] is set the tap re-opened an existing box to correct it (#36), and
 * ([x], [y]) is that box's bounds lower-left rather than the raw tap point.
 */
data class PendingTextTap(
    val pageIndex: Int,
    val x: Double,
    val y: Double,
    val editingId: String? = null,
    val fontSize: Double = DEFAULT_FONT_SIZE,
    val fontName: String = com.megapdf.engine.DEFAULT_FONT,
    val initialText: String = "",
)

/**
 * Sizes offered for added text (#43). A short list, not a free-entry number box:
 * the job is "match the form I am filling in", and six presets cover it.
 */
val TEXT_SIZES = listOf(8.0, 10.0, 12.0, 14.0, 18.0, 24.0)

/** What a new box starts at, before the user has chosen anything this session. */
const val DEFAULT_FONT_SIZE = 12.0

sealed interface ViewerUiState {
    data class Home(val recents: List<RecentEntry>, val error: String? = null) : ViewerUiState
    data object Loading : ViewerUiState
    data class PasswordNeeded(val uri: Uri, val wrongPassword: Boolean) : ViewerUiState
    data class Viewing(val displayName: String, val pageSizes: List<PageSize>) : ViewerUiState
}

/**
 * The owner-password dialog's state (#131). [attempt] counts wrong passwords so the field
 * empties after each; the password itself is never kept here.
 */
data class UnlockPrompt(
    val attempt: Int = 0,
    val wrongPassword: Boolean = false,
    /** True while the typed password is being tried. */
    val isChecking: Boolean = false,
)

/**
 * Owns the engine and the open document; renders a ±[RENDER_MARGIN]-page window
 * around the visible pages at the requested pixel width — the mobile port of the
 * desktop `MainViewModel` render-window virtualization. Bitmaps outside the
 * window are dropped so a long document never holds more than ~5 page bitmaps.
 */
class ViewerViewModel(application: Application) : AndroidViewModel(application) {

    private val engine = PdfEngine()
    private var document: PdfDocument? = null
    // Observable so the Password command can enable itself on it (#131).
    private var currentUri: Uri? by mutableStateOf(null)

    /**
     * The uri whose file the open document reads on demand (#147), or null when it reads a
     * private copy or nothing. Writing that file in place would change what the document
     * reads, so a save to it moves the document onto a copy first ([keepDocumentOffFile]).
     */
    private var documentReadsUri: Uri? = null
    private val recentsStore =
        RecentFilesStore(File(application.filesDir, "recent.json"))
    private val signatureStore =
        SignatureLibraryStore(File(application.filesDir, "signatures"))

    /** Signature library entries, newest last; backs the Sign dialog. */
    val signatures = androidx.compose.runtime.mutableStateListOf<SignatureEntry>().apply {
        addAll(signatureStore.load())
    }

    /** Non-null while waiting for the user to tap a placement spot. */
    var pendingSignature: SignatureEntry? by mutableStateOf(null)
        private set

    private val history = EditHistory()

    // --- Redaction (SDD §3.8 / F7, #173) ---
    //
    // A mark is the core's own and is never written to the file, so nothing here changes
    // the document: the screen draws the marks, and applying is a separate, confirmed step
    // taken on save.

    /** The Redact tool is armed: the next drag across a page marks an area. */
    var redactMode: Boolean by mutableStateOf(false)
        private set

    /** Every mark on the document, by page, for the overlay that draws them. */
    var redactionMarks: Map<Int, List<com.megapdf.engine.RedactionMark>> by mutableStateOf(emptyMap())
        private set

    /** How many areas are marked: what the save confirmation asks before it offers a copy. */
    val redactionMarkCount: Int get() = redactionMarks.values.sumOf { it.size }

    /** The summary after a redaction, shown once and dismissed. */
    var redactionSummary: String? by mutableStateOf(null)

    /** Why a redaction refused, shown once and dismissed. Nothing was removed. */
    var redactionRefusal: com.megapdf.engine.RedactionRefusal? by mutableStateOf(null)

    fun toggleRedactMode() {
        redactMode = !redactMode
        if (redactMode) {
            pendingSignature = null
            isPlacingText = false
        }
    }

    fun cancelRedactMode() {
        redactMode = false
    }

    /**
     * Marks the dragged area. A drag across text marks the text, grown to whole glyphs, so
     * half a glyph is never left behind; a drag across a picture marks the rectangle.
     * Nothing is removed, and nothing on the page changes.
     */
    fun markForRedaction(pageIndex: Int, rect: com.megapdf.engine.PdfRect) {
        val doc = document ?: return
        viewModelScope.launch {
            doc.onPageForRedaction(pageIndex) { page ->
                if (page.markTextForRedaction(rect) == 0) page.markForRedaction(rect)
            }
            refreshRedactionMarks()
            redactMode = false
        }
    }

    fun removeRedactionMark(pageIndex: Int, markId: Int) {
        val doc = document ?: return
        viewModelScope.launch {
            doc.onPageForRedaction(pageIndex) { it.removeRedactionMark(markId) }
            refreshRedactionMarks()
        }
    }

    private suspend fun <T> PdfDocument.onPageForRedaction(
        index: Int,
        body: suspend (com.megapdf.engine.PdfPage) -> T,
    ): T {
        val page = openPage(index)
        try {
            return body(page)
        } finally {
            page.close()
        }
    }

    private suspend fun refreshRedactionMarks() {
        val doc = document ?: return
        val byPage = mutableMapOf<Int, List<com.megapdf.engine.RedactionMark>>()
        for (index in 0 until doc.pageCount()) {
            val marks = doc.onPageForRedaction(index) { it.redactionMarks() }
            if (marks.isNotEmpty()) byPage[index] = marks
        }
        redactionMarks = byPage
    }

    /**
     * Applies every mark. True when the document was redacted and may be saved; false when
     * it refused — and then NOTHING was removed, the document is as it was, and the marks
     * are still on it.
     */
    suspend fun applyRedactions(): Boolean {
        val doc = document ?: return true
        if (redactionMarkCount == 0) return true
        val report = doc.applyRedactions()
        if (!report.applied) {
            redactionRefusal = report.refusals.firstOrNull()
                ?: com.megapdf.engine.RedactionRefusal(0, com.megapdf.engine.RedactionRefusalReason.ENGINE)
            refreshRedactionMarks()
            return false
        }
        // Undo cannot put the removed content back: the core freed the objects the history
        // was holding for exactly that.
        history.clear()
        canUndo = false
        canRedo = false
        redactionMarks = emptyMap()
        redactionSummary = describeRedaction(report.counts)
        renderedWidths.clear()
        pageBitmaps.clear()
        lastWindow?.let { (first, last, width) -> updateRenderWindow(first, last, width) }
        return true
    }

    private fun describeRedaction(counts: com.megapdf.engine.RedactionCounts): String {
        val context = getApplication<Application>()
        val removed = com.megapdf.engine.RedactionSummary.removed(
            counts,
            plural = { kind, n -> context.getString(pluralRes(kind), n.toString()) },
            singular = { kind -> context.getString(singularRes(kind)) },
            nothing = context.getString(R.string.redacted_nothing),
        )
        return if (counts.areas == 1) context.getString(R.string.redact_summary_one, removed)
        else context.getString(R.string.redact_summary_many, counts.areas.toString(), removed)
    }

    private fun pluralRes(kind: com.megapdf.engine.RedactionSummary.Kind) = when (kind) {
        com.megapdf.engine.RedactionSummary.Kind.CHARACTERS -> R.string.redacted_characters
        com.megapdf.engine.RedactionSummary.Kind.IMAGES -> R.string.redacted_images
        com.megapdf.engine.RedactionSummary.Kind.FORM_FIELDS -> R.string.redacted_form_fields
        com.megapdf.engine.RedactionSummary.Kind.ANNOTATIONS -> R.string.redacted_annotations
    }

    private fun singularRes(kind: com.megapdf.engine.RedactionSummary.Kind) = when (kind) {
        com.megapdf.engine.RedactionSummary.Kind.CHARACTERS -> R.string.redacted_characters_one
        com.megapdf.engine.RedactionSummary.Kind.IMAGES -> R.string.redacted_images_one
        com.megapdf.engine.RedactionSummary.Kind.FORM_FIELDS -> R.string.redacted_form_fields_one
        com.megapdf.engine.RedactionSummary.Kind.ANNOTATIONS -> R.string.redacted_annotations_one
    }

    /** What the refusal says, in the user's words rather than the engine's. */
    fun describeRefusal(refusal: com.megapdf.engine.RedactionRefusal): String {
        val context = getApplication<Application>()
        val why = when (refusal.reason) {
            com.megapdf.engine.RedactionRefusalReason.TYPE3_FONT,
            com.megapdf.engine.RedactionRefusalReason.FONT_CANNOT_REDRAW ->
                context.getString(R.string.redact_refused_font)
            com.megapdf.engine.RedactionRefusalReason.FORM_XOBJECT ->
                context.getString(R.string.redact_refused_shared)
            com.megapdf.engine.RedactionRefusalReason.LAYOUT_GUARD ->
                context.getString(R.string.redact_refused_layout)
            else -> context.getString(R.string.redact_refused_other)
        }
        return context.getString(R.string.redact_refused_body, (refusal.pageIndex + 1).toString()) + " " + why
    }

    /** Undo/redo availability (#34) — mirrored out of the history for the toolbar. */
    var canUndo: Boolean by mutableStateOf(false)
        private set
    var canRedo: Boolean by mutableStateOf(false)
        private set

    /** True between "Add text" and the tap that says where it goes. */
    var isPlacingText: Boolean by mutableStateOf(false)
        private set

    /**
     * The size and face the last box was given (#43). Sticky for the session, so
     * filling six fields on one form is not six trips through the pickers. Not
     * persisted: a new document is usually a new job.
     */
    private var lastFontSize = DEFAULT_FONT_SIZE
    private var lastFontName = com.megapdf.engine.DEFAULT_FONT

    /** Set by that tap; the screen shows the text field for it. */
    var pendingTextTap: PendingTextTap? by mutableStateOf(null)
        private set

    /** The line of the document's own text the editor is open on (#114). */
    var pendingBodyEdit: PendingBodyEdit? by mutableStateOf(null)
        private set

    /** A one-line notice over the page that clears itself — not a dialog (#114). */
    var notice: String? by mutableStateOf(null)
        private set
    private var noticeJob: Job? = null

    /** The scanned-page hint is shown once per document, not on every stray tap. */
    private var scannedHintShown = false

    /** Screenshot-mode sheet request ("sign" | "draw" | "search" | "text"); set via launch intent. */
    var screenshotSheet: String? by mutableStateOf(null)
        private set

    // --- Busy feedback (#145) ---

    /**
     * The open document's busy state: the strip under the top bar for opening, saving and
     * searching, the spinner on a page for the page checks and applying a change, and the lock
     * that keeps editing, Close and the file commands out while a save or a password change runs.
     * Reset whenever a document opens or closes.
     */
    val busy = BusyState(viewModelScope)

    /** The shared page checks and the warning before a change regenerating a page would alter (#139). */
    private val pageChecks = PageCheckGate(viewModelScope)

    /** Edits started and not yet finished: a tap or commit while one runs is ignored (#145). */
    private var editsInFlight by mutableIntStateOf(0)

    /**
     * True while a change is still going in, waiting on its page check or its warning, or a save
     * or password change runs. Further edits are blocked, never queued or dropped (#145).
     */
    val editingBlocked: Boolean
        get() = editsInFlight > 0 || pageChecks.isDeciding || busy.locksDocument

    /**
     * Whether the toolbar's editing tools show disabled: during a save or password change, and
     * once page work has run long enough to show its spinner. Quicker page work only ignores
     * taps, so the toolbar doesn't flicker on every checkbox.
     */
    val toolsDisabled: Boolean
        get() = busy.locksDocument || busy.page.isVisible

    /** The page the list reports as current; Add text checks it early (#145). */
    private var currentPage = 0

    /**
     * Marketing screenshot mode (mirrors iOS `-screenshot`): seeds the "Mega W."
     * demo signature and opens the bundled demo agreement in the requested UI
     * state. Never active in normal launches.
     */
    fun applyScreenshotMode(state: String?) {
        if (state == null) return
        val app = getApplication<Application>()
        if (signatures.isEmpty()) {
            runCatching {
                app.assets.open("demo-signature.png").use { input ->
                    android.graphics.BitmapFactory.decodeStream(input)
                }
            }.getOrNull()?.let { bmp ->
                signatureStore.add(app.getString(R.string.screenshot_signature_name), bmp)
                signatures.clear()
                signatures.addAll(signatureStore.load())
            }
        }
        when (state) {
            "home" -> {
                val now = System.currentTimeMillis()
                val day = 86_400_000L
                uiState = ViewerUiState.Home(listOf(
                    RecentEntry("demo://1", app.getString(R.string.screenshot_document_name), now - day / 2),
                    RecentEntry("demo://2", app.getString(R.string.screenshot_recent_2), now - 2 * day),
                    RecentEntry("demo://3", app.getString(R.string.screenshot_recent_3), now - 6 * day),
                ), null)
            }
            "viewer", "sign", "draw", "search", "text", "text-edit" -> {
                screenshotSheet = if (state == "viewer") null else state
                viewModelScope.launch {
                    try {
                        val bytes = withContext(Dispatchers.IO) {
                            app.assets.open(app.getString(R.string.screenshot_demo_asset)).use { it.readBytes() }
                        }
                        val doc = engine.open(bytes)
                        val count = doc.pageCount()
                        val sizes = ArrayList<PageSize>(count)
                        for (i in 0 until count) {
                            val page = doc.openPage(i)
                            sizes += PageSize(page.widthPoints, page.heightPoints)
                            page.close()
                        }
                        val security = doc.security()
                        closeCurrent()
                        attach(doc, security)
                        uiState = ViewerUiState.Viewing(app.getString(R.string.screenshot_document_name), sizes)
                        if (state == "search") {
                            // Seed here, not from the UI: the document and the
                            // Viewing state are both already set, so the sweep can
                            // never hit updateSearchQuery's "nothing open" early
                            // return, and the debounce is skipped so the hits and
                            // the "N of M" count are on screen without any wait.
                            startSearch(app.getString(R.string.screenshot_search_term), debounceMs = 0L)
                        }
                        if (state == "text") {
                            // The Add text dialog, open on a typed name with the size
                            // and face pickers showing (#43). Armed here rather than
                            // through onPageTapped because the tap point is chosen,
                            // not synthesised: just under the signature rule, where a
                            // printed name belongs on this agreement.
                            pendingTextTap = PendingTextTap(
                                0, SCREENSHOT_TEXT_X, SCREENSHOT_TEXT_Y,
                                initialText = app.getString(R.string.screenshot_text))
                        }
                        if (state == "text-edit") {
                            // The body-text editor open on the agreement's heading, mid-correction (#114).
                            val page = doc.openPage(0)
                            try {
                                page.textLines().firstOrNull()?.let {
                                    pendingBodyEdit = PendingBodyEdit(
                                        0, it, initialText = app.getString(R.string.screenshot_edited_heading))
                                }
                            } finally {
                                page.close()
                            }
                        }
                    } catch (e: CancellationException) {
                        throw e
                    } catch (e: Exception) {
                        statusMessage = str(R.string.open_failed)
                    }
                }
            }
        }
    }

    /** The stamp currently selected for move/resize/remove. */
    var selectedStamp: SelectedStamp? by mutableStateOf(null)
        private set

    /** The text box currently selected for drag/correct/remove (#36). */
    var selectedTextBox: SelectedTextBox? by mutableStateOf(null)
        private set

    /**
     * The warning on screen before the first text-box change on a page PDFium's rewrite would
     * alter (#139). The screen answers it with [answerPageRewrite]: Continue or Cancel.
     */
    val pageRewriteQuestion: CompletableDeferred<Boolean>?
        get() = pageChecks.question

    fun answerPageRewrite(proceed: Boolean) {
        pageChecks.answer(proceed)
    }

    /**
     * True when [operation] may go ahead: it leaves the page's content alone, the page was
     * already settled in this document, the page keeps its look when regenerated, its check
     * ran over budget, or the person chose Continue (#139, #145). The check started early is
     * reused; while it is awaited the spinner shows at [spot]. A restricted document is left to
     * [perform] to refuse.
     */
    private suspend fun confirmPageRewrite(
        operation: PdfEditOperation, doc: PdfDocument, spot: BusySpot? = null,
    ): Boolean {
        if (!PageRewriteWarnings.regeneratesUnjudged(operation) || !capabilities.allows(operation)) return true
        val pageIndex = operation.pageIndex
        val proceed = pageChecks.confirm(pageIndex, busy, spot ?: BusySpot(pageIndex))
        // The document may have been closed while the check ran or the question was up.
        return proceed && document === doc
    }

    /** Starts the page's check in the background, when the open document allows the changes it guards (#145). */
    private fun preparePageCheck(pageIndex: Int) {
        val state = uiState as? ViewerUiState.Viewing ?: return
        if (document == null || pageIndex !in state.pageSizes.indices) return
        if (!capabilities.canAddText) return
        pageChecks.prepare(pageIndex)
    }

    /** The page list's current page changed: check it early (#145). */
    fun onCurrentPageChanged(pageIndex: Int) {
        currentPage = pageIndex
        preparePageCheck(pageIndex)
    }

    /**
     * Runs one edit (#145): ignored while another is still going in, the page check or its
     * warning is deciding, or a save runs; any failure is reported as [failure] rather than
     * crashing.
     */
    private fun launchEdit(failure: Int, block: suspend () -> Unit) {
        if (editingBlocked) return
        editsInFlight++
        viewModelScope.launch {
            try {
                block()
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                statusMessage = str(failure)
            } finally {
                editsInFlight--
            }
        }
    }

    var uiState: ViewerUiState by mutableStateOf(ViewerUiState.Home(recentsStore.load()))
        private set

    /** Rendered page bitmaps, keyed by page index; observed by the page list UI. */
    val pageBitmaps = mutableStateMapOf<Int, Bitmap>()

    private var renderJob: Job? = null
    private val renderedWidths = HashMap<Int, Int>()
    private var lastWindow: RenderWindow? = null

    /** Unsaved changes, and D3 of #145: a save marks saved only if nothing changed while it ran. */
    private val dirty = DirtyTracker()

    /** True once the in-memory document differs from the file on disk. */
    val isDirty: Boolean get() = dirty.isDirty

    /** True while a save is streaming to the destination. */
    var isSaving: Boolean by mutableStateOf(false)
        private set

    /** One-shot user-facing status ("Saved", errors); cleared by [consumeStatus]. */
    var statusMessage: String? by mutableStateOf(null)
        private set

    fun consumeStatus() {
        statusMessage = null
    }

    // --- Document security (#131) ---

    /** What the open document's security lets the user do; everything when nothing is open. */
    var capabilities: DocumentCapabilities by mutableStateOf(DocumentCapabilities.FULL)
        private set

    /** Whether the open document has a file to write back to, which the Password command needs. */
    val hasDocumentFile: Boolean get() = currentUri != null

    /** Non-null while the owner-password dialog is up. */
    var unlockPrompt: UnlockPrompt? by mutableStateOf(null)
        private set

    /** Non-null while the Password command's dialog is up. */
    var passwordPrompt: PasswordCommandMode? by mutableStateOf(null)
        private set

    // --- Text search (#26) ---

    /** Current search query; matches update as it changes (small debounce). */
    var searchQuery: String by mutableStateOf("")
        private set

    /** All hits across the document, in page-then-reading order. */
    var searchHits: List<SearchHit> by mutableStateOf(emptyList())
        private set

    /** Index into [searchHits] of the current match; -1 when there are none. */
    var currentHitIndex: Int by mutableIntStateOf(-1)
        private set

    /** True from the first keystroke until that query's results are in. */
    var isSearching: Boolean by mutableStateOf(false)
        private set

    private var searchJob: Job? = null

    /** As-you-type search from the search bar; debounced against fast typing. */
    fun updateSearchQuery(query: String) = startSearch(query, SEARCH_DEBOUNCE_MS)

    /**
     * The search itself: wait out [debounceMs], then sweep every page on the
     * engine thread and aggregate hits into one flat document-ordered list.
     * Case-insensitive literal substring — the cross-platform contract.
     * Screenshot mode passes a zero debounce for its one deliberate query.
     * The sweep shows Searching… in the strip (#145) but never blocks editing.
     */
    private fun startSearch(query: String, debounceMs: Long) {
        searchQuery = query
        searchJob?.cancel()
        searchHits = emptyList()
        currentHitIndex = -1
        val state = uiState as? ViewerUiState.Viewing
        val doc = document
        if (query.isEmpty() || state == null || doc == null) {
            isSearching = false
            return
        }
        isSearching = true
        searchJob = viewModelScope.launch {
            var token: BusyToken? = null
            try {
                if (debounceMs > 0) delay(debounceMs)
                token = busy.beginDocument(BusyLabel.SEARCHING)
                val hits = ArrayList<SearchHit>()
                for (pageIndex in state.pageSizes.indices) {
                    val page = doc.openPage(pageIndex)
                    try {
                        page.search(query).forEach { hits += SearchHit(pageIndex, it.rects) }
                    } finally {
                        page.close()
                    }
                }
                searchHits = hits
                currentHitIndex = if (hits.isEmpty()) -1 else 0
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                statusMessage = str(R.string.search_failed)
            } finally {
                token?.end()
                // A superseding query has already reset the flag for itself;
                // only the query still on screen may clear it.
                if (searchQuery == query) isSearching = false
            }
        }
    }

    /** Next match; wraps past the last hit back to the first. */
    fun nextSearchHit() {
        if (searchHits.isEmpty()) return
        currentHitIndex = (currentHitIndex + 1) % searchHits.size
    }

    /** Previous match; wraps past the first hit back to the last. */
    fun previousSearchHit() {
        if (searchHits.isEmpty()) return
        currentHitIndex = (currentHitIndex - 1 + searchHits.size) % searchHits.size
    }

    /** Closes the search UI: stops any sweep and clears all highlights. */
    fun closeSearch() {
        searchJob?.cancel()
        searchQuery = ""
        searchHits = emptyList()
        currentHitIndex = -1
        isSearching = false
    }

    fun openUri(uri: Uri, password: String? = null) {
        uiState = ViewerUiState.Loading
        // Opening… (#145); showing the document resets the busy state, so the end is only for a failure.
        val token = busy.beginDocument(BusyLabel.OPENING)
        viewModelScope.launch {
            try {
                openAndShow(uri, password)
            } finally {
                token.end()
            }
        }
    }

    /** [openUri]'s work, for a caller already in a coroutine: true once the document is on screen. */
    private suspend fun openAndShow(uri: Uri, password: String?): Boolean {
        val failed: ViewerUiState = try {
            show(readAndOpen(uri, password), uri)
            return true
        } catch (e: CancellationException) {
            throw e
        } catch (_: PdfPasswordException) {
            ViewerUiState.PasswordNeeded(uri, wrongPassword = password != null)
        } catch (e: PdfLoadException) {
            // ADR-004 decision 8: protection PDFium can't open is neither corrupt nor a wrong password.
            ViewerUiState.Home(
                recentsStore.load(),
                when {
                    e.isUnsupportedSecurity -> str(R.string.open_unsupported_security)
                    e.isTooLarge -> str(R.string.open_too_large)
                    else -> str(R.string.open_failed_code, e.errorCode)
                },
            )
        } catch (_: SecurityException) {
            recentsStore.remove(uri.toString())
            ViewerUiState.Home(recentsStore.load(), str(R.string.open_access_revoked))
        } catch (_: Exception) {
            ViewerUiState.Home(recentsStore.load(), str(R.string.open_failed))
        } catch (_: OutOfMemoryError) {
            // Read on demand, a document no longer needs its size in memory (#147); one that
            // still runs out while its pages are measured is too big for this device.
            ViewerUiState.Home(recentsStore.load(), str(R.string.open_too_large))
        }
        // A security save reopens its file from the viewer (#131); if that fails, the document
        // still open no longer matches the file, so it goes too. From home this is a no-op.
        closeCurrent()
        uiState = failed
        return false
    }

    /**
     * A document opened from its uri, not yet on screen. [readsUri] when it reads the uri's
     * own file on demand, rather than a private copy of it (#147).
     */
    private class OpenedDocument(
        val doc: PdfDocument,
        val pageSizes: List<PageSize>,
        val security: PdfSecurity,
        val readsUri: Boolean,
    )

    private suspend fun readAndOpen(uri: Uri, password: String?): OpenedDocument {
        val (doc, readsUri) = openFromUri(uri, password)
        try {
            val count = doc.pageCount()
            val sizes = ArrayList<PageSize>(count)
            for (i in 0 until count) {
                val page = doc.openPage(i)
                sizes += PageSize(page.widthPoints, page.heightPoints)
                page.close()
            }
            return OpenedDocument(doc, sizes, doc.security(), readsUri)
        } catch (e: Throwable) {
            doc.close()
            throw e
        }
    }

    /**
     * Opens [uri] read on demand (#147, #148), so a document costs what PDFium parses rather
     * than its size, and a file of several gigabytes opens like any other. Through the
     * provider's descriptor when it gives a regular file (true); a provider that only streams
     * (a pipe) is copied into the cache and opened from there (false), and the copy's name
     * goes at once, since the open document holds the file.
     */
    private suspend fun openFromUri(uri: Uri, password: String?): Pair<PdfDocument, Boolean> {
        val app = getApplication<Application>()
        val fd = withContext(Dispatchers.IO) {
            try {
                app.contentResolver.openFileDescriptor(uri, "r")?.detachFd()
            } catch (_: java.io.FileNotFoundException) {
                null   // some providers stream but give no descriptor; the stream below says if the file is gone
            }
        }
        if (fd != null) {
            try {
                return engine.openFd(fd, password) to true
            } catch (e: PdfLoadException) {
                if (!e.isFileError) throw e
                // Not a regular file: a pipe from a provider that streams. Copy it instead.
            }
        }
        val copy = File(app.cacheDir, "open-${System.nanoTime()}.pdf")
        try {
            withContext(Dispatchers.IO) {
                app.contentResolver.openInputStream(uri)?.use { input ->
                    copy.outputStream().use { input.copyTo(it, COPY_BUFFER_BYTES) }
                } ?: throw IllegalStateException("provider returned no stream")
            }
            return engine.openFile(copy.path, password) to false
        } finally {
            withContext(NonCancellable + Dispatchers.IO) { copy.delete() }
        }
    }

    /**
     * Before [uri] is written in place: if it is the file [doc] reads on demand, [doc] moves
     * onto a private copy first (#147). Otherwise the save would change the bytes under the
     * open document, and the next page it loaded, or the next save, would read the new file
     * at the old one's offsets. Known when the document was opened from [uri]; also found by
     * identity, for a Save As that picks the document's own file. Throws when the copy cannot
     * be made (no space), and nothing is written.
     */
    private suspend fun keepDocumentOffFile(doc: PdfDocument, uri: Uri) {
        val reads = uri == documentReadsUri || withContext(Dispatchers.IO) {
            try {
                getApplication<Application>().contentResolver.openFileDescriptor(uri, "r")
            } catch (_: Exception) {
                null
            }
        }?.use { doc.readsFd(it.fd) } == true
        if (!reads) return
        val copy = File(getApplication<Application>().cacheDir, "reading-${System.nanoTime()}.pdf")
        doc.readFromCopy(copy.path)
        if (document === doc) documentReadsUri = null
    }

    /** Puts [opened] on screen in place of whatever was open. */
    private fun show(opened: OpenedDocument, uri: Uri) {
        // The window the page list last asked for, before closeCurrent() forgets it.
        // A document replaced in place — a password set, changed or removed, or an
        // owner-password unlock — leaves the list with nothing to report: same range,
        // same page sizes, same width. So it never calls back, and every bitmap has
        // just been thrown away. See RenderWindow (#146).
        val previousWindow = lastWindow
        closeCurrent()
        attach(opened.doc, opened.security)
        currentUri = uri
        documentReadsUri = if (opened.readsUri) uri else null
        val name = queryDisplayName(uri)
        persistReadPermission(uri)
        // The uri and name only: whatever password opened it stays with the open document.
        recentsStore.add(
            RecentEntry(uri.toString(), name, System.currentTimeMillis())
        )
        uiState = ViewerUiState.Viewing(name, opened.pageSizes)
        previousWindow?.clampedTo(opened.pageSizes.size)?.let {
            updateRenderWindow(it.firstVisible, it.lastVisible, it.targetWidthPx)
        }
        // ADR-004 decision 3: a restricted open says so; the menu offers the owner password.
        if (capabilities.isRestricted) showNotice(str(R.string.security_restricted_notice))
    }

    /** Makes [doc] the open document: its permissions, and its page checks (#145). */
    private fun attach(doc: PdfDocument, security: PdfSecurity) {
        document = doc
        capabilities = DocumentCapabilities.fromSecurity(security)
        // Each check runs off the engine's thread and stops when its coroutine is cancelled.
        pageChecks.open { pageIndex ->
            when (val result = doc.checkPageRegeneration(pageIndex)) {
                is PageCheck.Judged -> result.verdict
                else -> null
            }
        }
    }

    /**
     * Called by the page list whenever the visible range or target width changes.
     * Renders visible pages ± [RENDER_MARGIN] at [targetWidthPx] (capped), keeps
     * already-sharp bitmaps, and evicts everything outside the window.
     */
    fun updateRenderWindow(firstVisible: Int, lastVisible: Int, targetWidthPx: Int) {
        val state = uiState as? ViewerUiState.Viewing ?: return
        val doc = document ?: return
        lastWindow = RenderWindow(firstVisible, lastVisible, targetWidthPx)
        val window = (firstVisible - RENDER_MARGIN).coerceAtLeast(0)..
            (lastVisible + RENDER_MARGIN).coerceAtMost(state.pageSizes.size - 1)

        for (index in pageBitmaps.keys.toList()) {
            if (index !in window) {
                pageBitmaps.remove(index)
                renderedWidths.remove(index)
            }
        }

        renderJob?.cancel()
        renderJob = viewModelScope.launch {
            for (index in window) {
                val size = state.pageSizes[index]
                // The app's memory bound first (aspect preserved, unlike a per-axis
                // clamp), then the engine's render clamp (#93/#111), which is what
                // stops a poster-sized scan from asking for a raster nothing can hold.
                val idealWidth = targetWidthPx.coerceAtLeast(1).toDouble()
                val idealHeight = idealWidth * size.heightPoints / size.widthPoints
                val memoryScale = minOf(1.0, MAX_BITMAP_DIM / maxOf(idealWidth, idealHeight))
                val (width, height) = PdfEngine.renderSize(idealWidth * memoryScale, idealHeight * memoryScale)
                if (renderedWidths[index] == width) continue

                try {
                    val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
                    val page = doc.openPage(index)
                    try {
                        page.render(bitmap)
                    } finally {
                        page.close()
                    }
                    pageBitmaps[index] = bitmap
                    renderedWidths[index] = width
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    // A page that fails to render stays blank rather than taking the app down
                    // (#145); the rest of the window still renders.
                }
            }
        }
    }

    /**
     * Tap dispatch, the mobile port of the desktop `OnPageTapped` hit ordering:
     * form fields win over page content; then existing check marks (tap to
     * remove); then drawn-square candidates (tap to place a mark).
     * Fractions are tap position / rendered page size, top-left origin.
     * A tap while a change is still going in, or while a save runs, is ignored (#145).
     */
    fun onPageTapped(pageIndex: Int, xFraction: Float, yFraction: Float) {
        val state = uiState as? ViewerUiState.Viewing ?: return
        val doc = document ?: return
        launchEdit(R.string.edit_failed) {
            val size = state.pageSizes[pageIndex]
            val x = xFraction * size.widthPoints
            val y = (1 - yFraction) * size.heightPoints  // view top-left → PDF bottom-left

            pendingSignature?.let { entry ->
                pendingSignature = null
                placeSignature(doc, entry, pageIndex, size, x, y)
                return@launchEdit
            }

            if (isPlacingText) {
                isPlacingText = false
                statusMessage = null
                pendingTextTap = PendingTextTap(
                    pageIndex, x, y, fontSize = lastFontSize, fontName = lastFontName)
                // The box goes on this page: check it while the text is typed (#145).
                preparePageCheck(pageIndex)
                return@launchEdit
            }

            // Whichever edit the tap lands on, it goes through the history so it
            // can be taken back (#34).
            var operation: PdfEditOperation? = null
            val page = doc.openPage(pageIndex)
            try {
                val stamps = page.stamps()
                val signature = stamps
                    .filter { it.id.startsWith("sig:") }
                    .firstOrNull { it.rect.contains(x, y) }
                if (signature != null) {
                    if (!capabilities.canSign) {
                        // #131: moving or removing it is an edit the owner did not allow.
                        selectedStamp = null
                        selectedTextBox = null
                        showRestricted()
                        return@launchEdit
                    }
                    // Selection only — move/resize/remove happen via the overlay.
                    selectedStamp = SelectedStamp(
                        pageIndex, signature.annotIndex, signature.id, signature.rect)
                    selectedTextBox = null
                    return@launchEdit
                }
                selectedStamp = null

                // Text boxes (#36) rank with signatures: both are things the user
                // put on the page, so they win over the document underneath.
                // Last match wins — later page objects paint on top. The rect is
                // tight around the glyphs, and a 12 pt line is a few pixels tall
                // on a phone, so the hit test gets TAP_SLOP_POINTS of margin.
                val box = page.textBoxes().lastOrNull {
                    it.rect.grownBy(TAP_SLOP_POINTS).contains(x, y)
                }
                if (box != null) {
                    if (!capabilities.canAddText) {
                        selectedTextBox = null
                        showRestricted()
                        return@launchEdit
                    }
                    if (box.id.startsWith(UNTAGGED_TEXT_PREFIX)) {
                        // A box written by MegaPDF for Windows 1.6.x, before boxes
                        // carried an id. Its only handle is its page-object index,
                        // which the history would replay against a page whose
                        // indices had since shifted — so it would eventually move
                        // or delete the wrong box. Swallow the tap rather than let
                        // it fall through and toggle whatever is underneath.
                        selectedTextBox = null
                        statusMessage = str(R.string.text_untagged)
                        return@launchEdit
                    }
                    val selected = SelectedTextBox(
                        pageIndex, box.id, box.text, box.fontSize, box.fontName, box.rect)
                    // A selected box is about to be moved, corrected or removed: check its page (#145).
                    preparePageCheck(pageIndex)
                    if (selectedTextBox?.id == box.id) {
                        // A second tap on the selected box also opens the editor.
                        // The overlay's ✎ is the discoverable way in, because a
                        // *quick* second tap is claimed by double-tap-to-zoom —
                        // this path only fires after that disambiguation lapses.
                        selectedTextBox = selected
                        openTextBoxEditor(selected)
                    } else {
                        selectedTextBox = selected
                    }
                    return@launchEdit
                }
                selectedTextBox = null

                val field = page.formFields().firstOrNull { it.rect.contains(x, y) }
                operation = if (field != null) {
                    FieldToggleOperation(pageIndex, field.rect.centerX, field.rect.centerY)
                } else {
                    val mark = stamps
                        .filter { it.id.startsWith("mark:") }
                        .firstOrNull { it.rect.contains(x, y) }
                    if (mark != null) {
                        MarkOperation(
                            pageIndex, MarkOperation.squareFromMark(mark.rect), mark.id, false)
                    } else {
                        page.detectCheckboxSquares()
                            .firstOrNull { it.contains(x, y) }
                            ?.let {
                                MarkOperation(
                                    pageIndex, it, "mark:${java.util.UUID.randomUUID()}", true)
                            }
                    }
                }
                if (operation == null) {
                    // Nothing the user placed and nothing to tick: the document's own
                    // text (#114). A tap on a line opens the editor on it.
                    val lines = page.textLines()
                    val line = lines.firstOrNull { it.rect.grownBy(TAP_SLOP_POINTS).contains(x, y) }
                    if (line != null) {
                        // #118: on pages PDFium cannot rewrite faithfully, say so now
                        // rather than after the user has typed.
                        // #131: and on a document whose owner did not allow changes, say that.
                        if (!capabilities.canEditContent) {
                            showRestricted()
                        } else {
                            // #128: and say why — text elsewhere would move, or the page would look different.
                            // A run that is no longer text counts as refused, as it always has.
                            // #145: the check can take seconds on a heavy page, so the line shows a
                            // spinner, and taps are ignored until it answers.
                            val refusal = busy.pageWork(BusyLabel.CHECKING_PAGE, BusySpot(pageIndex, line.rect)) {
                                line.runs.firstNotNullOfOrNull { run ->
                                    val verdict = page.layoutVerdict(run.objectIndex)
                                    if (verdict == null) LayoutCause.REWRITE_FAILED
                                    else verdict.cause.takeIf { !verdict.editable }
                                }
                            }
                            if (refusal == null) {
                                if (document === doc) pendingBodyEdit = PendingBodyEdit(pageIndex, line)
                            } else {
                                showNotice(layoutNotice(refusal))
                            }
                        }
                    } else if (lines.isEmpty() && !scannedHintShown) {
                        // A page with no text at all is a picture of a page.
                        scannedHintShown = true
                        showNotice(str(R.string.body_text_scanned))
                    }
                }
            } finally {
                page.close()
            }
            operation?.let { perform(it, doc) }
        }
    }

    // --- The document's own text (#114) ---

    /**
     * Commits the body-text editor. The same text is a no-op; an empty field removes
     * the line. Either way it is one undoable edit.
     */
    fun commitBodyEdit(text: String) {
        val pending = pendingBodyEdit ?: return
        val doc = document ?: return
        // Blocked while another change is still going in (#145): the editor stays open.
        if (editingBlocked) return
        pendingBodyEdit = null
        val trimmed = text.trim()
        if (trimmed == pending.line.text) return
        launchEdit(R.string.text_change_failed) {
            try {
                if (trimmed.isEmpty()) {
                    perform(BodyTextDeleteOperation(pending.pageIndex, pending.line), doc,
                        BusySpot(pending.pageIndex, pending.line.rect))
                } else {
                    val operation = BodyTextEditOperation(pending.pageIndex, pending.line, trimmed)
                    perform(operation, doc, BusySpot(pending.pageIndex, pending.line.rect))
                    if (operation.lastOutcome == TextEditOutcome.SUBSTITUTED) {
                        showNotice(str(R.string.body_text_substituted))
                    }
                }
            } catch (e: com.megapdf.engine.TextLayoutException) {
                showNotice(layoutNotice(e.verdict?.cause))
            }
        }
    }

    fun cancelBodyEdit() {
        pendingBodyEdit = null
    }

    /** Shows a notice over the page for a few seconds. */
    private fun showNotice(text: String) {
        noticeJob?.cancel()
        notice = text
        noticeJob = viewModelScope.launch {
            delay(4_000)
            notice = null
        }
    }

    /** The notice for a line the layout guard refused (#118), by its cause (#128). */
    private fun layoutNotice(cause: LayoutCause?): String = str(
        when {
            cause == null -> R.string.body_text_layout
            cause.textWouldMove -> R.string.body_text_layout_text_moves
            cause == LayoutCause.RENDER -> R.string.body_text_layout_render
            else -> R.string.body_text_layout
        }
    )

    /** The notice for an edit the document's owner did not allow (#131). */
    private fun showRestricted() = showNotice(str(R.string.security_restricted_edit))

    // --- Added text (#34) ---

    /** Arms the next tap to place text. Tapping the page opens the text field. */
    fun startTextPlacement() {
        if (editingBlocked) return
        if (!capabilities.canAddText) {
            showRestricted()
            return
        }
        cancelPlacement()
        selectedStamp = null
        selectedTextBox = null
        isPlacingText = true
        statusMessage = str(R.string.tap_to_place_text)
        // The tool regenerates the page it lands on: check the current one early (#145).
        preparePageCheck(currentPage)
    }

    fun cancelTextPlacement() {
        isPlacingText = false
        pendingTextTap = null
        statusMessage = null
    }

    /**
     * Commits what the text dialog was left holding — a new box, or a change to
     * one already on the page. Text, size and face all arrive together, so
     * restyling and correcting a typo are the same single undoable edit.
     */
    fun commitText(text: String, fontSize: Double, fontName: String) {
        val pending = pendingTextTap ?: return
        val doc = document ?: return
        // Blocked while another change is still going in (#145): the dialog keeps what was typed.
        if (editingBlocked) return
        pendingTextTap = null
        val trimmed = text.trim()
        if (trimmed.isEmpty()) return
        lastFontSize = fontSize
        lastFontName = fontName
        val style = TextBoxStyle(trimmed, fontSize, fontName)
        val spot = BusySpot(
            pending.pageIndex,
            com.megapdf.engine.PdfRect(pending.x, pending.y, pending.x, pending.y + fontSize),
        )
        launchEdit(if (pending.editingId != null) R.string.text_change_failed else R.string.text_add_failed) {
            if (pending.editingId != null) {
                val before = TextBoxStyle(
                    pending.initialText, pending.fontSize, pending.fontName)
                if (before == style) return@launchEdit
                val edit = EditTextBoxOperation(
                    pending.pageIndex, pending.editingId, before, style,
                    pending.x, pending.y)
                if (!confirmPageRewrite(edit, doc, spot)) return@launchEdit
                perform(edit, doc, spot)
                reselectTextBox(doc, pending.pageIndex, pending.editingId)
            } else {
                val add = TextBoxOperation(
                    pending.pageIndex, "text:${java.util.UUID.randomUUID()}",
                    trimmed, fontSize, pending.x, pending.y, adding = true,
                    fontName = fontName)
                if (!confirmPageRewrite(add, doc, spot)) return@launchEdit
                perform(add, doc, spot)
            }
        }
    }

    // --- Selected text box: drag, correct, remove (#36) ---

    /**
     * Commits a drag from the selection overlay. Only the position changes — a
     * text box has no resize handle, because resizing one would mean changing
     * its font size, and SDD §3.1 keeps formatting controls out of the app.
     */
    fun commitTextBoxRect(newRect: com.megapdf.engine.PdfRect) {
        val sel = selectedTextBox ?: return
        val state = uiState as? ViewerUiState.Viewing ?: return
        val doc = document ?: return
        if (editingBlocked) {
            // #145: another change is still going in. The box never moved; dropping the selection
            // drops the dragged overlay with it.
            selectedTextBox = null
            return
        }
        launchEdit(R.string.text_move_failed) {
            val rect = clampToPage(newRect, state.pageSizes[sel.pageIndex])
            // A tap that slipped into a drag can land a sub-point move; don't put
            // a no-op on the undo stack for it.
            if (kotlin.math.abs(rect.left - sel.rect.left) < 0.01 &&
                kotlin.math.abs(rect.bottom - sel.rect.bottom) < 0.01) return@launchEdit
            val move = MoveTextBoxOperation(
                sel.pageIndex, sel.id,
                fromX = sel.rect.left, fromY = sel.rect.bottom,
                toX = rect.left, toY = rect.bottom)
            val spot = BusySpot(sel.pageIndex, rect)
            if (!confirmPageRewrite(move, doc, spot)) {
                // Cancel: the box never moved. Dropping the selection drops the dragged
                // overlay with it, so nothing on screen claims otherwise (#139).
                selectedTextBox = null
                return@launchEdit
            }
            perform(move, doc, spot)
            reselectTextBox(doc, sel.pageIndex, sel.id)
        }
    }

    /**
     * Opens the text field on the selected box so a typo can be corrected. The
     * anchor handed to the edit is the box's bounds lower-left, not a tap point.
     */
    fun editSelectedTextBox() {
        val sel = selectedTextBox ?: return
        if (editingBlocked) return
        openTextBoxEditor(sel)
    }

    private fun openTextBoxEditor(sel: SelectedTextBox) {
        selectedTextBox = null
        if (!capabilities.canAddText) {
            showRestricted()
            return
        }
        pendingTextTap = PendingTextTap(
            sel.pageIndex, sel.rect.left, sel.rect.bottom,
            editingId = sel.id, fontSize = sel.fontSize, fontName = sel.fontName,
            initialText = sel.text)
    }

    fun removeSelectedTextBox() {
        val sel = selectedTextBox ?: return
        val doc = document ?: return
        launchEdit(R.string.text_remove_failed) {
            // boundsAnchored: the coordinates are the box's reported rect, so
            // an undo must re-add against bounds, not the baseline.
            val remove = TextBoxOperation(
                sel.pageIndex, sel.id, sel.text, sel.fontSize,
                sel.rect.left, sel.rect.bottom, adding = false,
                boundsAnchored = true, fontName = sel.fontName)
            val spot = BusySpot(sel.pageIndex, sel.rect)
            if (!confirmPageRewrite(remove, doc, spot)) return@launchEdit
            perform(remove, doc, spot)
        }
    }

    /**
     * Re-reads the box after an edit and keeps it selected, so the handles stay
     * on it. The rect must be read back rather than reused: correcting the text
     * changes the box's width.
     */
    private suspend fun reselectTextBox(doc: PdfDocument, pageIndex: Int, id: String) {
        val page = doc.openPage(pageIndex)
        try {
            val box = page.textBoxes().firstOrNull { it.id == id } ?: return
            selectedTextBox =
                SelectedTextBox(pageIndex, id, box.text, box.fontSize, box.fontName, box.rect)
        } finally {
            page.close()
        }
    }

    // --- Undo / redo (#34) ---

    fun undo() {
        val doc = document ?: return
        launchEdit(R.string.undo_failed) {
            busy.pageWork(BusyLabel.APPLYING, null) { history.undo(doc) }?.let { afterHistoryChange(it) }
        }
    }

    fun redo() {
        val doc = document ?: return
        launchEdit(R.string.redo_failed) {
            busy.pageWork(BusyLabel.APPLYING, null) { history.redo(doc) }?.let { afterHistoryChange(it) }
        }
    }

    /**
     * The single funnel for every reversible change. It asks the document's permissions
     * first (#131, ADR-004 decision 2): the entry points ask too, so this is the backstop
     * for any path they miss — form fields and check marks rely on it. While the change goes
     * in, the page shows Applying… at [spot] once it takes long enough (#145).
     */
    private suspend fun perform(operation: PdfEditOperation, doc: PdfDocument, spot: BusySpot? = null) {
        if (!capabilities.allows(operation)) {
            showRestricted()
            return
        }
        busy.pageWork(BusyLabel.APPLYING, spot ?: BusySpot(operation.pageIndex)) {
            history.perform(operation, doc)
        }
        if (operation is BodyTextEditOperation || operation is BodyTextDeleteOperation) {
            // A change that regenerated the page already went in without a warning (#139, #145).
            pageChecks.settle(operation.pageIndex)
        }
        afterHistoryChange(operation.pageIndex)
    }

    private fun afterHistoryChange(pageIndex: Int) {
        canUndo = history.canUndo
        canRedo = history.canRedo
        selectedStamp = null
        selectedTextBox = null
        markEditedAndRerender(pageIndex)
    }

    // --- Signature library and placement (#16/#17) ---

    /** Imports a picked image: decode, cleanup per the SDD contract, store. */
    fun importSignature(uri: Uri) {
        viewModelScope.launch {
            try {
                val bitmap = withContext(Dispatchers.IO) {
                    getApplication<Application>().contentResolver.openInputStream(uri)
                        ?.use { android.graphics.BitmapFactory.decodeStream(it) }
                } ?: throw IllegalStateException("couldn't decode the image")

                val scaled = if (bitmap.width > MAX_SIGNATURE_SOURCE_DIM ||
                    bitmap.height > MAX_SIGNATURE_SOURCE_DIM
                ) {
                    val scale = MAX_SIGNATURE_SOURCE_DIM.toFloat() /
                        maxOf(bitmap.width, bitmap.height)
                    Bitmap.createScaledBitmap(
                        bitmap,
                        (bitmap.width * scale).toInt().coerceAtLeast(1),
                        (bitmap.height * scale).toInt().coerceAtLeast(1),
                        true,
                    )
                } else bitmap

                val result = withContext(Dispatchers.Default) {
                    var pixels = IntArray(scaled.width * scaled.height)
                    scaled.getPixels(pixels, 0, scaled.width, 0, 0, scaled.width, scaled.height)
                    // Already-transparent PNGs skip white removal (SDD contract).
                    if (!SignatureImageProcessor.hasTransparency(pixels)) {
                        pixels = SignatureImageProcessor.removeWhiteBackground(pixels)
                    }
                    SignatureImageProcessor.trimToInk(pixels, scaled.width, scaled.height)
                }
                val out = Bitmap.createBitmap(result.width, result.height, Bitmap.Config.ARGB_8888)
                out.setPixels(result.pixels, 0, result.width, 0, 0, result.width, result.height)

                val entry = withContext(Dispatchers.IO) {
                    signatureStore.add(defaultSignatureName(), out)
                }
                signatures.add(entry)
                statusMessage = str(R.string.signature_added)
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                statusMessage = str(R.string.signature_import_failed)
            }
        }
    }

    /** Stores a drawn signature: already transparent, so only trim applies. */
    fun addDrawnSignature(bitmap: Bitmap) {
        viewModelScope.launch {
            try {
                val result = withContext(Dispatchers.Default) {
                    val pixels = IntArray(bitmap.width * bitmap.height)
                    bitmap.getPixels(pixels, 0, bitmap.width, 0, 0, bitmap.width, bitmap.height)
                    SignatureImageProcessor.trimToInk(pixels, bitmap.width, bitmap.height)
                }
                val out = Bitmap.createBitmap(result.width, result.height, Bitmap.Config.ARGB_8888)
                out.setPixels(result.pixels, 0, result.width, 0, 0, result.width, result.height)
                val entry = withContext(Dispatchers.IO) {
                    signatureStore.add(defaultSignatureName(), out)
                }
                signatures.add(entry)
                statusMessage = str(R.string.signature_added)
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                statusMessage = str(R.string.signature_save_failed)
            }
        }
    }

    fun deleteSignature(id: String) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                signatureStore.delete(id)
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                // The card is already gone from the sheet; a file left behind is reloaded next launch.
            }
        }
        signatures.removeAll { it.id == id }
    }

    /** Renames a library entry in place; the id and image are untouched (#99). */
    fun renameSignature(id: String, displayName: String) {
        val name = displayName.trim()
        if (name.isEmpty()) return
        viewModelScope.launch(Dispatchers.IO) {
            try {
                signatureStore.rename(id, name)
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                withContext(Dispatchers.Main) { statusMessage = str(R.string.signature_save_failed) }
            }
        }
        val index = signatures.indexOfFirst { it.id == id }
        if (index >= 0) signatures[index] = signatures[index].copy(displayName = name)
    }

    /** The stored ink for a library card's thumbnail, decoded off the main thread (#99). */
    suspend fun loadSignatureBitmap(entry: SignatureEntry): Bitmap? =
        withContext(Dispatchers.IO) {
            try { signatureStore.loadBitmap(entry) } catch (_: Exception) { null }
        }

    fun startPlacement(entry: SignatureEntry) {
        if (editingBlocked) return
        if (!capabilities.canSign) {
            showRestricted()
            return
        }
        selectedTextBox = null
        pendingSignature = entry
        statusMessage = str(R.string.tap_to_place_signature)
    }

    fun cancelPlacement() {
        pendingSignature = null
    }

    private suspend fun placeSignature(
        doc: PdfDocument, entry: SignatureEntry, pageIndex: Int,
        size: PageSize, x: Double, y: Double,
    ) {
        val bitmap = withContext(Dispatchers.IO) { signatureStore.loadBitmap(entry) }
        if (bitmap == null) {
            statusMessage = str(R.string.signature_image_missing)
            return
        }
        // Default size: a third of the page width, aspect preserved (desktop default).
        var w = size.widthPoints / 3.0
        var h = w * bitmap.height / bitmap.width
        val maxH = size.heightPoints / 3.0
        if (h > maxH) {
            h = maxH
            w = h * bitmap.width / bitmap.height
        }
        val rect = clampToPage(
            com.megapdf.engine.PdfRect(x - w / 2, y - h / 2, x + w / 2, y + h / 2), size)

        val pixels = IntArray(bitmap.width * bitmap.height)
        bitmap.getPixels(pixels, 0, bitmap.width, 0, 0, bitmap.width, bitmap.height)
        val id = "sig:${java.util.UUID.randomUUID()}"

        perform(
            StampOperation(pageIndex, id, pixels, bitmap.width, bitmap.height,
                           rect, adding = true),
            doc, BusySpot(pageIndex, rect))
        // Keep it selected so the handles appear straight away.
        val page = doc.openPage(pageIndex)
        try {
            val placed = page.stamps().firstOrNull { it.id == id }
            if (placed != null) {
                selectedStamp = SelectedStamp(pageIndex, placed.annotIndex, id, placed.rect)
            }
        } finally {
            page.close()
        }
    }

    /**
     * Commits a move/resize from the selection overlay: read the image back at
     * native resolution, remove, re-add under the same id (the desktop pattern —
     * repeated moves never lose resolution, and it works for stamps placed by
     * Windows MegaPDF too).
     */
    fun commitStampRect(newRect: com.megapdf.engine.PdfRect) {
        val sel = selectedStamp ?: return
        val state = uiState as? ViewerUiState.Viewing ?: return
        val doc = document ?: return
        if (editingBlocked) {
            // #145: another change is still going in; drop the dragged overlay, the stamp never moved.
            selectedStamp = null
            return
        }
        launchEdit(R.string.signature_move_failed) {
            val rect = clampToPage(newRect, state.pageSizes[sel.pageIndex])
            var page = doc.openPage(sel.pageIndex)
            val packed = try {
                page.stampImagePacked(sel.annotIndex)
            } finally {
                page.close()
            }
            if (packed == null) {
                statusMessage = str(R.string.signature_image_unreadable)
                return@launchEdit
            }
            perform(
                MoveStampOperation(
                    sel.pageIndex, sel.id, packed.copyOfRange(2, packed.size),
                    packed[0], packed[1], from = sel.rect, to = rect),
                doc, BusySpot(sel.pageIndex, rect))
            page = doc.openPage(sel.pageIndex)
            try {
                val placed = page.stamps().firstOrNull { it.id == sel.id }
                if (placed != null) {
                    selectedStamp = sel.copy(annotIndex = placed.annotIndex, rect = placed.rect)
                }
            } finally {
                page.close()
            }
        }
    }

    fun removeSelectedStamp() {
        val sel = selectedStamp ?: return
        val doc = document ?: return
        launchEdit(R.string.signature_remove_failed) {
            // Read the image back first: without it, undo could not put the same
            // signature back.
            val page = doc.openPage(sel.pageIndex)
            val packed = try {
                page.stampImagePacked(sel.annotIndex)
            } finally {
                page.close()
            }
            if (packed == null) {
                statusMessage = str(R.string.signature_remove_failed)
                return@launchEdit
            }
            perform(
                StampOperation(sel.pageIndex, sel.id, packed.copyOfRange(2, packed.size),
                               packed[0], packed[1], sel.rect, adding = false),
                doc, BusySpot(sel.pageIndex, sel.rect))
        }
    }

    fun deselectStamp() {
        selectedStamp = null
    }

    private fun clampToPage(
        rect: com.megapdf.engine.PdfRect, size: PageSize,
    ): com.megapdf.engine.PdfRect {
        val w = (rect.right - rect.left).coerceAtMost(size.widthPoints)
        val h = (rect.top - rect.bottom).coerceAtMost(size.heightPoints)
        val left = rect.left.coerceIn(0.0, size.widthPoints - w)
        val bottom = rect.bottom.coerceIn(0.0, size.heightPoints - h)
        return com.megapdf.engine.PdfRect(left, bottom, left + w, bottom + h)
    }

    private fun markEditedAndRerender(pageIndex: Int) {
        dirty.markEdited()
        renderedWidths.remove(pageIndex)
        lastWindow?.let { updateRenderWindow(it.firstVisible, it.lastVisible, it.targetWidthPx) }
    }

    /**
     * Save = write back to the opened document's URI. SAF has no atomic rename,
     * so (desktop `AtomicFileWriter` analog, per #18): serialize into an
     * app-cache temp file first — a PDFium failure never touches the user's
     * file — verify the result reopens in the engine, then stream it to the
     * destination with truncation and fsync. Only then is the document clean,
     * and only if nothing changed while it ran (D3 of #145).
     */
    fun save() {
        val uri = currentUri ?: return
        writeTo(uri, isSaveAs = false)
    }

    /** Save from the unsaved-changes prompt: the document closes once it is saved and still clean. */
    fun saveAndClose() {
        val uri = currentUri ?: return
        val doc = document ?: return
        writeTo(uri, isSaveAs = false) {
            if (document === doc && !isDirty) closeDocument()
        }
    }

    /** "Save a copy" destination picked via ACTION_CREATE_DOCUMENT. */
    fun saveAs(uri: Uri) = writeTo(uri, isSaveAs = true)

    private fun writeTo(uri: Uri, isSaveAs: Boolean, afterSaved: (() -> Unit)? = null) {
        val doc = document ?: return
        if (isSaving || busy.locksDocument) return
        isSaving = true
        // #145: Saving… in the strip; editing, Close and the file commands wait until it ends.
        val token = busy.beginDocument(BusyLabel.SAVING, locks = true)
        val mark = dirty.beginSave()
        viewModelScope.launch {
            var saved = false
            try {
                // Verified opened like the document: a protected document's copy is still
                // protected (#132).
                writeVerified(doc, uri, token, { doc.save(it) }, { engine.openFileLike(doc, it.path).close() })

                if (isSaveAs) {
                    currentUri = uri
                    persistReadPermission(uri)
                    val name = queryDisplayName(uri)
                    recentsStore.add(RecentEntry(uri.toString(), name, System.currentTimeMillis()))
                    (uiState as? ViewerUiState.Viewing)?.let {
                        uiState = it.copy(displayName = name)
                    }
                }
                // D3 (#145): a change that went in while the bytes were written keeps the
                // document dirty, so a later close still asks.
                dirty.markSaved(mark)
                statusMessage = str(R.string.saved)
                saved = true
            } catch (e: CancellationException) {
                throw e
            } catch (_: SecurityException) {
                statusMessage = str(R.string.save_no_permission)
            } catch (_: Exception) {
                statusMessage = str(R.string.save_failed)
            } finally {
                isSaving = false
                token.end()
            }
            if (saved) afterSaved?.invoke()
        }
    }

    /**
     * The verified write every save shares (#18, #131): [serialize] into an app-cache temp
     * file, so a PDFium failure never touches the user's file; [verify] that file by opening
     * it, which throws when it doesn't open; only then stream it to [uri] with truncation and
     * fsync. Throws on any failure, for the caller to report. Bytes that did not verify
     * never reach the destination. [token] follows the steps: Saving…, Checking the saved
     * file…, Saving… (#145).
     *
     * Nothing here holds the document in memory (#147): the check reads the temp file on
     * demand and the write streams it. If [uri] is the file [doc] reads, [doc] moves onto a
     * copy before it is truncated.
     */
    private suspend fun writeVerified(
        doc: PdfDocument,
        uri: Uri,
        token: BusyToken?,
        serialize: suspend (java.io.OutputStream) -> Unit,
        verify: suspend (File) -> Unit,
    ) {
        val app = getApplication<Application>()
        val temp = File(app.cacheDir, "save-${System.currentTimeMillis()}.pdf")
        try {
            withContext(Dispatchers.IO) { temp.parentFile?.mkdirs() }
            // Opening and closing the stream is disk I/O and belongs off the
            // main thread like its neighbours (#55) — the serializer suspends onto
            // the engine thread, but the open, and the flush at close, did not.
            withContext(Dispatchers.IO) {
                java.io.FileOutputStream(temp).use { serialize(it) }
            }

            check(withContext(Dispatchers.IO) { temp.length() } > 0L) { "engine produced an empty document" }
            token?.relabel(BusyLabel.VERIFYING_SAVE)
            verify(temp)
            token?.relabel(BusyLabel.SAVING)
            keepDocumentOffFile(doc, uri)

            withContext(Dispatchers.IO) {
                // "wt" guarantees truncation; plain "w" can leave a stale tail
                // when the new file is shorter.
                val pfd = app.contentResolver.openFileDescriptor(uri, "wt")
                    ?: throw IllegalStateException("provider returned no descriptor")
                pfd.use {
                    java.io.FileOutputStream(it.fileDescriptor).use { out ->
                        java.io.FileInputStream(temp).use { input -> input.copyTo(out, COPY_BUFFER_BYTES) }
                        out.fd.sync()
                    }
                }
            }
        } finally {
            temp.delete()
        }
    }

    // --- Document security (#131) ---

    /** Opens the owner-password dialog for a restricted document (ADR-004 decision 3). */
    fun startUnlock() {
        if (!capabilities.isRestricted || currentUri == null || busy.locksDocument) return
        passwordPrompt = null
        unlockPrompt = UnlockPrompt()
    }

    /** Cancel keeps the restricted document as it is. */
    fun cancelUnlock() {
        unlockPrompt = null
    }

    /**
     * Reopens the document's file with [ownerPassword]. Only full access unlocks it: a user
     * password opens the file too, with the same restrictions, so it counts as wrong and the
     * dialog stays. Reopening from the file drops unsaved changes; the dialog says so.
     */
    fun unlock(ownerPassword: String) {
        val prompt = unlockPrompt ?: return
        val uri = currentUri ?: return
        if (prompt.isChecking || busy.locksDocument) return
        unlockPrompt = prompt.copy(isChecking = true)
        // Opening… (#145): the document is about to be replaced, so nothing else may change it.
        val token = busy.beginDocument(BusyLabel.OPENING, locks = true)
        viewModelScope.launch {
            try {
                val opened = try {
                    readAndOpen(uri, ownerPassword)
                } catch (e: CancellationException) {
                    throw e
                } catch (_: PdfPasswordException) {
                    null
                } catch (_: Exception) {
                    unlockPrompt = null
                    statusMessage = str(R.string.open_failed)
                    return@launch
                }
                if (unlockPrompt == null || currentUri != uri) {
                    // Cancelled, or the document closed, while the password was tried.
                    opened?.doc?.close()
                    return@launch
                }
                if (opened == null || !opened.security.hasFullAccess) {
                    opened?.doc?.close()
                    unlockPrompt = UnlockPrompt(attempt = prompt.attempt + 1, wrongPassword = true)
                    return@launch
                }
                unlockPrompt = null
                show(opened, uri)
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                unlockPrompt = null
                statusMessage = str(R.string.open_failed)
            } finally {
                token.end()
            }
        }
    }

    /** The Password command; its dialog follows from the document's security (ADR-004 decision 5). */
    fun startPasswordCommand() {
        if (document == null || currentUri == null || isSaving || editingBlocked) return
        unlockPrompt = null
        passwordPrompt = PasswordCommandMode.of(capabilities)
    }

    fun cancelPasswordCommand() {
        passwordPrompt = null
    }

    /** Sets or changes the password: AES-256, one password, every permission (decisions 4 and 5). */
    fun setPassword(newPassword: String) = saveSecurity(newPassword)

    fun removePassword() = saveSecurity(null)

    /**
     * Setting, changing or removing security is a save (ADR-004 decision 6): the verified
     * write, checked by opening the copy with the new password — or with none when
     * [newPassword] is null and the security goes — and then the file is reopened that way,
     * so the open document, its credentials and its permissions match what is on disk.
     */
    private fun saveSecurity(newPassword: String?) {
        // The reopen below replaces the document, so a change still going in must finish first
        // (#145); the dialog stays up until then.
        if (isSaving || editingBlocked) return
        passwordPrompt = null
        val doc = document ?: return
        val uri = currentUri ?: return
        if (!capabilities.canChangeSecurity) {
            // The dialog never offers this without full access, and the core refuses it too.
            showRestricted()
            return
        }
        val done = when {
            newPassword == null -> R.string.security_password_removed
            capabilities.isEncrypted -> R.string.security_password_changed
            else -> R.string.security_password_set
        }
        isSaving = true
        // #145: Saving…, then Opening… as the file is reopened; the document is locked throughout.
        val token = busy.beginDocument(BusyLabel.SAVING, locks = true)
        val mark = dirty.beginSave()
        viewModelScope.launch {
            try {
                val written = try {
                    if (newPassword == null) {
                        writeVerified(doc, uri, token, { doc.saveWithoutSecurity(it) }, { engine.openFile(it.path).close() })
                    } else {
                        writeVerified(
                            doc,
                            uri,
                            token,
                            { doc.saveWithSecurity(it, newPassword, null, PdfPermissions.ALL) },
                            { engine.openFile(it.path, newPassword).close() },
                        )
                    }
                    true
                } catch (e: CancellationException) {
                    throw e
                } catch (_: SecurityException) {
                    statusMessage = str(R.string.save_no_permission)
                    false
                } catch (_: Exception) {
                    // PdfRestrictedException and a copy that didn't verify land here: the file
                    // was not written.
                    statusMessage = str(R.string.save_failed)
                    false
                } finally {
                    isSaving = false
                }
                if (!written) return@launch
                // D3 (#145): editing is locked while this runs, so nothing should have changed. If
                // something did, reopening would throw it away: keep the document, still dirty.
                if (!dirty.markSaved(mark)) {
                    showNotice(str(done))
                    return@launch
                }
                token.relabel(BusyLabel.OPENING)
                if (openAndShow(uri, newPassword)) showNotice(str(done))
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                statusMessage = str(R.string.open_failed)
            } finally {
                token.end()
            }
        }
    }

    fun openRecent(entry: RecentEntry) = openUri(Uri.parse(entry.uri))

    fun closeDocument() {
        // #145: never out of a document while its save or password change is still writing it.
        if (busy.locksDocument) return
        closeCurrent()
        uiState = ViewerUiState.Home(recentsStore.load())
    }

    private fun closeCurrent() {
        renderJob?.cancel()
        closeSearch()
        pageBitmaps.clear()
        renderedWidths.clear()
        lastWindow = null
        currentUri = null
        documentReadsUri = null
        dirty.reset()
        pendingSignature = null
        selectedStamp = null
        selectedTextBox = null
        // History belongs to the open document — never offer to undo an edit made
        // to a file that is no longer on screen.
        history.clear()
        // So do the page checks and the pages already settled (#139, #145): every running
        // check is stopped, and a question still up is answered Cancel.
        pageChecks.reset()
        // And its busy state (#145).
        busy.reset()
        currentPage = 0
        canUndo = false
        canRedo = false
        isPlacingText = false
        pendingTextTap = null
        pendingBodyEdit = null
        scannedHintShown = false
        capabilities = DocumentCapabilities.FULL
        unlockPrompt = null
        passwordPrompt = null
        val doc = document ?: return
        document = null
        viewModelScope.launch {
            try {
                doc.close()
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                // Nothing to tell anyone: the document is already off screen.
            }
        }
    }

    /** A user-facing string in the app's current locale, for toasts and statuses. */
    private fun str(id: Int, vararg args: Any): String =
        getApplication<Application>().getString(id, *args)

    /**
     * "Signature N" for a new library entry. The name is persisted at creation
     * time, so it keeps the language the app was in when the signature was
     * added and does not re-translate if the locale changes later — accepted.
     */
    private fun defaultSignatureName(): String =
        str(R.string.signature_default_name, signatures.size + 1)

    private fun persistReadPermission(uri: Uri) {
        try {
            getApplication<Application>().contentResolver.takePersistableUriPermission(
                uri, android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION
            )
        } catch (_: SecurityException) {
            // Not a persistable grant (e.g. some third-party providers); recents
            // will just round-trip through the picker for this document.
        }
    }

    private fun queryDisplayName(uri: Uri): String {
        getApplication<Application>().contentResolver
            .query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)
            ?.use { cursor ->
                val col = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                if (col >= 0 && cursor.moveToFirst()) return cursor.getString(col)
            }
        return uri.lastPathSegment ?: str(R.string.document)
    }

    override fun onCleared() {
        // viewModelScope is already cancelled here, so the close cannot ride on it
        // without being cancelled — but it must NOT block the main thread either
        // (#53). runBlocking did, and because the engine dispatcher is a single
        // thread and renderJob.cancel() cannot interrupt a native render already
        // running, the wait was as long as that render took.
        //
        // PdfEngine.teardownScope outlives every ViewModel, so the document is
        // still closed exactly once and nothing is leaked. Its close stops any page
        // check still running before it closes the document (#145).
        renderJob?.cancel()
        pageChecks.reset()
        val doc = document
        document = null
        if (doc != null) {
            PdfEngine.closeDetached(doc)
        }
    }

    private companion object {
        /** Handle prefix the engine gives a marked box that carries no id. */
        const val UNTAGGED_TEXT_PREFIX = "text:untagged#"

        /** Margin added to a text box's tight glyph rect when hit-testing a tap. */
        const val TAP_SLOP_POINTS = 6.0

        const val RENDER_MARGIN = 2      // desktop MainViewModel's ±2-page window
        const val MAX_BITMAP_DIM = 2048  // bound worst-case bitmap memory
        const val MAX_SIGNATURE_SOURCE_DIM = 1500  // downscale huge photos before cleanup
        const val SEARCH_DEBOUNCE_MS = 250L  // keep typing from spamming the engine
        const val COPY_BUFFER_BYTES = 1 shl 20  // streaming a document between files (#147)
        // The screenshot search term, document names and typed text are string
        // resources (screenshot_*), so a French run shows French content (#91).

        // Marketing "Add text" shot: the name the customer would print under the
        // signature rule the demo agreement draws at y=400.
        const val SCREENSHOT_TEXT_X = 72.0
        const val SCREENSHOT_TEXT_Y = 372.0
    }
}

/** A line of the document's own text being retyped (#114). */
data class PendingBodyEdit(
    val pageIndex: Int,
    val line: com.megapdf.engine.TextLine,
    /** What the field opens with — the line itself, except in screenshot mode. */
    val initialText: String = line.text,
)
