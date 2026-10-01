package com.megapdf.android

import com.megapdf.engine.TextEditOutcome
import android.app.Application
import android.graphics.Bitmap
import android.net.Uri
import android.provider.OpenableColumns
import android.util.Log
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.megapdf.engine.DocumentFlags
import com.megapdf.engine.LayoutCause
import com.megapdf.engine.PageCheck
import com.megapdf.engine.PdfDocument
import com.megapdf.engine.PdfEngine
import com.megapdf.engine.PdfLoadException
import com.megapdf.engine.PdfPasswordException
import com.megapdf.engine.PdfPermissions
import com.megapdf.engine.PdfSecurity
import com.megapdf.engine.extractPages
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

/**
 * The tag a marketing-capture pose logs under, and the `::error::` convention the desktop
 * apps write to stdout, on the Android side. `scripts/capture-screenshots.sh` clears logcat
 * before each state and fails the run when one appears: the fr-CA `redact` shot of the
 * 2026-09-20 run photographed a page with no mark on it and the run stayed green, because
 * a pose that does not fire has nothing to say.
 */
private const val SCREENSHOT_TAG = "megapdf-screenshot"

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

/**
 * A redaction mark currently selected for move/resize/remove (#329). Marks have no id of
 * their own beyond the core's, so the pair identifies it and [rect] is what the chrome draws.
 */
