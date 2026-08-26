package com.megapdf.android

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
import com.megapdf.engine.PdfDocument
import com.megapdf.engine.PdfEngine
import com.megapdf.engine.PdfLoadException
import com.megapdf.engine.PdfPasswordException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
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
 * Owns the engine and the open document; renders a ±[RENDER_MARGIN]-page window
 * around the visible pages at the requested pixel width — the mobile port of the
 * desktop `MainViewModel` render-window virtualization. Bitmaps outside the
 * window are dropped so a long document never holds more than ~5 page bitmaps.
 */
class ViewerViewModel(application: Application) : AndroidViewModel(application) {

    private val engine = PdfEngine()
    private var document: PdfDocument? = null
    private var currentUri: Uri? = null
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

    /** Screenshot-mode sheet request ("sign" | "draw" | "search" | "text"); set via launch intent. */
    var screenshotSheet: String? by mutableStateOf(null)
        private set

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
                signatureStore.add("Mega W.", bmp)
                signatures.clear()
                signatures.addAll(signatureStore.load())
            }
        }
        when (state) {
            "home" -> {
                val now = System.currentTimeMillis()
                val day = 86_400_000L
                uiState = ViewerUiState.Home(listOf(
                    RecentEntry("demo://1", "Rental Agreement.pdf", now - day / 2),
                    RecentEntry("demo://2", "Field Trip Permission.pdf", now - 2 * day),
                    RecentEntry("demo://3", "Insurance Claim Form.pdf", now - 6 * day),
                ), null)
            }
            "viewer", "sign", "draw", "search", "text" -> {
                screenshotSheet = if (state == "viewer") null else state
                viewModelScope.launch {
                    val bytes = withContext(Dispatchers.IO) {
                        app.assets.open("demo.pdf").use { it.readBytes() }
                    }
                    val doc = engine.open(bytes)
                    val count = doc.pageCount()
                    val sizes = ArrayList<PageSize>(count)
                    for (i in 0 until count) {
                        val page = doc.openPage(i)
                        sizes += PageSize(page.widthPoints, page.heightPoints)
                        page.close()
                    }
                    closeCurrent()
                    document = doc
                    uiState = ViewerUiState.Viewing("Rental Agreement.pdf", sizes)
                    if (state == "search") {
                        // Seed here, not from the UI: the document and the
                        // Viewing state are both already set, so the sweep can
                        // never hit updateSearchQuery's "nothing open" early
                        // return, and the debounce is skipped so the hits and
                        // the "N of M" count are on screen without any wait.
                        startSearch(SCREENSHOT_SEARCH_TERM, debounceMs = 0L)
                    }
                    if (state == "text") {
                        // The Add text dialog, open on a typed name with the size
                        // and face pickers showing (#43). Armed here rather than
                        // through onPageTapped because the tap point is chosen,
                        // not synthesised: just under the signature rule, where a
                        // printed name belongs on this agreement.
                        pendingTextTap = PendingTextTap(
                            0, SCREENSHOT_TEXT_X, SCREENSHOT_TEXT_Y,
                            initialText = SCREENSHOT_TEXT)
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

    var uiState: ViewerUiState by mutableStateOf(ViewerUiState.Home(recentsStore.load()))
        private set

    /** Rendered page bitmaps, keyed by page index; observed by the page list UI. */
    val pageBitmaps = mutableStateMapOf<Int, Bitmap>()

    private var renderJob: Job? = null
    private val renderedWidths = HashMap<Int, Int>()
    private var lastWindow: Triple<Int, Int, Int>? = null

    /** True once the in-memory document differs from the file on disk. */
    var isDirty: Boolean by mutableStateOf(false)
        private set

    /** True while a save is streaming to the destination. */
    var isSaving: Boolean by mutableStateOf(false)
        private set

    /** One-shot user-facing status ("Saved", errors); cleared by [consumeStatus]. */
    var statusMessage: String? by mutableStateOf(null)
        private set

    fun consumeStatus() {
        statusMessage = null
    }

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
            try {
                if (debounceMs > 0) delay(debounceMs)
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
            } finally {
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
        viewModelScope.launch {
            try {
                val bytes = withContext(Dispatchers.IO) {
                    getApplication<Application>().contentResolver.openInputStream(uri)
                        ?.use { it.readBytes() }
                        ?: throw IllegalStateException("provider returned no stream")
                }
                val doc = engine.open(bytes, password)
                val count = doc.pageCount()
                val sizes = ArrayList<PageSize>(count)
                for (i in 0 until count) {
                    val page = doc.openPage(i)
                    sizes += PageSize(page.widthPoints, page.heightPoints)
                    page.close()
                }
                closeCurrent()
                document = doc
                currentUri = uri
                isDirty = false
                val name = queryDisplayName(uri)
                persistReadPermission(uri)
                recentsStore.add(
                    RecentEntry(uri.toString(), name, System.currentTimeMillis())
                )
                uiState = ViewerUiState.Viewing(name, sizes)
            } catch (_: PdfPasswordException) {
                uiState = ViewerUiState.PasswordNeeded(uri, wrongPassword = password != null)
            } catch (e: PdfLoadException) {
                toHome("Couldn't open this file (error ${e.errorCode}).")
            } catch (_: SecurityException) {
                recentsStore.remove(uri.toString())
                toHome("Access to this file was revoked. Pick it again to reopen it.")
            } catch (e: Exception) {
                toHome("Couldn't open this file: ${e.message}")
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
        lastWindow = Triple(firstVisible, lastVisible, targetWidthPx)
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
                val width = targetWidthPx.coerceIn(1, MAX_BITMAP_DIM)
                val height = (width * size.heightPoints / size.widthPoints).toInt()
                    .coerceIn(1, MAX_BITMAP_DIM)
                if (renderedWidths[index] == width) continue

                val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
                val page = doc.openPage(index)
                try {
                    page.render(bitmap)
                } finally {
                    page.close()
                }
                pageBitmaps[index] = bitmap
                renderedWidths[index] = width
            }
        }
    }

    /**
     * Tap dispatch, the mobile port of the desktop `OnPageTapped` hit ordering:
     * form fields win over page content; then existing check marks (tap to
     * remove); then drawn-square candidates (tap to place a mark).
     * Fractions are tap position / rendered page size, top-left origin.
     */
    fun onPageTapped(pageIndex: Int, xFraction: Float, yFraction: Float) {
        val state = uiState as? ViewerUiState.Viewing ?: return
        val doc = document ?: return
        viewModelScope.launch {
            val size = state.pageSizes[pageIndex]
            val x = xFraction * size.widthPoints
            val y = (1 - yFraction) * size.heightPoints  // view top-left → PDF bottom-left

            pendingSignature?.let { entry ->
                pendingSignature = null
                placeSignature(doc, entry, pageIndex, size, x, y)
                return@launch
            }

            if (isPlacingText) {
                isPlacingText = false
                statusMessage = null
                pendingTextTap = PendingTextTap(
                    pageIndex, x, y, fontSize = lastFontSize, fontName = lastFontName)
                return@launch
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
                    // Selection only — move/resize/remove happen via the overlay.
                    selectedStamp = SelectedStamp(
                        pageIndex, signature.annotIndex, signature.id, signature.rect)
                    selectedTextBox = null
                    return@launch
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
                    if (box.id.startsWith(UNTAGGED_TEXT_PREFIX)) {
                        // A box written by MegaPDF for Windows 1.6.x, before boxes
                        // carried an id. Its only handle is its page-object index,
                        // which the history would replay against a page whose
                        // indices had since shifted — so it would eventually move
                        // or delete the wrong box. Swallow the tap rather than let
                        // it fall through and toggle whatever is underneath.
                        selectedTextBox = null
                        statusMessage = "This text was added by an older version and can't be edited here"
                        return@launch
                    }
                    val selected = SelectedTextBox(
                        pageIndex, box.id, box.text, box.fontSize, box.fontName, box.rect)
                    if (selectedTextBox?.id == box.id) {
                        // A second tap on the selected box also opens the editor.
                        // The overlay's ✎ is the discoverable way in, because a
                        // *quick* second tap is claimed by double-tap-to-zoom —
                        // this path only fires after that disambiguation lapses.
                        selectedTextBox = selected
                        editSelectedTextBox()
                    } else {
                        selectedTextBox = selected
                    }
                    return@launch
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
            } finally {
                page.close()
            }
            operation?.let { perform(it, doc) }
        }
    }

    // --- Added text (#34) ---

    /** Arms the next tap to place text. Tapping the page opens the text field. */
    fun startTextPlacement() {
        cancelPlacement()
        selectedStamp = null
        selectedTextBox = null
        isPlacingText = true
        statusMessage = "Tap the page where the text should go"
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
        pendingTextTap = null
        val trimmed = text.trim()
        if (trimmed.isEmpty()) return
        lastFontSize = fontSize
        lastFontName = fontName
        val style = TextBoxStyle(trimmed, fontSize, fontName)
        viewModelScope.launch {
            try {
                if (pending.editingId != null) {
                    val before = TextBoxStyle(
                        pending.initialText, pending.fontSize, pending.fontName)
                    if (before == style) return@launch
                    perform(
                        EditTextBoxOperation(
                            pending.pageIndex, pending.editingId, before, style,
                            pending.x, pending.y),
                        doc)
                    reselectTextBox(doc, pending.pageIndex, pending.editingId)
                } else {
                    perform(
                        TextBoxOperation(
                            pending.pageIndex, "text:${java.util.UUID.randomUUID()}",
                            trimmed, fontSize, pending.x, pending.y, adding = true,
                            fontName = fontName),
                        doc)
                }
            } catch (e: Exception) {
                statusMessage =
                    if (pending.editingId != null) "Couldn't change that text"
                    else "Couldn't add that text"
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
        viewModelScope.launch {
            val rect = clampToPage(newRect, state.pageSizes[sel.pageIndex])
            // A tap that slipped into a drag can land a sub-point move; don't put
            // a no-op on the undo stack for it.
            if (kotlin.math.abs(rect.left - sel.rect.left) < 0.01 &&
                kotlin.math.abs(rect.bottom - sel.rect.bottom) < 0.01) return@launch
            try {
                perform(
                    MoveTextBoxOperation(
                        sel.pageIndex, sel.id,
                        fromX = sel.rect.left, fromY = sel.rect.bottom,
                        toX = rect.left, toY = rect.bottom),
                    doc)
                reselectTextBox(doc, sel.pageIndex, sel.id)
            } catch (e: Exception) {
                statusMessage = "Couldn't move that text"
            }
        }
    }

    /**
     * Opens the text field on the selected box so a typo can be corrected. The
     * anchor handed to the edit is the box's bounds lower-left, not a tap point.
     */
    fun editSelectedTextBox() {
        val sel = selectedTextBox ?: return
        selectedTextBox = null
        pendingTextTap = PendingTextTap(
            sel.pageIndex, sel.rect.left, sel.rect.bottom,
            editingId = sel.id, fontSize = sel.fontSize, fontName = sel.fontName,
            initialText = sel.text)
    }

    fun removeSelectedTextBox() {
        val sel = selectedTextBox ?: return
        val doc = document ?: return
        viewModelScope.launch {
            try {
                // boundsAnchored: the coordinates are the box's reported rect, so
                // an undo must re-add against bounds, not the baseline.
                perform(
                    TextBoxOperation(
                        sel.pageIndex, sel.id, sel.text, sel.fontSize,
                        sel.rect.left, sel.rect.bottom, adding = false,
                        boundsAnchored = true, fontName = sel.fontName),
                    doc)
            } catch (e: Exception) {
                statusMessage = "Couldn't remove that text"
            }
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
        viewModelScope.launch {
            try {
                history.undo(doc)?.let { afterHistoryChange(it) }
            } catch (e: Exception) {
                statusMessage = "Couldn't undo that"
            }
        }
    }

    fun redo() {
        val doc = document ?: return
        viewModelScope.launch {
            try {
                history.redo(doc)?.let { afterHistoryChange(it) }
            } catch (e: Exception) {
                statusMessage = "Couldn't redo that"
            }
        }
    }

    /** The single funnel for every reversible change. */
    private suspend fun perform(operation: PdfEditOperation, doc: PdfDocument) {
        history.perform(operation, doc)
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
                    signatureStore.add("Signature ${signatures.size + 1}", out)
                }
                signatures.add(entry)
                statusMessage = "Signature added"
            } catch (e: Exception) {
                statusMessage = "Couldn't import the signature: ${e.message}"
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
                    signatureStore.add("Signature ${signatures.size + 1}", out)
                }
                signatures.add(entry)
                statusMessage = "Signature added"
            } catch (e: Exception) {
                statusMessage = "Couldn't save the signature: ${e.message}"
            }
        }
    }

    fun deleteSignature(id: String) {
        viewModelScope.launch(Dispatchers.IO) { signatureStore.delete(id) }
        signatures.removeAll { it.id == id }
    }

    fun startPlacement(entry: SignatureEntry) {
        selectedTextBox = null
        pendingSignature = entry
        statusMessage = "Tap the page where the signature should go"
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
            statusMessage = "That signature's image is missing"
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
            doc)
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
        viewModelScope.launch {
            val rect = clampToPage(newRect, state.pageSizes[sel.pageIndex])
            var page = doc.openPage(sel.pageIndex)
            val packed = try {
                page.stampImagePacked(sel.annotIndex)
            } finally {
                page.close()
            }
            if (packed == null) {
                statusMessage = "Couldn't read this signature's image"
                return@launch
            }
            perform(
                MoveStampOperation(
                    sel.pageIndex, sel.id, packed.copyOfRange(2, packed.size),
                    packed[0], packed[1], from = sel.rect, to = rect),
                doc)
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
        viewModelScope.launch {
            // Read the image back first: without it, undo could not put the same
            // signature back.
            val page = doc.openPage(sel.pageIndex)
            val packed = try {
                page.stampImagePacked(sel.annotIndex)
            } finally {
                page.close()
            }
            if (packed == null) {
                statusMessage = "Couldn't remove this signature"
                return@launch
            }
            perform(
                StampOperation(sel.pageIndex, sel.id, packed.copyOfRange(2, packed.size),
                               packed[0], packed[1], sel.rect, adding = false),
                doc)
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
        isDirty = true
        renderedWidths.remove(pageIndex)
        lastWindow?.let { (first, last, width) -> updateRenderWindow(first, last, width) }
    }

    /**
     * Save = write back to the opened document's URI. SAF has no atomic rename,
     * so (desktop `AtomicFileWriter` analog, per #18): serialize into an
     * app-cache temp file first — a PDFium failure never touches the user's
     * file — verify the result reopens in the engine, then stream it to the
     * destination with truncation and fsync. Only then is the document clean.
     */
    fun save() {
        val uri = currentUri ?: return
        writeTo(uri, isSaveAs = false)
    }

    /** "Save a copy" destination picked via ACTION_CREATE_DOCUMENT. */
    fun saveAs(uri: Uri) = writeTo(uri, isSaveAs = true)

    private fun writeTo(uri: Uri, isSaveAs: Boolean) {
        val doc = document ?: return
        if (isSaving) return
        isSaving = true
        viewModelScope.launch {
            val app = getApplication<Application>()
            val temp = File(app.cacheDir, "save-${System.currentTimeMillis()}.pdf")
            try {
                withContext(Dispatchers.IO) { temp.parentFile?.mkdirs() }
                java.io.FileOutputStream(temp).use { doc.save(it) }

                val bytes = withContext(Dispatchers.IO) { temp.readBytes() }
                check(bytes.isNotEmpty()) { "engine produced an empty document" }
                engine.open(bytes).close()  // verify the output parses before touching the destination

                withContext(Dispatchers.IO) {
                    // "wt" guarantees truncation; plain "w" can leave a stale tail
                    // when the new file is shorter.
                    val pfd = app.contentResolver.openFileDescriptor(uri, "wt")
                        ?: throw IllegalStateException("provider returned no descriptor")
                    pfd.use {
                        java.io.FileOutputStream(it.fileDescriptor).use { out ->
                            out.write(bytes)
                            out.fd.sync()
                        }
                    }
                }

                if (isSaveAs) {
                    currentUri = uri
                    persistReadPermission(uri)
                    val name = queryDisplayName(uri)
                    recentsStore.add(RecentEntry(uri.toString(), name, System.currentTimeMillis()))
                    (uiState as? ViewerUiState.Viewing)?.let {
                        uiState = it.copy(displayName = name)
                    }
                }
                isDirty = false
                statusMessage = "Saved"
            } catch (_: SecurityException) {
                statusMessage = "No permission to write here anymore — use Save a copy."
            } catch (e: Exception) {
                statusMessage = "Save failed: ${e.message}"
            } finally {
                temp.delete()
                isSaving = false
            }
        }
    }

    fun openRecent(entry: RecentEntry) = openUri(Uri.parse(entry.uri))

    fun closeDocument() {
        closeCurrent()
        uiState = ViewerUiState.Home(recentsStore.load())
    }

    private fun toHome(error: String) {
        uiState = ViewerUiState.Home(recentsStore.load(), error)
    }

    private fun closeCurrent() {
        renderJob?.cancel()
        closeSearch()
        pageBitmaps.clear()
        renderedWidths.clear()
        lastWindow = null
        currentUri = null
        isDirty = false
        pendingSignature = null
        selectedStamp = null
        selectedTextBox = null
        // History belongs to the open document — never offer to undo an edit made
        // to a file that is no longer on screen.
        history.clear()
        canUndo = false
        canRedo = false
        isPlacingText = false
        pendingTextTap = null
        val doc = document ?: return
        document = null
        viewModelScope.launch { doc.close() }
    }

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
        return uri.lastPathSegment ?: "Document"
    }

    override fun onCleared() {
        // viewModelScope is already cancelled here; close synchronously on the
        // engine thread to not leak the native document.
        renderJob?.cancel()
        val doc = document
        document = null
        if (doc != null) runBlocking { doc.close() }
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
        // Marketing screenshot query: "rental" is the most-repeated real word in
        // the demo agreement (title, then twice in the opening paragraph).
        const val SCREENSHOT_SEARCH_TERM = "rental"

        // Marketing "Add text" shot: the name the customer would print under the
        // signature rule the demo agreement draws at y=400.
        const val SCREENSHOT_TEXT = "Jane Whitfield"
        const val SCREENSHOT_TEXT_X = 72.0
        const val SCREENSHOT_TEXT_Y = 372.0
    }
}
