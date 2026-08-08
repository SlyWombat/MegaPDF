package com.megapdf.android

import android.app.Application
import android.graphics.Bitmap
import android.net.Uri
import android.provider.OpenableColumns
import androidx.compose.runtime.getValue
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
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import java.io.File

/** Width/height of a page in PDF points (1/72 inch). */
data class PageSize(val widthPoints: Double, val heightPoints: Double)

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
    private val recentsStore =
        RecentFilesStore(File(application.filesDir, "recent.json"))

    var uiState: ViewerUiState by mutableStateOf(ViewerUiState.Home(recentsStore.load()))
        private set

    /** Rendered page bitmaps, keyed by page index; observed by the page list UI. */
    val pageBitmaps = mutableStateMapOf<Int, Bitmap>()

    private var renderJob: Job? = null
    private val renderedWidths = HashMap<Int, Int>()

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
        pageBitmaps.clear()
        renderedWidths.clear()
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
    }
}
