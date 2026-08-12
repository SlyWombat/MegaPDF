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

    /** Screenshot-mode sheet request ("sign" | "draw" | "search"); set via launch intent. */
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
            "viewer", "sign", "draw", "search" -> {
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
                }
            }
        }
    }

    /** The stamp currently selected for move/resize/remove. */
    var selectedStamp: SelectedStamp? by mutableStateOf(null)
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

            var edited = false
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
                    return@launch
                }
                selectedStamp = null

                val field = page.formFields().firstOrNull { it.rect.contains(x, y) }
                if (field != null) {
                    page.clickAt(field.rect.centerX, field.rect.centerY)
                    edited = true
                } else {
                    val mark = stamps
                        .filter { it.id.startsWith("mark:") }
                        .firstOrNull { it.rect.contains(x, y) }
                    if (mark != null) {
                        page.removeAnnot(mark.annotIndex)
                        edited = true
                    } else {
                        val square = page.detectCheckboxSquares()
                            .firstOrNull { it.contains(x, y) }
                        if (square != null) {
                            page.addCheckMark(square, "mark:${java.util.UUID.randomUUID()}")
                            edited = true
                        }
                    }
                }
            } finally {
                page.close()
            }
            if (edited) {
                markEditedAndRerender(pageIndex)
            }
        }
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

        val page = doc.openPage(pageIndex)
        try {
            page.addImageStamp(pixels, bitmap.width, bitmap.height, rect, id)
            val placed = page.stamps().first { it.id == id }
            selectedStamp = SelectedStamp(pageIndex, placed.annotIndex, id, placed.rect)
        } finally {
            page.close()
        }
        markEditedAndRerender(pageIndex)
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
            val page = doc.openPage(sel.pageIndex)
            try {
                val packed = page.stampImagePacked(sel.annotIndex)
                if (packed == null) {
                    statusMessage = "Couldn't read this signature's image"
                    return@launch
                }
                val w = packed[0]
                val h = packed[1]
                page.removeAnnot(sel.annotIndex)
                page.addImageStamp(packed.copyOfRange(2, packed.size), w, h, rect, sel.id)
                val placed = page.stamps().first { it.id == sel.id }
                selectedStamp = sel.copy(annotIndex = placed.annotIndex, rect = placed.rect)
            } finally {
                page.close()
            }
            markEditedAndRerender(sel.pageIndex)
        }
    }

    fun removeSelectedStamp() {
        val sel = selectedStamp ?: return
        val doc = document ?: return
        viewModelScope.launch {
            val page = doc.openPage(sel.pageIndex)
            try {
                page.removeAnnot(sel.annotIndex)
            } finally {
                page.close()
            }
            selectedStamp = null
            markEditedAndRerender(sel.pageIndex)
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
        const val RENDER_MARGIN = 2      // desktop MainViewModel's ±2-page window
        const val MAX_BITMAP_DIM = 2048  // bound worst-case bitmap memory
        const val MAX_SIGNATURE_SOURCE_DIM = 1500  // downscale huge photos before cleanup
        const val SEARCH_DEBOUNCE_MS = 250L  // keep typing from spamming the engine
        // Marketing screenshot query: "rental" is the most-repeated real word in
        // the demo agreement (title, then twice in the opening paragraph).
        const val SCREENSHOT_SEARCH_TERM = "rental"
    }
}