data class SelectedRedactionMark(
    val pageIndex: Int,
    val markId: Int,
    val rect: com.megapdf.engine.PdfRect,
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
 * A whiteout currently selected for drag/resize/remove (#3). No id of its own — a
 * whiteout is page content, not an annotation — so the pair identifies it, and
 * [objectIndex] changes on every move: there is no native "move in place" for one of
 * these, only a detach and a fresh add at the new bounds.
 */
data class SelectedWhiteout(
    val pageIndex: Int,
    val objectIndex: Int,
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
    data class Home(val recents: List<RecentRow>, val error: String? = null) : ViewerUiState
    data object Loading : ViewerUiState
    data class PasswordNeeded(val uri: Uri, val wrongPassword: Boolean) : ViewerUiState
    data class Viewing(
        val displayName: String,
        val pageSizes: List<PageSize>,
        /** #457: shows the calm, persistent explanation in place of a snackbar or a dialog. */
        val isDynamicXfa: Boolean = false,
    ) : ViewerUiState
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

    // --- Reading mode (#507, #513), docs/reading-mode-plan.md §2 and §4 ---
    //
    // A way of *looking at* the document, not a change to it: nothing here touches the
    // document, the undo history or the file, and the unsaved dot stays exactly as it was.
    // The mode itself is session state (#168 decision 2 — no per-document memory); the two
    // preferences under it are app-level and live in DataStore.

    private val readingPreferences = ReadingPreferences(application)

    /**
     * The chrome-free view is on. Not persisted: [openInReadingMode] is the only thing that
     * decides whether a *newly opened* document starts in it, and nothing remembers what any
     * particular document was last looked at in.
     */
    var readingMode: Boolean by mutableStateOf(false)
        private set

    /** Page colours (#513). Drives the render, and the gutter and bar the screen draws. */
    var pageTint: com.megapdf.engine.PageTint by mutableStateOf(com.megapdf.engine.PageTint.NORMAL)
        private set

    /** *Open documents in reading mode* (#513). Off by default. */
    var openInReadingMode: Boolean by mutableStateOf(false)
        private set

    /**
     * Marketing capture only (`--es screenshot reading`, #613): the floating bar stays up
     * instead of counting down to its idle fade.
     *
     * Set by [applyScreenshotMode] and by nothing else, so no launch a person makes can reach
     * it. The production rules are untouched and still the only ones that run otherwise: the
     * two-second countdown ([READING_BAR_IDLE_MS]), never arming it while touch exploration is
     * on ([readingBarAutoHides]), and a tap on the page toggling the bar either way. What this
     * removes is the race, not the behaviour — the capture is taken ten seconds after the
     * launch, which is five countdowns after the bar would have gone, and the bar is the only
     * thing in a reading-mode frame that says which application it is.
     */
    var screenshotPinsReadingBar: Boolean by mutableStateOf(false)
        private set

    /**
     * Both preferences are observed rather than read once: the Settings screen writes them
     * while a document is open, and the page behind it retints without a reopen. Called from
     * the `init` block at the foot of the class, after every property it touches exists.
     */
    private fun observeReadingPreferences() {
        viewModelScope.launch {
            readingPreferences.pageTint.collect { tint ->
                if (tint == pageTint) return@collect
                pageTint = tint
                // Only the window on screen is redrawn (#513): every other page's bitmap has
                // already been dropped by updateRenderWindow, and the ones still held are
                // re-rendered because the render cache key carries the tint.
                lastWindow?.let { updateRenderWindow(it.firstVisible, it.lastVisible, it.targetWidthPx) }
            }
        }
        viewModelScope.launch {
            readingPreferences.openInReadingMode.collect { openInReadingMode = it }
        }
    }

    /**
     * Enters the chrome-free view. Armed tools disarm here and do not re-arm on the way out
     * (#507): editing is off inside reading mode, and a tool left armed would be a mode the
     * user cannot see. Selections go too — their chrome is chrome, and a screen reader must
     * not find a ✕ or a ✎ floating on a page in the reader-friendly view.
     */
    fun enterReadingMode() {
        if (readingMode) return
        redactMode = false
        whiteoutMode = false
        pendingSignature = null
        isPlacingText = false
        selectedStamp = null
        selectedTextBox = null
        selectedRedactionMark = null
        selectedWhiteout = null
        readingMode = true
    }

    /** Leaves it. Nothing re-arms: what was armed on the way in stays off (#507). */
    fun exitReadingMode() {
        readingMode = false
    }

    // Named for what the Settings rows say rather than for the properties they move, which
    // also keeps them clear of the setters `var pageTint` and `var openInReadingMode` already
    // generate on the JVM.
    fun choosePageColours(tint: com.megapdf.engine.PageTint) {
        viewModelScope.launch { readingPreferences.setPageTint(tint) }
    }

    fun chooseOpenInReadingMode(on: Boolean) {
        viewModelScope.launch { readingPreferences.setOpenInReadingMode(on) }
    }
    private val signatureDir = File(application.filesDir, "signatures")
    private val signatureStore = SignatureLibraryStore(signatureDir)

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

    /** The mark the user has tapped, if any: it draws selection chrome with a removal ✕ (#329). */
    var selectedRedactionMark: SelectedRedactionMark? by mutableStateOf(null)
        private set

    fun toggleRedactMode() {
        redactMode = !redactMode
        if (redactMode) {
            pendingSignature = null
            isPlacingText = false
            whiteoutMode = false
        }
    }

    fun cancelRedactMode() {
        redactMode = false
    }

    // --- Whiteout (#3) ---
    //
    // Unlike a mark, a whiteout is real page content the moment it is placed: it is drawn
    // into the raster like a signature or added text, not held apart like a redaction mark.

    /** The Whiteout tool is armed: the next drag across a page covers that area. */
    var whiteoutMode: Boolean by mutableStateOf(false)
        private set

    /** The whiteout the user has tapped, if any: it draws the same drag/resize/✕ chrome a
     *  selected signature does (#3). */
    var selectedWhiteout: SelectedWhiteout? by mutableStateOf(null)
        private set

    fun toggleWhiteoutMode() {
        whiteoutMode = !whiteoutMode
        if (whiteoutMode) {
            pendingSignature = null
            isPlacingText = false
            redactMode = false
            selectedStamp = null
            selectedTextBox = null
            selectedRedactionMark = null
            selectedWhiteout = null
        }
    }

    fun cancelWhiteoutMode() {
        whiteoutMode = false
    }

    /**
     * Whether a whiteout may be placed or changed without asking first. #558: false is no longer a
     * refusal — it is a question the view model puts before the change — so this says what the
     * document asked for, not what the person may do.
     */
    val canWhiteout: Boolean get() = capabilities.canEditContent

    /**
     * Covers the dragged area (#3): a plain rectangle, never text-snapped the way Redact is —
     * a whiteout covers whatever is under it, drawn or not, so there is nothing to snap to.
     * Kept selected afterwards so its handles appear straight away, like a placed signature.
     */
    fun placeWhiteout(pageIndex: Int, rect: com.megapdf.engine.PdfRect) {
        val doc = document ?: return
        val state = uiState as? ViewerUiState.Viewing ?: return
        if (editingBlocked) return
        val clamped = clampToPage(rect, state.pageSizes[pageIndex])
        val spot = BusySpot(pageIndex, clamped)
        launchEdit(R.string.whiteout_failed) {
            val add = WhiteoutAddOperation(pageIndex, clamped)
            if (!confirmPageRewrite(add, doc, spot)) return@launchEdit
            perform(add, doc, spot)
            selectedWhiteout = SelectedWhiteout(pageIndex, add.currentObjectIndex, clamped)
            whiteoutMode = false
        }
    }

    /** Commits a drag or a corner-handle resize from the selection overlay (#3). */
    fun commitWhiteoutRect(newRect: com.megapdf.engine.PdfRect) {
        val sel = selectedWhiteout ?: return
        val state = uiState as? ViewerUiState.Viewing ?: return
        val doc = document ?: return
        if (editingBlocked) {
            // #145: another change is still going in; drop the dragged overlay, nothing moved.
            selectedWhiteout = null
            return
        }
        val rect = clampToPage(newRect, state.pageSizes[sel.pageIndex])
        if (rect == sel.rect) return
        val spot = BusySpot(sel.pageIndex, rect)
        launchEdit(R.string.whiteout_move_failed) {
            val move = MoveWhiteoutOperation(sel.pageIndex, sel.objectIndex, from = sel.rect, to = rect)
            if (!confirmPageRewrite(move, doc, spot)) {
                // Cancel: it never moved. Dropping the selection drops the dragged overlay too.
                selectedWhiteout = null
                return@launchEdit
            }
            perform(move, doc, spot)
            selectedWhiteout = sel.copy(objectIndex = move.currentObjectIndex, rect = rect)
        }
    }

    fun removeSelectedWhiteout() {
        val sel = selectedWhiteout ?: return
        val doc = document ?: return
        val spot = BusySpot(sel.pageIndex, sel.rect)
        launchEdit(R.string.whiteout_remove_failed) {
            val remove = WhiteoutRemoveOperation(sel.pageIndex, sel.objectIndex)
            if (!confirmPageRewrite(remove, doc, spot)) return@launchEdit
            perform(remove, doc, spot)
        }
    }

    fun deselectWhiteout() {
        selectedWhiteout = null
    }

    /**
     * Marks the dragged area. A drag across text marks the text, grown to whole glyphs, so
     * half a glyph is never left behind; a drag across a picture marks the rectangle.
     * Nothing is removed, and nothing on the page changes.
     *
     * The whole drag is **one** history entry carrying every mark it made (#329): a drag
     * across six lines is six core marks, and six presses of Undo to take back one gesture
     * is not what "undoable" means.
     */
    fun markForRedaction(pageIndex: Int, rect: com.megapdf.engine.PdfRect) {
        val doc = document ?: return
        launchEdit(R.string.redact_failed) {
            // Not perform(): the core's text selection MAKES the marks as it answers, so the
            // operation is built from what came back and recorded already-applied.
            val made = busy.pageWork(BusyLabel.APPLYING, BusySpot(pageIndex)) {
                doc.onPageForRedaction(pageIndex) { page ->
                    val ids = page.markTextForRedaction(rect).ifEmpty {
                        listOf(page.markForRedaction(rect)).filter { it >= 0 }
                    }
                    page.redactionMarks().filter { it.markId in ids }
                }
            }
            if (made.isEmpty()) return@launchEdit
            history.record(
                RedactMarkOperation(pageIndex, made.map { it.rect }, made.map { it.markId }, adding = true)
            )
            canUndo = history.canUndo
            canRedo = history.canRedo
            refreshRedactionMarks()
            redactMode = false
            // Say that it landed (#173). A mark is a faint translucent band and the tool
            // disarms itself once it is placed, so with nothing said there is no
            // confirmation at all — and for a screen reader there was nothing to find.
            // The Mac pass found the same silence; there the string existed and was
            // overwritten, here it existed and was never used.
            statusMessage = str(R.string.redact_mark_placed)
        }
    }

    /** Removes one mark, as an ordinary undoable step (#329). */
    fun removeRedactionMark(pageIndex: Int, markId: Int) {
        val doc = document ?: return
        val rect = redactionMarks[pageIndex]?.firstOrNull { it.markId == markId }?.rect
        launchEdit(R.string.redact_failed) {
            // The rectangle is what an undo has to put back, and only the core knows it.
            // An operation recorded without one could still remove the mark, but it could
            // never bring it back, so this refuses rather than half-works.
            if (rect == null) {
                refreshRedactionMarks()
                return@launchEdit
            }
            selectedRedactionMark = null
            perform(RedactMarkOperation(pageIndex, listOf(rect), listOf(markId), adding = false), doc)
            statusMessage = str(R.string.redact_mark_removed)
        }
    }

    /** Removes every mark on the document, as one undoable step (#329). */
    fun clearRedactionMarks() {
        val doc = document ?: return
        if (redactionMarkCount == 0) return
        launchEdit(R.string.redact_failed) {
            // The marks as they stand, ids and all: the undo re-marks the rectangles, and the
            // history needs the ids it replaces to keep the rest of the operations pointed at
            // the same marks (#429).
            val marks = redactionMarks
            // The history wants one page; a clear can span several, so it takes the first.
            val first = marks.keys.minOrNull() ?: 0
            selectedRedactionMark = null
            perform(ClearRedactionMarksOperation(first, marks), doc)
            statusMessage = str(R.string.redact_marks_cleared)
        }
    }

    /** Selects a mark, or clears the selection when the same one is tapped again (#329). */
    fun selectRedactionMark(pageIndex: Int, markId: Int) {
        val mark = redactionMarks[pageIndex]?.firstOrNull { it.markId == markId }
        val already = selectedRedactionMark
        selectedRedactionMark = when {
            mark == null -> null
            already?.markId == markId && already.pageIndex == pageIndex -> null
            else -> SelectedRedactionMark(pageIndex, markId, mark.rect)
        }
        if (selectedRedactionMark != null) {
            // One thing is selected at a time, as for stamps and text boxes.
            selectedStamp = null
            selectedTextBox = null
            selectedWhiteout = null
        }
    }

    fun deselectRedactionMark() {
        selectedRedactionMark = null
    }

    /** The same for redaction — see [canWhiteout] on what false means since #558. */
    val canRedact: Boolean get() = capabilities.canEditContent

    /**
     * Commits a dragged or resized mark, as one undoable step (#329). A resize is not a
     * re-snap: growing or shrinking keeps the rectangle the person drew, because apply
     * removes by intersection with the drawn area, and re-snapping on every drag tick would
     * make the box jump under the finger.
     */
    fun commitRedactionMarkRect(pageIndex: Int, markId: Int, rect: com.megapdf.engine.PdfRect) {
        val doc = document ?: return
        val current = selectedRedactionMark ?: return
        if (current.pageIndex != pageIndex || current.markId != markId) return
        val size = (uiState as? ViewerUiState.Viewing)?.pageSizes?.getOrNull(pageIndex) ?: return
        val clamped = clampToPage(rect, size)
        if (clamped == current.rect) return
        launchEdit(R.string.redact_failed) {
            perform(MoveRedactionMarkOperation(pageIndex, markId, current.rect, clamped), doc)
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

    /**
     * Waits up to [limitMs] for a mark to be on page 0, so the capture pose never photographs
     * a page whose mark has not landed yet — or one that never will. True when one is there.
     */
    private suspend fun awaitRedactionMark(limitMs: Long): Boolean {
        var waited = 0L
        while (redactionMarks[0].isNullOrEmpty() && waited < limitMs) {
            delay(50)
            waited += 50
        }
        return !redactionMarks[0].isNullOrEmpty()
    }

    /**
     * Reads every mark back from the core. This is the **only** function that writes the
     * marks, the count, and the selection that follows them — so it states the truth even
     * when the truth is that there is nothing (#329). It used to return early with no
     * document (`?: return`), which left the previous document's marks on screen and, worse,
     * left `redactionMarkCount` non-zero, so Save asked about a redaction the new document
     * did not have.
     */
    private suspend fun refreshRedactionMarks() {
        val doc = document
        if (doc == null) {
            redactionMarks = emptyMap()
            selectedRedactionMark = null
            return
        }
        val byPage = mutableMapOf<Int, List<com.megapdf.engine.RedactionMark>>()
        for (index in 0 until doc.pageCount()) {
            val marks = doc.onPageForRedaction(index) { it.redactionMarks() }
            if (marks.isNotEmpty()) byPage[index] = marks
        }
        redactionMarks = byPage
        // A selection the core no longer has is not a selection: undo, a removal and a clear
        // all take marks away, and chrome left pointing at one would move a mark that is not
        // there.
        selectedRedactionMark = selectedRedactionMark?.let { selected ->
            byPage[selected.pageIndex]?.firstOrNull { it.markId == selected.markId }
                ?.let { SelectedRedactionMark(selected.pageIndex, selected.markId, it.rect) }
        }
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
        selectedRedactionMark = null
        redactionSummary = describeRedaction(report.counts)
        renderedPages.clear()
        pageBitmaps.clear()
        lastWindow?.let { (first, last, width) -> updateRenderWindow(first, last, width) }
        return true
    }

    /**
     * Export as Markdown with marks on the document (#409, as iOS does): the marked content
     * has to be gone before any export reads the text, or it would land in the `.md` file.
     * Unlike the two save paths nothing is written to the PDF afterwards, so the removal is
     * in memory only — the document is marked edited, and closing it asks to save, exactly
     * as any other unsaved change would.
     */
    suspend fun applyRedactionsForExport(): Boolean {
        if (redactionMarkCount == 0) return true
        if (!applyRedactions()) return false
        dirty.markEdited()
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

    // --- Page tools (contract 10, #174) ---
    //
    // Rotate, delete, reorder, combine and extract. The engine does all of it; what lives here is
    // the Pages grid's state, the renumbering of everything this side keeps by page index
    // ([PageShift]), and turning the engine's refusals into sentences.
    //
    // Two engine limits reach the person rather than being hidden, and both are deliberate:
    //
    //  * **Fields in a hierarchy.** Import and extract refuse a page whose form fields sit in a
    //    /Parent hierarchy the page copy cannot carry — about 0.8% of a real corpus — and the
    //    refusal is whole: nothing is changed, because the alternative is a document whose fields
    //    have quietly lost their names and values. [pageToolRefusal] says so in the user's words.
    //  * **What PDFium's writer declines.** The other well-known limit — the layout guard that
    //    declines roughly half the corpus for an edit that would rewrite a page's content (#118,
    //    #128) — is not reachable from any of these calls, and that is worth stating rather than
    //    leaving to be discovered: rotating sets /Rotate, and delete, move, insert and import move
    //    whole page objects. None of them rewrites a content stream, so contract 10 has no
    //    MEGAPDF_ERR_LAYOUT among its statuses. The warning that guards the edits which *do*
    //    regenerate a page ([confirmPageRewrite]) therefore stays where it is, and the page tools
    //    neither ask it nor need to.

    /**
     * The Pages screen is up. Belongs to the open document, not to the app: closing the document
     * closes it, and nothing remembers it for the next one.
     */
    var isPagesOpen: Boolean by mutableStateOf(false)
        private set

    /**
     * Which pages the grid has selected. Page indices, so this is one of the things a page
     * operation renumbers — a page that is deleted leaves the selection, and a page that moves
     * stays selected where it went.
     */
    var selectedPages: Set<Int> by mutableStateOf(emptySet())
        private set

    /** Thumbnails for the grid, by page index: the visible window only, as the viewer's are (#147). */
    val pageThumbnails = mutableStateMapOf<Int, Bitmap>()
    private val renderedThumbnails = HashMap<Int, RenderedPage>()
    private var thumbnailJob: Job? = null
    private var lastThumbnailWindow: RenderWindow? = null

    /**
     * A page tool refused. Nothing was changed, and the screen says what happened and why — the
     * same stance the redaction refusal takes (#173): a refusal is information, not a failure.
     */
    var pageToolRefusal: com.megapdf.engine.PageToolRefusal? by mutableStateOf(null)

    fun openPages() {
        if (document == null) return
        // Editing tools armed in the viewer have nothing to do with pages, and a tool left armed
        // behind this screen would fire on the way back (the reading-mode rule, #507).
        redactMode = false
        whiteoutMode = false
        pendingSignature = null
        isPlacingText = false
        selectedStamp = null
        selectedTextBox = null
        selectedRedactionMark = null
        selectedWhiteout = null
        isPagesOpen = true
    }

    /** Back out of the grid. The selection and the thumbnails go with it. */
    fun closePages() {
        isPagesOpen = false
        selectedPages = emptySet()
        dropThumbnails()
    }

    fun togglePageSelection(pageIndex: Int) {
        selectedPages =
            if (pageIndex in selectedPages) selectedPages - pageIndex else selectedPages + pageIndex
    }

    fun selectAllPages() {
        selectedPages = ((uiState as? ViewerUiState.Viewing)?.pageSizes?.indices ?: IntRange.EMPTY).toSet()
    }

    fun clearPageSelection() {
        selectedPages = emptySet()
    }

    /**
     * True when the pages may be rearranged: the document allows it, or the person was told that
     * its author asked they not be and chose to continue (#558, ADR-004 decision 11).
     *
     * The grid no longer disables the tools for a restricted document — a greyed-out button is the
     * wall #558 replaced, and it cannot explain itself — so this is where the asking happens for
     * every one of them. The question is drawn by [MainActivity], over whichever screen is up,
     * because this one is nearly always asked from the Pages grid.
     */
    private suspend fun assembleAllowed(): Boolean =
        permitted(PermissionClass.ASSEMBLY, capabilities.canAssemblePages)

    /** Turns every selected page a quarter turn: clockwise for 1, anticlockwise for -1. */
    fun rotateSelectedPages(quarterTurns: Int) {
        val doc = document ?: return
        val pages = selectedPages.sorted()
        if (pages.isEmpty()) return
        launchPageEdit {
            if (!assembleAllowed()) return@launchPageEdit
            performPageEdit(RotatePagesOperation(pages, quarterTurns), doc)
            statusMessage =
                if (pages.size == 1) str(R.string.page_turned) else str(R.string.pages_turned, pages.size)
        }
    }

    /** Deletes the selection as one undoable step; refuses to empty the document. */
    fun deleteSelectedPages() {
        val doc = document ?: return
        val state = uiState as? ViewerUiState.Viewing ?: return
        val pages = selectedPages.sorted()
        // A PDF must keep a page. The button is disabled for this, so reaching it means the
        // selection changed underneath; either way nothing is asked of the engine.
        if (pages.isEmpty() || pages.size >= state.pageSizes.size) return
        launchPageEdit {
            if (!assembleAllowed()) return@launchPageEdit
            selectedPages = emptySet()
            performPageEdit(DeletePagesOperation(pages), doc)
            statusMessage =
                if (pages.size == 1) str(R.string.page_deleted) else str(R.string.pages_deleted, pages.size)
        }
    }

    /** Moves one page so that it stands at [to] afterwards — a drag, or *Move to…*. */
    fun movePage(from: Int, to: Int) {
        val doc = document ?: return
        val count = (uiState as? ViewerUiState.Viewing)?.pageSizes?.size ?: return
        if (from == to || from !in 0 until count || to !in 0 until count) return
        launchPageEdit {
            if (!assembleAllowed()) return@launchPageEdit
            performPageEdit(MovePageOperation(from, to), doc)
            statusMessage = str(R.string.page_moved, to + 1)
        }
    }

    /**
     * A blank page after the selection, or at the end when nothing is selected — the size of the
     * page it follows, so it matches a document of mixed page sizes rather than imposing Letter.
     */
    fun insertBlankPage() {
        val doc = document ?: return
        val state = uiState as? ViewerUiState.Viewing ?: return
        val after = selectedPages.maxOrNull() ?: (state.pageSizes.size - 1)
        val at = (after + 1).coerceIn(0, state.pageSizes.size)
        val model = state.pageSizes.getOrNull(after) ?: DEFAULT_PAGE_SIZE
        launchPageEdit {
            if (!assembleAllowed()) return@launchPageEdit
            performPageEdit(
                InsertBlankPageOperation(at, model.widthPoints, model.heightPoints), doc,
            )
            statusMessage = str(R.string.page_inserted, at + 1)
        }
    }

    /**
     * Combine (#174): the pages of the picked PDF, after the selection or at the end.
     *
     * The core reads the other file by path and keeps it open for as long as this document is —
     * imported pages are read from it on demand (#147) — so the picked document is copied into the
     * cache and opened from there. The copy's *name* goes straight away, as an open does: the file
     * itself lives on in the engine's own handle, so nothing is left behind in the cache and
     * nothing has to be cleaned up when the document closes.
     */
    fun importPagesFrom(uri: Uri) {
        val doc = document ?: return
        val state = uiState as? ViewerUiState.Viewing ?: return
        val at = (selectedPages.maxOrNull()?.plus(1) ?: state.pageSizes.size)
            .coerceIn(0, state.pageSizes.size)
        val app = getApplication<Application>()
        launchPageEdit {
            // Asked before the pick is copied into the cache, so a Cancel copies nothing. The
            // *source* file's own copy bit is a separate matter the core still refuses on: this
            // choice is about the open document, which the person may be the author of.
            if (!assembleAllowed()) return@launchPageEdit
            val copy = File(app.cacheDir, "import-${System.nanoTime()}.pdf")
            try {
                withContext(Dispatchers.IO) {
                    app.contentResolver.openInputStream(uri)?.use { input ->
                        copy.outputStream().use { input.copyTo(it, COPY_BUFFER_BYTES) }
                    } ?: throw IllegalStateException("provider returned no stream")
                }
                val operation = ImportPagesOperation(copy.path, at)
                performPageEdit(operation, doc)
                statusMessage = if (operation.imported == 1) str(R.string.page_added)
                else str(R.string.pages_added, operation.imported)
            } finally {
                withContext(NonCancellable + Dispatchers.IO) { copy.delete() }
            }
        }
    }

    /**
     * Split (#174): the selected pages as a new PDF at [uri].
     *
     * The document is not touched and nothing is recorded — an extract is a copy, not an edit — so
     * this writes like a Save a copy rather than going through the history: the core writes the
     * whole file to a temporary name of its own, reopens it and checks its page count, and only
     * then is it streamed to the destination the picker gave (SAF has no atomic rename, #18).
     */
    fun extractSelectedPagesTo(uri: Uri) {
        val doc = document ?: return
        val pages = selectedPages.sorted()
        if (pages.isEmpty() || busy.locksDocument) return
        val app = getApplication<Application>()
        // Stop only, no progress (#145): the engine has taken a cancel flag since #174, but it is
        // one call with no interior to count against — the same reasoning the Mac and Windows
        // passes followed for their own extract. A stopped extract leaves nothing at [uri], since
        // the engine's own contract for a cancelled megapdf_pages_extract is that nothing is left
        // at the temp path either, and [temp] is deleted below regardless.
        val cancel = com.megapdf.engine.PdfCancelFlag()
        var stoppedByUser = false
        viewModelScope.launch {
            // What an extract needs is the copy bit (#174). #558: that too is a request rather
            // than a lock, so it is said and then it is the person's call — its own class, because
            // the author said it separately from "do not change this" and may have meant it
            // differently (a handout fine to excerpt but not to restructure, or the reverse).
            if (!permitted(PermissionClass.EXTRACTION, capabilities.canExtractPages)) return@launch
            // Locks like a save: the pages being copied are read off the document, so an edit going
            // in underneath would put half of one into the new file. Taken after the question (#558),
            // so the document is not held locked, and no Stop appears in the busy strip, while a
            // dialog waits — which also means the document and the selection have to still be the
            // ones the question was asked about.
            if (document !== doc || busy.locksDocument) return@launch
            if (selectedPages.sorted() != pages) return@launch
            val token = busy.beginDocument(
                BusyLabel.SAVING, locks = true, cancel = { stoppedByUser = true; cancel.cancel() },
            )
            val temp = File(app.cacheDir, "extract-${System.nanoTime()}.pdf")
            try {
                doc.extractPages(pages, temp.path, cancel)
                withContext(Dispatchers.IO) {
                    val pfd = app.contentResolver.openFileDescriptor(uri, "wt")
                        ?: throw IllegalStateException("provider returned no descriptor")
                    pfd.use {
                        java.io.FileOutputStream(it.fileDescriptor).use { out ->
                            java.io.FileInputStream(temp).use { input -> input.copyTo(out, COPY_BUFFER_BYTES) }
                            out.fd.sync()
                        }
                    }
                }
                statusMessage = if (pages.size == 1) str(R.string.page_saved_as)
                else str(R.string.pages_saved_as, pages.size)
            } catch (e: CancellationException) {
                if (stoppedByUser) statusMessage = str(R.string.work_stopped)
                throw e
            } catch (e: com.megapdf.engine.PdfPagesException) {
                pageToolRefusal = e.refusal
            } catch (_: SecurityException) {
                statusMessage = str(R.string.save_no_permission)
            } catch (_: Exception) {
                statusMessage = str(R.string.pages_save_failed)
            } finally {
                withContext(NonCancellable + Dispatchers.IO) { temp.delete() }
                token.end()
            }
        }
    }

    /** What a refusal means, in the user's words rather than the engine's (#174). */
    fun describePageToolRefusal(refusal: com.megapdf.engine.PageToolRefusal): String = str(
        when (refusal) {
            com.megapdf.engine.PageToolRefusal.RESTRICTED -> R.string.pages_refused_restricted
            com.megapdf.engine.PageToolRefusal.SOURCE_NEEDS_PASSWORD -> R.string.pages_refused_source_password
            com.megapdf.engine.PageToolRefusal.SOURCE_RESTRICTED -> R.string.pages_refused_source_restricted
            com.megapdf.engine.PageToolRefusal.FIELD_HIERARCHY -> R.string.pages_refused_fields
            com.megapdf.engine.PageToolRefusal.LAST_PAGE -> R.string.pages_refused_last_page
            com.megapdf.engine.PageToolRefusal.FILE -> R.string.pages_refused_file
            com.megapdf.engine.PageToolRefusal.REDACTION_POISONED -> R.string.pages_refused_poisoned
            com.megapdf.engine.PageToolRefusal.ENGINE -> R.string.pages_refused_engine
        }
    )

    /**
     * One page operation, with the busy strip the Pages screen shows and the refusals named.
     *
     * A refusal leaves the document exactly as it was, which is contract 10's own promise, so
     * there is nothing to put back. Anything *else* going wrong is different: a multi-page delete
     * is several engine calls, and one of them failing part-way would leave the document no longer
     * the document this side thinks it has — so that path re-reads the pages from the engine
     * rather than trusting its own bookkeeping.
     */
    private fun launchPageEdit(block: suspend () -> Unit) {
        if (editingBlocked) return
        editsInFlight++
        viewModelScope.launch {
            try {
                block()
            } catch (e: CancellationException) {
                throw e
            } catch (e: com.megapdf.engine.PdfPagesException) {
                pageToolRefusal = e.refusal
                resyncPagesFromEngine()
            } catch (_: Exception) {
                statusMessage = str(R.string.pages_failed)
                resyncPagesFromEngine()
            } finally {
                editsInFlight--
            }
        }
    }

    /**
     * [perform], with document-level busy feedback: the Pages screen draws no page spinners, so
     * every structure operation reports in its own strip rather than at [PdfEditOperation.pageIndex]
     * — which for a delete is a page the operation is about to remove, the defect the Mac and
     * Windows passes found and fixed (#145). [busyLabel] names what the strip says while it runs.
     */
    private suspend fun performPageEdit(operation: PdfEditOperation, doc: PdfDocument) {
        val token = busy.beginDocument(operation.busyLabel)
        try {
            perform(operation, doc)
        } finally {
            token.end()
        }
    }

    /**
     * Everything this side keeps by page index, renumbered after a page operation (#174).
     *
     * The core has already put its own per-page state right — an open page handle follows its
     * page, and so do that page's marks, verdicts and detached objects — and contract 10 says in
     * as many words that the app renumbers its own. This is that: the page sizes the list lays
     * out from, the rendered bitmaps and thumbnails (kept, not thrown away, so a delete does not
     * cost a re-render of every page that did not move), the grid's selection, and the search
     * hits. Two things are dropped rather than renumbered on purpose: the marks, because the
     * core is asked for them again, and the pages already *settled* for the page-rewrite warning
     * (#139), because a settled index after a move names a different page and being asked once
     * more is the harmless way to be wrong.
     */
    private suspend fun afterPagesRenumbered(shifts: List<PageShift>) {
        val doc = document ?: return
        val state = uiState as? ViewerUiState.Viewing ?: return
        var sizes = state.pageSizes
        for (shift in shifts) {
            sizes = sizes.shiftedBy(shift, inserted = insertedPageSizes(doc, shift))
        }
        // The engine is the authority on how many pages there are; if this side's arithmetic
        // disagrees with it, this side is wrong and re-reads rather than lays out a document that
        // does not exist.
        val count = doc.pageCount()
        if (sizes.size != count) sizes = readPageSizes(doc)
        uiState = state.copy(pageSizes = sizes)

        val movedBitmaps = pageBitmaps.toMap().shiftedBy(shifts)
        pageBitmaps.clear()
        pageBitmaps.putAll(movedBitmaps)
        val movedRenders = renderedPages.toMap().shiftedBy(shifts)
        renderedPages.clear()
        renderedPages.putAll(movedRenders)
        val movedThumbnails = pageThumbnails.toMap().shiftedBy(shifts)
        pageThumbnails.clear()
        pageThumbnails.putAll(movedThumbnails)
        val movedThumbnailKeys = renderedThumbnails.toMap().shiftedBy(shifts)
        renderedThumbnails.clear()
        renderedThumbnails.putAll(movedThumbnailKeys)

        selectedPages = selectedPages.shiftedBy(shifts).filter { it < sizes.size }.toSet()
        searchHits = searchHits.mapNotNull { hit ->
            shifts.mapIndex(hit.pageIndex).takeIf { it >= 0 }?.let { hit.copy(pageIndex = it) }
        }
        currentHitIndex = if (searchHits.isEmpty()) -1 else currentHitIndex.coerceIn(0, searchHits.size - 1)
        currentPage = currentPage.coerceIn(0, (sizes.size - 1).coerceAtLeast(0))
        openPageChecks(doc)
        dirty.markEdited()
        refreshRedactionMarks()
        redrawPages(sizes.size)
    }

    /** The sizes of the pages a shift brought in — only the engine knows them. */
    private suspend fun insertedPageSizes(doc: PdfDocument, shift: PageShift): List<PageSize> {
        if (shift !is PageShift.Inserted) return emptyList()
        return (shift.at until shift.at + shift.count).map { index ->
            val page = doc.openPage(index)
            try {
                PageSize(page.widthPoints, page.heightPoints)
            } finally {
                page.close()
            }
        }
    }

    private suspend fun readPageSizes(doc: PdfDocument): List<PageSize> {
        val count = doc.pageCount()
        val sizes = ArrayList<PageSize>(count)
        for (index in 0 until count) {
            val page = doc.openPage(index)
            try {
                sizes += PageSize(page.widthPoints, page.heightPoints)
            } finally {
                page.close()
            }
        }
        return sizes
    }

    /**
     * A page operation half-applied, or one that failed in a way contract 10 does not promise to
     * leave the document untouched by: the document is re-read and every picture of it dropped.
     * Not an optimisation — the alternative is a page list laid out from sizes the document no
     * longer has.
     */
    private suspend fun resyncPagesFromEngine() {
        val doc = document ?: return
        val state = uiState as? ViewerUiState.Viewing ?: return
        val sizes = try {
            readPageSizes(doc)
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            return
        }
        if (sizes.size != state.pageSizes.size) dirty.markEdited()
        uiState = state.copy(pageSizes = sizes)
        pageBitmaps.clear()
        renderedPages.clear()
        dropThumbnails()
        selectedPages = selectedPages.filter { it < sizes.size }.toSet()
        currentPage = currentPage.coerceIn(0, (sizes.size - 1).coerceAtLeast(0))
        openPageChecks(doc)
        refreshRedactionMarks()
        redrawPages(sizes.size)
    }

    /** The pages the viewer and the grid are showing, redrawn for a document that has changed. */
    private fun redrawPages(pageCount: Int) {
        lastWindow?.clampedTo(pageCount)?.let {
            updateRenderWindow(it.firstVisible, it.lastVisible, it.targetWidthPx)
        }
        lastThumbnailWindow?.clampedTo(pageCount)?.let {
            updateThumbnailWindow(it.firstVisible, it.lastVisible, it.targetWidthPx)
        }
    }

    /** A rotation: the same pages in the same order, each of them a different shape. */
    private suspend fun afterPagesTurned(pages: List<Int>) {
        val doc = document ?: return
        val state = uiState as? ViewerUiState.Viewing ?: return
        val sizes = state.pageSizes.toMutableList()
        for (index in pages) {
            if (index !in sizes.indices) continue
            val page = doc.openPage(index)
            try {
                // megapdf_page_width/height answer the *rotated* size (contract 10), so a quarter
                // turn swaps them and the page list lays the page out the new way up.
                sizes[index] = PageSize(page.widthPoints, page.heightPoints)
            } finally {
                page.close()
            }
            renderedPages.remove(index)
            pageBitmaps.remove(index)
            renderedThumbnails.remove(index)
            pageThumbnails.remove(index)
        }
        uiState = state.copy(pageSizes = sizes)
        dirty.markEdited()
        redrawPages(sizes.size)
    }

    /**
     * The thumbnails the Pages grid is showing: the visible window ± [THUMBNAIL_MARGIN], and
     * nothing else held. A thousand-page document must open this screen as quickly as a two-page
     * one (#147), which means the grid asks for what it can see and the rest is never drawn.
     */
    fun updateThumbnailWindow(firstVisible: Int, lastVisible: Int, targetWidthPx: Int) {
        val state = uiState as? ViewerUiState.Viewing ?: return
        val doc = document ?: return
        if (state.pageSizes.isEmpty()) return
        lastThumbnailWindow = RenderWindow(firstVisible, lastVisible, targetWidthPx)
        val window = (firstVisible - THUMBNAIL_MARGIN).coerceAtLeast(0)..
            (lastVisible + THUMBNAIL_MARGIN).coerceAtMost(state.pageSizes.size - 1)

        for (index in pageThumbnails.keys.toList()) {
            if (index !in window) {
                pageThumbnails.remove(index)
                renderedThumbnails.remove(index)
            }
        }

        thumbnailJob?.cancel()
        val tint = pageTint
        thumbnailJob = viewModelScope.launch {
            for (index in window) {
                val size = state.pageSizes.getOrNull(index) ?: continue
                val idealWidth = targetWidthPx.coerceIn(1, MAX_THUMBNAIL_WIDTH).toDouble()
                val idealHeight = idealWidth * size.heightPoints / size.widthPoints
                val (width, height) = PdfEngine.renderSize(idealWidth, idealHeight)
                val key = RenderedPage(width, tint)
                if (renderedThumbnails[index] == key) continue
                try {
                    val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
                    val page = doc.openPage(index)
                    try {
                        page.render(bitmap, tint)
                    } finally {
                        page.close()
                    }
                    pageThumbnails[index] = bitmap
                    renderedThumbnails[index] = key
                } catch (e: CancellationException) {
                    throw e
                } catch (_: Exception) {
                    // A page that will not draw leaves an empty cell, as it leaves a blank page in
                    // the viewer (#145), and the rest of the grid still fills in.
                }
            }
        }
    }

    private fun dropThumbnails() {
        thumbnailJob?.cancel()
        thumbnailJob = null
        pageThumbnails.clear()
        renderedThumbnails.clear()
        lastThumbnailWindow = null
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
     *
     * **This is the only predicate for "an editing command is refused right now", and the screen
     * greys its controls out on this and nothing else.** It used to have a near-twin,
     * `toolsDisabled` (`locksDocument || page.isVisible`), which was what the toolbar actually
     * dimmed on — a strictly smaller condition, chosen so that page work too quick to show a
     * spinner would not make the whole toolbar blink. The cost of that choice was a window of a
     * few hundred milliseconds after every edit in which the toolbar offered an Undo this
     * property would refuse, and `launchEdit` drops a refused command in silence: the person
     * taps Undo, nothing happens, and that is reported as lost work rather than as a flicker.
     * Dave reversed the #145 trade-off for 2.2 on exactly that ground, so the twin is gone and
     * what is offered is what will be acted on.
     *
     * The blink it was avoiding was then measured, which #145 never did: on the CI emulator a
     * checkbox tap holds this for **5.6 ms**, a page rotation for under a millisecond, an undo
     * for 1.5-77 ms. A frame is 16.7 ms, so the common cases do not survive to be drawn at all.
     *
     * Nothing became *newly* enabled when the twin went, which is what made the swap safe to
     * make in one step: every `busy.page` spinner is started inside a [launchEdit] or
     * [launchPageEdit] block, so `page.isVisible` implies `editsInFlight > 0`, and
     * `locksDocument` is a term of this property too. The old condition was a subset of this
     * one, so unifying them can only ever disable something earlier, never offer something new.
     */
    val editingBlocked: Boolean
        get() = editsInFlight > 0 || pageRewriteDeciding || busy.locksDocument

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
        seedScreenshotSignatures(app)
        when (state) {
            "home" -> {
                val now = System.currentTimeMillis()
                val day = 86_400_000L
                uiState = ViewerUiState.Home(listOf(
                    RecentRow(RecentEntry("demo://1", app.getString(R.string.screenshot_document_name),
                        now - day / 2, listOf(app.getString(R.string.screenshot_location_1)))),
                    RecentRow(RecentEntry("demo://2", app.getString(R.string.screenshot_recent_2),
                        now - 2 * day, app.getString(R.string.screenshot_location_2).split(" › "))),
                    RecentRow(RecentEntry("demo://3", app.getString(R.string.screenshot_recent_3),
                        now - 6 * day, listOf(app.getString(R.string.screenshot_location_3)))),
                ), null)
            }
            "viewer", "sign", "draw", "search", "text", "text-edit", "redact",
            "reading", "pages" -> {
                // The sheet is what a pose *opens over* the document, and neither of the two
                // #613 poses is one: reading mode takes chrome away rather than adding any, and
                // the page grid replaces the viewer instead of sitting over it.
                screenshotSheet =
                    if (state == "viewer" || state == "reading" || state == "pages") null else state
                // Two poses need the six-page agreement rather than the one-page one: a grid of
                // a single thumbnail says nothing, and the reading bar would read "Page 1 of 1".
                // Same page 1, same document name, so the set is still one document (#613).
                val asset =
                    if (state == "reading" || state == "pages") R.string.screenshot_demo_pages_asset
                    else R.string.screenshot_demo_asset
                viewModelScope.launch {
                    try {
                        val bytes = withContext(Dispatchers.IO) {
                            app.assets.open(app.getString(asset)).use { it.readBytes() }
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
                        if (state == "reading" || state == "pages") {
                            // Both of these are a picture of several pages. The asset is
                            // chosen above, so a count of one here means the wrong file was
                            // bundled, not that the pose asked for the wrong thing.
                            if (count < 3) {
                                Log.e(
                                    SCREENSHOT_TAG,
                                    "::error:: the '$state' pose opened a document of $count "
                                        + "page(s); it needs the six-page agreement "
                                        + "(assets/demo*-pages.pdf)",
                                )
                            }
                        }
                        if (state == "reading") {
                            // Reading mode (#505, #507) as the listing's lead slot (#613). The
                            // hard part of this picture is that the feature's whole point is
                            // that there is nothing left to photograph: the app bars are not
                            // composed, the system bars go immersive, and what is left is a
                            // page. Two things are deliberate, and they are the same two the
                            // desktop pose makes.
                            //
                            // The floating bar is *pinned* rather than left to its two-second
                            // idle countdown. It is the only thing in the frame that names the
                            // application, and the capture is taken ten seconds after the
                            // launch — five idle countdowns later. Nothing a person does
                            // reaches this: READING_BAR_IDLE_MS and readingBarAutoHides are
                            // untouched, and the bar still hides on a tap as it always did.
                            //
                            // The page colour is left at Normal. Sepia and Night belong to
                            // reading mode too and would make the image unmistakable, but a
                            // set whose first image is the only tinted one reads as a different
                            // app from the seven behind it; the tint is named in the listing
                            // copy, where a reader meets it as a choice.
                            screenshotPinsReadingBar = true
                            enterReadingMode()
                            if (!readingMode) {
                                Log.e(
                                    SCREENSHOT_TAG,
                                    "::error:: reading pose: the mode did not turn on, so this "
                                        + "would be an ordinary viewer shot wearing the reading "
                                        + "slot's caption",
                                )
                            }
                        }
                        if (state == "pages") {
                            // The page grid with a selection (#174), for the page-tools slot
                            // (#613). On a phone the tools are a screen of their own — not the
                            // desktops' sidebar beside the document — and the selection turns
                            // the top bar into what can be done to the pages picked. That
                            // difference is the point of the picture, so it is shown rather
                            // than normalised towards the Mac's.
                            openPages()
                            // Two pages, not one: a single selected cell reads as "the page you
                            // are on", and every tool on this bar acts on a set.
                            togglePageSelection(1)
                            togglePageSelection(2)
                            if (!isPagesOpen || selectedPages != setOf(1, 2)) {
                                Log.e(
                                    SCREENSHOT_TAG,
                                    "::error:: pages pose: grid open=$isPagesOpen, "
                                        + "selection=$selectedPages — the selection bar is the "
                                        + "picture and it is not posed",
                                )
                            }
                            viewModelScope.launch {
                                // The grid asks for its own thumbnails once it is laid out, and
                                // a cell that has not rendered yet is a grey rectangle. The
                                // capture is taken at ten seconds; this looks at six, so a set
                                // of empty cells goes red rather than quietly shipping. Not a
                                // wait that *makes* them render — nothing here can — but the
                                // pose's word on whether they did.
                                delay(6_000)
                                val missing = setOf(0, 1, 2).filter { it !in pageThumbnails }
                                if (missing.isNotEmpty()) {
                                    Log.e(
                                        SCREENSHOT_TAG,
                                        "::error:: pages pose: no thumbnail rendered for "
                                            + "page(s) ${missing.map { it + 1 }} six seconds in "
                                            + "— the grid would photograph as empty cells",
                                    )
                                }
                            }
                        }
                        if (state == "search") {
                            // Seed here, not from the UI: the document and the
                            // Viewing state are both already set, so the sweep can
                            // never hit updateSearchQuery's "nothing open" early
                            // return, and the debounce is skipped so the hits and
                            // the "N of M" count are on screen without any wait.
                            startSearch(app.getString(R.string.screenshot_search_term), debounceMs = 0L)
                        }
                        if (state == "redact") {
                            // The state the feature has to be legible in (#173): a line of
                            // the demo document marked — a translucent box you can still
                            // read through, over text you are about to remove — with its
                            // selection chrome, so the shot also shows that a mark can be
                            // taken off (#329). The line is found by what it says rather
                            // than by a rectangle that has to be right, so the shot lands
                            // on a sentence in every language.
                            //
                            // Which sentence matters (#395): since #408 the ✕ chip hangs
                            // from the marked line's right end over the line below, so the
                            // phrase names the LAST line of the opening paragraph and the
                            // chip lands on whitespace. The phrase is the only green path:
                            // "client nommé" never matched (demo-fr.pdf breaks it across
                            // two lines) and the pose quietly marked the longest line
                            // instead, which was the wrong picture with a green run. The
                            // longest line is still marked when the phrase is missing, so
                            // the image can be read — but the run goes red naming it.
                            //
                            // Not armed, though the pose used to arm it: since #328 the
                            // tool is a row in the ⋯ menu, so an armed tool draws nothing
                            // on the page to photograph. A selected mark does.
                            val word = app.getString(R.string.screenshot_redacted_word)
                            val lines = doc.onPageForRedaction(0) { it.textLines() }
                            val line = lines.firstOrNull { it.text.contains(word) }
                                ?: lines.maxByOrNull { it.text.length }?.also {
                                    Log.e(
                                        SCREENSHOT_TAG,
                                        "::error:: redact pose: no line on page 1 contains "
                                            + "'$word'; marked the longest line instead: '${it.text}'",
                                    )
                                }
                            if (line == null) {
                                Log.e(SCREENSHOT_TAG, "::error:: redact pose: no text line on page 1")
                            } else {
                                viewModelScope.launch {
                                    // markForRedaction says nothing while an edit is in flight,
                                    // and this pose runs while the document's own open is still
                                    // settling — so wait the gate out rather than lose the shot's
                                    // mark to a busy moment and never hear about it.
                                    var settle = 0
                                    while (editingBlocked && settle < 2_000) {
                                        delay(50)
                                        settle += 50
                                    }
                                    markForRedaction(0, line.rect)
                                    // The mark lands asynchronously, so this waits for it rather
                                    // than guessing at a delay: this pose is a store capture, and
                                    // a missed selection would be a different picture from the one
                                    // that was checked. Twice, because on 2026-09-20 the fr-CA
                                    // leg placed no mark at all while en and fr-FR did, and the
                                    // shot still reached the artifact between the two.
                                    if (!awaitRedactionMark(3_000)) {
                                        markForRedaction(0, line.rect)
                                        awaitRedactionMark(3_000)
                                    }
                                    val mark = redactionMarks[0]?.firstOrNull()
                                    if (mark == null) {
                                        Log.e(
                                            SCREENSHOT_TAG,
                                            "::error:: redact pose: the mark was asked for twice and "
                                                + "never landed (editingBlocked=$editingBlocked)",
                                        )
                                    } else {
                                        selectRedactionMark(0, mark.markId)
                                    }
                                }
                            }
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
                        Log.e(SCREENSHOT_TAG, "::error:: the '$state' pose failed: ${e.message}", e)
                        statusMessage = str(R.string.open_failed)
                    }
                }
            }
        }
    }

    /**
     * The signature library a store capture poses with: exactly the bundled
     * "Mega W.", and nothing else.
     *
     * It used to seed only when the library was empty, which made the shot a
     * function of whatever the device already held — and the Windows set went
     * to review with a stale signature in it for that reason (#146). A capture
     * run owns its fixture, so whatever is already there goes first.
     *
     * Set aside rather than deleted, and under a name the app never lists, so
     * the launch extra cannot cost anyone their signatures: the same choice
     * tools/screenshots-windows/Reset-SignatureLibrary.ps1 makes.
     */
    private fun seedScreenshotSignatures(app: Application) {
        val demo = runCatching {
            app.assets.open("demo-signature.png").use { android.graphics.BitmapFactory.decodeStream(it) }
        }.getOrNull() ?: return          // no asset: leave the library alone rather than empty it
        if (signatureStore.load().isNotEmpty()) {
            // Set aside once, on the first pose of a run: by the second the library is
            // already our own fixture, and moving that over the real one would lose it.
            val aside = File(signatureDir.parentFile, "${signatureDir.name}.before-capture")
            if (aside.exists() || !signatureDir.renameTo(aside)) {
                signatureStore.load().forEach { signatureStore.delete(it.id) }
            }
        }
        signatureStore.add(app.getString(R.string.screenshot_signature_name), demo)
        signatures.clear()
        signatures.addAll(signatureStore.load())
    }

    /** The stamp currently selected for move/resize/remove. */
    var selectedStamp: SelectedStamp? by mutableStateOf(null)
        private set

    /** The text box currently selected for drag/correct/remove (#36). */
    var selectedTextBox: SelectedTextBox? by mutableStateOf(null)
        private set

    /**
     * The warning before the first text-box change on a page PDFium's rewrite would alter (#139),
     * and the person's answer to it, which is [answerPageRewrite]. It lives here rather than in
     * the check gate (#332): the gate answers whether the page would change, and asking about it
     * is the change's business.
     */
    private val pageRewriteQuestion = PageRewriteQuestion()

    /** True while that warning is on screen. */
    val pageRewriteWarningShown: Boolean
        get() = pageRewriteQuestion.isUp

    fun answerPageRewrite(proceed: Boolean) {
        pageRewriteQuestion.answer(proceed)
    }

    /**
     * What the person has chosen to go on past in this document (#558, ADR-004 decision 11).
     *
     * A permission the author withheld is not a wall here: it is said out loud and then it is the
     * person's call, because these bits were never enforceable — any tool with the owner password
     * clears them, plenty of tools ignore them, and the person holding the phone may well be the
     * author. A refusal treats a request as a lock and leaves someone stuck with their own
     * document; ignoring it throws away something the author put there deliberately. Saying it and
     * then deferring does both jobs.
     */
    private val permissions = PermissionOverride()

    /** The class whose question is on screen, or null — see [PermissionClass] (#558). */
    val permissionQuestion: PermissionClass?
        get() = permissions.asking

    fun answerPermission(proceed: Boolean) {
        permissions.answer(proceed)
    }

    /**
     * True when work of [klass] may go ahead: the document allows it ([allowed]), or the person
     * was told what its author asked and chose to continue — once per document per class (#558).
     *
     * The engine is told too. The core enforces the same advisory bits underneath every platform
     * (`PageToolsPreflight`, and the modify bit redaction needs), so a Continue that only the app
     * knew about would come back as `MEGAPDF_ERR_RESTRICTED` — the wall again, one tap later.
     * Told before every operation rather than once at the grant: a reopen (an unlock, a save) puts
     * a fresh handle in [document], and that handle starts restricted.
     */
    private suspend fun permitted(klass: PermissionClass, allowed: Boolean): Boolean {
        if (allowed) return true
        if (!permissions.permit(klass, allowed = false)) return false
        document?.allowRestrictedChanges()
        return true
    }

    /**
     * True while a change waits on its page check or on the warning (#139, #145): further edits
     * wait, and so do the tools. Never two warnings at once — the second change is refused rather
     * than queued, and this is what refuses it.
     */
    var pageRewriteDeciding: Boolean by mutableStateOf(false)
        private set

    /**
     * True when [operation] may go ahead: it leaves the page's content alone, the page was
     * already settled in this document, the page keeps its look when regenerated, its check
     * ran over budget, or the person chose Continue (#139, #145). The check started early is
     * reused; while it is awaited the spinner shows at [spot], and while the warning is up the
     * screen shows it. A restricted document is left to [perform] to refuse.
     */
    private suspend fun confirmPageRewrite(
        operation: PdfEditOperation, doc: PdfDocument, spot: BusySpot? = null,
    ): Boolean {
        if (!PageRewriteWarnings.regeneratesUnjudged(operation) || !capabilities.allows(operation)) return true
        if (pageRewriteDeciding) return false
        pageRewriteDeciding = true
        try {
            val pageIndex = operation.pageIndex
            return when (pageChecks.confirm(pageIndex, busy, spot ?: BusySpot(pageIndex))) {
                // The document may have been closed while the check ran.
                PageCheckOutcome.APPLY -> document === doc
                PageCheckOutcome.ABANDONED -> false
                PageCheckOutcome.WARN ->
                    // Continue settles the page, so it is never asked about again in this document;
                    // Cancel applies nothing and leaves it unsettled, ready to ask again.
                    pageRewriteQuestion.ask().also { if (it) pageChecks.settle(pageIndex) } &&
                        document === doc
            }
        } finally {
            pageRewriteDeciding = false
        }
    }

    /** Starts the page's check in the background, when the open document allows the changes it guards (#145). */
    private fun preparePageCheck(pageIndex: Int) {
        val state = uiState as? ViewerUiState.Viewing ?: return
        if (document == null || pageIndex !in state.pageSizes.indices) return
        // ...or once the person has chosen to go on past what its author asked (#558) — otherwise
        // the first text change on a restricted document pays for its own check at the keystroke
        // instead of having had it run while the page was being looked at.
        if (!capabilities.canAddText && !permissions.isGranted(PermissionClass.EDITING)) return
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

    /**
     * URIs an open has already proved gone, so their rows stay marked between
     * loads (#165). Cleared when the entry is opened again, or removed.
     *
     * Declared above [uiState] on purpose: [uiState] is initialised from
     * [recentRows], which reads this — and a property declared after it is still
     * null at that moment, which crashed the app on launch.
     */
    private val unavailableChecked = mutableSetOf<String>()

    var uiState: ViewerUiState by mutableStateOf(ViewerUiState.Home(recentRows()))
        private set

    /** Rendered page bitmaps, keyed by page index; observed by the page list UI. */
    val pageBitmaps = mutableStateMapOf<Int, Bitmap>()

    private var renderJob: Job? = null
    /**
     * What each held bitmap was drawn at: its pixel width, and — since #513 — the page
     * colours it was drawn under. Both belong in the key. Keyed on width alone, a change of
     * tint left every already-rendered page showing the old colours until something else
     * dirtied it, because the width had not moved.
     */
    private val renderedPages = HashMap<Int, RenderedPage>()
    private var lastWindow: RenderWindow? = null

    /** Unsaved changes, and D3 of #145: a save marks saved only if nothing changed while it ran. */
    private val dirty = DirtyTracker()

    /** True once the in-memory document differs from the file on disk. */
    val isDirty: Boolean get() = dirty.isDirty

    /** True while a save is streaming to the destination. */
    var isSaving: Boolean by mutableStateOf(false)
        private set

    private var statusText: String? by mutableStateOf(null)
    private var statusCount: Int by mutableStateOf(0)

    /**
     * The last user-facing status ("Saved", "Stopped.", errors). Shown as a toast, once each
     * time it is set — and then **kept**, not erased (#611).
     *
     * It used to be a one-shot: the screen's `LaunchedEffect` showed the toast and called
     * `consumeStatus()`, which put this back to `null` on the very next frame. That makes the
     * app's own word on what just happened unreadable by anything that is not the toast, because
     * a test asking "did it say Stopped.?" is racing a recomposition it cannot see — and a check
     * that samples a value the app deliberately erases fails in a way indistinguishable from the
     * operation never having happened. Half of #611 was exactly that: a cancelled extract that
     * *had* stopped and *had* left nothing behind, failing because the sentence saying so was
     * already gone by the time the assertion looked.
     *
     * The toast now keys on [statusSerial] instead, which is what makes it fire once per message
     * rather than once per value-change, so nothing about what a person sees changed.
     *
     * Setting it to `null` still means "take back an instruction that is no longer true" ("Tap
     * to place text"), and says nothing: only a message bumps the serial.
     */
    var statusMessage: String?
        get() = statusText
        private set(value) {
            statusText = value
            if (value != null) statusCount++
        }

    /**
     * Bumped every time [statusMessage] is given a message — including the same message twice,
     * so two saves are two toasts. Zero until the first one.
     */
    val statusSerial: Int get() = statusCount

    /** One-shot: a copy is ready to hand to the OS share sheet; cleared by [consumeShareFile]. */
    var shareFile: File? by mutableStateOf(null)
        private set

    fun consumeShareFile() {
        shareFile = null
    }

    /**
     * A document handed in from outside the app (#376): another app's "Open with" chooser,
     * via `ACTION_VIEW`, rather than this app's own SAF picker. Same unsaved-changes guard as
     * closing the current document by hand — silently discarding an edit just because a mail
     * client sent a second PDF would be worse than asking.
     */
    var pendingExternalOpen: Uri? by mutableStateOf(null)
        private set

    fun requestOpenExternal(uri: Uri) {
        if (isDirty) pendingExternalOpen = uri else openUri(uri)
    }

    fun cancelExternalOpen() {
        pendingExternalOpen = null
    }

    /** Discard the confirmation: the incoming document replaces the one on screen unsaved. */
    fun discardAndOpenExternal() {
        val uri = pendingExternalOpen ?: return
        pendingExternalOpen = null
        openUri(uri)
    }

    /** Save the confirmation: the incoming document opens once the current one is clean. */
    fun saveAndOpenExternal() {
        val uri = pendingExternalOpen ?: return
        val current = currentUri ?: return openUri(uri).also { pendingExternalOpen = null }
        val doc = document
        pendingExternalOpen = null
        requestOverwrite(current) {
            if (document === doc && !isDirty) openUri(uri)
        }
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

    /**
     * Set the moment [stopSearch] asks the running sweep to end, and reset by the next
     * [startSearch] — so the one `CancellationException` a cancelled [searchJob] throws can tell
     * "the person asked this to stop" from "a newer query superseded it", which cancels the same
     * job the same way but says nothing (#145): the newer query owns the result from then on, and
     * a status line about the query it just replaced would be a stale sentence about the wrong
     * search.
     */
    private var searchStopRequested = false

    /** As-you-type search from the search bar; debounced against fast typing. */
    fun updateSearchQuery(query: String) = startSearch(query, SEARCH_DEBOUNCE_MS)

    /**
     * Stops the running sweep (#145): a per-page loop with nothing at stake in abandoning it, the
     * same reasoning the Mac and Windows passes gave search. Unlike typing a new query, which
     * clears [searchHits] because the newer term owns the result, this leaves them exactly as
     * they stood the moment the sweep was asked to end.
     */
    private fun stopSearch() {
        searchStopRequested = true
        searchJob?.cancel()
    }

    /**
     * The search itself: wait out [debounceMs], then sweep every page on the
     * engine thread and aggregate hits into one flat document-ordered list.
     * Case-insensitive literal substring — the cross-platform contract.
     * Screenshot mode passes a zero debounce for its one deliberate query.
     * The sweep shows Searching… in the strip (#145) but never blocks editing, with the page it
     * is on against the page count, and it may be stopped.
     */
    private fun startSearch(query: String, debounceMs: Long) {
        searchStopRequested = false   // a new sweep supersedes any Stop still pending on the old one
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
                token = busy.beginDocument(BusyLabel.SEARCHING, cancel = ::stopSearch)
                val hits = ArrayList<SearchHit>()
                val pageCount = state.pageSizes.size
                for (pageIndex in state.pageSizes.indices) {
                    val page = doc.openPage(pageIndex)
                    try {
                        page.search(query).forEach { hits += SearchHit(pageIndex, it.rects) }
                    } finally {
                        page.close()
                    }
                    // After the page, not before it: "page 1 of N" while page 1 is still being
                    // read would be a count that finishes before the work does.
                    token?.report(pageIndex + 1, pageCount)
                }
                searchHits = hits
                currentHitIndex = if (hits.isEmpty()) -1 else 0
            } catch (e: CancellationException) {
                if (searchStopRequested) statusMessage = str(R.string.search_stopped)
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
                recentRows(),
                when {
                    e.isUnsupportedSecurity -> str(R.string.open_unsupported_security)
                    e.isTooLarge -> str(R.string.open_too_large)
                    else -> str(R.string.open_failed_code, e.errorCode)
                },
            )
        } catch (_: SecurityException) {
            // #165: the row stays, marked unavailable, with Remove behind a long
            // press. Dropping it silently was the old behaviour and left people
            // wondering where the file in their list had gone.
            unavailableChecked += uri.toString()
            ViewerUiState.Home(recentRows(), str(R.string.open_access_revoked))
        } catch (_: java.io.FileNotFoundException) {
            // The grant is still good; the file behind it is not — moved, renamed or
            // deleted since. Nothing on this side can see that until the open tries,
            // so this is where the row learns it (#165).
            unavailableChecked += uri.toString()
            ViewerUiState.Home(recentRows(), str(R.string.open_not_found))
        } catch (_: Exception) {
            ViewerUiState.Home(recentRows(), str(R.string.open_failed))
        } catch (_: OutOfMemoryError) {
            // Read on demand, a document no longer needs its size in memory (#147); one that
            // still runs out while its pages are measured is too big for this device.
            ViewerUiState.Home(recentRows(), str(R.string.open_too_large))
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
        val flags: DocumentFlags,
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
            // #457: read once at open, alongside security and the page sizes above.
            return OpenedDocument(doc, sizes, doc.security(), doc.documentFlags(), readsUri)
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
        attach(opened.doc, opened.security, opened.flags)
        currentUri = uri
        documentReadsUri = if (opened.readsUri) uri else null
        val name = queryDisplayName(uri)
        persistPermission(uri)
        // The uri, the name and where it lives (#165) — asked once, here, because the
        // list must not query a provider per row while it draws. Never a password:
        // whatever opened it stays with the open document.
        unavailableChecked -= uri.toString()
        recentsStore.add(
            RecentEntry(
                uri.toString(), name, System.currentTimeMillis(),
                DocumentLocations.segmentsFor(getApplication(), uri), uri.authority,
            )
        )
        uiState = ViewerUiState.Viewing(name, opened.pageSizes, opened.flags.isDynamicXfa)
        // *Open documents in reading mode* (#513), applied here rather than remembered per
        // document (#168 decision 2): one app-level answer, asked of every document alike.
        // closeCurrent() above has already put the mode back to off, so this is the only
        // thing that can turn it on for a document arriving now.
        if (openInReadingMode) enterReadingMode()
        previousWindow?.clampedTo(opened.pageSizes.size)?.let {
            updateRenderWindow(it.firstVisible, it.lastVisible, it.targetWidthPx)
        }
        // ADR-004 decision 3: a restricted open says so; the menu offers the owner password.
        if (capabilities.isRestricted) showNotice(str(R.string.security_restricted_notice))
    }

    /** Makes [doc] the open document: its permissions, its document-wide facts (#457), and its page checks (#145). */
    private fun attach(doc: PdfDocument, security: PdfSecurity, flags: DocumentFlags = DocumentFlags.NONE) {
        document = doc
        capabilities = DocumentCapabilities.fromSecurity(security, flags)
        // Marks belong to the document that carries them, and the view model outlives it
        // (#329): read them back from the document just adopted, which for a document that
        // has never been marked means the map and the count both go to nothing.
        viewModelScope.launch { refreshRedactionMarks() }
        openPageChecks(doc)
    }

    /**
     * Starts (or restarts) the page checks over [doc]. Each check runs off the engine's thread and
     * stops when its coroutine is cancelled.
     *
     * Called again after a page operation (#174): the gate remembers which pages have been settled
     * by index, and after a delete or a move those indices name different pages. Starting over
     * means a page may be asked about once more, which is the harmless direction to be wrong in.
     */
    private fun openPageChecks(doc: PdfDocument) {
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
                renderedPages.remove(index)
            }
        }

        renderJob?.cancel()
        // Read once for the whole pass, so a tint changed mid-pass cannot leave one page
        // drawn under the old colours and the next under the new: the preference collector
        // restarts this window when it moves.
        val tint = pageTint
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
                val key = RenderedPage(width, tint)
                if (renderedPages[index] == key) continue

                try {
                    val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
                    val page = doc.openPage(index)
                    try {
                        page.render(bitmap, tint)
                    } finally {
                        page.close()
                    }
                    pageBitmaps[index] = bitmap
                    renderedPages[index] = key
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
            // A tap that reaches the page is a tap that missed every mark: a mark's own node
            // takes the tap first, so anything landing here deselects (#329).
            selectedRedactionMark = null

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
                    // #131 refused the tap here, because moving or removing it is an edit the
                    // owner did not allow. #558: selecting changes nothing, so there is nothing to
                    // refuse and nothing yet to ask about — the overlay's move and remove go
                    // through [perform], which is where the author's request is put to the person.
                    // Selection only — move/resize/remove happen via the overlay.
                    selectedStamp = SelectedStamp(
                        pageIndex, signature.annotIndex, signature.id, signature.rect)
                    selectedTextBox = null
                    selectedWhiteout = null
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
                    // Not gated — see the signature above (#558): selecting is not changing.
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
                    selectedWhiteout = null
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

                // A whiteout (#3) ranks with signatures and text boxes: it too is
                // something the user put on the page. Last match wins, same reason.
                val whiteout = page.whiteouts().lastOrNull { it.rect.contains(x, y) }
                if (whiteout != null) {
                    // Not gated — see the signature above (#558): selecting is not changing.
                    selectedWhiteout = SelectedWhiteout(pageIndex, whiteout.objectIndex, whiteout.rect)
                    return@launchEdit
                }
                selectedWhiteout = null

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
                        // #131 refused here when the owner did not allow changes. #558: it is put
                        // to the person instead — and put *here*, before the editor opens, for
                        // #118's own reason: better than asking once they have typed. Cancel opens
                        // nothing, and the line is not selected.
                        if (permitted(PermissionClass.EDITING, capabilities.canEditContent)) {
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

    /**
     * The notice for the one thing a restricted open really cannot do (#131): set, change or
     * remove the password. #558 turned every other refusal into a question, but not this one —
     * it is not an advisory bit. Without the owner password there is no credential to write a new
     * copy with, and the core refuses it too ([PdfRestrictedException]), so there is nothing to
     * offer to continue *to*; the honest answer stays "unlock it first", which is what this says.
     */
    private fun showRestricted() = showNotice(str(R.string.security_restricted_edit))

    /**
     * The notice for arming a filling tool on a dynamic-XFA document (#457): explains rather
     * than silently doing nothing, or performing an edit that would not actually fill the
     * form (the tool stays armable — [DocumentCapabilities.isDynamicXfa] deliberately leaves
     * canSign/canFillForms/canAddText alone — so a tap must say why nothing happened).
     */
    private fun showDynamicXfaNotice() = showNotice(str(R.string.dynamic_xfa_fill_notice))

    // --- Added text (#34) ---

    /** Arms the next tap to place text. Tapping the page opens the text field. */
    fun startTextPlacement() {
        if (editingBlocked) return
        // #558: asked here, where #131 refused, so nobody aims a tool they are about to be asked
        // about. On a document that allows it this neither suspends nor hops a frame.
        viewModelScope.launch {
            if (!permitted(PermissionClass.EDITING, capabilities.canAddText)) return@launch
            armTextPlacement()
        }
    }

    private fun armTextPlacement() {
        // #457: arming Add text on a dynamic-XFA document explains rather than entering
        // placement mode — stamping text over Adobe's placeholder would not fill the form.
        if (capabilities.isDynamicXfa) {
            showDynamicXfaNotice()
            return
        }
        cancelPlacement()
        selectedStamp = null
        selectedTextBox = null
        selectedWhiteout = null
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
        lastFontSize = fontSize
        lastFontName = fontName
        val spot = BusySpot(
            pending.pageIndex,
            com.megapdf.engine.PdfRect(pending.x, pending.y, pending.x, pending.y + fontSize),
        )
        if (pending.editingId != null) {
            // Correcting an existing box always restyles one box in place — it never grows
            // into more than one line (#4): that is a different shape of edit (replace one
            // object with several, under one undo step) than "same id, new text, new size".
            val trimmed = text.trim()
            if (trimmed.isEmpty()) return
            val style = TextBoxStyle(trimmed, fontSize, fontName)
            launchEdit(R.string.text_change_failed) {
                val before = TextBoxStyle(
                    pending.initialText, pending.fontSize, pending.fontName)
                if (before == style) return@launchEdit
                val edit = EditTextBoxOperation(
                    pending.pageIndex, pending.editingId, before, style,
                    pending.x, pending.y)
                if (!confirmPageRewrite(edit, doc, spot)) return@launchEdit
                perform(edit, doc, spot)
                reselectTextBox(doc, pending.pageIndex, pending.editingId)
            }
            return
        }
        // A new placement (#4): the phone's Enter key is its "new line", so a note of more
        // than one non-blank line becomes that many boxes, placed and undone as one gesture,
        // each staying its own separately-selectable box afterwards.
        //
        // Normalized the same way the Windows pass found it had to be (#564): a soft
        // keyboard or an input method is not contractually bound to hand back "\n" for a
        // line break — "\r\n" and a lone "\r" are both real possibilities — and splitting on
        // "\n" alone against one of those would silently keep the whole note as a single
        // object with a control character sitting in the middle of it.
        val lines = text.replace("\r\n", "\n").replace('\r', '\n')
            .split("\n").map { it.trim() }.filter { it.isNotEmpty() }
        if (lines.isEmpty()) return
        launchEdit(R.string.text_add_failed) {
            if (lines.size == 1) {
                val add = TextBoxOperation(
                    pending.pageIndex, "text:${java.util.UUID.randomUUID()}",
                    lines[0], fontSize, pending.x, pending.y, adding = true,
                    fontName = fontName)
                if (!confirmPageRewrite(add, doc, spot)) return@launchEdit
                perform(add, doc, spot)
            } else {
                val ids = lines.map { "text:${java.util.UUID.randomUUID()}" }
                val add = AddTextBoxesOperation(
                    pending.pageIndex, ids, lines, fontSize, pending.x, pending.y, fontName)
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
        viewModelScope.launch {
            // #558: before the editor opens, not after the correction is typed.
            if (!permitted(PermissionClass.EDITING, capabilities.canAddText)) return@launch
            openTextBoxEditorNow(sel)
        }
    }

    private fun openTextBoxEditorNow(sel: SelectedTextBox) {
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
            busy.pageWork(BusyLabel.APPLYING, null) { history.undo(doc.asEditTarget()) }?.let { afterHistoryChange(it) }
        }
    }

    fun redo() {
        val doc = document ?: return
        launchEdit(R.string.redo_failed) {
            busy.pageWork(BusyLabel.APPLYING, null) { history.redo(doc.asEditTarget()) }?.let { afterHistoryChange(it) }
        }
    }

    /**
     * The single funnel for every reversible change. It asks the document's permissions
     * first (#131, ADR-004 decision 2): the entry points ask too, so this is the backstop
     * for any path they miss — form fields and check marks rely on it. While the change goes
     * in, the page shows Applying… at [spot] once it takes long enough (#145).
     */
    private suspend fun perform(operation: PdfEditOperation, doc: PdfDocument, spot: BusySpot? = null) {
        // #457: a filling tool on a dynamic-XFA document explains rather than acting — checked
        // before the permission gate below, so the two never talk over each other.
        if (capabilities.isDynamicXfa && capabilities.isFillingOperation(operation)) {
            showDynamicXfaNotice()
            return
        }
        // #558: what the author asked is said out loud, and then it is the person's call — once
        // per document per class. Only an operation with no advisory bit behind it is still
        // refused outright, which is [DocumentCapabilities.permissionClassOf]'s null: full access,
        // where there is no credential to write with and so nothing to offer.
        if (!capabilities.allows(operation)) {
            val klass = capabilities.permissionClassOf(operation)
            if (klass == null) {
                showRestricted()
                return
            }
            if (!permitted(klass, allowed = false)) return
            if (document !== doc) return
        }
        busy.pageWork(BusyLabel.APPLYING, spot ?: BusySpot(operation.pageIndex)) {
            history.perform(operation, doc.asEditTarget())
        }
        if (operation is BodyTextEditOperation || operation is BodyTextDeleteOperation) {
            // A change that regenerated the page already went in without a warning (#139, #145).
            pageChecks.settle(operation.pageIndex)
        }
        afterHistoryChange(operation)
    }

    private suspend fun afterHistoryChange(operation: PdfEditOperation) {
        canUndo = history.canUndo
        canRedo = history.canRedo
        selectedStamp = null
        selectedTextBox = null
        selectedWhiteout = null
        // A page operation is asked which way it just went (#174): a delete's undo inserts where
        // its apply deleted, so the renumbering depends on the direction, and the operation is the
        // only thing that knows. A rotation renumbers nothing but changes the shape of the pages it
        // turned, which the page list has to be told about for the same reason.
        val shifts = operation.lastPageShifts
        if (shifts.isNotEmpty()) {
            afterPagesRenumbered(shifts)
            return
        }
        if (operation is RotatePagesOperation) {
            afterPagesTurned(operation.pagesChanged)
            return
        }
        if (operation.changesDocument) {
            markEditedAndRerender(operation.pageIndex)
        } else {
            // A mark changes nothing on disk, so it must not mark the document unsaved, must
            // not write a journal entry and must not spend a re-render — the overlay draw is
            // the visible change. What it does need is the core read back, because undo and
            // redo have just changed which marks the core holds (#329).
            refreshRedactionMarks()
        }
    }

    // --- Signature library and placement (#16/#17) ---

    /** Imports a picked image: decode, cleanup per the SDD contract, store. */
    fun importSignature(uri: Uri) {
        viewModelScope.launch {
            try {
                if (libraryIsFull()) return@launch
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
                if (libraryIsFull()) return@launch
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
        // #558: asked here, where #131 refused — see [startTextPlacement].
        viewModelScope.launch {
            if (!permitted(PermissionClass.EDITING, capabilities.canSign)) return@launch
            armPlacement(entry)
        }
    }

    private fun armPlacement(entry: SignatureEntry) {
        // #457: arming Sign on a dynamic-XFA document explains rather than arming placement.
        if (capabilities.isDynamicXfa) {
            showDynamicXfaNotice()
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
        renderedPages.remove(pageIndex)
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
        requestOverwrite(uri)
    }

    /** Save from the unsaved-changes prompt: the document closes once it is saved and still clean. */
    fun saveAndClose() {
        val uri = currentUri ?: return
        val doc = document ?: return
        requestOverwrite(uri) {
            if (document === doc && !isDirty) closeDocument()
        }
    }

    /**
     * "Save a copy" destination picked via ACTION_CREATE_DOCUMENT, as a PDF. If the signed-save
     * warning's Save-a-copy button is what led here, [removeSignatureOnNextSaveAs] carries the
     * tick's answer across the picker (#576) -- the ordinary "Save a copy" menu row never sets
     * it, so it defaults off there, same as before this feature existed.
     */
    fun saveAs(uri: Uri) {
        val remove = removeSignatureOnNextSaveAs
        removeSignatureOnNextSaveAs = false
        writeTo(uri, isSaveAs = true, removeSignatureFirst = remove)
    }

    // --- Digital-signature overwrite warning (#476/#481) ---

    /**
     * Set while the #481 confirmation is up: [save], [saveAndClose], [saveAndShare] and
     * [saveAndOpenExternal] all write back to the document's own file (`isSaveAs = false` in
     * [writeTo]), which is exactly the move that invalidates an existing signature — measured
     * 33/33 on real GPO documents in #476, because [writeTo] re-serialises the whole file and
     * the original `/ByteRange` no longer covers it. [saveAs] (Save a copy) writes somewhere
     * else and is deliberately not gated here: the signed original stays untouched, so it only
     * earns the quiet, one-time notice [writeTo] shows once the copy lands.
     */
    var isSignedOverwritePending: Boolean by mutableStateOf(false)
        private set

    private var pendingOverwrite: Pair<Uri, (() -> Unit)?>? = null

    /**
     * #576: the signed-save warning's Save-a-copy button recorded this before launching the
     * picker; [saveAs] reads and clears it once the picker returns. Applying it here, rather
     * than before the picker, means a picker the person then cancels never touched the open
     * document's signature for nothing -- the same ordering every other platform uses.
     */
    private var removeSignatureOnNextSaveAs: Boolean = false

    /** Asks first when the open document is signed; otherwise writes immediately, as before. */
    private fun requestOverwrite(uri: Uri, afterSaved: (() -> Unit)? = null) {
        if (capabilities.isSigned) {
            pendingOverwrite = uri to afterSaved
            isSignedOverwritePending = true
        } else {
            writeTo(uri, isSaveAs = false, afterSaved)
        }
    }

    /**
     * The confirmation's deliberate choice: overwrite the signed original anyway. Refuses
     * nothing. [removeSignature] is the tick's answer (#576) -- the save was always going to
     * invalidate the signature either way (33/33 on real signed documents, #476); this only
     * decides whether the written file still carries the now-invalid bytes or admits it has
     * none.
     */
    fun confirmSignedOverwrite(removeSignature: Boolean = false) {
        val (uri, afterSaved) = pendingOverwrite ?: return
        pendingOverwrite = null
        isSignedOverwritePending = false
        writeTo(uri, isSaveAs = false, afterSaved, removeSignatureFirst = removeSignature)
    }

    /**
     * The confirmation's other two answers — Cancel, or Save a copy (its own picker, launched
     * by the UI with [onSaveAs] the same way the menu row does): either way, nothing is
     * written to the signed original, and whatever [save]/[saveAndClose]/[saveAndShare]/
     * [saveAndOpenExternal] was trying to do beyond writing is simply not carried out — the
     * person can ask for it again once they have decided what to do about the signature.
     *
     * [removeSignatureForSaveAs] is the tick's answer when this is reached through the
     * Save-a-copy button rather than a plain Cancel (#576); the UI passes false for an actual
     * Cancel, where the tick's state means nothing because nothing is being saved.
     */
    fun cancelSignedOverwrite(removeSignatureForSaveAs: Boolean = false) {
        pendingOverwrite = null
        isSignedOverwritePending = false
        removeSignatureOnNextSaveAs = removeSignatureForSaveAs
    }

    /**
     * "Export as Markdown" — its own menu row since #409, with its own `text/markdown` picker
     * in `MainActivity` (#386 had made it a second type of Save a copy's picker, which
     * DocumentsUI never offered): a one-way export of the document's text
     * (`megapdf_write_text`, `MEGAPDF_WRITE_MARKDOWN`), not an alternate save of the
     * document — a `.md` cannot be reopened as one (no form fields, no signatures, no layout;
     * contract 9's blocks are text only). Unlike [saveAs]/[writeTo] this never touches
     * [currentUri], the display name, [isDirty], the persisted grant or Recents: the exported
     * file is not, and never becomes, the app's current document — the SAF grant it comes
     * with is left unpersisted, and it earns no entry in Recents. The text written is the
     * document as it stands in memory, unsaved edits included, which is why no
     * unsaved-changes question precedes it (Share's does, because Share sends the file on
     * disk).
     *
     * No verify-by-reopening either (unlike [writeVerified]): there is nothing to reopen a
     * Markdown file as.
     */
    fun exportMarkdown(uri: Uri) {
        val doc = document ?: return
        if (isSaving || busy.locksDocument) return
        isSaving = true
        val token = busy.beginDocument(BusyLabel.EXPORTING, locks = true)
        val app = getApplication<Application>()
        viewModelScope.launch {
            try {
                withContext(Dispatchers.IO) {
                    val pfd = app.contentResolver.openFileDescriptor(uri, "wt")
                        ?: throw IllegalStateException("provider returned no descriptor")
                    pfd.use {
                        java.io.FileOutputStream(it.fileDescriptor).use { out ->
                            doc.writeMarkdown(out)
                            out.fd.sync()
                        }
                    }
                }
                statusMessage = str(R.string.exported_markdown)
            } catch (e: CancellationException) {
                throw e
            } catch (_: SecurityException) {
                statusMessage = str(R.string.save_no_permission)
            } catch (_: Exception) {
                statusMessage = str(R.string.markdown_export_failed)
            } finally {
                isSaving = false
                token.end()
            }
        }
    }

    /** Share with no unsaved changes, or Discard from the unsaved-changes prompt (#378): the
     * document on disk already is what gets shared, so this exports it as-is. */
    fun shareLastSaved() = exportForShare()

    /** Save from the unsaved-changes prompt, then share the now-saved file (#378). */
    fun saveAndShare() {
        val uri = currentUri ?: return
        val doc = document ?: return
        requestOverwrite(uri) {
            if (document === doc) exportForShare()
        }
    }

    /**
     * Copies the last-saved bytes at [currentUri] into the app's own `cacheDir/share/` (#378),
     * the one subtree the FileProvider grants — [currentUri] itself may belong to any content
     * provider and cannot be handed to another app directly. Setting [shareFile] is what tells
     * the UI a content:// grant is ready to build; [consumeShareFile] clears it once used.
     */
    private fun exportForShare() {
        val uri = currentUri ?: return
        val name = (uiState as? ViewerUiState.Viewing)?.displayName ?: "document.pdf"
        val app = getApplication<Application>()
        viewModelScope.launch {
            val target = shareFileFor(app.cacheDir, name)
            try {
                withContext(Dispatchers.IO) {
                    target.parentFile?.mkdirs()
                    app.contentResolver.openInputStream(uri)?.use { input ->
                        target.outputStream().use { input.copyTo(it, COPY_BUFFER_BYTES) }
                    } ?: throw IllegalStateException("provider returned no stream")
                }
                shareFile = target
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                statusMessage = str(R.string.share_failed)
            }
        }
    }

    private fun writeTo(
        uri: Uri, isSaveAs: Boolean, afterSaved: (() -> Unit)? = null, removeSignatureFirst: Boolean = false,
    ) {
        val doc = document ?: return
        if (isSaving || busy.locksDocument) return
        isSaving = true
        // #145: Saving… in the strip; editing, Close and the file commands wait until it ends.
        val token = busy.beginDocument(BusyLabel.SAVING, locks = true)
        val mark = dirty.beginSave()
        viewModelScope.launch {
            var saved = false
            // #576: read before removeSignatureFirst can flip it, the same way the .NET
            // platforms capture wasSigned before mutating IsSigned -- the notice below needs to
            // know what the document was, not what it becomes mid-save.
            val wasSigned = capabilities.isSigned
            var removedSignature = false
            try {
                // #576: after the picker (if any), immediately before the write -- never
                // earlier, so a save that is cancelled before this point never touches the
                // open document's signature. The removal is on the in-memory document only;
                // the file this was opened from is untouched either way.
                if (removeSignatureFirst && doc.removeDigitalSignatures()) {
                    removedSignature = true
                    capabilities = capabilities.copy(isSigned = false, isCertificationSigned = false)
                }
                // Verified opened like the document: a protected document's copy is still
                // protected (#132).
                writeVerified(doc, uri, token, { doc.save(it) }, { engine.openFileLike(doc, it.path).close() })

                if (isSaveAs) {
                    currentUri = uri
                    persistPermission(uri)
                    val name = queryDisplayName(uri)
                    unavailableChecked -= uri.toString()
                    recentsStore.add(
                        RecentEntry(
                            uri.toString(), name, System.currentTimeMillis(),
                            DocumentLocations.segmentsFor(getApplication(), uri), uri.authority,
                        )
                    )
                    (uiState as? ViewerUiState.Viewing)?.let {
                        uiState = it.copy(displayName = name)
                    }
                }
                // D3 (#145): a change that went in while the bytes were written keeps the
                // document dirty, so a later close still asks.
                dirty.markSaved(mark)
                statusMessage = str(R.string.saved)
                // #481, #576: Save a copy of a signed document says so, quietly and once — the
                // signed original this came from was untouched ([requestOverwrite] never gated
                // this path) either way. The signature dictionary does carry over into a copy
                // that keeps it (FPDF_SaveAsCopy re-serialises it) -- only its validity does
                // not, which is why [signed_copy_notice] no longer says it "doesn't carry over".
                if (isSaveAs) {
                    if (removedSignature) showNotice(str(R.string.signature_removed_notice))
                    else if (wasSigned) showNotice(str(R.string.signed_copy_notice))
                }
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

    /** Takes a document off the recents list, without touching the file (#165). */
    fun removeRecent(entry: RecentEntry) {
        unavailableChecked -= entry.uri
        recentsStore.remove(entry.uri)
        val state = uiState
        if (state is ViewerUiState.Home) {
            uiState = ViewerUiState.Home(recentRows(), state.error)
        }
    }

    fun closeDocument() {
        // #145: never out of a document while its save or password change is still writing it.
        if (busy.locksDocument) return
        closeCurrent()
        uiState = ViewerUiState.Home(recentRows())
    }

    private fun closeCurrent() {
        renderJob?.cancel()
        closeSearch()
        // Reading mode belongs to the document being looked at, not to the app (#507): the
        // next one starts from the *Open documents in reading mode* setting, whatever this
        // one was being read in.
        readingMode = false
        pageBitmaps.clear()
        renderedPages.clear()
        lastWindow = null
        // The Pages grid belongs to the document being worked on (#174), like reading mode above:
        // its selection is page indices of *this* document, and its thumbnails are pictures of it.
        isPagesOpen = false
        selectedPages = emptySet()
        pageToolRefusal = null
        dropThumbnails()
        currentUri = null
        documentReadsUri = null
        dirty.reset()
        pendingSignature = null
        selectedStamp = null
        selectedTextBox = null
        selectedWhiteout = null
        whiteoutMode = false
        // History belongs to the open document — never offer to undo an edit made
        // to a file that is no longer on screen.
        history.clear()
        // So do the marks (#329, ADR-005 decision 1: "marks do not survive closing the
        // document"). Nothing here reached disk, so this is not discarding work: it is
        // dropping a view state the core has already dropped with the document. Left
        // standing it was drawn over the next document at the same page indices, and made
        // Save ask about a redaction the new document had never heard of.
        redactionMarks = emptyMap()
        selectedRedactionMark = null
        redactMode = false
        redactionSummary = null
        redactionRefusal = null
        // So do the page checks and the pages already settled (#139, #145): the running check is
        // stopped, and a warning still up is answered Cancel — the change it was asking about
        // belonged to the document that just went away.
        pageChecks.reset()
        pageRewriteQuestion.abandon()
        // And so does what the person chose to go on past (#558): the choice was about *this*
        // document, which is now gone. It was never persisted, so there is nothing else to clear.
        permissions.reset()
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
     * Says so and answers true when the library is at its soft limit (#333), which
     * `add` would otherwise refuse as a bare `IllegalStateException` — reported to
     * the user as a save failure, which is not what happened. Checked ahead of the
     * work rather than after it: the library being full is not a reason to encode,
     * cleanup and trim an image that will be refused.
     */
    private suspend fun libraryIsFull(): Boolean {
        if (!withContext(Dispatchers.IO) { signatureStore.isFull() }) return false
        statusMessage = str(R.string.signature_library_full, SignatureLibraryStore.SOFT_LIMIT)
        return true
    }

    /**
     * "Signature N" for a new library entry. The name is persisted at creation
     * time, so it keeps the language the app was in when the signature was
     * added and does not re-translate if the locale changes later — accepted.
     */
    private fun defaultSignatureName(): String =
        str(R.string.signature_default_name, signatures.size + 1)

    private fun persistPermission(uri: Uri) {
        // Read and write when offered, read alone otherwise (UriGrants). Null is a
        // provider with no persistable grant: recents will just round-trip through
        // the picker for this document.
        UriGrants.persist { flags ->
            getApplication<Application>().contentResolver.takePersistableUriPermission(uri, flags)
        }
    }

    /**
     * The recents list, with each row marked openable or not (#165).
     *
     * Availability comes from one call, not one per row: the system already knows
     * every URI we hold a persisted grant for, so a content URI missing from that
     * set has had its permission revoked — the provider was uninstalled, the SD
     * card came out, the user cleared the grant. Anything that is not a content
     * URI is the demo, and is left alone.
     *
     * A file *deleted* while the grant survives cannot be seen from here without a
     * query per row, so that one is found at open time and the row is marked then.
     */
    private fun recentRows(): List<RecentRow> {
        val granted: Set<String> = try {
            getApplication<Application>().contentResolver.persistedUriPermissions
                .map { it.uri.toString() }
                .toSet()
        } catch (_: Exception) {
            emptySet()
        }
        val app = getApplication<Application>()
        return recentsStore.load().map { entry ->
            val available = when {
                // The demo's rows, which are not documents at all.
                !entry.uri.startsWith("content://") -> true
                // An open has already proved this one gone.
                entry.uri in unavailableChecked -> false
                else -> entry.uri in granted
            }
            // The root's name is asked for again here rather than trusted from the
            // store: it is the system's word or another app's, both of which follow
            // the app's language, and one recorded in French showed up in an English
            // session (#165). Once per load of the list, not once per row drawn.
            val root = if (entry.location.isEmpty()) null else try {
                DocumentLocations.rootNameFor(app, Uri.parse(entry.uri))
            } catch (_: Exception) {
                null
            }
            RecentRow(entry.copy(location = RecentLocation.withRoot(entry.location, root)), available)
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
        pageRewriteQuestion.abandon()
        permissions.reset()
        val doc = document
        document = null
        if (doc != null) {
            PdfEngine.closeDetached(doc)
        }
    }

    // Last in the class body on purpose: it reaches lastWindow and updateRenderWindow, and
    // an init block only sees what has been declared above it.
    init {
        observeReadingPreferences()
    }

    private companion object {
        /** Handle prefix the engine gives a marked box that carries no id. */
        const val UNTAGGED_TEXT_PREFIX = "text:untagged#"

        /** Margin added to a text box's tight glyph rect when hit-testing a tap. */
        const val TAP_SLOP_POINTS = 6.0

        const val RENDER_MARGIN = 2      // desktop MainViewModel's ±2-page window
        const val MAX_BITMAP_DIM = 2048  // bound worst-case bitmap memory
        /** Thumbnails held either side of the grid's visible range: about one row on a phone (#174). */
        const val THUMBNAIL_MARGIN = 6
        /** A thumbnail is never drawn wider than this, whatever the grid asks for. */
        const val MAX_THUMBNAIL_WIDTH = 320
        /** US Letter: what a blank page falls back to when the document has no page to copy. */
        val DEFAULT_PAGE_SIZE = PageSize(612.0, 792.0)
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
